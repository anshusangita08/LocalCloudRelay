using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace LocalCloudRelay;

public sealed class AntigravityGatewayException(string message, HttpStatusCode statusCode = HttpStatusCode.BadRequest) : Exception(message)
{
    public HttpStatusCode StatusCode { get; } = statusCode;
}

/// <summary>A finished request. agy reports no token counts, so Usage stays null for it.</summary>
public sealed record AntigravityGeneration(string Text, string Model, RelayUsageSnapshot? Usage = null);

/// <summary>Uses the official Antigravity CLI account without inspecting its credential store.</summary>
public sealed class AntigravityGateway : IDisposable
{
    public static readonly TimeSpan DefaultRequestTimeout = TimeSpan.FromMinutes(5);
    public const bool ToolsAreHardDisabled = false;
    public const string ToolPermissionNotice = "Antigravity CLI tools follow the user's Antigravity CLI permissions; this gateway does not hard-disable tools.";
    // Any lower-case slug: the catalog carries GPT-OSS and other families beside Gemini
    // and Claude, and a vendor allow-list silently dropped them.
    private static readonly Regex ModelSlug = new("^[a-z][a-z0-9]*(?:[-._][a-z0-9]+)+$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private readonly ProviderCliRunner _cliRunner;
    private readonly CliConversationPool _conversations;
    private readonly TimeSpan _requestTimeout;
    private readonly TimeProvider _timeProvider;

    public AntigravityGateway(ProviderCliRunner cliRunner, TimeSpan? requestTimeout = null, TimeProvider? timeProvider = null)
    {
        _cliRunner = cliRunner ?? throw new ArgumentNullException(nameof(cliRunner));
        _requestTimeout = requestTimeout ?? DefaultRequestTimeout;
        if (_requestTimeout <= TimeSpan.Zero && _requestTimeout != Timeout.InfiniteTimeSpan)
            throw new ArgumentOutOfRangeException(nameof(requestTimeout));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _conversations = new CliConversationPool(_cliRunner, _timeProvider);
    }

    /// <summary>Fetches slugs from the documented plain-text `agy models` listing. This does not verify login.</summary>
    public async Task<IReadOnlyList<string>> FetchModelsAsync(string executable, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executable);
        var output = new StringBuilder();
        var run = await _cliRunner.RunAsync(executable, ["models"], null,
            (line, _) => { output.AppendLine(line); return ValueTask.CompletedTask; }, cancellationToken);
        if (!run.Succeeded)
            throw new AntigravityGatewayException("Could not read the Antigravity model catalog. Check the installed `agy` CLI; if it requests sign-in, authenticate with the official `agy` command and retry.", HttpStatusCode.BadGateway);
        try { return ParseModels(output.ToString()); }
        catch (AntigravityGatewayException) { throw; }
    }

    private static readonly Regex Url = new(@"https://[^\s""'<>]+", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// Signs the CLI in without a terminal. A tiny headless request runs in a hidden
    /// process; when the CLI needs a login it starts Google sign-in in the browser. If it
    /// only prints the sign-in link, the link is handed to <paramref name="openBrowser"/>.
    /// Returns true once a request succeeds, which is the only proof of sign-in agy offers.
    /// </summary>
    public async Task<bool> SignInAsync(string executable, Action<Uri> openBrowser, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executable);
        ArgumentNullException.ThrowIfNull(openBrowser);
        var workingDirectory = Path.Combine(Path.GetTempPath(), "local-cloud-relay-antigravity-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workingDirectory);
        var status = (string?)null;
        var linkWanted = false;
        var opened = false;
        var gate = new object();
        void Watch(string line)
        {
            lock (gate)
            {
                if (line.Contains("Open the following URL", StringComparison.OrdinalIgnoreCase) ||
                    line.Contains("Failed to open browser", StringComparison.OrdinalIgnoreCase) ||
                    line.Contains("sign-in link", StringComparison.OrdinalIgnoreCase))
                    linkWanted = true;
                if (!linkWanted || opened) return;
                var match = Url.Match(line);
                if (!match.Success || !Uri.TryCreate(match.Value, UriKind.Absolute, out var uri)) return;
                opened = true;
                openBrowser(uri);
            }
        }
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5), _timeProvider);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
            var run = await _cliRunner.RunAsync(executable,
                ["-p", "Reply with the single word ok.", "--output-format", "stream-json", "--sandbox", "--mode", "plan"],
                null, (line, _) =>
                {
                    Watch(line);
                    try
                    {
                        using var document = JsonDocument.Parse(line);
                        if (GetString(document.RootElement, "event") == "result" &&
                            document.RootElement.TryGetProperty("result", out var result))
                            status = GetString(result, "status");
                    }
                    catch (JsonException)
                    {
                        // Sign-in prompts are plain text.
                    }
                    return ValueTask.CompletedTask;
                }, workingDirectory, Watch, linked.Token);
            return run.Succeeded && status == "SUCCESS";
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
        finally
        {
            try { Directory.Delete(workingDirectory, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>Parses only first-column model slugs from the documented plain-text catalog.</summary>
    public static IReadOnlyList<string> ParseModels(string output)
    {
        ArgumentNullException.ThrowIfNull(output);
        var models = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rawLine in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var token = rawLine.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            if (token is null || !ModelSlug.IsMatch(token) || !token.Any(char.IsDigit)) continue;
            if (seen.Add(token)) models.Add(token);
        }
        return models.Count > 0
            ? models
            : throw new AntigravityGatewayException("The Antigravity model catalog was empty or had an unrecognized format. Update the `agy` CLI and retry.", HttpStatusCode.BadGateway);
    }

    public async Task<AntigravityGeneration> RunAsync(
        string executable,
        string model,
        WireProtocol clientProtocol,
        string requestJson,
        bool stream,
        Func<string, ValueTask> onStreamEvent,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executable);
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        ArgumentNullException.ThrowIfNull(onStreamEvent);
        if (clientProtocol is not (WireProtocol.OpenAi or WireProtocol.Anthropic))
            throw new AntigravityGatewayException("Antigravity account profiles accept OpenAI Chat Completions or Anthropic Messages requests only.");

        var (transcript, turns) = ParseConversation(requestJson, clientProtocol, "Antigravity",
            message => new AntigravityGatewayException(message));
        // System messages are part of the history, so a changed system prompt never matches.
        cancellationToken.ThrowIfCancellationRequested();
        var key = executable + "\0" + model;
        CliConversation conversation;
        string input;
        if (_conversations.Take(key, turns) is { } continued)
        {
            conversation = continued.Conversation;
            input = string.Join("\n\n", continued.NewTurns.Select(turn => turn.Text));
        }
        else
        {
            try
            {
                conversation = _conversations.Start(key, executable,
                    ["--input-format", "stream-json", "--output-format", "stream-json", "--model", model, "--sandbox", "--mode", "plan"],
                    "local-cloud-relay-antigravity-");
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or IOException)
            {
                throw new AntigravityGatewayException("The Antigravity CLI could not be started. Check that `agy` is installed.", HttpStatusCode.BadGateway);
            }
            input = "Answer the user's request directly as a text assistant. Do not use tools or modify files.\n\n" + transcript;
        }

        var keep = false;
        try
        {
            var text = new StringBuilder();
            var status = (string?)null;
            var fallback = (string?)null;
            var id = "chatcmpl-" + Guid.NewGuid().ToString("N");
            var messageId = "msg_" + Guid.NewGuid().ToString("N");
            var created = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var streamStarted = false;
            using var timeout = new CancellationTokenSource(_requestTimeout, _timeProvider);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);

            async ValueTask EmitTextAsync(string chunk)
            {
                if (chunk.Length == 0) return;
                text.Append(chunk);
                if (!stream) return;
                if (!streamStarted)
                {
                    streamStarted = true;
                    if (clientProtocol == WireProtocol.Anthropic)
                    {
                        await onStreamEvent(AnthropicEvent("message_start", new { type = "message_start", message = new { id = messageId, type = "message", role = "assistant", model, content = Array.Empty<object>(), stop_reason = (string?)null, stop_sequence = (string?)null, usage = new { input_tokens = 0, output_tokens = 0 } } }));
                        await onStreamEvent(AnthropicEvent("content_block_start", new { type = "content_block_start", index = 0, content_block = new { type = "text", text = "" } }));
                    }
                    else await onStreamEvent(OpenAiChunk(id, created, model, "", first: true, finish: null));
                }
                if (clientProtocol == WireProtocol.Anthropic)
                    await onStreamEvent(AnthropicEvent("content_block_delta", new { type = "content_block_delta", index = 0, delta = new { type = "text_delta", text = chunk } }));
                else await onStreamEvent(OpenAiChunk(id, created, model, chunk, first: false, finish: null));
            }

            var userLine = JsonSerializer.Serialize(new { @event = "user", message = new { content = input } });
            bool finished;
            try
            {
                finished = await conversation.RunTurnAsync(userLine, async line =>
                {
                    try
                    {
                        using var document = JsonDocument.Parse(line);
                        var root = document.RootElement;
                        if (GetString(root, "event") == "step_update" && root.TryGetProperty("step_update", out var step) &&
                            GetString(step, "step_type") == "agent_response")
                            await EmitTextAsync(GetString(step, "text_delta") ?? string.Empty);
                        else if (GetString(root, "event") == "result" && root.TryGetProperty("result", out var result))
                        {
                            // One result event closes each turn; the process stays up for the next.
                            status = GetString(result, "status");
                            fallback = GetString(result, "response");
                            return true;
                        }
                    }
                    catch (JsonException)
                    {
                        // Only documented stream-json event lines are forwarded.
                    }
                    return false;
                }, linked.Token);
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                throw new AntigravityGatewayException($"Antigravity did not finish within {_requestTimeout.TotalMinutes:0.##} minutes.", HttpStatusCode.GatewayTimeout);
            }
            catch (IOException)
            {
                // The process exited before reading the message.
                finished = false;
            }

            if ((!finished || status != "SUCCESS") && RateLimitSignal.LooksLikeLimit(fallback))
                throw new AntigravityGatewayException("Antigravity reports that this account has reached a usage or rate limit.", HttpStatusCode.TooManyRequests);
            if ((!finished || status != "SUCCESS") && RateLimitSignal.LooksSignedOut(fallback))
                throw new AntigravityGatewayException("Antigravity is signed out. Select the provider and press Edit to sign in again.", HttpStatusCode.Unauthorized);
            if (!finished || status != "SUCCESS")
            {
                var safeStatus = status is "ERROR" or "CANCELED" or "INTERRUPTED" or "INVALID" or "WAITING" or "RUNNING"
                    ? status
                    : "failed";
                throw new AntigravityGatewayException($"The Antigravity CLI request {safeStatus}. Check the selected model and sign in with the official `agy` CLI if authentication is required.", HttpStatusCode.BadGateway);
            }
            if (text.Length == 0 && !string.IsNullOrEmpty(fallback)) await EmitTextAsync(fallback);

            if (stream)
            {
                if (clientProtocol == WireProtocol.Anthropic)
                {
                    if (!streamStarted)
                    {
                        await onStreamEvent(AnthropicEvent("message_start", new { type = "message_start", message = new { id = messageId, type = "message", role = "assistant", model, content = Array.Empty<object>(), stop_reason = (string?)null, stop_sequence = (string?)null, usage = new { input_tokens = 0, output_tokens = 0 } } }));
                        await onStreamEvent(AnthropicEvent("content_block_start", new { type = "content_block_start", index = 0, content_block = new { type = "text", text = "" } }));
                    }
                    await onStreamEvent(AnthropicEvent("content_block_stop", new { type = "content_block_stop", index = 0 }));
                    await onStreamEvent(AnthropicEvent("message_delta", new { type = "message_delta", delta = new { stop_reason = "end_turn", stop_sequence = (string?)null }, usage = new { output_tokens = 0 } }));
                    await onStreamEvent(AnthropicEvent("message_stop", new { type = "message_stop" }));
                }
                else
                {
                    await onStreamEvent(OpenAiChunk(id, created, model, "", first: false, finish: "stop"));
                    await onStreamEvent("data: [DONE]\n\n");
                }
            }
            conversation.History = [.. turns, new CliTurn("assistant", text.ToString())];
            keep = true;
            return new AntigravityGeneration(text.ToString(), model);
        }
        finally
        {
            // A clean turn leaves the process ready for the conversation's next message;
            // anything else (error, timeout, client gone mid-turn) stops it.
            if (keep) _conversations.Return(conversation);
            else conversation.Dispose();
        }
    }

    /// <summary>Stops every live Antigravity process.</summary>
    public void Dispose() => _conversations.Dispose();

    public static string BufferedResponse(AntigravityGeneration result, WireProtocol protocol)
    {
        if (protocol == WireProtocol.Anthropic)
            return JsonSerializer.Serialize(new { id = "msg_" + Guid.NewGuid().ToString("N"), type = "message", role = "assistant", model = result.Model, content = new[] { new { type = "text", text = result.Text } }, stop_reason = "end_turn", stop_sequence = (string?)null, usage = CliUsage.Anthropic(result.Usage) });
        return JsonSerializer.Serialize(new { id = "chatcmpl-" + Guid.NewGuid().ToString("N"), @object = "chat.completion", created = DateTimeOffset.UtcNow.ToUnixTimeSeconds(), model = result.Model, choices = new[] { new { index = 0, message = new { role = "assistant", content = result.Text }, finish_reason = "stop" } }, usage = CliUsage.OpenAi(result.Usage) });
    }

    private static string ParsePrompt(string json, WireProtocol protocol) =>
        ParsePrompt(json, protocol, "Antigravity", message => new AntigravityGatewayException(message));

    /// <summary>Flattens a text-only chat request into one transcript. Shared by the CLI gateways.</summary>
    internal static string ParsePrompt(string json, WireProtocol protocol, string product, Func<string, Exception> error) =>
        ParseConversation(json, protocol, product, error).Transcript;

    /// <summary>The flattened transcript and the messages it was built from, system ones included.</summary>
    internal static (string Transcript, IReadOnlyList<CliTurn> Turns) ParseConversation(
        string json, WireProtocol protocol, string product, Func<string, Exception> error)
    {
        JsonDocument document;
        try { document = JsonDocument.Parse(json); }
        catch (JsonException) { throw error($"{product} requires a valid JSON messages request."); }
        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("messages", out var messages) || messages.ValueKind != JsonValueKind.Array)
                throw error($"{product} requires a messages array.");
            foreach (var field in new[] { "tools", "tool_choice", "functions", "function_call" })
                if (root.TryGetProperty(field, out var value) && value.ValueKind is not (JsonValueKind.Null or JsonValueKind.False) &&
                    (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() != 0))
                    throw error($"{product} account profiles do not support client tool definitions or tool choices.");
            if (root.TryGetProperty("stream", out var streamValue) && streamValue.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                throw error($"{product} requires the `stream` request field to be a boolean.");
            var supportedFields = protocol == WireProtocol.Anthropic
                ? new HashSet<string>(["model", "messages", "stream", "system"], StringComparer.Ordinal)
                : new HashSet<string>(["model", "messages", "stream"], StringComparer.Ordinal);
            foreach (var property in root.EnumerateObject())
            {
                if (property.Name is "tools" or "tool_choice" or "functions" or "function_call") continue;
                if (!supportedFields.Contains(property.Name))
                    throw error($"{product} account profiles reject unsupported request field or generation control `{property.Name}`.");
            }
            var transcript = new StringBuilder();
            var turns = new List<CliTurn>();
            if (protocol == WireProtocol.Anthropic && root.TryGetProperty("system", out var system))
            {
                var text = ReadText(system, product, error);
                AppendMessage(transcript, "System", text);
                turns.Add(new CliTurn("system", text));
            }
            foreach (var message in messages.EnumerateArray())
            {
                if (message.ValueKind != JsonValueKind.Object) throw error($"{product} messages must be objects with a role and text content.");
                var role = GetString(message, "role")?.ToLowerInvariant();
                if (role is "system" or "developer") role = "system";
                if (role is not ("system" or "user" or "assistant"))
                    throw error($"{product} accepts only system, user, and assistant message roles; tool results are unsupported.");
                var messageText = ReadText(message.TryGetProperty("content", out var content) ? content : default, product, error);
                AppendMessage(transcript, role, messageText);
                turns.Add(new CliTurn(role, messageText));
            }
            if (!messages.EnumerateArray().Any(message => GetString(message, "role")?.Equals("user", StringComparison.OrdinalIgnoreCase) == true))
                throw error($"{product} requires at least one user text message.");
            return (transcript.ToString(), turns);
        }
    }

    private static void AppendMessage(StringBuilder transcript, string role, string content)
    {
        if (transcript.Length > 0) transcript.AppendLine().AppendLine();
        transcript.Append(role).Append(": ").Append(content);
    }

    private static string ReadText(JsonElement value, string product, Func<string, Exception> error)
    {
        if (value.ValueKind == JsonValueKind.String) return value.GetString() ?? "";
        if (value.ValueKind == JsonValueKind.Array)
        {
            var chunks = new List<string>();
            foreach (var block in value.EnumerateArray())
            {
                if (block.ValueKind != JsonValueKind.Object || GetString(block, "type") != "text" || !block.TryGetProperty("text", out var text) || text.ValueKind != JsonValueKind.String)
                    throw error($"{product} account profiles support text-only messages; images and other content blocks are unsupported.");
                chunks.Add(text.GetString() ?? "");
            }
            return string.Join("\n", chunks);
        }
        throw error($"{product} account profiles support text-only messages; images and other content blocks are unsupported.");
    }

    private static string? GetString(JsonElement element, string name) => element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    internal static string AnthropicEvent(string name, object data) => $"event: {name}\ndata: {JsonSerializer.Serialize(data)}\n\n";
    internal static string OpenAiChunk(string id, long created, string model, string text, bool first, string? finish) => "data: " + JsonSerializer.Serialize(new
    {
        id, @object = "chat.completion.chunk", created, model,
        choices = new[] { new { index = 0, delta = first ? new { role = (string?)"assistant", content = text } : new { role = (string?)null, content = text }, finish_reason = finish } }
    }) + "\n\n";
}
