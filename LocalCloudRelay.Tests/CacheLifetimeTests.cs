using System.Text;
using LocalCloudRelay;

namespace LocalCloudRelay.Tests;

/// <summary>
/// A conversation that asks Anthropic for a one-hour cache keeps that cache for an hour,
/// so the router must not treat it as cold after five idle minutes.
/// </summary>
public sealed class CacheLifetimeTests
{
    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 10, 3, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }

    private static byte[] Utf8(string json) => Encoding.UTF8.GetBytes(json);

    [Fact]
    public void TheLongestRequestedTtlIsFound()
    {
        var body = Utf8("""
            {"system":[{"type":"text","text":"s","cache_control":{"type":"ephemeral","ttl":"1h"}}],
             "messages":[{"role":"user","content":[{"type":"text","text":"q","cache_control":{"type":"ephemeral","ttl":"5m"}}]}]}
            """);
        Assert.Equal(TimeSpan.FromHours(1), RelaySession.CacheLifetime(body));
    }

    [Fact]
    public void NoTtlMeansTheDefault()
    {
        Assert.Null(RelaySession.CacheLifetime(Utf8("""{"messages":[{"role":"user","content":[{"type":"text","text":"q","cache_control":{"type":"ephemeral"}}]}]}""")));
        Assert.Null(RelaySession.CacheLifetime(Utf8("""{"cache_control":{"ttl":"forever"}}""")));
        Assert.Null(RelaySession.CacheLifetime(Utf8("not json with \"ttl\"")));
        Assert.Null(RelaySession.CacheLifetime(null));
    }

    [Fact]
    public void AnHourLongCacheStaysWarmAndIsRemembered()
    {
        var clock = new Clock();
        var engine = new RouterEngine(clock);
        Assert.False(engine.TouchIsWarm("k", TimeSpan.FromHours(1)));
        clock.Advance(TimeSpan.FromMinutes(30));
        Assert.True(engine.TouchIsWarm("k"));            // a later request without ttl still reads the 1h entry
        clock.Advance(TimeSpan.FromMinutes(61));
        Assert.False(engine.TouchIsWarm("k"));

        var plain = new RouterEngine(clock);
        plain.TouchIsWarm("p");
        clock.Advance(TimeSpan.FromMinutes(6));
        Assert.False(plain.TouchIsWarm("p"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void RoundRobinKeepsAnHourCacheConversationAfterALongPause(bool hourCache)
    {
        var a = new ProviderSettings("a", "a", ProviderKinds.Anthropic, "https://a.example", "k", true, 0);
        var b = new ProviderSettings("b", "b", ProviderKinds.Anthropic, "https://b.example", "k", true, 1);
        var router = new ProviderRouter([a, b],
            new CatalogSnapshot(DateTimeOffset.UtcNow, new Dictionary<string, IReadOnlyList<string>> { ["a"] = ["m"], ["b"] = ["m"] }),
            [RouterRule.Create("rr", RouterStrategies.RoundRobin, ["m"])]);
        var clock = new Clock();
        var engine = new RouterEngine(clock);
        TimeSpan? ttl = hourCache ? TimeSpan.FromHours(1) : null;

        Assert.True(router.TryResolve("rr", out var first, engine, "chat", "t1", ttl));
        clock.Advance(TimeSpan.FromMinutes(15));
        Assert.True(router.TryResolve("rr", out var later, engine, "chat", "t2", ttl));

        Assert.Equal(hourCache, first.Provider.Id == later.Provider.Id);
    }
}
