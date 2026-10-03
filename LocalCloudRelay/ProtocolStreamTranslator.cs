using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace LocalCloudRelay;

/// <summary>
/// Translates a server-sent-event stream between the Anthropic and OpenAI dialects.
///
/// Both protocols stream the same way - `event:`/`data:` lines separated by a blank line
/// - but the events inside are not the same shape, and an Anthropic client cannot read
/// chat.completion.chunk. Streaming is the default for Claude Code and OpenCode, so
/// without this the bridge would work only for clients that do not stream.
///
/// Stateful on purpose: the Anthropic protocol requires a message_start before any
/// content, and content blocks must be opened and closed in order, so the translator has
/// to remember what it has already emitted.
/// </summary>
public sealed class ProtocolStreamTranslator
{
    private readonly WireProtocol _from;
    private readonly WireProtocol _to;

    // OpenAI -> Anthropic state
    private bool _messageStarted;
    private int _blockIndex = -1;
    private string? _openBlockType;
    private readonly Dictionary<int, (StringBuilder Id, StringBuilder Name, StringBuilder Arguments)> _toolCalls = [];
    private string? _stopReason;
    private long _inputTokens;
    private long _outputTokens;

    // Anthropic -> OpenAI state
    private bool _roleSent;
    private int _toolCallIndex = -1;
    private string? _finishReason;
    private long _anthropicInputTokens;
    private long _anthropicOutputTokens;
    private long _anthropicCacheReadTokens;
    private long _anthropicCacheWriteTokens;

    private readonly StringBuilder _data = new();
    private string _eventName = string.Empty;

    public bool IsTerminal { get; private set; }

    public ProtocolStreamTranslator(WireProtocol from, WireProtocol to)
    {
        _from = from;
        _to = to;
    }

    public static ProtocolStreamTranslator? For(WireProtocol from, WireProtocol to) =>
        ProtocolBridge.CanBridge(from, to) ? new ProtocolStreamTranslator(from, to) : null;

    /// <summary>
    /// Feeds one line of the upstream stream and returns the lines to send downstream.
    /// A blank line completes an event, which is where the work happens.
    /// </summary>
    public IReadOnlyList<string> Transform(string line)
    {
        if (IsTerminal) return [];
        if (line.Length == 0)
        {
            var completed = Complete();
            _data.Clear();
            _eventName = string.Empty;
            return completed;
        }

        if (line.StartsWith(':')) return [line, string.Empty];

        if (line.StartsWith("event:", StringComparison.OrdinalIgnoreCase))
        {
            _eventName = line[6..].Trim();
            return [];
        }

        if (line.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            var payload = line[5..].TrimStart();
            if (_data.Length > 0) _data.Append('\n');
            _data.Append(payload);
        }

        return [];
    }

    /// <summary>Emits whatever the protocol still owes at end of stream.</summary>
    public IReadOnlyList<string> Finish()
    {
        if (IsTerminal) return [];
        var output = new List<string>(Complete());
        _data.Clear();
        _eventName = string.Empty;
        if (!IsTerminal)
        {
            if (_from != WireProtocol.OpenAi || _stopReason is null)
                throw new InvalidDataException("The upstream event stream ended before its completion event.");
            output.AddRange(FinishOpenAiMessage());
        }
        return output;
    }

    private IReadOnlyList<string> Complete()
    {
        var payload = _data.ToString();
        if (payload.Length == 0) return [];

        if (_to == WireProtocol.Anthropic) return FromOpenAiChunk(payload);
        if (_to == WireProtocol.OpenAi) return FromAnthropicEvent(payload);
        return [];
    }

    // ------------------------------------------------- OpenAI upstream -> Anthropic client

    private IReadOnlyList<string> FromOpenAiChunk(string payload)
    {
        if (payload == "[DONE]")
            return FinishOpenAiMessage();

        JsonNode? node;
        try
        {
            node = JsonNode.Parse(payload);
        }
        catch (JsonException)
        {
            return [];
        }
        if (node is not JsonObject chunk) return [];

        // Check for error response
        if (chunk["error"] is JsonObject error)
            return HandleOpenAiError(error);

        var output = new List<string>();

        if (!_messageStarted)
        {
            _inputTokens = UsageValue(chunk, "prompt_tokens");
            output.AddRange(Event("message_start", new JsonObject
            {
                ["type"] = "message_start",
                ["message"] = MessageEnvelope(
                    chunk["id"]?.GetValue<string>() ?? "msg_relay",
                    chunk["model"]?.GetValue<string>() ?? string.Empty,
                    _inputTokens)
            }));
            _messageStarted = true;
        }

        // Usage arrives in the final chunk on OpenAI's stream, not the first, so both
        // counts are taken wherever they appear. Reading prompt_tokens only at the start
        // meant every streamed request recorded zero input tokens.
        if (UsageValue(chunk, "prompt_tokens") is var prompt && prompt > 0) _inputTokens = prompt;
        if (UsageValue(chunk, "completion_tokens") is var completion && completion > 0) _outputTokens = completion;

        var choice = chunk["choices"] is JsonArray choices && choices.Count > 0 ? choices[0] : null;
        var delta = choice?["delta"];

        if (delta?["content"] is JsonNode content && content.GetValueKind() == JsonValueKind.String)
        {
            var text = content.GetValue<string>();
            if (text.Length > 0)
            {
                OpenBlock("text", output, null, null);
                output.AddRange(Event("content_block_delta", new JsonObject
                {
                    ["type"] = "content_block_delta",
                    ["index"] = _blockIndex,
                    ["delta"] = new JsonObject { ["type"] = "text_delta", ["text"] = text }
                }));
            }
        }

        if (delta?["tool_calls"] is JsonArray calls)
        {
            for (int i = 0; i < calls.Count; i++)
            {
                var call = calls[i];
                if (call is not JsonObject toolCall) continue;
                var index = toolCall["index"]?.GetValue<int>() ?? i;

                if (!_toolCalls.TryGetValue(index, out var tool))
                {
                    tool = (new StringBuilder(), new StringBuilder(), new StringBuilder());
                    _toolCalls.Add(index, tool);
                }
                tool.Id.Append(toolCall["id"]?.GetValue<string>());
                tool.Name.Append(toolCall["function"]?["name"]?.GetValue<string>());
                tool.Arguments.Append(toolCall["function"]?["arguments"]?.GetValue<string>());
            }
        }

        if (choice?["finish_reason"] is JsonNode finish && finish.GetValueKind() == JsonValueKind.String)
        {
            _stopReason = ProtocolBridge.StopReasonToAnthropic(finish.GetValue<string>());
            EmitToolCalls(output);
        }

        return output;
    }

    private void EmitToolCalls(List<string> output)
    {
        // Tool fragments can alternate between indexes. Emit each complete block only
        // once, so a later fragment never targets a block already closed by another tool.
        foreach (var (index, tool) in _toolCalls.OrderBy(pair => pair.Key))
        {
            OpenBlock("tool_use", output, tool.Id.Length > 0 ? tool.Id.ToString() : $"toolu_{index}", tool.Name.ToString());
            if (tool.Arguments.Length > 0)
                output.AddRange(Event("content_block_delta", new JsonObject
                {
                    ["type"] = "content_block_delta",
                    ["index"] = _blockIndex,
                    ["delta"] = new JsonObject { ["type"] = "input_json_delta", ["partial_json"] = tool.Arguments.ToString() }
                }));
            CloseBlock(output);
        }
        _toolCalls.Clear();
    }

    private IReadOnlyList<string> FinishOpenAiMessage()
    {
        var output = new List<string>();
        if (!_messageStarted)
        {
            output.AddRange(Event("message_start", new JsonObject
            {
                ["type"] = "message_start",
                ["message"] = MessageEnvelope("msg_relay", string.Empty, 0)
            }));
            _messageStarted = true;
        }
        EmitToolCalls(output);
        CloseBlock(output);
        output.AddRange(Event("message_delta", new JsonObject
        {
            ["type"] = "message_delta",
            ["delta"] = new JsonObject
            {
                ["stop_reason"] = _stopReason ?? "end_turn",
                ["stop_sequence"] = null
            },
            // OpenAI usage may arrive after finish_reason, immediately before DONE.
            ["usage"] = new JsonObject { ["input_tokens"] = _inputTokens, ["output_tokens"] = _outputTokens }
        }));
        output.AddRange(Event("message_stop", new JsonObject { ["type"] = "message_stop" }));
        IsTerminal = true;
        return output;
    }

    private static JsonObject MessageEnvelope(string id, string model, long inputTokens) => new()
    {
        ["id"] = id,
        ["type"] = "message",
        ["role"] = "assistant",
        ["model"] = model,
        ["content"] = new JsonArray(),
        ["stop_reason"] = null,
        ["stop_sequence"] = null,
        ["usage"] = new JsonObject { ["input_tokens"] = inputTokens, ["output_tokens"] = 0 }
    };

    private void OpenBlock(string type, List<string> output, string? id, string? name)
    {
        // Consecutive text deltas belong to the same block. Opening one per chunk split a
        // single message into a block per token, which is not what the client expects to
        // render. Tool calls are the exception: each call is its own block, and the caller
        // opens one per completed call.
        if (type == "text" && _openBlockType == "text") return;
        CloseBlock(output);
        _blockIndex++;
        _openBlockType = type;
        var block = type == "text"
            ? new JsonObject { ["type"] = "text", ["text"] = string.Empty }
            : new JsonObject { ["type"] = "tool_use", ["id"] = id ?? string.Empty, ["name"] = name ?? string.Empty, ["input"] = new JsonObject() };
        output.AddRange(Event("content_block_start", new JsonObject
        {
            ["type"] = "content_block_start",
            ["index"] = _blockIndex,
            ["content_block"] = block
        }));
    }

    private void CloseBlock(List<string> output)
    {
        if (_openBlockType is null) return;
        output.AddRange(Event("content_block_stop", new JsonObject
        {
            ["type"] = "content_block_stop",
            ["index"] = _blockIndex
        }));
        _openBlockType = null;
    }

    private static long UsageValue(JsonObject chunk, string name) =>
        chunk["usage"]?[name]?.GetValue<long>() ?? 0;

    private IReadOnlyList<string> HandleOpenAiError(JsonObject error)
    {
        IsTerminal = true;
        // OpenAI error format: {"error":{"message":"...","type":"api_error",...}}
        // Translate to Anthropic error event format
        var message = error["message"]?.GetValue<string>() ?? "Unknown error";
        return Event("error", new JsonObject
        {
            ["type"] = "error",
            ["error"] = new JsonObject
            {
                ["type"] = "api_error",
                ["message"] = message
            }
        });
    }

    // ------------------------------------------------- Anthropic upstream -> OpenAI client

    private IReadOnlyList<string> FromAnthropicEvent(string payload)
    {
        JsonNode? node;
        try
        {
            node = JsonNode.Parse(payload);
        }
        catch (JsonException)
        {
            return [];
        }
        if (node is not JsonObject e) return [];

        var output = new List<string>();
        switch (e["type"]?.GetValue<string>())
        {
            case "ping":
                output.Add(": ping");
                output.Add(string.Empty);
                break;
            case "message_start":
                // Track usage from message_start
                if (e["message"]?["usage"] is JsonObject msgUsage)
                {
                    if (msgUsage["input_tokens"] is JsonNode input)
                        _anthropicInputTokens = Math.Max(_anthropicInputTokens, input.GetValue<long>());
                    if (msgUsage["output_tokens"] is JsonNode outTok)
                        _anthropicOutputTokens = Math.Max(_anthropicOutputTokens, outTok.GetValue<long>());
                    if (msgUsage["cache_read_input_tokens"] is JsonNode cacheRead)
                        _anthropicCacheReadTokens = Math.Max(_anthropicCacheReadTokens, cacheRead.GetValue<long>());
                    if (msgUsage["cache_creation_input_tokens"] is JsonNode cacheWrite)
                        _anthropicCacheWriteTokens = Math.Max(_anthropicCacheWriteTokens, cacheWrite.GetValue<long>());
                }
                if (!_roleSent)
                {
                    output.AddRange(ChunkLines(e["message"]?["id"]?.GetValue<string>() ?? "chatcmpl-relay",
                        e["message"]?["model"]?.GetValue<string>() ?? string.Empty,
                        new JsonObject { ["role"] = "assistant", ["content"] = string.Empty },
                        null,
                        null));
                    _roleSent = true;
                }
                break;

            case "content_block_start":
                if (e["content_block"]?["type"]?.GetValue<string>() == "tool_use")
                {
                    _toolCallIndex++;
                    output.AddRange(ChunkLines(ToolChunkId(e), null,
                        new JsonObject
                        {
                            ["tool_calls"] = new JsonArray
                            {
                                new JsonObject
                                {
                                    ["index"] = _toolCallIndex,
                                    ["id"] = e["content_block"]?["id"]?.DeepClone() ?? string.Empty,
                                    ["type"] = "function",
                                    ["function"] = new JsonObject
                                    {
                                        ["name"] = e["content_block"]?["name"]?.DeepClone() ?? string.Empty,
                                        ["arguments"] = string.Empty
                                    }
                                }
                            }
                        },
                        null, null));
                }
                break;

            case "content_block_delta":
                var delta = e["delta"];
                if (delta?["type"]?.GetValue<string>() == "text_delta")
                {
                    output.AddRange(ChunkLines(ToolChunkId(e), null,
                        new JsonObject { ["content"] = delta["text"]?.DeepClone() ?? string.Empty }, null, null));
                }
                else if (delta?["type"]?.GetValue<string>() == "input_json_delta")
                {
                    output.AddRange(ChunkLines(ToolChunkId(e), null,
                        new JsonObject
                        {
                            ["tool_calls"] = new JsonArray
                            {
                                new JsonObject
                                {
                                    ["index"] = _toolCallIndex,
                                    ["function"] = new JsonObject
                                    {
                                        ["arguments"] = delta["partial_json"]?.DeepClone() ?? string.Empty
                                    }
                                }
                            }
                        }, null, null));
                }
                break;

            case "message_delta":
                if (e["delta"]?["stop_reason"] is JsonNode stop && stop.GetValueKind() == JsonValueKind.String)
                    _finishReason = ProtocolBridge.FinishReasonFromAnthropic(stop.GetValue<string>());
                // Track usage from message_delta (cumulative)
                if (e["usage"] is JsonObject usage)
                {
                    if (usage["input_tokens"] is JsonNode input)
                        _anthropicInputTokens = Math.Max(_anthropicInputTokens, input.GetValue<long>());
                    if (usage["output_tokens"] is JsonNode outTok)
                        _anthropicOutputTokens = Math.Max(_anthropicOutputTokens, outTok.GetValue<long>());
                    if (usage["cache_read_input_tokens"] is JsonNode cacheRead)
                        _anthropicCacheReadTokens = Math.Max(_anthropicCacheReadTokens, cacheRead.GetValue<long>());
                    if (usage["cache_creation_input_tokens"] is JsonNode cacheWrite)
                        _anthropicCacheWriteTokens = Math.Max(_anthropicCacheWriteTokens, cacheWrite.GetValue<long>());
                }
                break;

            case "message_stop":
                IsTerminal = true;
                // Emit final chunk with usage and empty choices
                // Full usage, cache included, so the Session tab sees what the cache saved.
                var final = Chunk("chatcmpl-relay", string.Empty, new JsonObject(), _finishReason ?? "stop", null);
                var finalChunk = JsonNode.Parse(final["data: ".Length..])!.AsObject();
                finalChunk["usage"] = ProtocolBridge.OpenAiUsage(_anthropicInputTokens, _anthropicOutputTokens,
                    _anthropicCacheReadTokens, _anthropicCacheWriteTokens);
                output.Add("data: " + finalChunk.ToJsonString());
                output.Add(string.Empty);
                output.Add("data: [DONE]");
                output.Add(string.Empty);
                break;

            case "error":
                IsTerminal = true;
                // Anthropic error event; translate to OpenAI error format
                var errorPayload = e["error"];
                if (errorPayload is JsonObject err)
                {
                    var errorMsg = err["message"]?.GetValue<string>() ?? "An error occurred";
                    output.Add("data: " + new JsonObject
                    {
                        ["error"] = new JsonObject
                        {
                            ["message"] = errorMsg,
                            ["type"] = err["type"]?.GetValue<string>() ?? "api_error"
                        }
                    }.ToJsonString());
                    output.Add(string.Empty);
                }
                break;
        }

        return output;
    }

    private static string ToolChunkId(JsonObject e) => e["id"]?.GetValue<string>() ?? "chatcmpl-relay";

    private static string Chunk(string id, string? model, JsonObject delta, string? finishReason, long? totalTokens)
    {
        var choice = new JsonObject
        {
            ["index"] = 0,
            ["delta"] = delta,
            ["finish_reason"] = finishReason
        };
        var chunk = new JsonObject
        {
            ["id"] = id,
            ["object"] = "chat.completion.chunk",
            ["created"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            ["choices"] = new JsonArray { choice }
        };
        if (model is not null) chunk["model"] = model;
        if (totalTokens is not null) chunk["usage"] = new JsonObject { ["total_tokens"] = totalTokens };
        return "data: " + chunk.ToJsonString();
    }

    private static IReadOnlyList<string> ChunkLines(string id, string? model, JsonObject delta, string? finishReason, long? totalTokens) =>
        [Chunk(id, model, delta, finishReason, totalTokens), string.Empty];

    private static IReadOnlyList<string> Event(string name, JsonObject payload) =>
        [$"event: {name}", "data: " + payload.ToJsonString(), string.Empty];
}
