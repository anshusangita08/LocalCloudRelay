using System.Text;
using System.Text.Json;
using System.Net;

namespace LocalCloudRelay;

public sealed class ClaudeCodeGatewayException(string message, HttpStatusCode statusCode = HttpStatusCode.BadRequest) : Exception(message)
{
    public HttpStatusCode StatusCode { get; } = statusCode;
}

/// <summary>A finished turn. Usage comes from the CLI's result event when it reports one.</summary>
public sealed record ClaudeCodeGeneration(string Text, string Model, RelayUsageSnapshot? Usage = null);

/// <summary>Runs Claude Code with its own signed-in account, without reading its credential store.</summary>
public sealed class ClaudeCodeGateway : IDisposable
{
    public static readonly TimeSpan DefaultRequestTimeout = TimeSpan.FromMinutes(5);
    private readonly ProviderCliRunner _cliRunner;
    private readonly CliConversationPool _conversations;
    private readonly TimeSpan _requestTimeout;
    private readonly TimeProvider _timeProvider;

    public ClaudeCodeGateway(ProviderCliRunner cliRunner, TimeSpan? requestTimeout = null, TimeProvider? timeProvider = null)
    {
        _cliRunner = cliRunner ?? throw new ArgumentNullException(nameof(cliRunner));
        _requestTimeout = requestTimeout ?? DefaultRequestTimeout;
        if (_requestTimeout <= TimeSpan.Zero && _requestTimeout != Timeout.InfiniteTimeSpan)
            throw new ArgumentOutOfRangeException(nameof(requestTimeout));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _conversations = new CliConversationPool(_cliRunner, _timeProvider);
    }

    public async Task<bool> CheckAuthenticationAsync(string executable, CancellationToken cancellationToken = default)
    {
        // The CLI pretty-prints its status, so the JSON spans several lines.
        var status = new StringBuilder();
        var run = await _cliRunner.RunAsync(executable, ["auth", "status"], null,
            (line, _) => { status.AppendLine(line); return ValueTask.CompletedTask; }, cancellationToken);
        return run.Succeeded && IsAuthenticatedStatus(status.ToString());
    }

    /// <summary>The aliases Claude Code resolves to the newest model of each family.</summary>
    public static readonly IReadOnlyList<string> ModelAliases = ["fable", "opus", "sonnet", "haiku"];

    /// <summary>
    /// Resolves each alias to the exact model the account gets today. The CLI names the
    /// model in its init event, before any request is sent, so the process is stopped
    /// there and no tokens are spent. Aliases that do not resolve are left out.
    /// </summary>
    public async Task<IReadOnlyList<string>> ResolveModelsAsync(string executable, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executable);
        var models = new List<string>();
        foreach (var alias in ModelAliases)
        {
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            stop.CancelAfter(TimeSpan.FromSeconds(30));
            string? resolved = null;
            var workingDirectory = Path.Combine(Path.GetTempPath(), "local-cloud-relay-claude-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(workingDirectory);
            try
            {
                await _cliRunner.RunAsync(executable,
                    ["-p", "--setting-sources", "project", "--strict-mcp-config", "--output-format", "stream-json", "--verbose",
                     "--model", alias, "--no-session-persistence", "--disallowedTools", "*"],
                    "ok\n", async (line, _) =>
                    {
                        if (resolved is not null) return;
                        try
                        {
                            using var document = JsonDocument.Parse(line);
                            var root = document.RootElement;
                            if (root.ValueKind == JsonValueKind.Object &&
                                root.TryGetProperty("subtype", out var subtype) && subtype.ValueKind == JsonValueKind.String && subtype.GetString() == "init" &&
                                root.TryGetProperty("model", out var model) && model.ValueKind == JsonValueKind.String)
                            {
                                resolved = model.GetString();
                                await stop.CancelAsync();
                            }
                        }
                        catch (JsonException)
                        {
                            // Hook and status lines are not the init event.
                        }
                    }, workingDirectory, stop.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // Expected: stopped at the init event, or the alias timed out.
            }
            finally
            {
                try { Directory.Delete(workingDirectory, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
            if (!string.IsNullOrWhiteSpace(resolved) && !models.Contains(resolved, StringComparer.Ordinal))
                models.Add(resolved);
        }
        return models;
    }

    /// <summary>
    /// Resolved alias ids first, then every other published Claude model. A dated id is
    /// dropped when its undated twin is listed, since both name the same model.
    /// </summary>
    public static IReadOnlyList<string> MergeCatalog(IReadOnlyList<string> resolvedAliases, IReadOnlyList<string> published)
    {
        var all = resolvedAliases.Concat(published.Where(id => id.StartsWith("claude-", StringComparison.Ordinal)))
            .Distinct(StringComparer.Ordinal).ToList();
        var set = new HashSet<string>(all, StringComparer.Ordinal);
        return all.Where(id => !(DatedSuffix.IsMatch(id) && set.Contains(DatedSuffix.Replace(id, string.Empty))))
            .ToArray();
    }

    private static readonly System.Text.RegularExpressions.Regex DatedSuffix =
        new("-\\d{8}$", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    public static bool IsAuthenticatedStatus(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.ValueKind == JsonValueKind.Object &&
                document.RootElement.TryGetProperty("loggedIn", out var loggedIn) &&
                loggedIn.ValueKind == JsonValueKind.True;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public async Task<ClaudeCodeGeneration> RunAsync(
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
            throw new ClaudeCodeGatewayException("Claude Code account profiles accept OpenAI Chat Completions or Anthropic Messages requests only.");

        var prompt = ParsePrompt(requestJson, clientProtocol);
        // A live process is reused only for the same CLI, model and system prompt, and
        // only when the request repeats the history that process has already seen.
        cancellationToken.ThrowIfCancellationRequested();
        var key = executable + "\0" + model + "\0" + prompt.System;
        CliConversation conversation;
        string input;
        if (_conversations.Take(key, prompt.Turns) is { } continued)
        {
            conversation = continued.Conversation;
            input = string.Join("\n\n", continued.NewTurns.Select(turn => turn.Text));
        }
        else
        {
            var arguments = new List<string>
            {
                // No --bare: it reads only ANTHROPIC_API_KEY and would ignore the subscription login.
                // Project-only settings in an empty temp folder keep the user's hooks and
                // plugins out of relay requests; --strict-mcp-config does the same for MCP.
                // stream-json input keeps the process alive for the conversation's next turn.
                "-p", "--setting-sources", "project", "--strict-mcp-config",
                "--input-format", "stream-json", "--output-format", "stream-json", "--verbose", "--include-partial-messages",
                "--model", model, "--no-session-persistence", "--permission-mode", "dontAsk",
                "--permission-prompts", "none", "--disallowedTools", "*"
            };
            if (!string.IsNullOrWhiteSpace(prompt.System))
            {
                arguments.Add("--append-system-prompt");
                arguments.Add(prompt.System);
            }
            try
            {
                conversation = _conversations.Start(key, executable, arguments, "local-cloud-relay-claude-");
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or IOException)
            {
                throw new ClaudeCodeGatewayException("The Claude Code CLI could not be started. Check that `claude` is installed.", HttpStatusCode.BadGateway);
            }
            input = prompt.Text;
        }

        var keep = false;
        try
        {
            var text = new StringBuilder();
            var messageId = "msg_" + Guid.NewGuid().ToString("N");
            var completionId = "chatcmpl-" + Guid.NewGuid().ToString("N");
            var created = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var streamStarted = false;
            var errorResult = false;
            var fallbackText = (string?)null;
            RelayUsageSnapshot? usage = null;
            using var timeout = new CancellationTokenSource(_requestTimeout, _timeProvider);
            using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);

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
                        await onStreamEvent(AnthropicEvent("message_start", new
                        {
                            type = "message_start",
                            message = new { id = messageId, type = "message", role = "assistant", model, content = Array.Empty<object>(), stop_reason = (string?)null, stop_sequence = (string?)null, usage = new { input_tokens = 0, output_tokens = 0 } }
                        }));
                        await onStreamEvent(AnthropicEvent("content_block_start", new { type = "content_block_start", index = 0, content_block = new { type = "text", text = "" } }));
                    }
                    else
                    {
                        await onStreamEvent(OpenAiChunk(completionId, created, model, "", first: true, finish: null));
                    }
                }

                if (clientProtocol == WireProtocol.Anthropic)
                    await onStreamEvent(AnthropicEvent("content_block_delta", new { type = "content_block_delta", index = 0, delta = new { type = "text_delta", text = chunk } }));
                else
                    await onStreamEvent(OpenAiChunk(completionId, created, model, chunk, first: false, finish: null));
            }

            var userLine = JsonSerializer.Serialize(new { type = "user", message = new { role = "user", content = input } });
            bool finished;
            try
            {
                finished = await conversation.RunTurnAsync(userLine, async line =>
                {
                    try
                    {
                        using var document = JsonDocument.Parse(line);
                        var root = document.RootElement;
                        var type = GetString(root, "type");
                        if (type == "stream_event" && root.TryGetProperty("event", out var evt) &&
                            GetString(evt, "type") == "content_block_delta" && evt.TryGetProperty("delta", out var delta) &&
                            GetString(delta, "type") == "text_delta")
                        {
                            await EmitTextAsync(GetString(delta, "text") ?? string.Empty);
                        }
                        else if (type == "result")
                        {
                            // One result event closes each turn; the process stays up for the next.
                            errorResult = root.TryGetProperty("is_error", out var isError) && isError.ValueKind == JsonValueKind.True;
                            fallbackText = GetString(root, "result");
                            // The result event carries the turn's Anthropic usage, cache reads included.
                            usage = RelayUsageParser.Parse(line);
                            return true;
                        }
                    }
                    catch (JsonException)
                    {
                        // The CLI's documented stream-json events are JSON lines; ignore
                        // non-event diagnostics rather than exposing them to the client.
                    }
                    return false;
                }, linkedCancellation.Token);
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                throw new ClaudeCodeGatewayException(
                    $"Claude Code did not finish within {_requestTimeout.TotalMinutes:0.##} minutes.",
                    HttpStatusCode.GatewayTimeout);
            }
            catch (IOException)
            {
                // The process exited before reading the message.
                finished = false;
            }

            if ((!finished || errorResult) && RateLimitSignal.LooksLikeLimit(fallbackText))
                throw new ClaudeCodeGatewayException("Claude reports that this account has reached a usage or rate limit.", HttpStatusCode.TooManyRequests);
            if ((!finished || errorResult) && RateLimitSignal.LooksSignedOut(fallbackText))
                // The CLI's own text is not echoed: it can carry account details.
                throw new ClaudeCodeGatewayException("Claude Code is signed out or its sign-in expired. Select the provider and press Edit to sign in again.", HttpStatusCode.Unauthorized);
            if (!finished || errorResult)
                throw new ClaudeCodeGatewayException("Claude Code could not complete the account request. Check `claude auth status` and the CLI output, then retry.", HttpStatusCode.BadGateway);
            if (text.Length == 0 && !string.IsNullOrEmpty(fallbackText))
                await EmitTextAsync(fallbackText);

            if (stream)
            {
                if (clientProtocol == WireProtocol.Anthropic)
                {
                    if (!streamStarted)
                    {
                        await onStreamEvent(AnthropicEvent("message_start", new
                        {
                            type = "message_start",
                            message = new { id = messageId, type = "message", role = "assistant", model, content = Array.Empty<object>(), stop_reason = (string?)null, stop_sequence = (string?)null, usage = new { input_tokens = 0, output_tokens = 0 } }
                        }));
                        await onStreamEvent(AnthropicEvent("content_block_start", new { type = "content_block_start", index = 0, content_block = new { type = "text", text = "" } }));
                    }
                    await onStreamEvent(AnthropicEvent("content_block_stop", new { type = "content_block_stop", index = 0 }));
                    await onStreamEvent(AnthropicEvent("message_delta", new { type = "message_delta", delta = new { stop_reason = "end_turn", stop_sequence = (string?)null }, usage = CliUsage.Anthropic(usage) }));
                    await onStreamEvent(AnthropicEvent("message_stop", new { type = "message_stop" }));
                }
                else
                {
                    await onStreamEvent(OpenAiChunk(completionId, created, model, "", first: false, finish: "stop"));
                    if (usage is not null) await onStreamEvent(CliUsage.OpenAiUsageChunk(completionId, created, model, usage));
                    await onStreamEvent("data: [DONE]\n\n");
                }
            }
            conversation.History = [.. prompt.Turns, new CliTurn("assistant", text.ToString())];
            keep = true;
            return new ClaudeCodeGeneration(text.ToString(), model, usage);
        }
        finally
        {
            // A clean turn leaves the process ready for the conversation's next message;
            // anything else (error, timeout, client gone mid-turn) stops it.
            if (keep) _conversations.Return(conversation);
            else conversation.Dispose();
        }
    }

    /// <summary>Stops every live Claude Code process.</summary>
    public void Dispose() => _conversations.Dispose();

    public static string BufferedResponse(ClaudeCodeGeneration result, WireProtocol protocol)
    {
        if (protocol == WireProtocol.Anthropic)
            return JsonSerializer.Serialize(new
            {
                id = "msg_" + Guid.NewGuid().ToString("N"), type = "message", role = "assistant", model = result.Model,
                content = new[] { new { type = "text", text = result.Text } }, stop_reason = "end_turn", stop_sequence = (string?)null,
                usage = CliUsage.Anthropic(result.Usage)
            });
        return JsonSerializer.Serialize(new
        {
            id = "chatcmpl-" + Guid.NewGuid().ToString("N"), @object = "chat.completion", created = DateTimeOffset.UtcNow.ToUnixTimeSeconds(), model = result.Model,
            choices = new[] { new { index = 0, message = new { role = "assistant", content = result.Text }, finish_reason = "stop" } },
            usage = CliUsage.OpenAi(result.Usage)
        });
    }

    private sealed record Prompt(string Text, string? System, IReadOnlyList<CliTurn> Turns);

    private static Prompt ParsePrompt(string json, WireProtocol protocol)
    {
        JsonDocument document;
        try { document = JsonDocument.Parse(json); }
        catch (JsonException) { throw new ClaudeCodeGatewayException("Claude Code requires a valid JSON messages request."); }
        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("messages", out var messages) || messages.ValueKind != JsonValueKind.Array)
                throw new ClaudeCodeGatewayException("Claude Code requires a messages array.");
            foreach (var field in protocol == WireProtocol.OpenAi ? new[] { "tools", "tool_choice" } : new[] { "tools", "tool_choice" })
                if (root.TryGetProperty(field, out var value) && value.ValueKind is not (JsonValueKind.Null or JsonValueKind.False) &&
                    (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() != 0))
                    throw new ClaudeCodeGatewayException("Claude Code account profiles do not accept client tool definitions or tool choices.");

            var system = protocol == WireProtocol.Anthropic && root.TryGetProperty("system", out var systemNode)
                ? ReadText(systemNode)
                : null;
            var transcript = new StringBuilder();
            var turns = new List<CliTurn>();
            foreach (var message in messages.EnumerateArray())
            {
                if (message.ValueKind != JsonValueKind.Object)
                    throw new ClaudeCodeGatewayException("Claude Code messages must be objects with a role and text content.");
                var role = GetString(message, "role")?.ToLowerInvariant();
                if (role is "system" or "developer")
                {
                    var text = ReadText(message.TryGetProperty("content", out var sysContent) ? sysContent : default);
                    system = string.IsNullOrWhiteSpace(system) ? text : system + "\n\n" + text;
                    continue;
                }
                if (role is not ("user" or "assistant"))
                    throw new ClaudeCodeGatewayException("Claude Code account profiles accept only user and assistant message roles; tool results are unsupported.");
                var content = ReadText(message.TryGetProperty("content", out var node) ? node : default);
                turns.Add(new CliTurn(role, content));
                if (transcript.Length > 0) transcript.AppendLine().AppendLine();
                transcript.Append(role == "user" ? "User: " : "Assistant: ").Append(content);
            }
            if (transcript.Length == 0)
                throw new ClaudeCodeGatewayException("Claude Code requires at least one user or assistant text message.");
            return new Prompt(transcript.ToString(), system, turns);
        }
    }

    private static string ReadText(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.String) return value.GetString() ?? string.Empty;
        if (value.ValueKind == JsonValueKind.Array)
        {
            var parts = new List<string>();
            foreach (var part in value.EnumerateArray())
            {
                if (part.ValueKind != JsonValueKind.Object || GetString(part, "type") != "text" || !part.TryGetProperty("text", out var text) || text.ValueKind != JsonValueKind.String)
                    throw new ClaudeCodeGatewayException("Claude Code account profiles support text-only messages; images and other content blocks are unsupported.");
                parts.Add(text.GetString() ?? string.Empty);
            }
            return string.Join("\n", parts);
        }
        throw new ClaudeCodeGatewayException("Claude Code account profiles support text-only messages; images and other content blocks are unsupported.");
    }

    private static string? GetString(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string AnthropicEvent(string name, object data) =>
        $"event: {name}\ndata: {JsonSerializer.Serialize(data)}\n\n";

    private static string OpenAiChunk(string id, long created, string model, string text, bool first, string? finish) =>
        "data: " + JsonSerializer.Serialize(new
        {
            id, @object = "chat.completion.chunk", created, model,
            choices = new[] { new { index = 0, delta = first ? new { role = (string?)"assistant", content = text } : new { role = (string?)null, content = text }, finish_reason = finish } }
        }) + "\n\n";
}
