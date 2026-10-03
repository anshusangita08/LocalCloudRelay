using LocalCloudRelay;

namespace LocalCloudRelay.Tests;

/// <summary>
/// A relay restart (every reboot, for a tray app) used to forget which model each
/// conversation was on, moving every live chat to a cold cache. Routing memory now
/// survives the restart, minus whatever expired while the relay was down.
/// </summary>
public sealed class RouterPersistenceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "relay-memory-" + Guid.NewGuid().ToString("N"));
    private string FilePath => Path.Combine(_dir, "routing.json");

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 10, 3, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static ProviderSettings Provider(string id) =>
        new(id, id, ProviderKinds.OpenAi, $"https://{id}.example/v1", "k", true, 0);

    private static ProviderRouter Router(string strategy) => new(
        [Provider("a"), Provider("b")],
        new CatalogSnapshot(DateTimeOffset.UtcNow, new Dictionary<string, IReadOnlyList<string>> { ["a"] = ["m"], ["b"] = ["m"] }),
        [RouterRule.Create("r", strategy, ["m"])]);

    [Fact]
    public void AConversationKeepsItsProviderAcrossARestart()
    {
        var clock = new Clock();
        var router = Router(RouterStrategies.RoundRobin);
        var before = new RouterEngine(clock);
        // Two conversations so the second lands on the other provider; the restart must
        // not reset either.
        Assert.True(router.TryResolve("r", out var one, before, "chat-1", "t"));
        Assert.True(router.TryResolve("r", out var two, before, "chat-2", "t"));
        Assert.NotEqual(one.Provider.Id, two.Provider.Id);
        new RouterMemoryStore(FilePath).Save(before.Export());

        clock.Now += TimeSpan.FromMinutes(2);
        var after = new RouterEngine(clock);
        after.Import(new RouterMemoryStore(FilePath).Load()!);

        Assert.True(router.TryResolve("r", out var oneAgain, after, "chat-1", "t2"));
        Assert.True(router.TryResolve("r", out var twoAgain, after, "chat-2", "t2"));
        Assert.Equal(one.Provider.Id, oneAgain.Provider.Id);
        Assert.Equal(two.Provider.Id, twoAgain.Provider.Id);
    }

    [Fact]
    public void ExpiredStateIsDroppedOnLoad()
    {
        var clock = new Clock();
        var engine = new RouterEngine(clock) { AllowanceFor = (_, _) => 100 };
        engine.SetSticky("old", "a\0m");
        engine.MarkProviderBusy(Provider("a"), TimeSpan.FromMinutes(1));
        engine.RecordRequest(Provider("a"), "m");
        var memory = engine.Export();

        clock.Now += RouterEngine.AllowanceWindow + TimeSpan.FromMinutes(1);
        var restored = new RouterEngine(clock) { AllowanceFor = (_, _) => 100 };
        restored.Import(memory);

        Assert.Null(restored.StickyFor("old"));
        Assert.Null(restored.Unavailable(Provider("a"), "m"));
        Assert.Equal(0, restored.RequestsInWindow(Provider("a"), "m", clock.Now));
    }

    [Fact]
    public void AllowanceCountsSurviveARestart()
    {
        // OpenCode Go counts requests over five hours; forgetting them on restart would
        // let a model run past its allowance.
        var clock = new Clock();
        var engine = new RouterEngine(clock) { AllowanceFor = (_, _) => 100 };
        for (var i = 0; i < 7; i++) engine.RecordRequest(Provider("a"), "m");

        var restored = new RouterEngine(clock) { AllowanceFor = (_, _) => 100 };
        restored.Import(engine.Export());
        Assert.Equal(7, restored.RequestsInWindow(Provider("a"), "m", clock.Now));
    }

    [Theory]
    [InlineData(3.5, true)]
    [InlineData(4.5, false)]
    public void MemoryOlderThanFourHoursIsDroppedAndRebuilt(double hoursOld, bool kept)
    {
        // Used all day, restarted next morning: yesterday's placements are discarded.
        var clock = new Clock();
        var engine = new RouterEngine(clock);
        engine.SetSticky("chat", "a\0m");
        var store = new RouterMemoryStore(FilePath);
        store.Save(engine.Export());

        var loaded = store.Load(clock.Now + TimeSpan.FromHours(hoursOld));

        Assert.Equal(kept, loaded is not null);
        Assert.Equal(kept, File.Exists(FilePath));   // a stale file is deleted, not kept around
    }

    [Fact]
    public void PruneDropsStaleEntriesWithoutWaitingForCapacity()
    {
        // An all-day session never reaches the 10k cap; stale entries must still go.
        var clock = new Clock();
        var engine = new RouterEngine(clock);
        engine.SetSticky("old", "a\0m");
        engine.TouchIsWarm("old");
        engine.MarkFailed(Provider("a"), "m");
        engine.MarkProviderBusy(Provider("b"), TimeSpan.FromMinutes(1));
        clock.Now += RouterEngine.MaxHold + TimeSpan.FromMinutes(1);
        engine.SetSticky("live", "b\0m");

        engine.Prune();

        Assert.Equal(1, engine.TableSizes.Sticky);
        Assert.Equal(0, engine.TableSizes.LastUsed);
        Assert.Equal((0, 0, 0), engine.ShortLivedTableSizes);
    }

    [Fact]
    public void AMissingOrCorruptFileMeansStartingFresh()
    {
        Assert.Null(new RouterMemoryStore(FilePath).Load());
        Directory.CreateDirectory(_dir);
        File.WriteAllText(FilePath, "{not json");
        Assert.Null(new RouterMemoryStore(FilePath).Load());
    }

    [Fact]
    public async Task TheServerSavesOnStopAndLoadsOnStart()
    {
        var store = new RouterMemoryStore(FilePath);
        var first = new RelayServer(new RelayTelemetryStore(), port: 0, routerMemory: store);
        first.RouterEngine.SetSticky("r|chat", "a\0m");
        await first.StartAsync();
        await first.StopAsync();
        first.Dispose();
        Assert.True(File.Exists(FilePath));

        using var second = new RelayServer(new RelayTelemetryStore(), port: 0, routerMemory: store);
        Assert.Equal("a\0m", second.RouterEngine.StickyFor("r|chat"));
    }
}
