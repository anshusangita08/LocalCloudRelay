using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace LocalCloudRelay;

/// <summary>Which wire shape a request or response is written in.</summary>
public enum WireProtocol
{
    /// <summary>Not a shape this relay bridges - Gemini, for instance. Forwarded untouched.</summary>
    Other,
    Anthropic,
    OpenAi
}

/// <summary>
/// Translates between the Anthropic Messages API and the OpenAI Chat Completions API.
///
/// One base URL and one key are supposed to reach every model, but a gateway only speaks
/// the one protocol it implements: an OpenAI-only server has no /v1/messages, and an
/// Anthropic-only one has no /v1/chat/completions. Without this, picking a model is
/// effectively gated on the client speaking the same protocol as the upstream, which is
/// the opposite of the point.
///
/// Only the fields that carry meaning across the two are mapped. Anything the two
/// dialects do not share is dropped rather than guessed at, and a body that is already
/// in the right shape is never touched.
/// </summary>
public static class ProtocolBridge
{
    public const string AnthropicPath = "/v1/messages";
    public const string OpenAiPath = "/v1/chat/completions";

    public static WireProtocol FromRequestPath(string? path)
    {
        if (string.IsNullOrEmpty(path)) return WireProtocol.Other;
        var trimmed = path.TrimEnd('/');
        if (trimmed.EndsWith(AnthropicPath, StringComparison.OrdinalIgnoreCase)) return WireProtocol.Anthropic;
        if (trimmed.EndsWith(OpenAiPath, StringComparison.OrdinalIgnoreCase)) return WireProtocol.OpenAi;
        return WireProtocol.Other;
    }

    /// <summary>
    /// The protocol a provider is expected to speak. Only a hint: a gateway that serves
    /// both is labelled OpenAi here and still answers /v1/messages natively, which is why
    /// the caller verifies before translating.
    /// </summary>
    public static WireProtocol ForProviderKind(string? kind) =>
        string.Equals(kind, ProviderKinds.Anthropic, StringComparison.OrdinalIgnoreCase) ? WireProtocol.Anthropic
        : string.Equals(kind, ProviderKinds.OpenAi, StringComparison.OrdinalIgnoreCase) ? WireProtocol.OpenAi
        : string.Equals(kind, ProviderKinds.Local, StringComparison.OrdinalIgnoreCase) ? WireProtocol.OpenAi
        : WireProtocol.Other;

    public static string PathFor(WireProtocol protocol) => protocol switch
    {
        WireProtocol.Anthropic => AnthropicPath,
        WireProtocol.OpenAi => OpenAiPath,
        _ => string.Empty
    };

    public static bool CanBridge(WireProtocol from, WireProtocol to) =>
        from != to && from != WireProtocol.Other && to != WireProtocol.Other;

    // ------------------------------------------------------------------ requests

    public static string TranslateRequest(WireProtocol from, WireProtocol to, string body) =>
        (from, to) switch
        {
            (WireProtocol.Anthropic, WireProtocol.OpenAi) => AnthropicRequestToOpenAi(body),
            (WireProtocol.OpenAi, WireProtocol.Anthropic) => OpenAiRequestToAnthropic(body),
            _ => body
        };

    private static string AnthropicRequestToOpenAi(string body)
    {
        var source = Parse(body);
        if (source is null) return body;
        var target = new JsonObject();

        Copy(source, target, "model");
        Copy(source, target, "max_tokens");
        Copy(source, target, "temperature");
        Copy(source, target, "top_p");
        if (source["stop_sequences"] is JsonArray stops) target["stop"] = stops.DeepClone();
        if (source["stream"] is not null)
        {
            target["stream"] = source["stream"]!.DeepClone();
            if (source["stream"]?.GetValue<bool>() == true)
                target["stream_options"] = new JsonObject { ["include_usage"] = true };
        }

        var messages = new JsonArray();

        // Anthropic keeps the system prompt out of the message list; OpenAI wants it in.
        var system = TextOf(source["system"]);
        if (!string.IsNullOrWhiteSpace(system))
            messages.Add(new JsonObject { ["role"] = "system", ["content"] = system });

        if (source["messages"] is JsonArray turns)
        {
            foreach (var turn in turns)
            {
                if (turn is not JsonObject message) continue;
                AppendTurn(message, messages);
            }
        }
        target["messages"] = messages;

        if (source["tools"] is JsonArray tools && tools.Count > 0)
        {
            var converted = new JsonArray();
            foreach (var tool in tools)
            {
                if (tool is not JsonObject t) continue;
                var function = new JsonObject { ["name"] = t["name"]?.DeepClone() };
                if (t["description"] is not null) function["description"] = t["description"]!.DeepClone();
                function["parameters"] = t["input_schema"]?.DeepClone() ?? new JsonObject { ["type"] = "object" };
                converted.Add(new JsonObject { ["type"] = "function", ["function"] = function });
            }
            if (converted.Count > 0) target["tools"] = converted;
        }

        if (ToolChoiceToOpenAi(source["tool_choice"]) is { } choice) target["tool_choice"] = choice;

        return target.ToJsonString();
    }

    /// <summary>
    /// One Anthropic turn becomes one or more OpenAI messages, because Anthropic puts
    /// tool results inside a user turn while OpenAI gives them their own role.
    /// </summary>
    private static void AppendTurn(JsonObject message, JsonArray messages)
    {
        var role = message["role"]?.GetValue<string>() ?? "user";
        var content = message["content"];

        if (content is JsonValue)
        {
            messages.Add(new JsonObject { ["role"] = role, ["content"] = content.DeepClone() });
            return;
        }

        if (content is not JsonArray blocks)
        {
            messages.Add(new JsonObject { ["role"] = role, ["content"] = string.Empty });
            return;
        }

        var parts = new JsonArray();
        var toolCalls = new JsonArray();

        foreach (var block in blocks)
        {
            if (block is not JsonObject b) continue;
            switch (b["type"]?.GetValue<string>())
            {
                case "text":
                    parts.Add(new JsonObject { ["type"] = "text", ["text"] = b["text"]?.DeepClone() ?? string.Empty });
                    break;
                case "image":
                    var url = ImageUrl(b);
                    if (url is not null)
                        parts.Add(new JsonObject { ["type"] = "image_url", ["image_url"] = new JsonObject { ["url"] = url } });
                    break;
                case "tool_use":
                    toolCalls.Add(new JsonObject
                    {
                        ["id"] = b["id"]?.DeepClone() ?? string.Empty,
                        ["type"] = "function",
                        ["function"] = new JsonObject
                        {
                            ["name"] = b["name"]?.DeepClone() ?? string.Empty,
                            // OpenAI carries arguments as a JSON string, not an object.
                            ["arguments"] = (b["input"] ?? new JsonObject()).ToJsonString()
                        }
                    });
                    break;
                case "tool_result":
                    // Its own message, so it is appended separately rather than merged.
                    messages.Add(new JsonObject
                    {
                        ["role"] = "tool",
                        ["tool_call_id"] = b["tool_use_id"]?.DeepClone() ?? string.Empty,
                        ["content"] = TextOf(b["content"]) ?? string.Empty
                    });
                    break;
            }
        }

        // Text-only content is sent as a plain string, which is what most gateways and
        // older OpenAI-compatible servers expect.
        if (parts.Count > 0)
        {
            var entry = new JsonObject { ["role"] = role };
            entry["content"] = parts.Count == 1 && parts[0]?["type"]?.GetValue<string>() == "text"
                ? parts[0]!["text"]?.DeepClone()
                : parts;
            messages.Add(entry);
        }
        else if (toolCalls.Count == 0)
        {
            messages.Add(new JsonObject { ["role"] = role, ["content"] = string.Empty });
        }

        if (toolCalls.Count > 0)
            messages.Add(new JsonObject { ["role"] = "assistant", ["content"] = null, ["tool_calls"] = toolCalls });
    }

    private static string? ImageUrl(JsonObject block)
    {
        if (block["source"] is not JsonObject source) return null;
        if (source["type"]?.GetValue<string>() != "base64") return source["url"]?.GetValue<string>();
        var media = source["media_type"]?.GetValue<string>() ?? "image/png";
        var data = source["data"]?.GetValue<string>();
        return data is null ? null : $"data:{media};base64,{data}";
    }

    private static JsonNode? ToolChoiceToOpenAi(JsonNode? choice)
    {
        if (choice is not JsonObject c) return null;
        return c["type"]?.GetValue<string>() switch
        {
            "auto" => "auto",
            "any" => "required",
            "none" => "none",
            "tool" when c["name"] is not null => new JsonObject
            {
                ["type"] = "function",
                ["function"] = new JsonObject { ["name"] = c["name"]!.DeepClone() }
            },
            _ => null
        };
    }

    private static string OpenAiRequestToAnthropic(string body)
    {
        var source = Parse(body);
        if (source is null) return body;
        var target = new JsonObject();

        Copy(source, target, "model");
        Copy(source, target, "max_tokens");
        Copy(source, target, "temperature");
        Copy(source, target, "top_p");
        // Handle stop as either string (single value) or array
        if (source["stop"] is JsonValue stopValue)
            target["stop_sequences"] = new JsonArray { stopValue.DeepClone() };
        else if (source["stop"] is not null)
            target["stop_sequences"] = source["stop"]!.DeepClone();
        if (source["stream"] is not null) target["stream"] = source["stream"]!.DeepClone();
        // Use max_completion_tokens if max_tokens is absent
        if (target["max_tokens"] is null && source["max_completion_tokens"] is not null)
            target["max_tokens"] = source["max_completion_tokens"]!.DeepClone();
        // Anthropic requires max_tokens; OpenAI treats it as optional.
        target["max_tokens"] ??= 4096;

        var messages = new JsonArray();
        var system = new StringBuilder();

        if (source["messages"] is JsonArray turns)
        {
            foreach (var turn in turns)
            {
                if (turn is not JsonObject message) continue;
                var role = message["role"]?.GetValue<string>();
                if (string.Equals(role, "system", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(role, "developer", StringComparison.OrdinalIgnoreCase))
                {
                    if (TextOf(message["content"]) is { Length: > 0 } text)
                    {
                        if (system.Length > 0) system.Append('\n');
                        system.Append(text);
                    }
                    continue;
                }
                if (string.Equals(role, "tool", StringComparison.OrdinalIgnoreCase))
                {
                    messages.Add(new JsonObject
                    {
                        ["role"] = "user",
                        ["content"] = new JsonArray
                        {
                            new JsonObject
                            {
                                ["type"] = "tool_result",
                                ["tool_use_id"] = message["tool_call_id"]?.DeepClone() ?? string.Empty,
                                ["content"] = message["content"]?.DeepClone() ?? string.Empty
                            }
                        }
                    });
                    continue;
                }

                var entry = new JsonObject { ["role"] = role ?? "user" };
                JsonArray blocks;

                if (message["content"] is JsonValue contentValue)
                {
                    blocks = new JsonArray
                    {
                        new JsonObject { ["type"] = "text", ["text"] = contentValue.DeepClone() }
                    };
                    entry["content"] = blocks;
                }
                else if (message["content"] is JsonArray)
                {
                    blocks = ContentToAnthropic(message["content"]);
                    entry["content"] = blocks;
                }
                else
                {
                    blocks = new JsonArray();
                    entry["content"] = blocks;
                }

                if (message["tool_calls"] is JsonArray calls)
                {
                    foreach (var call in calls)
                    {
                        if (call?["function"] is not JsonObject function) continue;
                        blocks.Add(new JsonObject
                        {
                            ["type"] = "tool_use",
                            ["id"] = call["id"]?.DeepClone() ?? string.Empty,
                            ["name"] = function["name"]?.DeepClone() ?? string.Empty,
                            ["input"] = ParseArguments(function["arguments"])
                        });
                    }
                }
                messages.Add(entry);
            }
        }

        if (system.Length > 0) target["system"] = system.ToString();
        target["messages"] = messages;

        if (source["tools"] is JsonArray tools && tools.Count > 0)
        {
            var converted = new JsonArray();
            foreach (var tool in tools)
            {
                if (tool?["function"] is not JsonObject function) continue;
                var entry = new JsonObject
                {
                    ["name"] = function["name"]?.DeepClone() ?? string.Empty,
                    ["input_schema"] = function["parameters"]?.DeepClone() ?? new JsonObject { ["type"] = "object" }
                };
                if (function["description"] is not null) entry["description"] = function["description"]!.DeepClone();
                converted.Add(entry);
            }
            if (converted.Count > 0) target["tools"] = converted;
        }

        if (ToolChoiceToAnthropic(source["tool_choice"]) is { } choice) target["tool_choice"] = choice;

        AddCacheBreakpoints(target);
        return target.ToJsonString();
    }

    private static readonly HashSet<string> CacheableBlocks = new(StringComparer.Ordinal)
    {
        "text", "image", "tool_use", "tool_result", "document"
    };

    /// <summary>
    /// Marks where Anthropic may cache the prompt. OpenAI clients never send cache_control,
    /// and Anthropic caches nothing without it, so every translated turn used to pay full
    /// price for the whole conversation again. Up to three of the four allowed breakpoints
    /// are set: the end of the tools, the end of the system prompt, and the end of the
    /// conversation so far, which the next turn then reads back at a tenth of the price.
    ///
    /// Skipped for a one-off question with no tools, where nothing would be reused and the
    /// cache write would cost a quarter more. Prompts below Anthropic's minimum cacheable
    /// length are simply not cached and not charged extra, so no size check is needed.
    /// </summary>
    internal static void AddCacheBreakpoints(JsonObject target)
    {
        var messages = target["messages"] as JsonArray;
        var tools = target["tools"] as JsonArray;
        var conversation = messages is { Count: >= 2 };
        if (!conversation && tools is not { Count: > 0 }) return;

        static JsonObject Ephemeral() => new() { ["type"] = "ephemeral" };

        if (tools is { Count: > 0 } && tools[^1] is JsonObject lastTool) lastTool["cache_control"] = Ephemeral();

        if (target["system"] is JsonValue systemText && systemText.GetValueKind() == JsonValueKind.String)
        {
            target["system"] = new JsonArray
            {
                new JsonObject { ["type"] = "text", ["text"] = systemText.GetValue<string>(), ["cache_control"] = Ephemeral() }
            };
        }

        if (messages is null) return;
        var last = MarkLastBlock(messages, messages.Count - 1, userOnly: false);

        // The fourth breakpoint goes on the user turn before the one just marked, which is
        // where the previous request put its last one. Reading there is an exact hit;
        // without it Anthropic only looks back about 20 blocks from the new breakpoint, and
        // a turn that added more than that (a burst of tool calls) misses the cache.
        if (last > 0) MarkLastBlock(messages, last - 1, userOnly: true);

        static int MarkLastBlock(JsonArray messages, int from, bool userOnly)
        {
            for (var i = from; i >= 0; i--)
            {
                if (userOnly && messages[i]?["role"]?.GetValue<string>() != "user") continue;
                if (messages[i]?["content"] is not JsonArray blocks) continue;
                for (var j = blocks.Count - 1; j >= 0; j--)
                {
                    if (blocks[j] is JsonObject block && block["type"]?.GetValue<string>() is { } type && CacheableBlocks.Contains(type) &&
                        !(type == "text" && string.IsNullOrEmpty(block["text"]?.ToString())))
                    {
                        block["cache_control"] = Ephemeral();
                        return i;
                    }
                }
            }
            return -1;
        }
    }

    private static JsonNode ParseArguments(JsonNode? arguments)
    {
        var text = arguments?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(text) || text == "null") return new JsonObject();
        try
        {
            return JsonNode.Parse(text)!;
        }
        catch (JsonException)
        {
            // A gateway that sent malformed arguments must not fail the whole call.
            return new JsonObject();
        }
    }

    private static JsonArray ContentToAnthropic(JsonNode? content)
    {
        var blocks = new JsonArray();
        if (content is JsonArray parts)
        {
            foreach (var part in parts)
            {
                switch (part?["type"]?.GetValue<string>())
                {
                    case "text":
                        blocks.Add(new JsonObject { ["type"] = "text", ["text"] = part["text"]?.DeepClone() ?? string.Empty });
                        break;
                    case "image_url":
                        var url = part["image_url"]?["url"]?.GetValue<string>();
                        if (url is not null) blocks.Add(ImageBlock(url));
                        break;
                }
            }
        }
        else if (content is JsonValue value)
        {
            blocks.Add(new JsonObject { ["type"] = "text", ["text"] = value.DeepClone() });
        }
        return blocks;
    }

    private static JsonObject ImageBlock(string url)
    {
        var block = new JsonObject { ["type"] = "image" };
        var comma = url.IndexOf(',');
        if (url.StartsWith("data:", StringComparison.OrdinalIgnoreCase) && comma > 0)
        {
            var header = url[5..comma];
            var semicolon = header.IndexOf(';');
            var media = semicolon > 0 ? header[..semicolon] : "image/png";
            block["source"] = new JsonObject
            {
                ["type"] = "base64",
                ["media_type"] = media,
                ["data"] = url[(comma + 1)..]
            };
        }
        else
        {
            block["source"] = new JsonObject { ["type"] = "url", ["url"] = url };
        }
        return block;
    }

    private static JsonNode? ToolChoiceToAnthropic(JsonNode? choice)
    {
        if (choice is JsonValue value)
        {
            return value.GetValue<string>() switch
            {
                "auto" => new JsonObject { ["type"] = "auto" },
                "required" => new JsonObject { ["type"] = "any" },
                "none" => new JsonObject { ["type"] = "none" },
                _ => null
            };
        }
        if (choice is JsonObject o && o["function"]?["name"] is JsonNode name)
            return new JsonObject { ["type"] = "tool", ["name"] = name.DeepClone() };
        return null;
    }

    // ----------------------------------------------------------------- responses

    public static string TranslateResponse(WireProtocol from, WireProtocol to, string body) =>
        (from, to) switch
        {
            (WireProtocol.OpenAi, WireProtocol.Anthropic) => OpenAiResponseToAnthropic(body),
            (WireProtocol.Anthropic, WireProtocol.OpenAi) => AnthropicResponseToOpenAi(body),
            _ => body
        };

    public static string TranslateError(WireProtocol from, WireProtocol to, string body) =>
        (from, to) switch
        {
            (WireProtocol.OpenAi, WireProtocol.Anthropic) => TranslateOpenAiErrorToAnthropic(body),
            (WireProtocol.Anthropic, WireProtocol.OpenAi) => TranslateAnthropicErrorToOpenAi(body),
            _ => body
        };

    private static string OpenAiResponseToAnthropic(string body)
    {
        var source = Parse(body);
        if (source is null) return body;
        var choices = source["choices"] as JsonArray;
        if (choices is null || choices.Count == 0) return body;
        var message = choices[0]?["message"];

        var content = new JsonArray();
        var text = TextOf(message?["content"]);
        if (!string.IsNullOrWhiteSpace(text))
            content.Add(new JsonObject { ["type"] = "text", ["text"] = text });

        if (message?["tool_calls"] is JsonArray calls)
        {
            foreach (var call in calls)
            {
                if (call?["function"] is not JsonObject function) continue;
                content.Add(new JsonObject
                {
                    ["type"] = "tool_use",
                    ["id"] = call["id"]?.DeepClone() ?? string.Empty,
                    ["name"] = function["name"]?.DeepClone() ?? string.Empty,
                    ["input"] = ParseArguments(function["arguments"])
                });
            }
        }

        var target = new JsonObject
        {
            ["id"] = source["id"]?.DeepClone() ?? "msg_relay",
            ["type"] = "message",
            ["role"] = "assistant",
            ["model"] = source["model"]?.DeepClone() ?? string.Empty,
            ["content"] = content,
            ["stop_reason"] = StopReasonToAnthropic(source["choices"]?[0]?["finish_reason"]?.GetValue<string>()),
            ["stop_sequence"] = null
        };

        var usage = source["usage"];
        target["usage"] = new JsonObject
        {
            ["input_tokens"] = usage?["prompt_tokens"]?.DeepClone() ?? 0,
            ["output_tokens"] = usage?["completion_tokens"]?.DeepClone() ?? 0
        };

        return target.ToJsonString();
    }

    public static string? StopReasonToAnthropic(string? finishReason) => finishReason switch
    {
        "length" => "max_tokens",
        "tool_calls" => "tool_use",
        "content_filter" => "end_turn",
        "stop" => "end_turn",
        null => null,
        _ => "end_turn"
    };

    private static string AnthropicResponseToOpenAi(string body)
    {
        var source = Parse(body);
        if (source is null) return body;

        var text = new StringBuilder();
        var toolCalls = new JsonArray();
        if (source["content"] is JsonArray blocks)
        {
            foreach (var block in blocks)
            {
                switch (block?["type"]?.GetValue<string>())
                {
                    case "text":
                        text.Append(block["text"]?.GetValue<string>() ?? string.Empty);
                        break;
                    case "tool_use":
                        toolCalls.Add(new JsonObject
                        {
                            ["id"] = block["id"]?.DeepClone() ?? string.Empty,
                            ["type"] = "function",
                            ["function"] = new JsonObject
                            {
                                ["name"] = block["name"]?.DeepClone() ?? string.Empty,
                                ["arguments"] = (block["input"] ?? new JsonObject()).ToJsonString()
                            }
                        });
                        break;
                }
            }
        }

        var message = new JsonObject
        {
            ["role"] = "assistant",
            ["content"] = text.Length > 0 ? text.ToString() : null
        };
        if (toolCalls.Count > 0) message["tool_calls"] = toolCalls;

        var usage = source["usage"];
        var target = new JsonObject
        {
            ["id"] = source["id"]?.DeepClone() ?? "chatcmpl-relay",
            ["object"] = "chat.completion",
            ["created"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            ["model"] = source["model"]?.DeepClone() ?? string.Empty,
            ["choices"] = new JsonArray
            {
                new JsonObject
                {
                    ["index"] = 0,
                    ["message"] = message,
                    ["finish_reason"] = FinishReasonFromAnthropic(source["stop_reason"]?.GetValue<string>())
                }
            },
            ["usage"] = OpenAiUsage(
                usage?["input_tokens"]?.GetValue<long>() ?? 0,
                usage?["output_tokens"]?.GetValue<long>() ?? 0,
                usage?["cache_read_input_tokens"]?.GetValue<long>() ?? 0,
                usage?["cache_creation_input_tokens"]?.GetValue<long>() ?? 0)
        };

        return target.ToJsonString();
    }

    /// <summary>
    /// OpenAI's prompt_tokens include cached tokens and report them in
    /// prompt_tokens_details; Anthropic's input_tokens leave cache reads and writes out.
    /// </summary>
    internal static JsonObject OpenAiUsage(long input, long output, long cacheRead, long cacheWrite)
    {
        var prompt = input + cacheRead + cacheWrite;
        var usage = new JsonObject
        {
            ["prompt_tokens"] = prompt,
            ["completion_tokens"] = output,
            ["total_tokens"] = prompt + output
        };
        if (cacheRead > 0 || cacheWrite > 0)
            usage["prompt_tokens_details"] = new JsonObject { ["cached_tokens"] = cacheRead, ["cache_creation_tokens"] = cacheWrite };
        return usage;
    }

    public static string? FinishReasonFromAnthropic(string? stopReason) => stopReason switch
    {
        "max_tokens" => "length",
        "tool_use" => "tool_calls",
        "end_turn" => "stop",
        "stop_sequence" => "stop",
        null => null,
        _ => "stop"
    };

    private static string TranslateOpenAiErrorToAnthropic(string body)
    {
        var source = Parse(body);
        if (source is null) return body;

        var error = source["error"];
        if (error is not JsonObject errorObj) return body;

        var message = errorObj["message"]?.GetValue<string>() ?? string.Empty;
        var errorType = errorObj["type"]?.GetValue<string>() ?? "api_error";

        var target = new JsonObject
        {
            ["type"] = "error",
            ["error"] = new JsonObject
            {
                ["type"] = errorType,
                ["message"] = message
            }
        };

        return target.ToJsonString();
    }

    private static string TranslateAnthropicErrorToOpenAi(string body)
    {
        var source = Parse(body);
        if (source is null) return body;

        var error = source["error"];
        if (error is not JsonObject errorObj) return body;

        var message = errorObj["message"]?.GetValue<string>() ?? string.Empty;
        var errorType = errorObj["type"]?.GetValue<string>() ?? "api_error";

        var target = new JsonObject
        {
            ["error"] = new JsonObject
            {
                ["message"] = message,
                ["type"] = errorType
            }
        };

        return target.ToJsonString();
    }

    // -------------------------------------------------------------------- shared

    /// <summary>
    /// Flattens Anthropic content to plain text. Used where the target protocol has one
    /// text field - a system prompt, a tool result - and dropping the structure is the
    /// only honest mapping.
    /// </summary>
    private static string? TextOf(JsonNode? node)
    {
        switch (node)
        {
            case null:
                return null;
            case JsonValue value:
                return value.GetValueKind() == JsonValueKind.String ? value.GetValue<string>() : value.ToJsonString();
            case JsonArray array:
            {
                var text = new StringBuilder();
                bool first = true;
                foreach (var block in array)
                {
                    if (block is JsonValue part)
                    {
                        if (!first) text.Append('\n');
                        text.Append(part.GetValueKind() == JsonValueKind.String ? part.GetValue<string>() : part.ToJsonString());
                        first = false;
                        continue;
                    }
                    if (block?["type"]?.GetValue<string>() == "text")
                    {
                        if (!first) text.Append('\n');
                        text.Append(block["text"]?.GetValue<string>() ?? string.Empty);
                        first = false;
                    }
                }
                return text.ToString();
            }
            default:
                return node.ToJsonString();
        }
    }

    private static void Copy(JsonObject source, JsonObject target, string name)
    {
        if (source[name] is { } value) target[name] = value.DeepClone();
    }

    private static JsonObject? Parse(string body)
    {
        try
        {
            return JsonNode.Parse(body) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
