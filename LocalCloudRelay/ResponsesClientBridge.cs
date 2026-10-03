using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace LocalCloudRelay;

/// <summary>
/// Lets a Responses API client (Codex, newer OpenAI SDKs) use any provider or router.
///
/// The request is turned into a Chat Completions request at the edge, so routing,
/// failover, the Anthropic bridge and the ChatGPT-account bridge all work unchanged, and
/// the Chat Completions reply - buffered or streamed - is turned back into a Responses
/// object or event stream. Requests are stateless (Codex sends <c>store: false</c> and the
/// full input every turn); <c>previous_response_id</c> is not supported.
///
/// Custom (freeform) tools such as Codex's <c>apply_patch</c> become function tools with a
/// single string argument <c>input</c>, and their calls come back as
/// <c>custom_tool_call</c> items with that input.
/// </summary>
public static class ResponsesClientBridge
{
    public const string Path = "/v1/responses";

    /// <summary>What the reply translation needs to know about the request.</summary>
    public sealed record State(string Model, IReadOnlySet<string> CustomTools);

    /// <summary>True for a Responses create request: POST to /v1/responses.</summary>
    public static bool IsResponsesRequest(string method, string normalizedPath) =>
        Microsoft.AspNetCore.Http.HttpMethods.IsPost(method) && normalizedPath.TrimEnd('/').Equals(Path, StringComparison.OrdinalIgnoreCase);

    // ------------------------------------------------------------------ request

    public static string ToChat(string body, out State state)
    {
        JsonObject source;
        try { source = JsonNode.Parse(body) as JsonObject ?? throw new JsonException(); }
        catch (JsonException) { throw new OpenAiResponsesUnsupportedException("The Responses request body must be a JSON object."); }

        var model = Str(source["model"]);
        var custom = new HashSet<string>(StringComparer.Ordinal);
        var target = new JsonObject { ["model"] = model };
        var messages = new JsonArray();

        if (Str(source["instructions"]) is { Length: > 0 } instructions)
            messages.Add(new JsonObject { ["role"] = "system", ["content"] = instructions });

        switch (source["input"])
        {
            case JsonValue value when value.TryGetValue<string>(out var text):
                messages.Add(new JsonObject { ["role"] = "user", ["content"] = text });
                break;
            case JsonArray items:
                foreach (var item in items.OfType<JsonObject>()) AddInputItem(item, messages);
                break;
        }
        target["messages"] = messages;

        if (source["tools"] is JsonArray tools)
        {
            var chatTools = new JsonArray();
            foreach (var tool in tools.OfType<JsonObject>())
            {
                var name = Str(tool["name"]);
                if (name.Length == 0) continue;
                switch (Str(tool["type"]))
                {
                    case "function":
                        var function = new JsonObject
                        {
                            ["name"] = name,
                            ["parameters"] = tool["parameters"]?.DeepClone() ?? new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() }
                        };
                        if (tool["description"] is JsonNode description) function["description"] = description.DeepClone();
                        if (tool["strict"] is JsonNode strict) function["strict"] = strict.DeepClone();
                        chatTools.Add(new JsonObject { ["type"] = "function", ["function"] = function });
                        break;
                    case "custom":
                        // Freeform tools have no JSON schema; the whole input is one string.
                        custom.Add(name);
                        var describe = Str(tool["description"]);
                        if (tool["format"] is JsonObject format && Str(format["definition"]) is { Length: > 0 } grammar)
                            describe += $"\n\nThe input must follow this {Str(format["syntax"])} grammar:\n{grammar}";
                        chatTools.Add(new JsonObject
                        {
                            ["type"] = "function",
                            ["function"] = new JsonObject
                            {
                                ["name"] = name,
                                ["description"] = describe,
                                ["parameters"] = new JsonObject
                                {
                                    ["type"] = "object",
                                    ["properties"] = new JsonObject { ["input"] = new JsonObject { ["type"] = "string", ["description"] = "The raw tool input." } },
                                    ["required"] = new JsonArray("input")
                                }
                            }
                        });
                        break;
                    // Hosted tools (web_search, file_search, local_shell...) run inside OpenAI;
                    // no other provider can run them, so they are left out.
                }
            }
            if (chatTools.Count > 0) target["tools"] = chatTools;
        }

        switch (source["tool_choice"])
        {
            case JsonValue value when value.TryGetValue<string>(out var mode) && mode is "auto" or "none" or "required":
                if (target["tools"] is not null) target["tool_choice"] = mode;
                break;
            case JsonObject choice when Str(choice["name"]) is { Length: > 0 } chosen && target["tools"] is not null:
                target["tool_choice"] = new JsonObject { ["type"] = "function", ["function"] = new JsonObject { ["name"] = chosen } };
                break;
        }
        if (target["tools"] is not null && source["parallel_tool_calls"] is JsonValue parallel) target["parallel_tool_calls"] = parallel.DeepClone();
        if (source["max_output_tokens"] is JsonValue max) target["max_tokens"] = max.DeepClone();
        foreach (var key in new[] { "temperature", "top_p" })
            if (source[key] is JsonValue number) target[key] = number.DeepClone();
        if (source["text"]?["format"] is JsonObject textFormat && Str(textFormat["type"]) == "json_schema")
        {
            target["response_format"] = new JsonObject
            {
                ["type"] = "json_schema",
                ["json_schema"] = new JsonObject
                {
                    ["name"] = Str(textFormat["name"]) is { Length: > 0 } schemaName ? schemaName : "response",
                    ["schema"] = textFormat["schema"]?.DeepClone(),
                    ["strict"] = textFormat["strict"]?.DeepClone()
                }
            };
        }
        if (source["stream"] is JsonValue stream && stream.TryGetValue<bool>(out var streaming) && streaming)
        {
            target["stream"] = true;
            target["stream_options"] = new JsonObject { ["include_usage"] = true };
        }

        state = new State(model, custom);
        return target.ToJsonString();
    }

    private static void AddInputItem(JsonObject item, JsonArray messages)
    {
        var type = Str(item["type"]);
        if (type.Length == 0 && item["role"] is not null) type = "message";
        switch (type)
        {
            case "message":
                var role = Str(item["role"]) switch { "developer" => "system", var r => r };
                if (role is not ("user" or "assistant" or "system")) return;
                var content = MessageContent(item["content"], role);
                if (content is null) return;
                messages.Add(new JsonObject { ["role"] = role, ["content"] = content });
                return;
            case "function_call":
            case "custom_tool_call":
                var callId = Str(item["call_id"]) is { Length: > 0 } id ? id : Str(item["id"]);
                var arguments = type == "custom_tool_call"
                    ? new JsonObject { ["input"] = Str(item["input"]) }.ToJsonString()
                    : Str(item["arguments"]) is { Length: > 0 } args ? args : "{}";
                var call = new JsonObject
                {
                    ["id"] = callId, ["type"] = "function",
                    ["function"] = new JsonObject { ["name"] = Str(item["name"]), ["arguments"] = arguments }
                };
                // Consecutive calls belong to one assistant turn.
                if (messages.Count > 0 && messages[^1] is JsonObject last && Str(last["role"]) == "assistant")
                {
                    if (last["tool_calls"] is not JsonArray calls) last["tool_calls"] = calls = new JsonArray();
                    calls.Add(call);
                }
                else
                {
                    messages.Add(new JsonObject { ["role"] = "assistant", ["content"] = null, ["tool_calls"] = new JsonArray(call) });
                }
                return;
            case "function_call_output":
            case "custom_tool_call_output":
                messages.Add(new JsonObject
                {
                    ["role"] = "tool",
                    ["tool_call_id"] = Str(item["call_id"]),
                    ["content"] = item["output"] is JsonArray parts ? JoinText(parts) : Str(item["output"])
                });
                return;
            // reasoning items, hosted tool calls and anything newer carry nothing a
            // Chat Completions model can use.
        }
    }

    private static JsonNode? MessageContent(JsonNode? content, string role)
    {
        if (content is JsonValue value && value.TryGetValue<string>(out var text)) return text;
        if (content is not JsonArray parts) return null;
        if (role != "user") return JoinText(parts) is { Length: > 0 } joined ? joined : null;
        var chatParts = new JsonArray();
        foreach (var part in parts.OfType<JsonObject>())
        {
            switch (Str(part["type"]))
            {
                case "input_text" or "output_text" or "text":
                    chatParts.Add(new JsonObject { ["type"] = "text", ["text"] = Str(part["text"]) });
                    break;
                case "input_image" when Str(part["image_url"]) is { Length: > 0 } url:
                    chatParts.Add(new JsonObject { ["type"] = "image_url", ["image_url"] = new JsonObject { ["url"] = url } });
                    break;
            }
        }
        return chatParts.Count > 0 ? chatParts : null;
    }

    private static string JoinText(JsonArray parts) =>
        string.Concat(parts.OfType<JsonObject>()
            .Where(p => Str(p["type"]) is "input_text" or "output_text" or "text")
            .Select(p => Str(p["text"])));

    // ------------------------------------------------------------------ buffered reply

    /// <summary>A Chat Completions reply as a Responses object.</summary>
    public static string FromChat(string chatJson, State state)
    {
        var chat = JsonNode.Parse(chatJson) as JsonObject ?? throw new JsonException("Not a JSON object.");
        var builder = new ResponseBuilder(state, Str(chat["model"]));
        var choice = chat["choices"] is JsonArray choices && choices.Count > 0 ? choices[0] as JsonObject : null;
        var message = choice?["message"] as JsonObject;
        if (Str(message?["content"]) is { Length: > 0 } text) builder.AppendText(text);
        if (message?["tool_calls"] is JsonArray calls)
        {
            var index = 0;
            foreach (var call in calls.OfType<JsonObject>())
            {
                builder.StartTool(index, Str(call["id"]), Str(call["function"]?["name"]));
                builder.AppendArguments(index, Str(call["function"]?["arguments"]));
                index++;
            }
        }
        builder.FinishReason = Str(choice?["finish_reason"]);
        builder.ReadUsage(chat["usage"] as JsonObject);
        return builder.Response("completed").ToJsonString();
    }

    // ------------------------------------------------------------------ streamed reply

    /// <summary>
    /// Turns a Chat Completions event stream into Responses events, one SSE line at a time.
    /// </summary>
    public sealed class StreamTranslator(State state)
    {
        private readonly ResponseBuilder _builder = new(state, state.Model);
        private readonly List<string> _out = [];
        private int _sequence;
        private bool _started;
        private bool _finished;
        private int _openText = -1;

        public bool Finished => _finished;

        /// <summary>Feeds one line of the Chat stream and returns the SSE text to send.</summary>
        public string Feed(string line)
        {
            _out.Clear();
            if (_finished || !line.StartsWith("data:", StringComparison.Ordinal)) return string.Empty;
            var data = line[5..].Trim();
            if (data.Length == 0) return string.Empty;
            if (data == "[DONE]") { Finish(); return Drain(); }

            JsonObject? chunk;
            try { chunk = JsonNode.Parse(data) as JsonObject; }
            catch (JsonException) { return string.Empty; }
            if (chunk is null) return string.Empty;

            Start(Str(chunk["model"]));
            if (chunk["error"] is JsonObject error)
            {
                var failed = _builder.Response("failed");
                failed["error"] = new JsonObject { ["code"] = "upstream_error", ["message"] = Str(error["message"]) };
                Emit("response.failed", new JsonObject { ["response"] = failed });
                _finished = true;
                return Drain();
            }
            if (chunk["usage"] is JsonObject usage) _builder.ReadUsage(usage);
            if (chunk["choices"] is JsonArray choices && choices.Count > 0 && choices[0] is JsonObject choice)
            {
                if (choice["delta"] is JsonObject delta)
                {
                    if (Str(delta["content"]) is { Length: > 0 } text) Text(text);
                    if (delta["tool_calls"] is JsonArray calls)
                        foreach (var call in calls.OfType<JsonObject>()) Tool(call);
                }
                if (Str(choice["finish_reason"]) is { Length: > 0 } reason) _builder.FinishReason = reason;
            }
            return Drain();
        }

        /// <summary>Closes the response when the upstream ended without [DONE].</summary>
        public string Complete()
        {
            _out.Clear();
            if (!_finished) { Start(state.Model); Finish(); }
            return Drain();
        }

        private void Start(string model)
        {
            if (_started) return;
            _started = true;
            if (model.Length > 0) _builder.Model = model;
            Emit("response.created", new JsonObject { ["response"] = _builder.Response("in_progress", includeOutput: false) });
            Emit("response.in_progress", new JsonObject { ["response"] = _builder.Response("in_progress", includeOutput: false) });
        }

        private void Text(string text)
        {
            if (_openText < 0)
            {
                _openText = _builder.StartText();
                var item = _builder.Items[_openText];
                Emit("response.output_item.added", new JsonObject
                {
                    ["output_index"] = _openText,
                    ["item"] = new JsonObject { ["id"] = item.Id, ["type"] = "message", ["status"] = "in_progress", ["role"] = "assistant", ["content"] = new JsonArray() }
                });
                Emit("response.content_part.added", new JsonObject
                {
                    ["item_id"] = item.Id, ["output_index"] = _openText, ["content_index"] = 0,
                    ["part"] = new JsonObject { ["type"] = "output_text", ["text"] = "", ["annotations"] = new JsonArray() }
                });
            }
            _builder.AppendText(text);
            Emit("response.output_text.delta", new JsonObject
            {
                ["item_id"] = _builder.Items[_openText].Id, ["output_index"] = _openText, ["content_index"] = 0, ["delta"] = text
            });
        }

        private void CloseText()
        {
            if (_openText < 0) return;
            var item = _builder.Items[_openText];
            Emit("response.output_text.done", new JsonObject
            {
                ["item_id"] = item.Id, ["output_index"] = _openText, ["content_index"] = 0, ["text"] = item.Text.ToString()
            });
            Emit("response.content_part.done", new JsonObject
            {
                ["item_id"] = item.Id, ["output_index"] = _openText, ["content_index"] = 0,
                ["part"] = new JsonObject { ["type"] = "output_text", ["text"] = item.Text.ToString(), ["annotations"] = new JsonArray() }
            });
            Emit("response.output_item.done", new JsonObject { ["output_index"] = _openText, ["item"] = item.ToJson(state) });
            _openText = -1;
        }

        private void Tool(JsonObject call)
        {
            var index = call["index"] is JsonValue i && i.TryGetValue<int>(out var n) ? n : 0;
            var function = call["function"] as JsonObject;
            if (!_builder.HasTool(index))
            {
                CloseText();
                var output = _builder.StartTool(index, Str(call["id"]), Str(function?["name"]));
                var item = _builder.Items[output];
                // Custom tool input arrives as JSON arguments; it is only known once complete,
                // so custom calls are announced and finished together at the end.
                if (!item.IsCustom(state))
                    Emit("response.output_item.added", new JsonObject { ["output_index"] = output, ["item"] = item.ToJson(state, inProgress: true) });
            }
            if (Str(function?["arguments"]) is { Length: > 0 } arguments)
            {
                var output = _builder.AppendArguments(index, arguments);
                var item = _builder.Items[output];
                if (!item.IsCustom(state))
                    Emit("response.function_call_arguments.delta", new JsonObject { ["item_id"] = item.Id, ["output_index"] = output, ["delta"] = arguments });
            }
        }

        private void Finish()
        {
            _finished = true;
            CloseText();
            for (var index = 0; index < _builder.Items.Count; index++)
            {
                var item = _builder.Items[index];
                if (item.Kind != ItemKind.Tool) continue;
                if (item.IsCustom(state))
                {
                    Emit("response.output_item.added", new JsonObject { ["output_index"] = index, ["item"] = item.ToJson(state, inProgress: true) });
                }
                else
                {
                    Emit("response.function_call_arguments.done", new JsonObject
                    {
                        ["item_id"] = item.Id, ["output_index"] = index, ["arguments"] = item.Arguments.ToString()
                    });
                }
                Emit("response.output_item.done", new JsonObject { ["output_index"] = index, ["item"] = item.ToJson(state) });
            }
            var status = _builder.FinishReason == "length" ? "incomplete" : "completed";
            var response = _builder.Response(status);
            if (status == "incomplete") response["incomplete_details"] = new JsonObject { ["reason"] = "max_output_tokens" };
            Emit(status == "incomplete" ? "response.incomplete" : "response.completed", new JsonObject { ["response"] = response });
        }

        private void Emit(string type, JsonObject payload)
        {
            payload["type"] = type;
            payload["sequence_number"] = _sequence++;
            _out.Add($"event: {type}\ndata: {payload.ToJsonString()}\n\n");
        }

        private string Drain() => string.Concat(_out);
    }

    // ------------------------------------------------------------------ shared builder

    private enum ItemKind { Message, Tool }

    private sealed class Item(ItemKind kind, string id)
    {
        public ItemKind Kind { get; } = kind;
        public string Id { get; } = id;
        public StringBuilder Text { get; } = new();
        public StringBuilder Arguments { get; } = new();
        public string CallId { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;

        public bool IsCustom(State state) => Kind == ItemKind.Tool && state.CustomTools.Contains(Name);

        public JsonObject ToJson(State state, bool inProgress = false)
        {
            var status = inProgress ? "in_progress" : "completed";
            if (Kind == ItemKind.Message)
                return new JsonObject
                {
                    ["id"] = Id, ["type"] = "message", ["status"] = status, ["role"] = "assistant",
                    ["content"] = new JsonArray(new JsonObject { ["type"] = "output_text", ["text"] = Text.ToString(), ["annotations"] = new JsonArray() })
                };
            if (IsCustom(state))
            {
                var input = string.Empty;
                try { input = Str((JsonNode.Parse(Arguments.Length > 0 ? Arguments.ToString() : "{}") as JsonObject)?["input"]); }
                catch (JsonException) { input = Arguments.ToString(); }
                return new JsonObject
                {
                    ["id"] = Id, ["type"] = "custom_tool_call", ["status"] = status,
                    ["call_id"] = CallId, ["name"] = Name, ["input"] = inProgress ? string.Empty : input
                };
            }
            return new JsonObject
            {
                ["id"] = Id, ["type"] = "function_call", ["status"] = status,
                ["call_id"] = CallId, ["name"] = Name, ["arguments"] = inProgress ? string.Empty : Arguments.ToString()
            };
        }
    }

    private sealed class ResponseBuilder(State state, string model)
    {
        private readonly string _id = "resp_" + Guid.NewGuid().ToString("N");
        private readonly long _created = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        private readonly Dictionary<int, int> _tools = [];
        private JsonObject? _usage;

        public List<Item> Items { get; } = [];
        public string Model { get; set; } = model.Length > 0 ? model : state.Model;
        public string FinishReason { get; set; } = string.Empty;

        public int StartText()
        {
            Items.Add(new Item(ItemKind.Message, "msg_" + Guid.NewGuid().ToString("N")));
            return Items.Count - 1;
        }

        public void AppendText(string text)
        {
            if (Items.Count == 0 || Items[^1].Kind != ItemKind.Message) StartText();
            Items[^1].Text.Append(text);
        }

        public bool HasTool(int index) => _tools.ContainsKey(index);

        public int StartTool(int index, string callId, string name)
        {
            Items.Add(new Item(ItemKind.Tool, "fc_" + Guid.NewGuid().ToString("N"))
            {
                CallId = callId.Length > 0 ? callId : "call_" + Guid.NewGuid().ToString("N"),
                Name = name
            });
            _tools[index] = Items.Count - 1;
            return Items.Count - 1;
        }

        public int AppendArguments(int index, string arguments)
        {
            if (!_tools.TryGetValue(index, out var output)) output = StartTool(index, string.Empty, string.Empty);
            Items[output].Arguments.Append(arguments);
            return output;
        }

        public void ReadUsage(JsonObject? usage)
        {
            if (usage is null) return;
            var input = Long(usage["prompt_tokens"]);
            var output = Long(usage["completion_tokens"]);
            _usage = new JsonObject
            {
                ["input_tokens"] = input,
                ["input_tokens_details"] = new JsonObject { ["cached_tokens"] = Long(usage["prompt_tokens_details"]?["cached_tokens"]) },
                ["output_tokens"] = output,
                ["output_tokens_details"] = new JsonObject { ["reasoning_tokens"] = Long(usage["completion_tokens_details"]?["reasoning_tokens"]) },
                ["total_tokens"] = Long(usage["total_tokens"]) is > 0 and var total ? total : input + output
            };
        }

        public JsonObject Response(string status, bool includeOutput = true) => new()
        {
            ["id"] = _id,
            ["object"] = "response",
            ["created_at"] = _created,
            ["status"] = status,
            ["model"] = Model,
            ["output"] = includeOutput ? new JsonArray(Items.Select(item => (JsonNode)item.ToJson(state)).ToArray()) : new JsonArray(),
            ["usage"] = _usage?.DeepClone(),
            ["error"] = null,
            ["incomplete_details"] = null
        };
    }

    private static string Str(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : string.Empty;

    private static long Long(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<long>(out var number) ? number
        : node is JsonValue v && v.TryGetValue<double>(out var d) ? (long)d : 0;
}

/// <summary>
/// Sits between the relay pipeline and the client's response body for a Responses
/// request. Everything upstream of it writes Chat Completions; this writes Responses.
/// Error replies (status 400 and up) pass through untouched: their JSON
/// <c>{"error":{"message"}}</c> shape is what Responses clients read too.
/// </summary>
public sealed class ResponsesClientStream(Stream inner, ResponsesClientBridge.State state, Func<int> statusCode, Func<string?> contentType)
    : Stream
{
    private readonly MemoryStream _buffer = new();
    private readonly Decoder _decoder = Encoding.UTF8.GetDecoder();
    private readonly StringBuilder _line = new();
    private ResponsesClientBridge.StreamTranslator? _stream;
    private bool? _passThrough;
    private bool _completed;

    private bool PassThrough => _passThrough ??= statusCode() >= 400;
    private bool Streaming => contentType()?.Contains("event-stream", StringComparison.OrdinalIgnoreCase) == true;

    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Flush() => inner.Flush();
    public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);
    public override void Write(byte[] buffer, int offset, int count) => WriteAsync(buffer, offset, count).GetAwaiter().GetResult();

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (PassThrough) { await inner.WriteAsync(buffer, cancellationToken); return; }
        if (!Streaming) { _buffer.Write(buffer.Span); return; }

        _stream ??= new ResponsesClientBridge.StreamTranslator(state);
        var chars = new char[_decoder.GetCharCount(buffer.Span, flush: false)];
        _decoder.GetChars(buffer.Span, chars, flush: false);
        var output = new StringBuilder();
        foreach (var ch in chars)
        {
            if (ch == '\n')
            {
                output.Append(_stream.Feed(_line.ToString().TrimEnd('\r')));
                _line.Clear();
            }
            else _line.Append(ch);
        }
        if (output.Length > 0) await inner.WriteAsync(Encoding.UTF8.GetBytes(output.ToString()), cancellationToken);
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    /// <summary>Writes whatever is still owed: the converted body, or the closing events.</summary>
    public async Task CompleteAsync(CancellationToken cancellationToken)
    {
        if (_completed) return;
        _completed = true;
        if (PassThrough) return;
        if (_stream is not null)
        {
            var tail = _line.Length > 0 ? _stream.Feed(_line.ToString()) : string.Empty;
            tail += _stream.Complete();
            if (tail.Length > 0) await inner.WriteAsync(Encoding.UTF8.GetBytes(tail), cancellationToken);
            await inner.FlushAsync(cancellationToken);
            return;
        }
        if (_buffer.Length == 0) return;
        var body = Encoding.UTF8.GetString(_buffer.ToArray());
        string converted;
        try { converted = ResponsesClientBridge.FromChat(body, state); }
        catch (JsonException) { converted = body; }
        await inner.WriteAsync(Encoding.UTF8.GetBytes(converted), cancellationToken);
    }
}
