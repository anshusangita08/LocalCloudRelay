using LocalCloudRelay;

namespace LocalCloudRelay.Tests;

/// <summary>
/// The engine remembers which model each conversation is on. Hitting its capacity must
/// not make every live conversation forget its model and lose its prompt cache.
/// </summary>
public sealed class RouterMemoryTests
{
    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 10, 3, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }

    [Fact]
    public void OverCapacityDropsTheLeastRecentlyUsedNotEverything()
    {
        var clock = new Clock();
        var engine = new RouterEngine(clock) { StickyCapacity = 3 };
        engine.SetSticky("a", "m"); clock.Advance(TimeSpan.FromSeconds(1));
        engine.SetSticky("b", "m"); clock.Advance(TimeSpan.FromSeconds(1));
        engine.SetSticky("c", "m"); clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal("m", engine.StickyFor("a"));   // "a" is now the most recently used
        clock.Advance(TimeSpan.FromSeconds(1));
        engine.SetSticky("d", "m");

        Assert.Equal("m", engine.StickyFor("a"));
        Assert.Null(engine.StickyFor("b"));         // oldest use goes first
        Assert.Equal("m", engine.StickyFor("c"));
        Assert.Equal("m", engine.StickyFor("d"));
    }

    [Fact]
    public void IdleConversationsAreDroppedBeforeActiveOnes()
    {
        var clock = new Clock();
        var engine = new RouterEngine(clock) { StickyCapacity = 2 };
        engine.SetSticky("old", "m");
        clock.Advance(RouterEngine.MaxHold + TimeSpan.FromMinutes(1));
        engine.SetSticky("live-1", "m");
        engine.SetSticky("live-2", "m");

        Assert.Null(engine.StickyFor("old"));
        Assert.Equal("m", engine.StickyFor("live-1"));
        Assert.Equal("m", engine.StickyFor("live-2"));
    }

    private static ProviderSettings Provider(string id) =>
        new(id, id, ProviderKinds.OpenAi, $"https://{id}.example/v1", "k", true, 0);

    private static ProviderRouter Router(string strategy, params (ProviderSettings Provider, string[] Models)[] entries)
    {
        var catalog = new CatalogSnapshot(DateTimeOffset.UtcNow,
            entries.ToDictionary(e => e.Provider.Id, e => (IReadOnlyList<string>)e.Models, StringComparer.Ordinal));
        return new ProviderRouter(entries.Select(e => e.Provider), catalog,
            [RouterRule.Create("r", strategy, ["shared"])]);
    }

    [Fact]
    public void StickyStaysOnTheSameProviderNotJustTheSameModelId()
    {
        var a = Provider("a");
        var b = Provider("b");
        var router = Router(RouterStrategies.Sticky, (a, ["shared"]), (b, ["shared"]));
        var engine = new RouterEngine();

        Assert.True(router.TryResolve("r", out var first, engine, "chat"));
        for (var i = 0; i < 5; i++)
        {
            Assert.True(router.TryResolve("r", out var again, engine, "chat"));
            Assert.Equal(first.Provider.Id, again.Provider.Id);
        }
    }

    [Fact]
    public void StickyMovesToTheSameModelElsewhereWhenItsProviderFails()
    {
        var a = Provider("a");
        var b = Provider("b");
        var router = Router(RouterStrategies.Sticky, (a, ["shared"]), (b, ["shared"]));
        var engine = new RouterEngine();

        Assert.True(router.TryResolve("r", out var first, engine, "chat"));
        engine.MarkFailed(first.Provider, "shared");
        Assert.True(router.TryResolve("r", out var moved, engine, "chat"));
        Assert.NotEqual(first.Provider.Id, moved.Provider.Id);
        engine.MarkSucceeded(first.Provider, "shared");
        Assert.True(router.TryResolve("r", out var stays, engine, "chat"));
        Assert.Equal(moved.Provider.Id, stays.Provider.Id);   // no bounce back to a cold cache
    }

    [Fact]
    public void RoundRobinWithoutATurnKeyKeepsTheConversationInPlace()
    {
        var router = Router(RouterStrategies.RoundRobin, (Provider("a"), ["shared"]), (Provider("b"), ["shared"]));
        var engine = new RouterEngine();

        Assert.True(router.TryResolve("r", out var first, engine, "chat", null));
        for (var i = 0; i < 4; i++)
        {
            Assert.True(router.TryResolve("r", out var again, engine, "chat", null));
            Assert.Equal(first.Provider.Id, again.Provider.Id);
        }
    }

    [Fact]
    public void SideTablesAreTrimmedWithTheStickyMap()
    {
        var clock = new Clock();
        var provider = new ProviderSettings("p", "p", ProviderKinds.OpenAi, "https://p.example/v1", "k", true, 0);
        var engine = new RouterEngine(clock) { StickyCapacity = 1, AllowanceFor = (_, _) => 100 };
        engine.TouchIsWarm("gone");
        engine.SetHeldSince("gone");
        engine.SetSticky("gone", "m");
        engine.RecordRequest(provider, "m");

        clock.Advance(RouterEngine.AllowanceWindow + TimeSpan.FromMinutes(1));
        engine.SetSticky("new-1", "m");
        engine.SetSticky("new-2", "m");

        var sizes = engine.TableSizes;
        Assert.Equal(1, sizes.Sticky);
        Assert.Equal(0, sizes.LastUsed);
        Assert.Equal(0, sizes.HeldSince);
        Assert.Equal(0, sizes.Requests);
    }
}
