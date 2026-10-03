using System.Diagnostics;
using System.Text;
using LocalCloudRelay;

namespace LocalCloudRelay.Tests;

public sealed class GeminiCliGatewayTests : IDisposable
{
    private readonly string _home = Path.Combine(Path.GetTempPath(), "gemini-home-" + Guid.NewGuid().ToString("N"));

    public GeminiCliGatewayTests() => Directory.CreateDirectory(_home);

    public void Dispose() => Directory.Delete(_home, recursive: true);

    [Fact]
    public void IsSignedInNeedsGoogleLoginTypeAndCredentialFile()
    {
        var gateway = new GeminiCliGateway(new ProviderCliRunner(), geminiHome: _home);
        Assert.False(gateway.IsSignedIn());

        File.WriteAllText(Path.Combine(_home, "settings.json"), """{"security":{"auth":{"selectedType":"oauth-personal"}}}""");
        Assert.False(gateway.IsSignedIn());

        File.WriteAllText(Path.Combine(_home, "oauth_creds.json"), "{}");
        Assert.True(gateway.IsSignedIn());

        File.WriteAllText(Path.Combine(_home, "settings.json"), """{"security":{"auth":{"selectedType":"gemini-api-key"}}}""");
        Assert.False(gateway.IsSignedIn());
    }

    [Fact]
    public void UsageComesFromResultStatsWithCachedTokensSplitOut()
    {
        using var withInput = System.Text.Json.JsonDocument.Parse(
            """{"type":"result","stats":{"total_tokens":1100,"input_tokens":1000,"output_tokens":100,"cached":600,"input":400}}""");
        var usage = GeminiCliGateway.UsageFromStats(withInput.RootElement)!;
        Assert.Equal(400, usage.InputTokens);
        Assert.Equal(600, usage.CacheReadInputTokens);
        Assert.Equal(100, usage.OutputTokens);

        // Older builds omit "input": the uncached part is the prompt less the cached part.
        using var withoutInput = System.Text.Json.JsonDocument.Parse(
            """{"type":"result","stats":{"input_tokens":1000,"output_tokens":100,"cached":250}}""");
        Assert.Equal(750, GeminiCliGateway.UsageFromStats(withoutInput.RootElement)!.InputTokens);

        using var empty = System.Text.Json.JsonDocument.Parse("""{"type":"result","stats":{}}""");
        Assert.Null(GeminiCliGateway.UsageFromStats(empty.RootElement));
    }

    [Fact]
    public async Task RunAsyncStreamsAssistantDeltasAsAnthropicEvents()
    {
        var process = new FakeProcess("""
            {"type":"init","session_id":"s","model":"gemini-2.5-flash"}
            {"type":"message","role":"user","content":"hi"}
            {"type":"message","role":"assistant","content":"Hel","delta":true}
            {"type":"message","role":"assistant","content":"lo","delta":true}
            {"type":"result","status":"success","stats":{}}
            """, 0);
        var factory = new FakeProcessFactory(process);
        var gateway = new GeminiCliGateway(new ProviderCliRunner(factory), geminiHome: _home);
        var events = new StringBuilder();

        var result = await gateway.RunAsync("gemini.js", "gemini-2.5-flash", WireProtocol.Anthropic,
            """{"model":"gemini-2.5-flash","messages":[{"role":"user","content":"Say hello"}],"stream":true}""",
            true, chunk => { events.Append(chunk); return ValueTask.CompletedTask; });

        Assert.Equal("Hello", result.Text);
        Assert.Contains("Say hello", process.Input.ToString());
        Assert.Contains("\"text\":\"Hel\"", events.ToString());
        Assert.Contains("message_stop", events.ToString());
        var args = factory.StartInfo!.ArgumentList;
        Assert.Equal("node", factory.StartInfo.FileName);
        Assert.Equal("gemini.js", args[0]);
        Assert.Contains("stream-json", args);
        Assert.Contains("plan", args);
        Assert.Contains("gemini-2.5-flash", args);
    }

    [Fact]
    public async Task RunAsyncReportsTheCliErrorMessage()
    {
        var gateway = new GeminiCliGateway(new ProviderCliRunner(new FakeProcessFactory(new FakeProcess(
            """{"type":"result","status":"error","error":{"type":"QuotaError","message":"Quota exceeded"}}""", 1))),
            geminiHome: _home);

        var error = await Assert.ThrowsAsync<GeminiCliGatewayException>(() => gateway.RunAsync("gemini.js", "gemini-2.5-pro",
            WireProtocol.OpenAi, """{"messages":[{"role":"user","content":"x"}]}""", false, _ => ValueTask.CompletedTask));

        Assert.Contains("Quota exceeded", error.Message);
    }

    [Fact]
    public async Task RunAsyncRejectsModelIdsThatCouldBeFlags()
    {
        var gateway = new GeminiCliGateway(new ProviderCliRunner(), geminiHome: _home);

        await Assert.ThrowsAsync<GeminiCliGatewayException>(() => gateway.RunAsync("gemini.js", "--yolo",
            WireProtocol.OpenAi, """{"messages":[{"role":"user","content":"x"}]}""", false, _ => ValueTask.CompletedTask));
    }

    private sealed class FakeProcessFactory(FakeProcess process) : IProviderCliProcessFactory
    {
        public ProcessStartInfo? StartInfo { get; private set; }
        public IProviderCliProcess Start(ProcessStartInfo info) { StartInfo = info; return process; }
    }

    private sealed class FakeProcess(string stdout, int exitCode) : IProviderCliProcess
    {
        private readonly StringReader _stdout = new(stdout);
        private readonly StringReader _stderr = new("");
        public StringWriter Input { get; } = new();
        public TextWriter StandardInput => Input;
        public TextReader StandardOutput => _stdout;
        public TextReader StandardError => _stderr;
        public int ExitCode => exitCode;
        public Task WaitForExitAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public void Kill(bool entireProcessTree) { }
        public void Dispose() { _stdout.Dispose(); _stderr.Dispose(); Input.Dispose(); }
    }
}

public sealed class AddProviderFormTests
{
    [Theory]
    [InlineData(AddProviderForm.ChatGpt, true, ProviderKinds.OpenAi, ProviderAuthMode.OAuth)]
    [InlineData(AddProviderForm.ChatGpt, false, ProviderKinds.OpenAi, ProviderAuthMode.ApiKey)]
    [InlineData(AddProviderForm.Claude, true, ProviderKinds.ClaudeCode, ProviderAuthMode.CliAccount)]
    [InlineData(AddProviderForm.Claude, false, ProviderKinds.Anthropic, ProviderAuthMode.ApiKey)]
    [InlineData(AddProviderForm.Gemini, true, ProviderKinds.Antigravity, ProviderAuthMode.CliAccount)]
    [InlineData(AddProviderForm.Gemini, false, ProviderKinds.Gemini, ProviderAuthMode.ApiKey)]
    [InlineData(AddProviderForm.Antigravity, true, ProviderKinds.Antigravity, ProviderAuthMode.CliAccount)]
    public void BuildMapsEachChoiceToACompleteProfile(string label, bool account, string kind, ProviderAuthMode mode)
    {
        var provider = AddProviderForm.Build(label, account, account ? null : "sk-test", "id1");

        Assert.Equal(kind, provider.Kind);
        Assert.Equal(mode, provider.AuthMode);
        Assert.True(provider.Enabled);
        Assert.Equal(account ? null : "sk-test", provider.ApiKey);
        Assert.True(Uri.IsWellFormedUriString(provider.BaseUrl, UriKind.Absolute));
    }

    [Fact]
    public void CliAccountsStartWithModelsSoTheyCanJoinARouterAtOnce()
    {
        var claude = AddProviderForm.Build(AddProviderForm.Claude, true, null, "c");
        var gemini = AddProviderForm.Build(AddProviderForm.Gemini, true, null, "g") with
        {
            ImportedModels = ["gemini-3.1-pro-high"]
        };

        Assert.Equal(["fable", "opus", "sonnet", "haiku"], claude.ImportedModels);

        var router = new ProviderRouter([claude, gemini], CatalogSnapshot.Empty);
        Assert.Contains(router.RoutesForPicker, route => route.Model == "sonnet" && route.Provider.Id == "c");
        Assert.Contains(router.RoutesForPicker, route => route.Model == "gemini-3.1-pro-high" && route.Provider.Id == "g");
    }

    [Fact]
    public void PresetChoicesKeepTheirGatewayUrl()
    {
        var zen = AddProviderForm.Build("OpenCode Zen", false, "key", "z");

        Assert.Equal("https://opencode.ai/zen/v1", zen.BaseUrl);
        Assert.Equal("key", zen.ApiKey);
    }
}
