using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace LocalCloudRelay;

/// <summary>Maps Responses SSE events to the OpenAI Chat Completions or Anthropic Messages stream.</summary>
public sealed class OpenAiResponsesStreamTranslator
{
    private readonly WireProtocol _target;
    private readonly Dictionary<int, (string Id, string Name, int ClientIndex)> _tools = [];
    private readonly Dictionary<int, int> _anthropicToolBlocks = [];
    private bool _started;
    private bool _openTextBlock;
    private int _anthropicBlockIndex = -1;
    private int _nextToolIndex;
    private bool _hasToolCalls;
    private long _inputTokens;
    private long _outputTokens;
    private string _responseId = "resp-relay";
    private string _responseModel = string.Empty;

    public OpenAiResponsesStreamTranslator(WireProtocol target)
    {
        if (target is not (WireProtocol.OpenAi or WireProtocol.Anthropic))
            throw new ArgumentOutOfRangeException(nameof(target));
        _target = target;
    }

    public bool IsCompleted { get; private set; }
    public bool IsFailed { get; private set; }
    public JsonObject? CompletedResponse { get; private set; }

    public IReadOnlyList<string> Transform(string eventName, string data)
    {
        JsonObject? payload;
        try { payload = JsonNode.Parse(data) as JsonObject; }
        catch (JsonException) { return []; }
        if (payload is null) return [];
        switch (eventName)
        {
            case "response.created":
                UpdateEnvelope(payload["response"] as JsonObject);
                return [];
            case "response.output_item.added":
                return AddOutputItem(payload);
            case "response.output_text.delta":
                return AddTextDelta(String(payload["delta"]));
            case "response.refusal.delta":
                return AddRefusalDelta(String(payload["delta"]));
            case "response.function_call_arguments.delta":
                return AddArgumentsDelta(payload);
            case "response.completed":
                CompletedResponse = payload["response"]?.DeepClone() as JsonObject;
                UpdateEnvelope(CompletedResponse);
                IsCompleted = CompletedResponse is not null && String(CompletedResponse["status"]) == "completed";
                if (!IsCompleted)
                {
                    IsFailed = true;
                    return EmitError("The OpenAI Responses request did not complete successfully.");
                }
                ReadUsage(CompletedResponse!);
                return Finish();
            case "response.failed":
            case "response.incomplete":
                IsFailed = true;
                var error = payload["response"]?["error"]?["message"] is JsonNode message
                    ? String(message) : "The OpenAI Responses request did not complete successfully.";
                return EmitError(error);
            default:
                return [];
        }
    }

    public async Task PumpAsync(Stream source, Stream destination, CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(source, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
        var eventName = string.Empty;
        var data = new StringBuilder();
        while (true)
        {
            var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line is null)
            {
                await DispatchAsync().ConfigureAwait(false);
                break;
            }
            if (line.Length == 0)
            {
                await DispatchAsync().ConfigureAwait(false);
                if (IsCompleted || IsFailed) return;
                continue;
            }
            if (line.StartsWith("event:", StringComparison.Ordinal)) eventName = line[6..].Trim();
            else if (line.StartsWith("data:", StringComparison.Ordinal))
            {
                if (data.Length > 0) data.Append('\n');
                data.Append(line[5..].TrimStart());
            }
        }
        if (!IsCompleted && !IsFailed)
            throw new OpenAiResponsesIncompleteException("The OpenAI Responses stream ended before response.completed.");

        async Task DispatchAsync()
        {
            if (data.Length == 0) { eventName = string.Empty; return; }
            foreach (var output in Transform(eventName, data.ToString()))
            {
                var bytes = Encoding.UTF8.GetBytes(output + "\n");
                await destination.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
            }
            await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
            eventName = string.Empty;
            data.Clear();
        }
    }

    public static string TranslateCompletedResponse(string body, WireProtocol target)
    {
        JsonObject response;
        try { response = JsonNode.Parse(body) as JsonObject ?? throw new JsonException(); }
        catch (JsonException) { throw new OpenAiResponsesIncompleteException("The OpenAI Responses API returned an invalid JSON response."); }
        if (String(response["status"]) != "completed")
            throw new OpenAiResponsesIncompleteException("The OpenAI Responses API returned a response that was not completed.");
        return CompletedJson(response, target).ToJsonString();
    }

    public static string TranslateError(string body, WireProtocol target)
    {
        string message;
        try
        {
            var root = JsonNode.Parse(body) as JsonObject;
            message = String(root?["error"]?["message"] ?? root?["message"]);
        }
        catch (JsonException) { message = string.Empty; }
        if (message.Length == 0) message = "The OpenAI Responses request failed.";
        if (target == WireProtocol.OpenAi)
            return new JsonObject { ["error"] = new JsonObject { ["message"] = message, ["type"] = "api_error" } }.ToJsonString();
        return new JsonObject
        {
            ["type"] = "error",
            ["error"] = new JsonObject { ["type"] = "api_error", ["message"] = message }
        }.ToJsonString();
    }

    public static async Task<string> ReadCompletedResponseAsync(Stream source, WireProtocol target, CancellationToken cancellationToken)
    {
        var translator = new OpenAiResponsesStreamTranslator(target);
        await using var ignored = new MemoryStream();
        await translator.PumpAsync(source, ignored, cancellationToken).ConfigureAwait(false);
        if (!translator.IsCompleted || translator.CompletedResponse is null)
            throw new OpenAiResponsesIncompleteException("The OpenAI Responses stream did not include a successful response.completed event.");
        return CompletedJson(translator.CompletedResponse, target).ToJsonString();
    }

    private IReadOnlyList<string> AddOutputItem(JsonObject payload)
    {
        if (payload["item"] is not JsonObject item || String(item["type"]) != "function_call") return [];
        var outputIndex = Int(payload["output_index"]);
        var callId = String(item["call_id"]);
        if (callId.Length == 0) callId = String(item["id"]);
        var name = String(item["name"]);
        if (callId.Length == 0 || name.Length == 0) return [];
        var index = _nextToolIndex++;
        _tools[outputIndex] = (callId, name, index);
        _hasToolCalls = true;
        var output = new List<string>();
        EnsureStarted(output);
        if (_target == WireProtocol.OpenAi)
        {
            output.AddRange(ChatChunk(new JsonObject
            {
                ["tool_calls"] = new JsonArray(new JsonObject
                {
                    ["index"] = index,
                    ["id"] = callId,
                    ["type"] = "function",
                    ["function"] = new JsonObject { ["name"] = name, ["arguments"] = string.Empty }
                })
            }));
            return output;
        }
        CloseAnthropicText(output);
        var blockIndex = OpenAnthropicTool(callId, name, output);
        _anthropicToolBlocks[outputIndex] = blockIndex;
        return output;
    }

    private IReadOnlyList<string> AddTextDelta(string text)
    {
        if (text.Length == 0) return [];
        var output = new List<string>();
        EnsureStarted(output);
        if (_target == WireProtocol.OpenAi)
        {
            output.AddRange(ChatChunk(new JsonObject { ["content"] = text }));
            return output;
        }
        if (!_openTextBlock)
        {
            CloseAnthropicBlock(output);
            _anthropicBlockIndex++;
            _openTextBlock = true;
            output.AddRange(AnthropicEvent("content_block_start", new JsonObject
            {
                ["type"] = "content_block_start", ["index"] = _anthropicBlockIndex,
                ["content_block"] = new JsonObject { ["type"] = "text", ["text"] = string.Empty }
            }));
        }
        output.AddRange(AnthropicEvent("content_block_delta", new JsonObject
        {
            ["type"] = "content_block_delta", ["index"] = _anthropicBlockIndex,
            ["delta"] = new JsonObject { ["type"] = "text_delta", ["text"] = text }
        }));
        return output;
    }

    private IReadOnlyList<string> AddRefusalDelta(string text)
    {
        if (text.Length == 0) return [];
        if (_target == WireProtocol.Anthropic) return AddTextDelta(text);
        var output = new List<string>();
        EnsureStarted(output);
        output.AddRange(ChatChunk(new JsonObject { ["content"] = text, ["refusal"] = text }));
        return output;
    }

    private IReadOnlyList<string> AddArgumentsDelta(JsonObject payload)
    {
        var outputIndex = Int(payload["output_index"]);
        if (!_tools.TryGetValue(outputIndex, out var tool)) return [];
        var delta = String(payload["delta"]);
        if (delta.Length == 0) return [];
        if (_target == WireProtocol.OpenAi)
        {
            return ChatChunk(new JsonObject
            {
                ["tool_calls"] = new JsonArray(new JsonObject
                {
                    ["index"] = tool.ClientIndex,
                    ["function"] = new JsonObject { ["arguments"] = delta }
                })
            });
        }
        var prefix = new List<string>();
        EnsureStarted(prefix);
        if (!_anthropicToolBlocks.TryGetValue(outputIndex, out var blockIndex))
        {
            blockIndex = OpenAnthropicTool(tool.Id, tool.Name, prefix);
            _anthropicToolBlocks[outputIndex] = blockIndex;
            prefix.AddRange(AnthropicEvent("content_block_delta", new JsonObject
            {
                ["type"] = "content_block_delta", ["index"] = blockIndex,
                ["delta"] = new JsonObject { ["type"] = "input_json_delta", ["partial_json"] = delta }
            }));
            return prefix;
        }
        prefix.AddRange(AnthropicEvent("content_block_delta", new JsonObject
        {
            ["type"] = "content_block_delta", ["index"] = blockIndex,
            ["delta"] = new JsonObject { ["type"] = "input_json_delta", ["partial_json"] = delta }
        }));
        return prefix;
    }

    private IReadOnlyList<string> Finish()
    {
        var output = new List<string>();
        EnsureStarted(output);
        if (_target == WireProtocol.OpenAi)
        {
            var final = new JsonObject
            {
                ["finish_reason"] = _hasToolCalls ? "tool_calls" : "stop"
            };
            output.AddRange(ChatChunk(new JsonObject(), final));
            if (CompletedResponse?["usage"] is JsonObject usage)
            {
                _inputTokens = Long(usage["input_tokens"]);
                _outputTokens = Long(usage["output_tokens"]);
                output.AddRange(ChatUsageChunk(_inputTokens, _outputTokens));
            }
            output.Add("data: [DONE]");
            output.Add(string.Empty);
            return output;
        }
        var lines = output;
        CloseAnthropicBlock(lines);
        lines.AddRange(AnthropicEvent("message_delta", new JsonObject
        {
            ["type"] = "message_delta",
            ["delta"] = new JsonObject { ["stop_reason"] = _hasToolCalls ? "tool_use" : "end_turn", ["stop_sequence"] = null },
            ["usage"] = new JsonObject { ["input_tokens"] = _inputTokens, ["output_tokens"] = _outputTokens }
        }));
        lines.AddRange(AnthropicEvent("message_stop", new JsonObject { ["type"] = "message_stop" }));
        return lines;
    }

    private IReadOnlyList<string> EmitError(string message)
    {
        if (_target == WireProtocol.OpenAi)
            return [$"data: {{\"error\":{{\"message\":{JsonSerializer.Serialize(message)},\"type\":\"api_error\"}}}}", string.Empty];
        return AnthropicEvent("error", new JsonObject
        {
            ["type"] = "error",
            ["error"] = new JsonObject { ["type"] = "api_error", ["message"] = message }
        });
    }

    private void EnsureStarted(List<string> output)
    {
        if (_started) return;
        _started = true;
        if (_target == WireProtocol.OpenAi)
        {
            output.AddRange(ChatChunk(new JsonObject { ["role"] = "assistant" }));
            return;
        }
        output.AddRange(AnthropicEvent("message_start", new JsonObject
        {
            ["type"] = "message_start",
            ["message"] = new JsonObject
            {
                ["id"] = _responseId, ["type"] = "message", ["role"] = "assistant", ["model"] = _responseModel,
                ["content"] = new JsonArray(), ["stop_reason"] = null, ["stop_sequence"] = null,
                ["usage"] = new JsonObject { ["input_tokens"] = _inputTokens, ["output_tokens"] = 0 }
            }
        }));
    }

    private int OpenAnthropicTool(string callId, string name, List<string> output)
    {
        // Responses may interleave argument deltas for parallel calls. Keep each tool block
        // open until text resumes or the response completes, so later deltas reuse its index.
        CloseAnthropicText(output);
        _anthropicBlockIndex++;
        output.AddRange(AnthropicEvent("content_block_start", new JsonObject
        {
            ["type"] = "content_block_start", ["index"] = _anthropicBlockIndex,
            ["content_block"] = new JsonObject { ["type"] = "tool_use", ["id"] = callId, ["name"] = name, ["input"] = new JsonObject() }
        }));
        return _anthropicBlockIndex;
    }

    private void CloseAnthropicText(List<string> output)
    {
        if (!_openTextBlock) return;
        output.AddRange(AnthropicEvent("content_block_stop", new JsonObject { ["type"] = "content_block_stop", ["index"] = _anthropicBlockIndex }));
        _openTextBlock = false;
    }

    private void CloseAnthropicBlock(List<string> output)
    {
        CloseAnthropicText(output);
        while (_anthropicToolBlocks.Count > 0)
        {
            var last = _anthropicToolBlocks.Values.Max();
            output.AddRange(AnthropicEvent("content_block_stop", new JsonObject { ["type"] = "content_block_stop", ["index"] = last }));
            _anthropicToolBlocks.Remove(_anthropicToolBlocks.First(pair => pair.Value == last).Key);
        }
    }

    private IReadOnlyList<string> ChatChunk(JsonObject delta, JsonObject? choiceOverride = null)
    {
        var choice = choiceOverride ?? new JsonObject { ["finish_reason"] = null };
        if (choiceOverride is null) choice["delta"] = delta;
        var data = new JsonObject
        {
            ["id"] = _responseId,
            ["object"] = "chat.completion.chunk",
            ["model"] = _responseModel,
            ["choices"] = new JsonArray(new JsonObject { ["index"] = 0, ["delta"] = choice["delta"]?.DeepClone() ?? delta, ["finish_reason"] = choice["finish_reason"]?.DeepClone() })
        };
        return [$"data: {data.ToJsonString()}", string.Empty];
    }

    private IReadOnlyList<string> ChatUsageChunk(long input, long output)
    {
        var data = new JsonObject
        {
            ["id"] = _responseId,
            ["object"] = "chat.completion.chunk",
            ["model"] = _responseModel,
            ["choices"] = new JsonArray(),
            ["usage"] = new JsonObject { ["prompt_tokens"] = input, ["completion_tokens"] = output, ["total_tokens"] = input + output }
        };
        return [$"data: {data.ToJsonString()}", string.Empty];
    }

    private IReadOnlyList<string> AnthropicEvent(string eventName, JsonObject payload) =>
        [$"event: {eventName}", $"data: {payload.ToJsonString()}", string.Empty];

    private void UpdateEnvelope(JsonObject? response)
    {
        if (response is null) return;
        if (String(response["id"]) is { Length: > 0 } id) _responseId = id;
        if (String(response["model"]) is { Length: > 0 } model) _responseModel = model;
        if (response["usage"] is JsonObject) ReadUsage(response);
    }

    private void ReadUsage(JsonObject response)
    {
        _inputTokens = Long(response["usage"]?["input_tokens"]);
        _outputTokens = Long(response["usage"]?["output_tokens"]);
    }

    private static JsonObject CompletedJson(JsonObject response, WireProtocol target)
    {
        var id = String(response["id"]);
        var model = String(response["model"]);
        var inputTokens = Long(response["usage"]?["input_tokens"]);
        var outputTokens = Long(response["usage"]?["output_tokens"]);
        var text = new StringBuilder();
        var refusal = new StringBuilder();
        var toolCalls = new JsonArray();
        var anthropicContent = new JsonArray();
        if (response["output"] is JsonArray items)
        {
            foreach (var node in items.OfType<JsonObject>())
            {
                var type = String(node["type"]);
                if (type == "message" && node["content"] is JsonArray content)
                {
                    foreach (var part in content.OfType<JsonObject>())
                    {
                        if (String(part["type"]) == "output_text")
                        {
                            var piece = String(part["text"]);
                            text.Append(piece);
                            anthropicContent.Add(new JsonObject { ["type"] = "text", ["text"] = piece });
                        }
                        else if (String(part["type"]) == "refusal")
                        {
                            var piece = String(part["refusal"]);
                            refusal.Append(piece);
                            anthropicContent.Add(new JsonObject { ["type"] = "text", ["text"] = piece });
                        }
                    }
                }
                else if (type == "refusal")
                {
                    var piece = String(node["refusal"] ?? node["text"]);
                    refusal.Append(piece);
                    anthropicContent.Add(new JsonObject { ["type"] = "text", ["text"] = piece });
                }
                else if (type == "function_call")
                {
                    var arguments = String(node["arguments"]);
                    JsonNode parsedArguments;
                    try { parsedArguments = JsonNode.Parse(arguments) ?? new JsonObject(); }
                    catch (JsonException) { parsedArguments = JsonValue.Create(arguments)!; }
                    var callId = String(node["call_id"]);
                    if (callId.Length == 0) callId = String(node["id"]);
                    var name = String(node["name"]);
                    toolCalls.Add(new JsonObject
                    {
                        ["id"] = callId, ["type"] = "function",
                        ["function"] = new JsonObject { ["name"] = name, ["arguments"] = arguments }
                    });
                    anthropicContent.Add(new JsonObject { ["type"] = "tool_use", ["id"] = callId, ["name"] = name, ["input"] = parsedArguments });
                }
            }
        }
        if (target == WireProtocol.OpenAi)
        {
            var visibleText = text.ToString() + refusal;
            var message = new JsonObject { ["role"] = "assistant", ["content"] = visibleText.Length > 0 ? JsonValue.Create(visibleText) : null };
            if (refusal.Length > 0) message["refusal"] = refusal.ToString();
            if (toolCalls.Count > 0) message["tool_calls"] = toolCalls;
            return new JsonObject
            {
                ["id"] = id, ["object"] = "chat.completion", ["model"] = model,
                ["choices"] = new JsonArray(new JsonObject
                {
                    ["index"] = 0, ["message"] = message,
                    ["finish_reason"] = toolCalls.Count > 0 ? "tool_calls" : "stop"
                }),
                ["usage"] = new JsonObject { ["prompt_tokens"] = inputTokens, ["completion_tokens"] = outputTokens, ["total_tokens"] = inputTokens + outputTokens }
            };
        }
        return new JsonObject
        {
            ["id"] = id, ["type"] = "message", ["role"] = "assistant", ["model"] = model,
            ["content"] = anthropicContent,
            ["stop_reason"] = toolCalls.Count > 0 ? "tool_use" : "end_turn",
            ["stop_sequence"] = null,
            ["usage"] = new JsonObject { ["input_tokens"] = inputTokens, ["output_tokens"] = outputTokens }
        };
    }

    private static string String(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : string.Empty;
    private static int Int(JsonNode? node) => node is JsonValue value && value.TryGetValue<int>(out var number) ? number : 0;
    private static long Long(JsonNode? node) => node is JsonValue value && value.TryGetValue<long>(out var number) ? number : 0;
}

public sealed class OpenAiResponsesIncompleteException(string message) : Exception(message);
