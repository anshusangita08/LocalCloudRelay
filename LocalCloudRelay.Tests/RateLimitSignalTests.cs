using LocalCloudRelay;

namespace LocalCloudRelay.Tests;

public sealed class RateLimitSignalTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 3, 12, 0, 0, TimeSpan.Zero);

    private static Dictionary<string, string> Headers(params (string Name, string Value)[] pairs) =>
        pairs.ToDictionary(p => p.Name, p => p.Value, StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void A429WaitsForRetryAfterSeconds() =>
        Assert.Equal(TimeSpan.FromSeconds(30), RateLimitSignal.BackoffFor429(Headers(("retry-after", "30")), Now));

    [Fact]
    public void A429WithoutRetryAfterWaitsTheDefault() =>
        Assert.Equal(RateLimitSignal.DefaultBackoff, RateLimitSignal.BackoffFor429(Headers(), Now));

    [Fact]
    public void A429RetryAfterIsCapped() =>
        Assert.Equal(RateLimitSignal.MaxBackoff, RateLimitSignal.BackoffFor429(Headers(("retry-after", "86400")), Now));

    [Fact]
    public void OpenAiHeadersWithLittleLeftWaitForTheReset()
    {
        var wait = RateLimitSignal.LowHeadroomFor(Headers(
            ("x-ratelimit-limit-requests", "500"),
            ("x-ratelimit-remaining-requests", "20"),
            ("x-ratelimit-reset-requests", "1m30s")), Now);

        Assert.Equal(TimeSpan.FromSeconds(90), wait);
    }

    [Fact]
    public void AnthropicHeadersWithLittleLeftWaitUntilTheResetTime()
    {
        var wait = RateLimitSignal.LowHeadroomFor(Headers(
            ("anthropic-ratelimit-tokens-limit", "400000"),
            ("anthropic-ratelimit-tokens-remaining", "1000"),
            ("anthropic-ratelimit-tokens-reset", "2026-10-03T12:02:00Z")), Now);

        Assert.Equal(TimeSpan.FromMinutes(2), wait);
    }

    [Fact]
    public void PlentyOfHeadroomMeansNoWait() =>
        Assert.Null(RateLimitSignal.LowHeadroomFor(Headers(
            ("x-ratelimit-limit-tokens", "1000000"),
            ("x-ratelimit-remaining-tokens", "900000")), Now));

    [Fact]
    public void ARateLimitedProviderSendsAWarmConversationElsewhere()
    {
        var providerA = new ProviderSettings("provider-a", "A", ProviderKinds.OpenAi, "https://a.example/v1", "k", true, 0);
        var providerB = new ProviderSettings("provider-b", "B", ProviderKinds.OpenAi, "https://b.example/v1", "k", true, 1);
        var router = new ProviderRouter([providerA, providerB],
            new CatalogSnapshot(Now, new Dictionary<string, IReadOnlyList<string>> { ["provider-a"] = ["shared"], ["provider-b"] = ["shared"] }),
            [RouterRule.Create("rr", RouterStrategies.RoundRobin, ["shared"])]);
        var engine = new RouterEngine();

        Assert.True(router.TryResolve("rr", out var first, engine, "chat", "turn-1"));
        engine.MarkProviderBusy(first.Provider, TimeSpan.FromMinutes(1));
        Assert.True(router.TryResolve("rr", out var second, engine, "chat", "turn-2"));

        Assert.NotEqual(first.Provider.Id, second.Provider.Id);
    }

    [Theory]
    [InlineData(ProviderAuthMode.CliAccount, true)]
    [InlineData(ProviderAuthMode.OAuth, true)]
    [InlineData(ProviderAuthMode.ApiKey, false)]
    public void ABusyRoundRobinConversationMovesAfterTheHoldLimitOnlyOnPlanAccounts(ProviderAuthMode mode, bool moves)
    {
        // Plan accounts publish no rate-limit headers, so time is the only guard. An API-key
        // provider reports its limits, and moving it would only throw away a warm cache.
        var providerA = new ProviderSettings("provider-a", "A", ProviderKinds.OpenAi, "https://a.example/v1", "k", true, 0) { AuthMode = mode, ImportedModels = ["shared"] };
        var providerB = new ProviderSettings("provider-b", "B", ProviderKinds.OpenAi, "https://b.example/v1", "k", true, 1) { AuthMode = mode, ImportedModels = ["shared"] };
        var router = new ProviderRouter([providerA, providerB],
            new CatalogSnapshot(Now, new Dictionary<string, IReadOnlyList<string>> { ["provider-a"] = ["shared"], ["provider-b"] = ["shared"] }),
            [RouterRule.Create("rr", RouterStrategies.RoundRobin, ["shared"])]);
        var clock = new Clock();
        var engine = new RouterEngine(clock);

        Assert.True(router.TryResolve("rr", out var first, engine, "chat", "turn-1"));
        // A turn every two minutes keeps the cache warm the whole time.
        var current = first;
        for (var minute = 2; minute < RouterEngine.MaxHold.TotalMinutes; minute += 2)
        {
            clock.Now += TimeSpan.FromMinutes(2);
            Assert.True(router.TryResolve("rr", out current, engine, "chat", $"turn-{minute}"));
            Assert.Equal(first.Provider.Id, current.Provider.Id);
        }
        clock.Now += TimeSpan.FromMinutes(2);
        Assert.True(router.TryResolve("rr", out var afterHold, engine, "chat", "turn-late"));

        Assert.Equal(moves, first.Provider.Id != afterHold.Provider.Id);
    }

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = RateLimitSignalTests.Now;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
