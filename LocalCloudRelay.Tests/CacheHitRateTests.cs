using LocalCloudRelay;

namespace LocalCloudRelay.Tests;

/// <summary>
/// The Session view shows how much of each prompt came from cache, so the routing and
/// breakpoint changes can be checked against real traffic.
/// </summary>
public sealed class CacheHitRateTests
{
    [Fact]
    public void HitRateIsCacheReadsOverTheWholePrompt()
    {
        Assert.Equal(0.8, RelayTelemetryStore.CacheHitRate(input: 100, cacheRead: 800, cacheWrite: 100));
        Assert.Equal(0.0, RelayTelemetryStore.CacheHitRate(input: 50, cacheRead: 0, cacheWrite: 0));
        Assert.Null(RelayTelemetryStore.CacheHitRate(0, 0, 0));
    }

    [Fact]
    public void TheReportAggregatesHitRateByModelAndOverall()
    {
        var store = new RelayTelemetryStore();
        void Record(string model, long input, long read, long write) => store.Record(new RelayRequestRecord(
            DateTimeOffset.UtcNow, Guid.NewGuid().ToString("n"), "s", "POST", "/v1/messages", 200, 10,
            model, "p", null, null, input, 10, input + read + write + 10, read, write, null, null, "unknown",
            new Dictionary<string, string>()));

        Record("a", input: 100, read: 0, write: 900);    // first turn writes the cache
        Record("a", input: 100, read: 900, write: 0);    // second turn reads it
        Record("b", input: 200, read: 0, write: 0);

        var report = store.GetReport();
        var a = report.Models.Single(m => m.Key == "a");
        Assert.Equal(900.0 / 2000, a.CacheHitRate);
        Assert.Equal(900, a.CacheCreationInputTokens);
        Assert.Equal(0.0, report.Models.Single(m => m.Key == "b").CacheHitRate);
        Assert.Equal(900.0 / 2200, report.CacheHitRate);
    }

    [Fact]
    public void PlanAccountUsageIsReportedApartFromSpend()
    {
        var store = new RelayTelemetryStore();
        store.Record(new RelayRequestRecord(DateTimeOffset.UtcNow, "r1", "s", "POST", "/v1/messages", 200, 10,
            "sonnet", "Claude Code", null, null, 100, 10, 110, 0, 0, null, 0m, "plan",
            new Dictionary<string, string>(), null, null, ApiEquivalentUsd: 0.25m));
        store.Record(new RelayRequestRecord(DateTimeOffset.UtcNow, "r2", "s", "POST", "/v1/chat/completions", 200, 10,
            "kimi-k3", "Zen", null, null, 100, 10, 110, 0, 0, null, 0.01m, "estimated",
            new Dictionary<string, string>()));

        var report = store.GetReport();
        Assert.Equal(0.01m, report.TotalConsumedCostUsd);   // only real spend
        Assert.Equal(0.25m, report.ApiEquivalentUsd);        // the subscription's API value, apart
        Assert.Equal(0, report.RequestsWithUnknownCost);
    }
}