using System.Diagnostics;
using System.Net;
using System.Text;
using System.Net.Http.Headers;
using LocalCloudRelay;

namespace LocalCloudRelay.Tests;

public sealed class AntigravityGatewayTests
{
    [Fact]
    public void ParseModelsExtractsOnlyRecognizableSlugsFromDocumentedPlainTextCatalog()
    {
        var models = AntigravityGateway.ParseModels("""
            gemini-3.8-flash-high     Gemini 3.8 Flash (High)
            gemini-3.1-pro-high       Gemini 3.1 Pro (High)
            claude-sonnet-4-6        Claude Sonnet 4.6 (Thinking)
            gpt-oss-120b-medium      GPT-OSS 120B (Medium)
            Antigravity-Model-3      Heading, not a model row
            Loading models...
            """);

        Assert.Equal(["gemini-3.8-flash-high", "gemini-3.1-pro-high", "claude-sonnet-4-6", "gpt-oss-120b-medium"], models);
    }

    [Theory]
    [InlineData(1, "")]
    [InlineData(0, "No models are available")]
    public async Task FetchModelsAsyncFailsWhenCatalogOutputIsEmptyOrUnrecognized(int exitCode, string output)
    {
        var gateway = CreateGateway(output, exitCode);

        var exception = await Assert.ThrowsAsync<AntigravityGatewayException>(() => gateway.FetchModelsAsync("agy"));

        Assert.Contains("catalog", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task FetchModelsAsyncRunsOfficialModelsCommandAndDoesNotClaimAuthentication()
    {
        var factory = new FakeProcessFactory(new FakeProcess("gemini-3.1-pro-high Gemini 3.1 Pro (High)\n", 0));
        var gateway = new AntigravityGateway(new ProviderCliRunner(factory));

        var models = await gateway.FetchModelsAsync("agy");

        Assert.Equal(["gemini-3.1-pro-high"], models);
        Assert.Equal("agy", factory.StartInfo!.FileName);
        Assert.Equal("models", factory.StartInfo.ArgumentList.Single());
    }

    [Fact]
    public async Task RunAsyncUsesSupportedHeadlessFlagsAndConvertsTextOnlyRequests()
    {
        var output = """
            {"event":"init","init":{"permission_mode":"plan"}}
            {"event":"step_update","step_update":{"step_type":"agent_response","text_delta":"Hello"}}
            {"event":"result","result":{"status":"SUCCESS","response":"Hello"}}
            """;
        var factory = new FakeProcessFactory(new FakeProcess(output, 0));
        var gateway = new AntigravityGateway(new ProviderCliRunner(factory));

        var result = await gateway.RunAsync("agy", "gemini-3.1-pro-high", WireProtocol.OpenAi,
            """{"messages":[{"role":"user","content":"Say hello"}]}""", false, _ => ValueTask.CompletedTask);

        Assert.Equal("Hello", result.Text);
        Assert.Equal("gemini-3.1-pro-high", result.Model);
        Assert.Contains("Say hello", factory.LastProcess!.Input.ToString());
        Assert.Contains("answer", factory.LastProcess.Input.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("--input-format", factory.StartInfo!.ArgumentList);
        Assert.Contains("stream-json", factory.StartInfo.ArgumentList);
        Assert.Contains("--sandbox", factory.StartInfo.ArgumentList);
        Assert.Contains("--mode", factory.StartInfo.ArgumentList);
        Assert.Contains("plan", factory.StartInfo.ArgumentList);
        Assert.DoesNotContain("--dangerously-skip-permissions", factory.StartInfo.ArgumentList);
        Assert.False(Directory.Exists(factory.StartInfo.WorkingDirectory));
    }

    [Fact]
    public async Task RunAsyncStreamsTextAsOpenAiAndAnthropicAndBufferedResponses()
    {
        const string output = "{\"event\":\"step_update\",\"step_update\":{\"step_type\":\"agent_response\",\"text_delta\":\"Hi\"}}\n" +
            "{\"event\":\"result\",\"result\":{\"status\":\"SUCCESS\",\"response\":\"Hi\"}}\n";
        var openAi = new List<string>();
        await CreateGateway(output).RunAsync("agy", "gemini-3.1-pro-high", WireProtocol.OpenAi,
            """{"messages":[{"role":"user","content":"Hi"}]}""", true, value => { openAi.Add(value); return ValueTask.CompletedTask; });
        var anthropic = new List<string>();
        await CreateGateway(output).RunAsync("agy", "gemini-3.1-pro-high", WireProtocol.Anthropic,
            """{"messages":[{"role":"user","content":"Hi"}]}""", true, value => { anthropic.Add(value); return ValueTask.CompletedTask; });
        var buffered = AntigravityGateway.BufferedResponse(new AntigravityGeneration("Hi", "gemini-3.1-pro-high"), WireProtocol.Anthropic);

        Assert.Contains("chat.completion.chunk", string.Concat(openAi));
        Assert.Contains("data: [DONE]", string.Concat(openAi));
        Assert.Contains("event: content_block_delta", string.Concat(anthropic));
        Assert.Contains("message_stop", string.Concat(anthropic));
        Assert.Contains("Hi", buffered);
        Assert.Contains("gemini-3.1-pro-high", buffered);
    }

    [Fact]
    public async Task RunAsyncRejectsToolsAndMultimodalRequests()
    {
        var gateway = CreateGateway("");

        var tools = await Assert.ThrowsAsync<AntigravityGatewayException>(() => gateway.RunAsync("agy", "m", WireProtocol.OpenAi,
            """{"messages":[{"role":"user","content":"Hi"}],"tools":[{"type":"function"}]}""", false, _ => ValueTask.CompletedTask));
        var image = await Assert.ThrowsAsync<AntigravityGatewayException>(() => gateway.RunAsync("agy", "m", WireProtocol.Anthropic,
            """{"messages":[{"role":"user","content":[{"type":"image","source":{"type":"url","url":"https://x"}}]}]}""", false, _ => ValueTask.CompletedTask));

        Assert.Contains("tool", tools.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("text-only", image.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("\"temperature\":0.2")]
    [InlineData("\"max_tokens\":100")]
    [InlineData("\"top_p\":0.5")]
    public async Task RunAsyncRejectsUnsupportedGenerationControls(string field)
    {
        var gateway = CreateGateway("");

        var exception = await Assert.ThrowsAsync<AntigravityGatewayException>(() => gateway.RunAsync("agy", "m", WireProtocol.OpenAi,
            $"{{\"messages\":[{{\"role\":\"user\",\"content\":\"Hi\"}}],{field}}}", false, _ => ValueTask.CompletedTask));

        Assert.Contains("unsupported", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RunAsyncDoesNotExposeTerminalErrorDetails()
    {
        var gateway = CreateGateway("{\"event\":\"result\",\"result\":{\"status\":\"ERROR\",\"response\":\"\",\"error\":\"authentication required; access_token=super-secret-token\"}}\n");

        var exception = await Assert.ThrowsAsync<AntigravityGatewayException>(() => gateway.RunAsync("agy", "m", WireProtocol.OpenAi,
            """{"messages":[{"role":"user","content":"Hi"}]}""", false, _ => ValueTask.CompletedTask));

        Assert.Contains("authentication", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("super-secret-token", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("{\"access_token\":\"opaque-secret\"}", "opaque-secret")]
    [InlineData("Authorization: Bearer opaque-secret", "opaque-secret")]
    public async Task RunAsyncDoesNotExposeJsonOrBearerCredentialsFromTerminalError(string error, string secret)
    {
        var line = System.Text.Json.JsonSerializer.Serialize(new { @event = "result", result = new { status = "ERROR", error } });
        var gateway = CreateGateway(line + "\n");

        var exception = await Assert.ThrowsAsync<AntigravityGatewayException>(() => gateway.RunAsync("agy", "m", WireProtocol.OpenAi,
            """{"messages":[{"role":"user","content":"Hi"}]}""", false, _ => ValueTask.CompletedTask));

        Assert.Contains("selected model", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(secret, exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("\"true\"")]
    [InlineData("1")]
    [InlineData("null")]
    public async Task RunAsyncRejectsNonBooleanStreamValue(string streamValue)
    {
        var gateway = CreateGateway("");

        var exception = await Assert.ThrowsAsync<AntigravityGatewayException>(() => gateway.RunAsync("agy", "m", WireProtocol.OpenAi,
            $"{{\"messages\":[{{\"role\":\"user\",\"content\":\"Hi\"}}],\"stream\":{streamValue}}}", false, _ => ValueTask.CompletedTask));

        Assert.Contains("stream", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("boolean", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ToolUseIsNotClaimedToBeDisabledByTheGateway()
    {
        Assert.False(AntigravityGateway.ToolsAreHardDisabled);
        Assert.Contains("user's Antigravity CLI permissions", AntigravityGateway.ToolPermissionNotice, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("ERROR")]
    [InlineData("WAITING")]
    public async Task RunAsyncRequiresTerminalSuccessStatus(string status)
    {
        var gateway = CreateGateway($"{{\"event\":\"result\",\"result\":{{\"status\":\"{status}\",\"response\":\"No\"}}}}\n");

        var exception = await Assert.ThrowsAsync<AntigravityGatewayException>(() => gateway.RunAsync("agy", "m", WireProtocol.OpenAi,
            """{"messages":[{"role":"user","content":"Hi"}]}""", false, _ => ValueTask.CompletedTask));

        Assert.Equal(HttpStatusCode.BadGateway, exception.StatusCode);
    }

    [Fact]
    public async Task RunAsyncReportsAuthenticationRequiredCliFailureWithoutLeakingStderr()
    {
        var gateway = CreateGateway("", exitCode: 1);

        var exception = await Assert.ThrowsAsync<AntigravityGatewayException>(() => gateway.RunAsync("agy", "m", WireProtocol.OpenAi,
            """{"messages":[{"role":"user","content":"Hi"}]}""", false, _ => ValueTask.CompletedTask));

        Assert.Equal(HttpStatusCode.BadGateway, exception.StatusCode);
        Assert.Contains("authentication", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RunAsyncCancellationKillsProcessTree()
    {
        var process = new FakeProcess("", 0, waitForExit: true);
        var gateway = new AntigravityGateway(new ProviderCliRunner(new FakeProcessFactory(process)));
        using var cancellation = new CancellationTokenSource();
        var run = gateway.RunAsync("agy", "m", WireProtocol.OpenAi,
            """{"messages":[{"role":"user","content":"Hi"}]}""", false, _ => ValueTask.CompletedTask, cancellation.Token);
        await process.WaitStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        Assert.True(process.KilledEntireTree);
    }

    [Fact]
    public async Task RunAsyncTimeoutKillsProcessTreeAndReturnsGatewayTimeout()
    {
        var clock = new ManualTimeProvider();
        var process = new FakeProcess("", 0, waitForExit: true);
        var gateway = new AntigravityGateway(new ProviderCliRunner(new FakeProcessFactory(process)), TimeSpan.FromMinutes(5), clock);
        var run = gateway.RunAsync("agy", "m", WireProtocol.OpenAi,
            """{"messages":[{"role":"user","content":"Hi"}]}""", false, _ => ValueTask.CompletedTask);
        await process.WaitStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        clock.FireTimer();

        var exception = await Assert.ThrowsAsync<AntigravityGatewayException>(() => run);
        Assert.Equal(HttpStatusCode.GatewayTimeout, exception.StatusCode);
        Assert.True(process.KilledEntireTree);
    }

    [Fact]
    public async Task RelayServerRoutesAntigravityCliAccountRequests()
    {
        const string key = "antigravity-test-key";
        const string output = "{\"event\":\"step_update\",\"step_update\":{\"step_type\":\"agent_response\",\"text_delta\":\"CLI reply\"}}\n" +
            "{\"event\":\"result\",\"result\":{\"status\":\"SUCCESS\",\"response\":\"CLI reply\"}}\n";
        var process = new FakeProcess(output, 0, waitForExit: true);
        var gateway = new AntigravityGateway(new ProviderCliRunner(new FakeProcessFactory(process)));
        using var server = new RelayServer(new RelayTelemetryStore(), port: 0, antigravityGateway: gateway);
        var provider = new ProviderSettings("agy-account", "Antigravity", ProviderKinds.Antigravity,
            "https://unused.invalid", null, true, 0, AuthMode: ProviderAuthMode.CliAccount, CliExecutable: "agy", ImportedModels: ["gemini-3.1-pro-high"]);
        server.Apply(key, new ProviderRouter([provider], new CatalogSnapshot(DateTimeOffset.UtcNow,
            new Dictionary<string, IReadOnlyList<string>> { [provider.Id] = ["gemini-3.1-pro-high"] })));
        await server.StartAsync();
        using var client = new HttpClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{server.Port}/v1/chat/completions")
        {
            Content = new StringContent("""{"model":"gemini-3.1-pro-high","messages":[{"role":"user","content":"Hi"}]}""", Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        var responseTask = client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        await process.WaitStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        process.CompleteExit();
        using var response = await responseTask;
        var body = await response.Content.ReadAsStringAsync();
        await server.StopAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("CLI reply", body);
        Assert.Contains("user's Antigravity CLI permissions", response.Headers.GetValues("x-relay-provider-capability").Single(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SignInAsyncOpensAPrintedSignInLinkAndReportsSuccess()
    {
        var gateway = CreateGateway("""
            Open the following URL in your local browser to authenticate:
            https://accounts.google.com/o/oauth2/auth?client_id=x&state=y
            {"event":"result","result":{"status":"SUCCESS","response":"ok"}}
            """);
        var opened = new List<Uri>();

        Assert.True(await gateway.SignInAsync("agy", opened.Add));

        Assert.Equal("accounts.google.com", Assert.Single(opened).Host);
    }

    [Fact]
    public async Task SignInAsyncDoesNotOpenLinksTheCliDidNotAskFor()
    {
        var gateway = CreateGateway("""
            See https://antigravity.google/docs/cli/reference
            {"event":"result","result":{"status":"ERROR"}}
            """);
        var opened = new List<Uri>();

        Assert.False(await gateway.SignInAsync("agy", opened.Add));

        Assert.Empty(opened);
    }

    private static AntigravityGateway CreateGateway(string stdout, int exitCode = 0) =>
        new(new ProviderCliRunner(new FakeProcessFactory(new FakeProcess(stdout, exitCode))));

    private sealed class FakeProcessFactory(FakeProcess process) : IProviderCliProcessFactory
    {
        public ProcessStartInfo? StartInfo { get; private set; }
        public FakeProcess? LastProcess => process;
        public IProviderCliProcess Start(ProcessStartInfo info) { StartInfo = info; return process; }
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
        public TaskCompletionSource WaitStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task WaitForExitAsync(CancellationToken cancellationToken)
        {
            WaitStarted.TrySetResult();
            if (waitForExit) await _exit.Task.WaitAsync(cancellationToken);
        }
        public void Kill(bool entireProcessTree) { KilledEntireTree = entireProcessTree; _exit.TrySetResult(); }
        public void CompleteExit() => _exit.TrySetResult();
        public void Dispose() { _stdout.Dispose(); _stderr.Dispose(); Input.Dispose(); }
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private ManualTimer? _timer;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            _timer = new ManualTimer(callback, state);
            return _timer;
        }
        public void FireTimer() => _timer?.Fire();
        private sealed class ManualTimer(TimerCallback callback, object? state) : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;
            public void Fire() => callback(state);
            public void Dispose() { }
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
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
