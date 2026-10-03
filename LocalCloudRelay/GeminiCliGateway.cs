using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace LocalCloudRelay;

public sealed class GeminiCliGatewayException(string message, HttpStatusCode statusCode = HttpStatusCode.BadRequest) : Exception(message)
{
    public HttpStatusCode StatusCode { get; } = statusCode;
}

/// <summary>A finished request. Usage comes from the CLI's result stats when it reports them.</summary>
public sealed record GeminiCliGeneration(string Text, string Model, RelayUsageSnapshot? Usage = null);

/// <summary>
/// Uses the official Gemini CLI and its "Login with Google" account. The relay never reads
/// the CLI's credentials; it only checks that a login exists and runs the CLI headless.
/// </summary>
public sealed class GeminiCliGateway
{
    public static readonly TimeSpan DefaultRequestTimeout = TimeSpan.FromMinutes(5);

    /// <summary>
    /// The CLI has no model-list command. These are the models it names itself, offered as
    /// a starting list that the user can edit.
    /// </summary>
    public static readonly IReadOnlyList<string> DefaultModels =
        ["gemini-2.5-pro", "gemini-2.5-flash", "gemini-3-pro-preview", "gemini-3-flash-preview", "gemini-3.1-flash-lite"];

    private const string LoginWithGoogle = "oauth-personal";
    private static readonly Regex ModelId = new("^[a-z0-9][a-z0-9._-]*$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private readonly ProviderCliRunner _cliRunner;
    private readonly TimeSpan _requestTimeout;
    private readonly TimeProvider _timeProvider;
    private readonly string _geminiHome;

    public GeminiCliGateway(ProviderCliRunner cliRunner, TimeSpan? requestTimeout = null,
        TimeProvider? timeProvider = null, string? geminiHome = null)
    {
        _cliRunner = cliRunner ?? throw new ArgumentNullException(nameof(cliRunner));
        _requestTimeout = requestTimeout ?? DefaultRequestTimeout;
        if (_requestTimeout <= TimeSpan.Zero && _requestTimeout != Timeout.InfiniteTimeSpan)
            throw new ArgumentOutOfRangeException(nameof(requestTimeout));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _geminiHome = geminiHome ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".gemini");
    }

    /// <summary>
    /// True when the CLI is set to "Login with Google" and holds a login. Only the auth
    /// type in settings.json is read; the credential file is checked for existence only.
    /// </summary>
    public bool IsSignedIn()
    {
        if (!File.Exists(Path.Combine(_geminiHome, "oauth_creds.json"))) return false;
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(_geminiHome, "settings.json")));
            var root = document.RootElement;
            var selected = root.TryGetProperty("security", out var security) &&
                           security.TryGetProperty("auth", out var auth) &&
                           auth.TryGetProperty("selectedType", out var type) && type.ValueKind == JsonValueKind.String
                ? type.GetString()
                : root.TryGetProperty("selectedAuthType", out var legacy) && legacy.ValueKind == JsonValueKind.String
                    ? legacy.GetString()
                    : null;
            return selected == LoginWithGoogle;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// npm installs the CLI as a .cmd shim, which Process.Start cannot run without a shell.
    /// Running its script with node directly keeps every argument out of cmd.exe parsing.
    /// </summary>
    public static (string FileName, IReadOnlyList<string> Prefix) ResolveCommand(string executable)
    {
        if (executable.EndsWith(".js", StringComparison.OrdinalIgnoreCase)) return ("node", [executable]);
        if (Path.IsPathRooted(executable) && executable.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return (executable, []);
        var name = Path.GetFileNameWithoutExtension(executable);
        var directories = Path.IsPathRooted(executable)
            ? [Path.GetDirectoryName(executable)!]
            : (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
        foreach (var directory in directories)
        {
            var script = Path.Combine(directory, "node_modules", "@google", "gemini-cli", "bundle", "gemini.js");
            if (File.Exists(Path.Combine(directory, name + ".cmd")) && File.Exists(script)) return ("node", [script]);
            if (File.Exists(Path.Combine(directory, name + ".exe"))) return (Path.Combine(directory, name + ".exe"), []);
        }
        throw new GeminiCliGatewayException("The Gemini CLI was not found. Install it with `npm install -g @google/gemini-cli`.", HttpStatusCode.BadGateway);
    }

    public async Task<GeminiCliGeneration> RunAsync(
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
            throw new GeminiCliGatewayException("Gemini account profiles accept OpenAI Chat Completions or Anthropic Messages requests only.");
        if (!ModelId.IsMatch(model))
            throw new GeminiCliGatewayException("The Gemini model id is not valid.");

        var prompt = AntigravityGateway.ParsePrompt(requestJson, clientProtocol, "Gemini",
            message => new GeminiCliGatewayException(message));
        var (fileName, prefix) = ResolveCommand(executable);
        var workingDirectory = Path.Combine(Path.GetTempPath(), "local-cloud-relay-gemini-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workingDirectory);
        try
        {
            // The transcript goes in on stdin; -p is appended to it and switches on headless
            // mode. Plan approval mode is the CLI's read-only mode, so no tool can change files.
            var arguments = new List<string>(prefix)
            {
                "-p", "Answer the conversation above as a text assistant. Do not use tools.",
                "-o", "stream-json", "-m", model, "--approval-mode", "plan", "--skip-trust"
            };
            var text = new StringBuilder();
            var status = (string?)null;
            RelayUsageSnapshot? usage = null;
            var errorMessage = (string?)null;
            var id = "chatcmpl-" + Guid.NewGuid().ToString("N");
            var messageId = "msg_" + Guid.NewGuid().ToString("N");
            var created = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var streamStarted = false;
            using var timeout = new CancellationTokenSource(_requestTimeout, _timeProvider);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);

            async ValueTask StartAsync()
            {
                if (streamStarted) return;
                streamStarted = true;
                if (clientProtocol == WireProtocol.Anthropic)
                {
                    await onStreamEvent(AntigravityGateway.AnthropicEvent("message_start", new { type = "message_start", message = new { id = messageId, type = "message", role = "assistant", model, content = Array.Empty<object>(), stop_reason = (string?)null, stop_sequence = (string?)null, usage = new { input_tokens = 0, output_tokens = 0 } } }));
                    await onStreamEvent(AntigravityGateway.AnthropicEvent("content_block_start", new { type = "content_block_start", index = 0, content_block = new { type = "text", text = "" } }));
                }
                else await onStreamEvent(AntigravityGateway.OpenAiChunk(id, created, model, "", first: true, finish: null));
            }

            async ValueTask EmitTextAsync(string chunk)
            {
                if (chunk.Length == 0) return;
                text.Append(chunk);
                if (!stream) return;
                await StartAsync();
                if (clientProtocol == WireProtocol.Anthropic)
                    await onStreamEvent(AntigravityGateway.AnthropicEvent("content_block_delta", new { type = "content_block_delta", index = 0, delta = new { type = "text_delta", text = chunk } }));
                else await onStreamEvent(AntigravityGateway.OpenAiChunk(id, created, model, chunk, first: false, finish: null));
            }

            ProviderCliResult run;
            try
            {
                run = await _cliRunner.RunAsync(fileName, arguments, prompt + "\n", async (line, _) =>
                {
                    try
                    {
                        using var document = JsonDocument.Parse(line);
                        var root = document.RootElement;
                        switch (GetString(root, "type"))
                        {
                            case "message" when GetString(root, "role") == "assistant":
                                await EmitTextAsync(GetString(root, "content") ?? string.Empty);
                                break;
                            case "result":
                                status = GetString(root, "status");
                                if (root.TryGetProperty("error", out var error)) errorMessage = GetString(error, "message");
                                usage = UsageFromStats(root);
                                break;
                        }
                    }
                    catch (JsonException)
                    {
                        // Startup notices are plain text; only JSON event lines matter.
                    }
                }, workingDirectory, linked.Token);
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                throw new GeminiCliGatewayException($"Gemini did not finish within {_requestTimeout.TotalMinutes:0.##} minutes.", HttpStatusCode.GatewayTimeout);
            }

            if ((!run.Succeeded || status != "success") && RateLimitSignal.LooksLikeLimit(errorMessage))
                throw new GeminiCliGatewayException($"Gemini reports a usage or rate limit: {errorMessage}", HttpStatusCode.TooManyRequests);
            if ((!run.Succeeded || status != "success") && RateLimitSignal.LooksSignedOut(errorMessage))
                throw new GeminiCliGatewayException("The Gemini CLI is signed out or its sign-in expired. Select the provider and press Edit to sign in again.", HttpStatusCode.Unauthorized);
            if (!run.Succeeded || status != "success")
            {
                // The CLI's own message names the cause (quota, model, login) without secrets.
                var detail = string.IsNullOrWhiteSpace(errorMessage) ? "Check the model id and sign in again." : errorMessage;
                throw new GeminiCliGatewayException($"The Gemini CLI request failed: {detail}", HttpStatusCode.BadGateway);
            }

            if (stream)
            {
                await StartAsync();
                if (clientProtocol == WireProtocol.Anthropic)
                {
                    await onStreamEvent(AntigravityGateway.AnthropicEvent("content_block_stop", new { type = "content_block_stop", index = 0 }));
                    await onStreamEvent(AntigravityGateway.AnthropicEvent("message_delta", new { type = "message_delta", delta = new { stop_reason = "end_turn", stop_sequence = (string?)null }, usage = CliUsage.Anthropic(usage) }));
                    await onStreamEvent(AntigravityGateway.AnthropicEvent("message_stop", new { type = "message_stop" }));
                }
                else
                {
                    await onStreamEvent(AntigravityGateway.OpenAiChunk(id, created, model, "", first: false, finish: "stop"));
                    if (usage is not null) await onStreamEvent(CliUsage.OpenAiUsageChunk(id, created, model, usage));
                    await onStreamEvent("data: [DONE]\n\n");
                }
            }
            return new GeminiCliGeneration(text.ToString(), model, usage);
        }
        finally
        {
            try { Directory.Delete(workingDirectory, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    public static string BufferedResponse(GeminiCliGeneration result, WireProtocol protocol) =>
        AntigravityGateway.BufferedResponse(new AntigravityGeneration(result.Text, result.Model, result.Usage), protocol);

    /// <summary>
    /// Token counts from the result event's stats. input_tokens is the whole prompt with
    /// cached tokens inside it; input, when present, is the uncached part. Input is stored
    /// as the uncached part, as everywhere else in telemetry.
    /// </summary>
    internal static RelayUsageSnapshot? UsageFromStats(JsonElement result)
    {
        if (!result.TryGetProperty("stats", out var stats) || stats.ValueKind != JsonValueKind.Object) return null;
        long? Long(string name) => stats.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n : null;
        var prompt = Long("input_tokens");
        var output = Long("output_tokens");
        var cached = Long("cached");
        var uncached = Long("input") ?? (prompt is { } p ? Math.Max(0, p - (cached ?? 0)) : null);
        if (uncached is null && output is null) return null;
        return new RelayUsageSnapshot(uncached, output, Long("total_tokens"), cached);
    }

    private static string? GetString(JsonElement element, string name) => element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
