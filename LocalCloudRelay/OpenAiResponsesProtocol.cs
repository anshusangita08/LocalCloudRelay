using System.Text.Json;
using System.Text.Json.Nodes;

namespace LocalCloudRelay;

public sealed class OpenAiResponsesUnsupportedException(string message) : Exception(message);

/// <summary>Maps the relay's Chat Completions and Anthropic request shapes to stateless Responses input.</summary>
public static class OpenAiResponsesProtocol
{
    public static string BuildRequest(string body, WireProtocol sourceProtocol, string routeModel)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(routeModel);
        JsonObject source;
        try { source = JsonNode.Parse(body) as JsonObject ?? throw new JsonException(); }
        catch (JsonException) { throw new OpenAiResponsesUnsupportedException("The request body must be a JSON object."); }

        var request = new JsonObject
        {
            ["model"] = routeModel,
            ["store"] = false,
            ["stream"] = true
        };
        var instructions = new List<string>();
        var input = new JsonArray();

        if (sourceProtocol == WireProtocol.OpenAi)
            ReadChatCompletions(source, instructions, input, request);
        else if (sourceProtocol == WireProtocol.Anthropic)
            ReadAnthropicMessages(source, instructions, input, request);
        else
            throw new OpenAiResponsesUnsupportedException("Only OpenAI Chat Completions and Anthropic Messages requests are supported for this account.");

        if (instructions.Count > 0) request["instructions"] = string.Join("\n\n", instructions);
        request["input"] = input;
        return request.ToJsonString();
    }

    private static void ReadChatCompletions(JsonObject source, List<string> instructions, JsonArray input, JsonObject target)
    {
        if (source["messages"] is not JsonArray messages)
            throw new OpenAiResponsesUnsupportedException("A Chat Completions request must include a messages array.");
        foreach (var node in messages)
        {
            if (node is not JsonObject message) throw new OpenAiResponsesUnsupportedException("A Chat Completions message has an unsupported shape.");
            var role = String(message["role"]);
            var content = message["content"];
            if (role is "system" or "developer")
            {
                var text = LenientText(content);
                if (text.Length > 0) instructions.Add(text);
                continue;
            }
            if (role == "tool")
            {
                input.Add(new JsonObject
                {
                    ["type"] = "function_call_output",
                    ["call_id"] = String(message["tool_call_id"]),
                    ["output"] = LenientText(content)
                });
                continue;
            }
            // Other roles (function, a client's own extensions) carry nothing a Responses
            // model can use; skipped rather than failing the whole turn.
            if (role is not ("user" or "assistant")) continue;

            var parts = ChatParts(content, role);
            if (parts.Count > 0) input.Add(MessageItem(role, parts));

            if (message["tool_calls"] is JsonArray calls)
            {
                foreach (var callNode in calls)
                {
                    if (callNode is not JsonObject call || call["function"] is not JsonObject function)
                        throw new OpenAiResponsesUnsupportedException("A Chat Completions tool call has an unsupported shape.");
                    var name = String(function["name"]);
                    var id = String(call["id"]);
                    if (name.Length == 0 || id.Length == 0)
                        throw new OpenAiResponsesUnsupportedException("A Chat Completions tool call requires an id and function name.");
                    input.Add(new JsonObject
                    {
                        ["type"] = "function_call",
                        ["call_id"] = id,
                        ["name"] = name,
                        ["arguments"] = JsonArguments(function["arguments"])
                    });
                }
            }
        }
        if (source["tools"] is JsonArray tools) target["tools"] = ChatTools(tools);
        if (source["tool_choice"] is JsonNode choice) target["tool_choice"] = ChatToolChoice(choice);
        MapControls(source, target, allowParallelToolCalls: true);
    }

    private static void ReadAnthropicMessages(JsonObject source, List<string> instructions, JsonArray input, JsonObject target)
    {
        var system = source["system"] is JsonNode systemNode ? LenientText(systemNode) : string.Empty;
        if (system.Length > 0) instructions.Add(system);
        if (source["messages"] is not JsonArray messages)
            throw new OpenAiResponsesUnsupportedException("An Anthropic Messages request must include a messages array.");
        foreach (var node in messages)
        {
            if (node is not JsonObject message) throw new OpenAiResponsesUnsupportedException("An Anthropic message has an unsupported shape.");
            var role = String(message["role"]);
            if (role is not ("user" or "assistant")) continue;
            var blocks = Blocks(message["content"]);
            var parts = new JsonArray();
            foreach (var block in blocks)
            {
                var type = String(block["type"]);
                if (type == "text")
                {
                    var piece = String(block["text"]);
                    if (piece.Length > 0)
                        parts.Add(new JsonObject { ["type"] = role == "assistant" ? "output_text" : "input_text", ["text"] = piece });
                }
                else if (type == "image" && role == "user" && AnthropicImageUrl(block) is { } imageUrl)
                    parts.Add(new JsonObject { ["type"] = "input_image", ["image_url"] = imageUrl });
                else if (type == "tool_use" && role == "assistant")
                {
                    var id = String(block["id"]);
                    var name = String(block["name"]);
                    if (id.Length == 0 || name.Length == 0)
                        throw new OpenAiResponsesUnsupportedException("An Anthropic tool use requires an id and name.");
                    input.Add(new JsonObject
                    {
                        ["type"] = "function_call",
                        ["call_id"] = id,
                        ["name"] = name,
                        ["arguments"] = (block["input"]?.DeepClone() ?? new JsonObject()).ToJsonString()
                    });
                }
                else if (type == "tool_result" && role == "user")
                {
                    input.Add(new JsonObject
                    {
                        ["type"] = "function_call_output",
                        ["call_id"] = String(block["tool_use_id"]),
                        ["output"] = LenientText(block["content"])
                    });
                }
                // thinking, redacted_thinking, documents and server tool blocks belong to
                // Anthropic models; another model can neither read nor needs them.
            }
            if (parts.Count > 0) input.Add(MessageItem(role, parts));
        }
        if (source["tools"] is JsonArray tools) target["tools"] = AnthropicTools(tools);
        if (source["tool_choice"] is JsonNode choice) target["tool_choice"] = AnthropicToolChoice(choice);
        MapControls(source, target, allowParallelToolCalls: false);
    }

    private static void MapControls(JsonObject source, JsonObject target, bool allowParallelToolCalls)
    {
        string[] tokenKeys = allowParallelToolCalls
            ? new[] { "max_tokens", "max_completion_tokens" }
            : new[] { "max_tokens" };
        // max_completion_tokens is the newer name; when a client sends both, it wins.
        var providedTokenKeys = tokenKeys.Where(source.ContainsKey).Reverse().ToArray();
        if (providedTokenKeys.Length >= 1)
        {
            var value = source[providedTokenKeys[0]];
            if (value is not JsonValue jsonValue || !jsonValue.TryGetValue<int>(out _))
                throw new OpenAiResponsesUnsupportedException($"Request field '{providedTokenKeys[0]}' must be an integer.");
            target["max_output_tokens"] = value.DeepClone();
        }

        foreach (var key in new[] { "temperature", "top_p" })
        {
            if (!source.ContainsKey(key)) continue;
            var value = source[key];
            if (value is null || value.GetValueKind() != JsonValueKind.Number)
                throw new OpenAiResponsesUnsupportedException($"Request field '{key}' must be a number.");
            target[key] = value.DeepClone();
        }

        if (allowParallelToolCalls && source.ContainsKey("parallel_tool_calls"))
        {
            var value = source["parallel_tool_calls"];
            if (value is null || value.GetValueKind() is not (JsonValueKind.True or JsonValueKind.False))
                throw new OpenAiResponsesUnsupportedException("Request field 'parallel_tool_calls' must be a boolean.");
            target["parallel_tool_calls"] = value.DeepClone();
        }
    }

    private static JsonObject MessageItem(string role, JsonArray parts) => new()
    {
        ["type"] = "message",
        ["role"] = role,
        ["content"] = parts
    };

    /// <summary>Chat content as Responses parts: text, and images for user turns; anything else skipped.</summary>
    private static JsonArray ChatParts(JsonNode? content, string role)
    {
        var textType = role == "assistant" ? "output_text" : "input_text";
        var parts = new JsonArray();
        if (content is JsonValue value && value.TryGetValue<string>(out var text))
        {
            if (text.Length > 0) parts.Add(new JsonObject { ["type"] = textType, ["text"] = text });
            return parts;
        }
        if (content is not JsonArray array) return parts;
        foreach (var part in array.OfType<JsonObject>())
        {
            var type = String(part["type"]);
            if (type == "text" && String(part["text"]) is { Length: > 0 } piece)
                parts.Add(new JsonObject { ["type"] = textType, ["text"] = piece });
            else if (type == "image_url" && role == "user")
            {
                var url = part["image_url"] is JsonObject image ? String(image["url"]) : String(part["image_url"]);
                if (url.Length > 0) parts.Add(new JsonObject { ["type"] = "input_image", ["image_url"] = url });
            }
        }
        return parts;
    }

    /// <summary>An Anthropic image block as a URL: data URL for base64, the URL itself otherwise.</summary>
    private static string? AnthropicImageUrl(JsonObject block)
    {
        if (block["source"] is not JsonObject source) return null;
        return String(source["type"]) switch
        {
            "base64" => $"data:{String(source["media_type"])};base64,{String(source["data"])}",
            "url" => String(source["url"]) is { Length: > 0 } url ? url : null,
            _ => null
        };
    }

    /// <summary>The text in a content value or block list, ignoring blocks that are not text.</summary>
    private static string LenientText(JsonNode? content)
    {
        if (content is JsonValue value && value.TryGetValue<string>(out var text)) return text;
        if (content is JsonArray array)
            return string.Join("", array.OfType<JsonObject>().Where(b => String(b["type"]) is "text" or "input_text" or "output_text").Select(b => String(b["text"])));
        return string.Empty;
    }

    private static IReadOnlyList<JsonObject> Blocks(JsonNode? content)
    {
        if (content is JsonArray array)
            return array.Select(node => node as JsonObject ?? throw new OpenAiResponsesUnsupportedException("Anthropic message content contains an invalid block."))
                .ToArray();
        if (content is JsonValue value && value.TryGetValue<string>(out var text))
            return [new JsonObject { ["type"] = "text", ["text"] = text }];
        throw new OpenAiResponsesUnsupportedException("Anthropic message content must be text or a supported block array.");
    }

    private static JsonArray ChatTools(JsonArray tools)
    {
        var result = new JsonArray();
        foreach (var node in tools)
        {
            // Built-in tools of other APIs have no Responses equivalent here; skipped.
            if (node is not JsonObject tool || tool["function"] is not JsonObject function || String(function["name"]).Length == 0)
                continue;
            var item = new JsonObject { ["type"] = "function", ["name"] = function["name"]?.DeepClone() };
            Copy(function, item, "description", "parameters", "strict");
            result.Add(item);
        }
        return result;
    }

    private static JsonArray AnthropicTools(JsonArray tools)
    {
        var result = new JsonArray();
        foreach (var node in tools)
        {
            // Anthropic server tools (web search, code execution, ...) carry a versioned type
            // and no schema; only client tools translate.
            if (node is not JsonObject tool || String(tool["name"]).Length == 0) continue;
            if (String(tool["type"]) is { Length: > 0 } toolType && toolType != "custom") continue;
            var item = new JsonObject { ["type"] = "function", ["name"] = tool["name"]?.DeepClone() };
            Copy(tool, item, "description");
            item["parameters"] = tool["input_schema"]?.DeepClone() ?? new JsonObject { ["type"] = "object" };
            result.Add(item);
        }
        return result;
    }

    private static JsonNode ChatToolChoice(JsonNode choice)
    {
        if (choice is JsonValue value && value.TryGetValue<string>(out var text))
        {
            if (text is "none" or "required" or "auto") return JsonValue.Create(text)!;
            throw new OpenAiResponsesUnsupportedException("The Chat Completions tool_choice mode is not supported.");
        }
        if (choice is JsonObject obj && obj["function"] is JsonObject function && String(function["name"]).Length > 0)
            return new JsonObject { ["type"] = "function", ["name"] = function["name"]?.DeepClone() };
        throw new OpenAiResponsesUnsupportedException("The Chat Completions tool_choice shape is not supported.");
    }

    private static JsonNode AnthropicToolChoice(JsonNode choice)
    {
        if (choice is not JsonObject obj) throw new OpenAiResponsesUnsupportedException("The Anthropic tool_choice shape is not supported.");
        var type = String(obj["type"]);
        if (type == "auto") return JsonValue.Create("auto")!;
        if (type == "none") return JsonValue.Create("none")!;
        if (type == "any") return JsonValue.Create("required")!;
        if (type == "tool" && String(obj["name"]).Length > 0)
            return new JsonObject { ["type"] = "function", ["name"] = obj["name"]?.DeepClone() };
        throw new OpenAiResponsesUnsupportedException("The Anthropic tool_choice mode is not supported.");
    }

    private static string JsonArguments(JsonNode? arguments)
    {
        if (arguments is JsonValue value && value.TryGetValue<string>(out var text)) return text;
        return (arguments?.DeepClone() ?? new JsonObject()).ToJsonString();
    }

    private static string String(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : string.Empty;

    private static void Copy(JsonObject source, JsonObject target, params string[] names)
    {
        foreach (var name in names)
            if (source[name] is JsonNode value) target[name] = value.DeepClone();
    }

}
