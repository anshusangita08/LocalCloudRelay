using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace LocalCloudRelay;

/// <summary>
/// Works out which conversation a request belongs to, so a sticky router can hold one
/// model for the length of a chat and start fresh when the user starts a new one.
/// </summary>
public static class RelaySession
{
    /// <summary>
    /// Session headers clients actually send. The relay's own comes first because it is the
    /// one a client can set deliberately; the rest are the native ones agents use.
    /// </summary>
    public static readonly string[] Headers =
    [
        "x-relay-session-id",
        "x-claude-code-session-id",
        "x-session-id",
        "x-opencode-session"
    ];

    private static readonly Regex SystemReminder =
        new(@"<system-reminder>.*?</system-reminder>", RegexOptions.Singleline | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    private static readonly Regex MetadataSession =
        new(@"session_([0-9a-fA-F-]{8,})", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    /// <summary>
    /// A stable key for the conversation, or the client's address when nothing better is
    /// available.
    ///
    /// The fingerprint is the point: a client that sends no session header would otherwise
    /// leave a sticky router holding one model forever. The opening user message is what
    /// makes a conversation that conversation, and it stays the same for every later turn
    /// of the same chat - so a new chat picks a new model and a continuing one does not.
    ///
    /// Only a digest leaves this method. The text itself is never stored or logged.
    /// </summary>
    public static string Key(Func<string, string?> header, byte[]? body, string? clientAddress)
    {
        foreach (var name in Headers)
        {
            var value = header(name);
            if (!string.IsNullOrWhiteSpace(value)) return $"{name}:{value.Trim()}";
        }

        // Claude Code names its session inside metadata.user_id even when no header
        // reaches the relay. It survives compaction, which rewrites the opening message.
        if (MetadataSessionId(body) is { } metadataSession) return $"metadata:{metadataSession}";

        var address = string.IsNullOrWhiteSpace(clientAddress) ? "unknown" : clientAddress;
        return $"{address}:{ConversationFingerprint(body)}";
    }

    /// <summary>
    /// A short digest of the opening user turn, or "none" when the body carries nothing
    /// that identifies a conversation.
    /// </summary>
    internal static string ConversationFingerprint(byte[]? body)
    {
        var opening = FirstUserText(body);
        if (opening is null) return "none";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(opening));
        return Convert.ToHexString(hash.AsSpan(0, 8)).ToLowerInvariant();
    }

    /// <summary>A digest key shared by requests in one human turn, including tool continuations.</summary>
    internal static string? TurnKey(byte[]? body)
    {
        if (body is null || body.Length == 0) return null;
        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("messages", out var messages) ||
                messages.ValueKind != JsonValueKind.Array) return null;

            var turnCount = 0;
            string? latestText = null;
            foreach (var message in messages.EnumerateArray())
            {
                if (message.ValueKind != JsonValueKind.Object ||
                    !message.TryGetProperty("role", out var role) || role.ValueKind != JsonValueKind.String ||
                    !string.Equals(role.GetString(), "user", StringComparison.OrdinalIgnoreCase) ||
                    !message.TryGetProperty("content", out var content)) continue;

                if (content.ValueKind == JsonValueKind.Array &&
                    content.EnumerateArray().All(block => block.ValueKind == JsonValueKind.Object &&
                        block.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String &&
                        string.Equals(type.GetString(), "tool_result", StringComparison.OrdinalIgnoreCase)))
                    continue;

                if (content.ValueKind == JsonValueKind.String && string.IsNullOrWhiteSpace(content.GetString())) continue;
                turnCount++;
                latestText = content.ValueKind switch
                {
                    JsonValueKind.String => content.GetString(),
                    JsonValueKind.Array => FirstTextBlock(content),
                    _ => null
                };
            }

            if (turnCount == 0) return null;
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(latestText ?? string.Empty));
            return $"{turnCount}:{Convert.ToHexString(hash.AsSpan(0, 8)).ToLowerInvariant()}";
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// The longest prompt-cache lifetime the request asks for, or null for the default.
    /// Anthropic accepts <c>cache_control: {"type":"ephemeral","ttl":"1h"}</c> on tools,
    /// system blocks and message blocks; a conversation written that way stays warm for an
    /// hour, so moving it after five idle minutes would throw a live cache away.
    /// </summary>
    internal static TimeSpan? CacheLifetime(byte[]? body)
    {
        if (body is null || body.Length == 0) return null;
        // Cheap pre-check: most bodies carry no ttl at all.
        if (body.AsSpan().IndexOf("\"ttl\""u8) < 0) return null;
        try
        {
            using var document = JsonDocument.Parse(body);
            TimeSpan? longest = null;
            Walk(document.RootElement);
            return longest;

            void Walk(JsonElement element)
            {
                if (element.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in element.EnumerateArray()) Walk(item);
                    return;
                }
                if (element.ValueKind != JsonValueKind.Object) return;
                if (element.TryGetProperty("cache_control", out var control) && control.ValueKind == JsonValueKind.Object &&
                    control.TryGetProperty("ttl", out var ttl) && ttl.ValueKind == JsonValueKind.String &&
                    ParseTtl(ttl.GetString()) is { } lifetime && (longest is null || lifetime > longest))
                    longest = lifetime;
                foreach (var property in element.EnumerateObject())
                    if (property.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array) Walk(property.Value);
            }
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>True when the request offers the model tools to call (any dialect).</summary>
    internal static bool HasTools(byte[]? body)
    {
        if (body is null || body.Length == 0 || body.AsSpan().IndexOf("\"tools\""u8) < 0) return false;
        try
        {
            using var document = JsonDocument.Parse(body);
            return document.RootElement.ValueKind == JsonValueKind.Object &&
                   document.RootElement.TryGetProperty("tools", out var tools) &&
                   tools.ValueKind == JsonValueKind.Array && tools.GetArrayLength() > 0;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>Rough tokens an image costs; Anthropic and OpenAI both land near this for a typical screenshot.</summary>
    private const long ImageTokens = 1_600;

    /// <summary>
    /// A rough prompt size in tokens: characters of text over four, plus a flat cost per
    /// image. Base64 payloads are skipped, because counting a 1 MB screenshot as 250k
    /// tokens would wrongly rule out every model. Only used to skip models that plainly
    /// cannot hold the request, so being roughly right is enough.
    /// </summary>
    internal static long? EstimatePromptTokens(byte[]? body)
    {
        if (body is null || body.Length == 0) return null;
        try
        {
            using var document = JsonDocument.Parse(body);
            long characters = 0, images = 0;
            Walk(document.RootElement, null);
            return characters / 4 + images * ImageTokens;

            void Walk(JsonElement element, string? name)
            {
                switch (element.ValueKind)
                {
                    case JsonValueKind.Object:
                        if (element.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String &&
                            type.GetString() is "image" or "image_url" or "input_image")
                        {
                            images++;
                            return;
                        }
                        foreach (var property in element.EnumerateObject()) Walk(property.Value, property.Name);
                        break;
                    case JsonValueKind.Array:
                        foreach (var item in element.EnumerateArray()) Walk(item, name);
                        break;
                    case JsonValueKind.String:
                        if (name is "data" or "model") return;
                        var text = element.GetString();
                        if (text is null || text.StartsWith("data:", StringComparison.Ordinal)) return;
                        characters += text.Length;
                        break;
                }
            }
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static TimeSpan? ParseTtl(string? ttl)
    {
        if (string.IsNullOrWhiteSpace(ttl) || ttl.Length < 2) return null;
        if (!int.TryParse(ttl.AsSpan(0, ttl.Length - 1), System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out var amount) || amount <= 0) return null;
        return char.ToLowerInvariant(ttl[^1]) switch
        {
            'h' => TimeSpan.FromHours(amount),
            'm' => TimeSpan.FromMinutes(amount),
            's' => TimeSpan.FromSeconds(amount),
            _ => null
        };
    }

    /// <summary>
    /// The session id in Anthropic's metadata.user_id. Claude Code has sent it both as
    /// "user_..._session_&lt;uuid&gt;" and as a JSON object with a session_id field.
    /// </summary>
    internal static string? MetadataSessionId(byte[]? body)
    {
        if (body is null || body.Length == 0) return null;
        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("metadata", out var metadata) ||
                metadata.ValueKind != JsonValueKind.Object ||
                !metadata.TryGetProperty("user_id", out var userId) ||
                userId.ValueKind != JsonValueKind.String) return null;

            var text = userId.GetString();
            if (string.IsNullOrWhiteSpace(text)) return null;
            if (text.TrimStart().StartsWith('{'))
            {
                try
                {
                    using var inner = JsonDocument.Parse(text);
                    if (inner.RootElement.ValueKind == JsonValueKind.Object &&
                        inner.RootElement.TryGetProperty("session_id", out var sessionId) &&
                        sessionId.ValueKind == JsonValueKind.String &&
                        !string.IsNullOrWhiteSpace(sessionId.GetString()))
                        return sessionId.GetString()!.Trim();
                }
                catch (JsonException)
                {
                }
                return null;
            }
            var match = MetadataSession.Match(text);
            return match.Success ? match.Groups[1].Value.ToLowerInvariant() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Text with Claude Code's &lt;system-reminder&gt; blocks removed. Those blocks open
    /// every chat with the same boilerplate, so hashing them would merge unrelated chats.
    /// </summary>
    internal static string? WithoutReminders(string? text) =>
        text is null ? null : SystemReminder.Replace(text, string.Empty).Trim();

    /// <summary>
    /// The first user message, in either dialect. Both keep content as a string or a block
    /// list; OpenAI puts the system turn first, which is skipped by the role check.
    /// </summary>
    private static string? FirstUserText(byte[]? body)
    {
        if (body is null || body.Length == 0) return null;
        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind != JsonValueKind.Object) return null;
            if (!document.RootElement.TryGetProperty("messages", out var messages) ||
                messages.ValueKind != JsonValueKind.Array) return null;

            foreach (var message in messages.EnumerateArray())
            {
                if (message.ValueKind != JsonValueKind.Object) continue;
                if (!message.TryGetProperty("role", out var role) || role.ValueKind != JsonValueKind.String) continue;
                if (!string.Equals(role.GetString(), "user", StringComparison.OrdinalIgnoreCase)) continue;
                if (!message.TryGetProperty("content", out var content)) continue;

                var text = content.ValueKind switch
                {
                    JsonValueKind.String => WithoutReminders(content.GetString()),
                    JsonValueKind.Array => FirstTextBlock(content, skipReminders: true),
                    _ => null
                };
                if (!string.IsNullOrWhiteSpace(text)) return text;
            }
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? FirstTextBlock(JsonElement blocks, bool skipReminders = false)
    {
        foreach (var block in blocks.EnumerateArray())
        {
            string? value = null;
            if (block.ValueKind == JsonValueKind.String) value = block.GetString();
            else if (block.ValueKind == JsonValueKind.Object &&
                block.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String)
                value = text.GetString();
            else continue;

            if (!skipReminders) return value;
            value = WithoutReminders(value);
            if (!string.IsNullOrWhiteSpace(value)) return value;
        }
        return null;
    }
}
