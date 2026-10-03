using LocalCloudRelay;

namespace LocalCloudRelay.Tests;

/// <summary>
/// Usage is kept per day across restarts and summarised for today, 7 and 30 days; failed
/// and retried requests leave one diagnostic line each, and the log cannot grow without end.
/// </summary>
public sealed class UsageHistoryTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "relay-history-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 10, 3, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }

    private static RelayRequestRecord Record(DateTimeOffset at, int status = 200, decimal cost = 0.01m, string source = "estimated",
        decimal? apiEquivalent = null, string? decision = null, string? error = null) =>
        new(at, Guid.NewGuid().ToString("N"), "s", "POST", "/v1/messages", status, 10, "kimi-k3", "Zen", null, null,
            100, 10, 110, 300, 0, null, cost, source, new Dictionary<string, string>(), "10.0.0.5", "claude-cli",
            apiEquivalent, decision, error);

    [Fact]
    public void RecordsAreSummarisedByPeriodAndSurviveANewInstance()
    {
        var clock = new Clock();
        var history = new UsageHistory(_dir, clock);
        history.Append(Record(clock.Now));
        history.Append(Record(clock.Now, status: 502, cost: 0m, source: "unknown"));
        history.Append(Record(clock.Now.AddDays(-3), cost: 0m, source: "plan", apiEquivalent: 0.5m));
        history.Append(Record(clock.Now.AddDays(-20)));

        // A restart is a new instance reading the same files.
        var periods = new UsageHistory(_dir, clock).Summaries();
        var today = periods.Single(p => p.Label == "Today");
        Assert.Equal(2, today.Requests);
        Assert.Equal(1, today.Failed);
        Assert.Equal(0.01m, today.CostUsd);
        Assert.Equal(600.0 / 800, today.CacheHitRate);   // 2 x (100 in + 300 cache read)

        var week = periods.Single(p => p.Label == "Last 7 days");
        Assert.Equal(3, week.Requests);
        Assert.Equal(0.5m, week.ApiEquivalentUsd);       // plan usage, kept apart from cost
        Assert.Equal(0.01m, week.CostUsd);

        Assert.Equal(4, periods.Single(p => p.Label == "Last 30 days").Requests);
    }

    [Fact]
    public void FilesOlderThanThirtyDaysAreDeletedAndBrokenLinesSkipped()
    {
        var clock = new Clock();
        var history = new UsageHistory(_dir, clock);
        history.Append(Record(clock.Now.AddDays(-45)));
        history.Append(Record(clock.Now));
        File.AppendAllText(Path.Combine(_dir, "2026-10-03.jsonl"), "{\"cut short\n");

        history.Prune();

        Assert.Single(Directory.GetFiles(_dir, "*.jsonl"));
        Assert.Equal(1, history.Summaries()[0].Requests);
    }

    [Fact]
    public void OnlyFailedAndRetriedRequestsAreLogged()
    {
        var log = new DiagnosticLog(Path.Combine(_dir, "diagnostics.log"));
        var now = DateTimeOffset.UtcNow;
        log.Write(Record(now));                                                       // fine: not logged
        log.Write(Record(now, status: 499));                                          // client left: not logged
        log.Write(Record(now, status: 503, decision: "router=free; chose=Zen/kimi-k3"));
        log.Write(Record(now, decision: "router=free; chose=Go/glm; attempt=2"));     // retried, then fine
        log.Write(Record(now, status: 502, error: "HttpRequestException: Connection refused"));

        var lines = File.ReadAllLines(log.FilePath);
        Assert.Equal(3, lines.Length);
        Assert.Contains("503", lines[0]);
        Assert.Contains("attempt=2", lines[1]);
        Assert.Contains("Connection refused", lines[2]);
        Assert.All(lines, line => Assert.DoesNotContain("\"messages\"", line));
    }

    [Fact]
    public void TheLogRollsOverInsteadOfGrowing()
    {
        var log = new DiagnosticLog(Path.Combine(_dir, "diagnostics.log"), maxBytes: 600);
        for (var i = 0; i < 20; i++) log.Write(Record(DateTimeOffset.UtcNow, status: 503));

        Assert.True(new FileInfo(log.FilePath).Length <= 600);
        Assert.True(File.Exists(log.FilePath + ".1"));
        Assert.Equal(2, Directory.GetFiles(_dir, "diagnostics.log*").Length);   // never more than one old file
    }

    [Fact]
    public void TheTelemetryStoreFeedsBothThroughItsEvent()
    {
        var store = new RelayTelemetryStore();
        var seen = new List<RelayRequestRecord>();
        store.Recorded += seen.Add;
        store.Record(Record(DateTimeOffset.UtcNow));
        Assert.Single(seen);
    }
}
