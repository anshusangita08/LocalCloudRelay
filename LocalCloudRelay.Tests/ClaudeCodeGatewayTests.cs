using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using LocalCloudRelay;

namespace LocalCloudRelay.Tests;

public sealed class ClaudeCodeGatewayTests
{
    [Theory]
    [InlineData("{\"loggedIn\":true,\"authMethod\":\"claude.ai\"}", true)]
    [InlineData("{\"loggedIn\":false}", false)]
    [InlineData("{}", false)]
    [InlineData("not json", false)]
    public void AuthStatusRequiresExplicitLoggedInTrue(string json, bool expected)
    {
        Assert.Equal(expected, ClaudeCodeGateway.IsAuthenticatedStatus(json));
    }

    [Fact]
    public async Task CheckAuthenticationAsyncInvokesOfficialCliStatusCommand()
    {
        var factory = new FakeProcessFactory(new FakeProcess("{\"loggedIn\":true}\n", 0));
        var gateway = new ClaudeCodeGateway(new ProviderCliRunner(factory));

        var authenticated = await gateway.CheckAuthenticationAsync("claude");

        Assert.True(authenticated);
        Assert.Equal("claude", factory.StartInfo!.FileName);
        Assert.Equal(2, factory.StartInfo.ArgumentList.Count);
        Assert.Equal("auth", factory.StartInfo.ArgumentList[0]);
        Assert.Equal("status", factory.StartInfo.ArgumentList[1]);
    }

    [Fact]
    public async Task AUsageLimitResultIsReportedAs429SoTheRouterRestsTheAccount()
    {
        var gateway = new ClaudeCodeGateway(new ProviderCliRunner(new FakeProcessFactory(new FakeProcess(
            "{\"type\":\"result\",\"is_error\":true,\"result\":\"Claude AI usage limit reached|1759500000\"}\n", 1))));

        var error = await Assert.ThrowsAsync<ClaudeCodeGatewayException>(() => gateway.RunAsync("claude", "sonnet",
            WireProtocol.OpenAi, """{"messages":[{"role":"user","content":"x"}]}""", false, _ => ValueTask.CompletedTask));

        Assert.Equal(System.Net.HttpStatusCode.TooManyRequests, error.StatusCode);
    }

    [Fact]
    public async Task AnExpiredSignInIsReportedAs401WithTheCliReason()
    {
        // The text the signed-out CLI really prints; it used to surface as a bare 502.
        var gateway = new ClaudeCodeGateway(new ProviderCliRunner(new FakeProcessFactory(new FakeProcess(
            "{\"type\":\"result\",\"subtype\":\"success\",\"is_error\":true,\"result\":\"Failed to authenticate: OAuth session expired and could not be refreshed\"}\n", 1))));

        var error = await Assert.ThrowsAsync<ClaudeCodeGatewayException>(() => gateway.RunAsync("claude", "sonnet",
            WireProtocol.OpenAi, """{"messages":[{"role":"user","content":"x"}]}""", false, _ => ValueTask.CompletedTask));

        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, error.StatusCode);
        Assert.Contains("signed out", error.Message);
        Assert.Contains("Edit", error.Message);
    }

    [Theory]
    [InlineData("Failed to authenticate: OAuth session expired and could not be refreshed", true)]
    [InlineData("Not logged in · Please run /login", true)]
    [InlineData("Invalid API key", true)]
    [InlineData("Claude AI usage limit reached", false)]
    [InlineData("model not found", false)]
    public void SignedOutTextIsRecognised(string text, bool signedOut) =>
        Assert.Equal(signedOut, RateLimitSignal.LooksSignedOut(text));

    [Fact]
    public async Task ResolveModelsAsyncReadsExactIdsFromInitEvents()
    {
        var gateway = new ClaudeCodeGateway(new ProviderCliRunner(new FakeProcessFactory(new FakeProcess(
            "{\"type\":\"system\",\"subtype\":\"hook_started\"}\n{\"type\":\"system\",\"subtype\":\"init\",\"model\":\"claude-opus-5-5\"}\n", 0))));

        var models = await gateway.ResolveModelsAsync("claude");

        // The fake answers every alias with the same model; duplicates collapse to one.
        Assert.Equal(["claude-opus-5-5"], models);
    }

    [Fact]
    public async Task CheckAuthenticationAsyncReadsPrettyPrintedStatus()
    {
        // The real CLI prints indented JSON across several lines.
        var gateway = new ClaudeCodeGateway(new ProviderCliRunner(new FakeProcessFactory(
            new FakeProcess("{\n  \"loggedIn\": true,\n  \"authMethod\": \"claude.ai\"\n}\n", 0))));

        Assert.True(await gateway.CheckAuthenticationAsync("claude"));
    }

    [Fact]
    public async Task CheckAuthenticationAsyncReturnsFalseWhenTheCliReportsFailure()
    {
        var gateway = new ClaudeCodeGateway(new ProviderCliRunner(new FakeProcessFactory(new FakeProcess("{\"loggedIn\":true}\n", 1))));

        Assert.False(await gateway.CheckAuthenticationAsync("claude"));
    }

    [Fact]
    public async Task RunAsyncUsesCliAccountAndConvertsBufferedChatResponse()
    {
        var process = new FakeProcess(
            "{\"type\":\"stream_event\",\"event\":{\"type\":\"content_block_delta\",\"delta\":{\"type\":\"text_delta\",\"text\":\"Hello\"}}}\n" +
            "{\"type\":\"result\",\"result\":\"Hello\",\"is_error\":false}\n", 0);
        var factory = new FakeProcessFactory(process);
        var gateway = new ClaudeCodeGateway(new ProviderCliRunner(factory));

        var result = await gateway.RunAsync("claude", "sonnet", WireProtocol.OpenAi,
            """{"messages":[{"role":"user","content":"Say hello"}]}""", stream: false, _ => ValueTask.CompletedTask);

        Assert.Equal("Hello", result.Text);
        Assert.Equal("sonnet", result.Model);
        Assert.Contains("Say hello", process.Input.ToString());
        Assert.Equal("claude", factory.StartInfo!.FileName);
        Assert.Equal("-p", factory.StartInfo.ArgumentList[0]);
        Assert.Contains("--output-format", factory.StartInfo.ArgumentList);
        Assert.Contains("stream-json", factory.StartInfo.ArgumentList);
        Assert.Contains("--no-session-persistence", factory.StartInfo.ArgumentList);
        Assert.Contains("--disallowedTools", factory.StartInfo.ArgumentList);
        Assert.Contains("*", factory.StartInfo.ArgumentList);
        // --bare reads only ANTHROPIC_API_KEY, which would lock out a subscription login.
        Assert.DoesNotContain("--bare", factory.StartInfo.ArgumentList);
        Assert.Contains("--strict-mcp-config", factory.StartInfo.ArgumentList);
        Assert.Contains("--setting-sources", factory.StartInfo.ArgumentList);
        Assert.Contains("--model", factory.StartInfo.ArgumentList);
        Assert.Contains("sonnet", factory.StartInfo.ArgumentList);
        Assert.False(Directory.Exists(factory.StartInfo.WorkingDirectory));
    }

    [Fact]
    public async Task RunAsyncReportsUsageFromTheResultEvent()
    {
        // The result event carries the turn's Anthropic usage; without it the Session view
        // showed no tokens and no cache hits for Claude Code account traffic.
        var gateway = CreateGateway(
            "{\"type\":\"stream_event\",\"event\":{\"type\":\"content_block_delta\",\"delta\":{\"type\":\"text_delta\",\"text\":\"Hi\"}}}\n" +
            "{\"type\":\"result\",\"result\":\"Hi\",\"is_error\":false,\"total_cost_usd\":0.01," +
            "\"usage\":{\"input_tokens\":12,\"cache_read_input_tokens\":3400,\"cache_creation_input_tokens\":150,\"output_tokens\":40}}\n");

        var result = await gateway.RunAsync("claude", "sonnet", WireProtocol.Anthropic,
            """{"messages":[{"role":"user","content":"Hi"}]}""", stream: false, _ => ValueTask.CompletedTask);

        Assert.NotNull(result.Usage);
        Assert.Equal(12, result.Usage!.InputTokens);
        Assert.Equal(40, result.Usage.OutputTokens);
        Assert.Equal(3400, result.Usage.CacheReadInputTokens);
        Assert.Equal(150, result.Usage.CacheCreationInputTokens);
    }

    private const string WithUsage =
        "{\"type\":\"stream_event\",\"event\":{\"type\":\"content_block_delta\",\"delta\":{\"type\":\"text_delta\",\"text\":\"Hi\"}}}\n" +
        "{\"type\":\"result\",\"result\":\"Hi\",\"is_error\":false," +
        "\"usage\":{\"input_tokens\":12,\"cache_read_input_tokens\":3400,\"cache_creation_input_tokens\":150,\"output_tokens\":40}}\n";

    [Fact]
    public async Task TheClientGetsTheRealUsageNotZeros()
    {
        // Claude Code tracks context fill from these numbers; zeros meant it never compacted.
        var anthropic = new StringBuilder();
        await CreateGateway(WithUsage).RunAsync("claude", "sonnet", WireProtocol.Anthropic,
            """{"messages":[{"role":"user","content":"Hi"}]}""", stream: true, line => { anthropic.Append(line); return ValueTask.CompletedTask; });
        var delta = anthropic.ToString().Split("\n\n").Single(e => e.Contains("message_delta"));
        Assert.Contains("\"input_tokens\":12", delta);
        Assert.Contains("\"cache_read_input_tokens\":3400", delta);
        Assert.Contains("\"cache_creation_input_tokens\":150", delta);
        Assert.Contains("\"output_tokens\":40", delta);

        var openAi = new StringBuilder();
        await CreateGateway(WithUsage).RunAsync("claude", "sonnet", WireProtocol.OpenAi,
            """{"messages":[{"role":"user","content":"Hi"}]}""", stream: true, line => { openAi.Append(line); return ValueTask.CompletedTask; });
        var events = openAi.ToString().Split("\n\n", StringSplitOptions.RemoveEmptyEntries);
        var usageChunk = events[^2];                           // just before [DONE]
        Assert.Contains("\"prompt_tokens\":3562", usageChunk);  // 12 + 3400 + 150
        Assert.Contains("\"cached_tokens\":3400", usageChunk);
        Assert.Contains("\"completion_tokens\":40", usageChunk);
        Assert.Equal("data: [DONE]", events[^1]);

        var buffered = await CreateGateway(WithUsage).RunAsync("claude", "sonnet", WireProtocol.Anthropic,
            """{"messages":[{"role":"user","content":"Hi"}]}""", stream: false, _ => ValueTask.CompletedTask);
        Assert.Contains("\"input_tokens\":12", ClaudeCodeGateway.BufferedResponse(buffered, WireProtocol.Anthropic));
        Assert.Contains("\"prompt_tokens\":3562", ClaudeCodeGateway.BufferedResponse(buffered, WireProtocol.OpenAi));
    }

    [Fact]
    public async Task RunAsyncEmitsAnthropicAndOpenAiChunksForTextDeltas()
    {
        const string output = "{\"type\":\"stream_event\",\"event\":{\"type\":\"content_block_delta\",\"delta\":{\"type\":\"text_delta\",\"text\":\"Hi\"}}}\n" +
            "{\"type\":\"result\",\"result\":\"Hi\",\"is_error\":false}\n";
        var anthropicEvents = new List<string>();
        await CreateGateway(output).RunAsync("claude", "sonnet", WireProtocol.Anthropic,
            """{"messages":[{"role":"user","content":"Hi"}]}""", stream: true, line => { anthropicEvents.Add(line); return ValueTask.CompletedTask; });
        var openAiEvents = new List<string>();
        await CreateGateway(output).RunAsync("claude", "sonnet", WireProtocol.OpenAi,
            """{"messages":[{"role":"user","content":"Hi"}]}""", stream: true, line => { openAiEvents.Add(line); return ValueTask.CompletedTask; });

        Assert.Contains("event: content_block_delta", string.Concat(anthropicEvents));
        Assert.Contains("Hi", string.Concat(anthropicEvents));
        Assert.Contains("data: [DONE]", string.Concat(openAiEvents));
        Assert.Contains("chat.completion.chunk", string.Concat(openAiEvents));
    }

    [Fact]
    public async Task RunAsyncRejectsToolsAndMultimodalMessagesClearly()
    {
        var gateway = CreateGateway("");

        var tools = await Assert.ThrowsAsync<ClaudeCodeGatewayException>(() => gateway.RunAsync("claude", "sonnet", WireProtocol.OpenAi,
            """{"messages":[{"role":"user","content":"hi"}],"tools":[{"type":"function"}]}""", false, _ => ValueTask.CompletedTask));
        var image = await Assert.ThrowsAsync<ClaudeCodeGatewayException>(() => gateway.RunAsync("claude", "sonnet", WireProtocol.Anthropic,
            """{"messages":[{"role":"user","content":[{"type":"image","source":{"type":"url","url":"https://example.com/i.png"}}]}]}""", false, _ => ValueTask.CompletedTask));

        Assert.Contains("tool", tools.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("text-only", image.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RunAsyncReturnsGatewayFailureWhenTheCliExitsUnsuccessfully()
    {
        var gateway = CreateGateway("{\"type\":\"result\",\"is_error\":true,\"result\":\"private details\"}\n", exitCode: 1);

        var exception = await Assert.ThrowsAsync<ClaudeCodeGatewayException>(() => gateway.RunAsync("claude", "sonnet", WireProtocol.OpenAi,
            """{"messages":[{"role":"user","content":"Hi"}]}""", false, _ => ValueTask.CompletedTask));

        Assert.Equal(HttpStatusCode.BadGateway, exception.StatusCode);
        Assert.DoesNotContain("private details", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsyncHonorsCancellationBeforeStartingTheCli()
    {
        var factory = new FakeProcessFactory(new FakeProcess("", 0));
        var gateway = new ClaudeCodeGateway(new ProviderCliRunner(factory));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => gateway.RunAsync("claude", "sonnet", WireProtocol.OpenAi,
            """{"messages":[{"role":"user","content":"Hi"}]}""", false, _ => ValueTask.CompletedTask, cancellation.Token));

        Assert.Null(factory.StartInfo);
    }

    [Fact]
    public async Task RunAsyncTimeoutKillsHungCliProcessTreeAndReturnsGatewayTimeout()
    {
        var timeProvider = new ManualTimeProvider();
        var process = new FakeProcess("", 0, waitForExit: true);
        var gateway = new ClaudeCodeGateway(new ProviderCliRunner(new FakeProcessFactory(process)),
            TimeSpan.FromMinutes(5), timeProvider);
        var run = gateway.RunAsync("claude", "sonnet", WireProtocol.OpenAi,
            """{"messages":[{"role":"user","content":"Hi"}]}""", false, _ => ValueTask.CompletedTask);
        await process.WaitStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        timeProvider.FireTimer();

        var exception = await Assert.ThrowsAsync<ClaudeCodeGatewayException>(() => run);
        Assert.Equal(HttpStatusCode.GatewayTimeout, exception.StatusCode);
        Assert.True(process.KilledEntireTree);
    }

    [Fact]
    public async Task RelayServerRoutesClaudeCodeAccountRequestsThroughTheCli()
    {
        const string localKey = "claude-relay-test-key";
        var telemetry = new RelayTelemetryStore();
        var output = "{\"type\":\"stream_event\",\"event\":{\"type\":\"content_block_delta\",\"delta\":{\"type\":\"text_delta\",\"text\":\"CLI reply\"}}}\n" +
            "{\"type\":\"result\",\"result\":\"CLI reply\",\"is_error\":false,\"usage\":{\"input_tokens\":10,\"cache_read_input_tokens\":90,\"output_tokens\":5}}\n";
        var process = new FakeProcess(output, 0, waitForExit: true);
        var factory = new FakeProcessFactory(process);
        using var server = new RelayServer(telemetry, port: 0, claudeCodeGateway: new ClaudeCodeGateway(new ProviderCliRunner(factory)),
            durationMilliseconds: _ => process.ExitObserved ? 4321 : 123);
        var provider = new ProviderSettings("claude-account", "Claude Code", ProviderKinds.ClaudeCode,
            "https://unused.invalid", null, true, 0, AuthMode: ProviderAuthMode.CliAccount,
            CliExecutable: "claude", ImportedModels: ["sonnet"]);
        server.Apply(localKey, new ProviderRouter([provider], new CatalogSnapshot(DateTimeOffset.UtcNow,
            new Dictionary<string, IReadOnlyList<string>> { [provider.Id] = ["sonnet"] })));
        await server.StartAsync();
        using var client = new HttpClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{server.Port}/v1/chat/completions")
        {
            Content = new StringContent("""{"model":"sonnet","messages":[{"role":"user","content":"Hi"}]}""", Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", localKey);

        // The reply arrives on the turn's result event while the CLI keeps running for the
        // conversation's next message.
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead).WaitAsync(TimeSpan.FromSeconds(10));
        var body = await response.Content.ReadAsStringAsync();
        Assert.False(process.KilledEntireTree);
        await server.StopAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("CLI reply", body);
        Assert.Contains(200, telemetry.GetReport().RecentRequests.Select(record => record.StatusCode));
        // CLI usage reaches telemetry, so Claude Code account traffic shows its cache hits.
        var recorded = telemetry.GetReport().RecentRequests.Single(record => record.StatusCode == 200);
        Assert.Equal(10, recorded.InputTokens);
        Assert.Equal(90, recorded.CacheReadInputTokens);
        Assert.Equal(0.9, telemetry.GetReport().CacheHitRate);
        // A subscription is not spend: nothing is charged per request.
        Assert.Equal("plan", recorded.CostSource);
        Assert.Equal(0m, recorded.ProviderCostUsd);
        Assert.Equal(0m, telemetry.GetReport().TotalConsumedCostUsd);
        Assert.Equal("123", response.Headers.GetValues("x-relay-duration-ms").Single());
    }

    private static ClaudeCodeGateway CreateGateway(string stdout, int exitCode = 0) =>
        new(new ProviderCliRunner(new FakeProcessFactory(new FakeProcess(stdout, exitCode))));

    private sealed class FakeProcessFactory(FakeProcess process) : IProviderCliProcessFactory
    {
        public ProcessStartInfo? StartInfo { get; private set; }
        public IProviderCliProcess Start(ProcessStartInfo startInfo) { StartInfo = startInfo; return process; }
    }

    private sealed class FakeProcess(string stdout, int exitCode, bool waitForExit = false) : IProviderCliProcess
    {
        private readonly StringReader _stdout = new(stdout);
        private TextReader? _live;
        private readonly StringReader _stderr = new("");
        private readonly TaskCompletionSource _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public StringWriter Input { get; } = new();
        public TextWriter StandardInput => Input;
        public TextReader StandardOutput => waitForExit ? _live ??= new LiveOutput(_stdout, _exit.Task, WaitStarted) : _stdout;
        public TextReader StandardError => _stderr;
        public int ExitCode => exitCode;
        public bool KilledEntireTree { get; private set; }
        public bool ExitObserved { get; private set; }
        public TaskCompletionSource WaitStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task WaitForExitAsync(CancellationToken cancellationToken)
        {
            WaitStarted.TrySetResult();
            if (waitForExit) await _exit.Task.WaitAsync(cancellationToken);
            ExitObserved = true;
        }
        public void Kill(bool entireProcessTree)
        {
            KilledEntireTree = entireProcessTree;
            _exit.TrySetResult();
        }
        public void CompleteExit() => _exit.TrySetResult();
        public void Dispose() { _stdout.Dispose(); _stderr.Dispose(); Input.Dispose(); }
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private ManualTimer? _timer;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            _timer = new ManualTimer(callback, state);
            _timer.Change(dueTime, period);
            return _timer;
        }

        public void FireTimer() => _timer?.Fire();

        private sealed class ManualTimer(TimerCallback callback, object? state) : ITimer
        {
            private bool _disposed;
            public bool Change(TimeSpan dueTime, TimeSpan period) => !_disposed;
            public void Fire()
            {
                if (!_disposed) callback(state);
            }
            public void Dispose() => _disposed = true;
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }

    /// <summary>
    /// Stdout of a CLI that stays running: its lines, then nothing until it exits or is
    /// killed. Reading it is what "started" means for a live process.
    /// </summary>
    private sealed class LiveOutput(TextReader lines, Task exited, TaskCompletionSource started) : TextReader
    {
        public override async ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken)
        {
            started.TrySetResult();
            if (await lines.ReadLineAsync(cancellationToken) is { } line) return line;
            await exited.WaitAsync(cancellationToken);
            return null;
        }

        public override Task<string?> ReadLineAsync() => ReadLineAsync(CancellationToken.None).AsTask();
    }
}
