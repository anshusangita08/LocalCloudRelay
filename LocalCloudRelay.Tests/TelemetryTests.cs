using LocalCloudRelay;
namespace LocalCloudRelay.Tests;

public sealed class TelemetryTests
{
    [Fact]
    public void ParsesAnthropicUsageAndThinkingTokens()
    {
        var usage = RelayUsageParser.Parse("""
        {"model":"claude-sonnet","usage":{"input_tokens":10,"output_tokens":7,"cache_read_input_tokens":4,"cache_creation_input_tokens":2,"output_tokens_details":{"thinking_tokens":3}}}
        """);

        Assert.NotNull(usage);
        Assert.Equal(10, usage!.InputTokens);
        Assert.Equal(7, usage.OutputTokens);
        // Every token processed: 10 uncached + 4 cache read + 2 cache write in, 7 out.
        Assert.Equal(23, usage.TotalTokens);
        Assert.Equal(4, usage.CacheReadInputTokens);
        Assert.Equal(2, usage.CacheCreationInputTokens);
        Assert.Equal(3, usage.ReasoningTokens);
        Assert.Equal("claude-sonnet", usage.Model);
    }

    [Fact]
    public void ParsesOpenAiUsageAndLiteLlmCost()
    {
        var usage = RelayUsageParser.Parse("""
        {"model":"gpt-5","cost":0.0123,"usage":{"prompt_tokens":20,"completion_tokens":8,"prompt_tokens_details":{"cached_tokens":12},"completion_tokens_details":{"reasoning_tokens":5}}}
        """);

        Assert.NotNull(usage);
        // prompt_tokens (20) includes the 12 cached; input keeps the 8 uncached, so a
        // cached token is never priced twice.
        Assert.Equal(8, usage!.InputTokens);
        Assert.Equal(8, usage.OutputTokens);
        Assert.Equal(28, usage.TotalTokens);
        Assert.Equal(12, usage.CacheReadInputTokens);
        Assert.Equal(5, usage.ReasoningTokens);
        Assert.Equal(0.0123m, usage.CostUsd);
    }

    [Fact]
    public void ParsesFinalStreamingUsageWithoutChangingEvents()
    {
        var usage = RelayUsageParser.ParseServerSentEvents("data: {\"choices\":[]}\n\ndata: {\"usage\":{\"prompt_tokens\":3,\"completion_tokens\":2}}\n\ndata: [DONE]\n");

        Assert.NotNull(usage);
        Assert.Equal(3, usage!.InputTokens);
        Assert.Equal(2, usage.OutputTokens);
    }

    [Fact]
    public void ReadsLiteLlmCostAndRateLimitHeaders()
    {
        var headers = new[]
        {
            new KeyValuePair<string, IEnumerable<string>>("x-litellm-response-cost", ["0.42"]),
            new KeyValuePair<string, IEnumerable<string>>("x-litellm-call-id", ["call-1"]),
            new KeyValuePair<string, IEnumerable<string>>("x-ratelimit-remaining-tokens", ["1000"])
        };

        var snapshot = RelayUsageParser.ReadHeaders(headers);

        Assert.Equal(0.42m, snapshot.CostUsd);
        Assert.Equal("call-1", snapshot.LiteLlmCallId);
        Assert.Equal("1000", snapshot.RateLimits["x-ratelimit-remaining-tokens"]);
    }

    [Fact]
    public void AggregatesSessionCostAndTokens()
    {
        var store = new RelayTelemetryStore();
        var now = DateTimeOffset.UtcNow;
        store.Record(new RelayRequestRecord(now, "r1", "s1", "POST", "/v1/messages", 200, 20, "claude", "anthropic", null, null, 10, 5, 15, 2, 1, 3, 0.10m, "provider", new Dictionary<string, string>()));
        store.Record(new RelayRequestRecord(now, "r2", "s1", "POST", "/v1/messages", 200, 30, "gpt", "azure", null, null, 4, 6, 10, 0, 0, 2, 0.20m, "provider", new Dictionary<string, string>()));

        var report = store.GetReport("s1");

        Assert.Equal(2, report.RequestCount);
        Assert.Equal(14, report.InputTokens);
        Assert.Equal(0.30m, report.ProviderCostUsd);
        Assert.Equal(5, report.ReasoningTokens);
    }

    [Theory]
    [InlineData("claude-sonnet", "anthropic")]
    [InlineData("gemini-2.5-pro", "gemini-vertex")]
    [InlineData("azure-gpt-5", "openai-azure")]
    [InlineData("custom-model", "unknown")]
    public void ResolvesProviderThinkingProfile(string model, string family)
    {
        Assert.Equal(family, RelayCapabilityCatalog.Resolve(model).ProviderFamily);
    }

    [Fact]
    public void ClientCountUsesTheRemoteAddressNotOurOwnSessionHeader()
    {
        // Claude Code, Cline and most clients never send x-relay-session-id, so keying
        // the count on it reported zero for a relay that was actively serving traffic.
        var store = new RelayTelemetryStore();
        var now = DateTimeOffset.UtcNow;
        var old = now.AddHours(-2);

        Record(store, now, "unassigned", "192.168.1.20", "claude-cli/2.0");
        Record(store, now, "unassigned", "192.168.1.20", "claude-cli/2.0");
        Record(store, now, "unassigned", "192.168.1.31", "opencode/1.0");
        Record(store, old, "unassigned", "192.168.1.99", "opencode/1.0");

        Assert.Equal(2, RelayStatus.RecentClientCount(store));
        var clients = RelayStatus.RecentClients(store);
        Assert.Contains(clients, c => c.Contains("claude-cli/2.0", StringComparison.Ordinal));
        Assert.Contains(clients, c => c.Contains("192.168.1.31", StringComparison.Ordinal));
        Assert.DoesNotContain(clients, c => c.Contains("192.168.1.99", StringComparison.Ordinal));
    }

    [Fact]
    public void CombinesAnthropicCumulativeUsageWithMaxNotSum()
    {
        // Anthropic SSE streams send cumulative usage: message_start has input_tokens N and
        // output_tokens 1, then message_delta sends input_tokens N again and output_tokens M
        // (cumulative). Sum would double-count the input; max gives the right total.
        var sse = """
        event: message_start
        data: {"type":"message_start","usage":{"input_tokens":100,"output_tokens":1,"cache_read_input_tokens":50}}

        event: message_delta
        data: {"type":"message_delta","usage":{"input_tokens":100,"output_tokens":42,"cache_read_input_tokens":50}}

        event: message_stop
        data: {"type":"message_stop"}
        """;

        var usage = RelayUsageParser.ParseServerSentEvents(sse);

        Assert.NotNull(usage);
        Assert.Equal(100, usage!.InputTokens);
        Assert.Equal(42, usage.OutputTokens);
        Assert.Equal(50, usage.CacheReadInputTokens);
    }

    private static void Record(RelayTelemetryStore store, DateTimeOffset at, string session, string? address, string? agent) =>
        store.Record(new RelayRequestRecord(at, "r", session, "POST", "/v1/messages", 200, 10, "m", "p", null, null,
            1, 1, 2, null, null, null, null, "unknown", new Dictionary<string, string>(), address, agent));
}
