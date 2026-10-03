using System.Net;
using LocalCloudRelay;

namespace LocalCloudRelay.Tests;

/// <summary>
/// A model that keeps failing is tried less and less often, a provider's Retry-After is
/// believed, and a refused key or sign-in stays out until the operator acts.
/// </summary>
public sealed class BackoffTests
{
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 10, 3, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static readonly ProviderSettings Zen = new("zen", "Zen", ProviderKinds.OpenAi, "https://zen.example/v1", "k", true, 0);

    private static bool Resting(RouterEngine engine, string model = "m") => engine.Unavailable(Zen, model) is not null;

    [Fact]
    public void RestsDoubleWithEachFailureInARowAndResetOnSuccess()
    {
        var clock = new Clock();
        var engine = new RouterEngine(clock);
        foreach (var expected in new[] { 1, 2, 4, 8 })
        {
            engine.MarkFailed(Zen, "m");
            clock.Now += TimeSpan.FromMinutes(expected) - TimeSpan.FromSeconds(1);
            Assert.True(Resting(engine));
            clock.Now += TimeSpan.FromSeconds(2);
            Assert.False(Resting(engine));
        }

        engine.MarkSucceeded(Zen, "m");
        engine.MarkFailed(Zen, "m");
        clock.Now += TimeSpan.FromSeconds(61);
        Assert.False(Resting(engine));                       // back to the first step
    }

    [Fact]
    public void RestsStopAtTheCap()
    {
        var clock = new Clock();
        var engine = new RouterEngine(clock);
        for (var i = 0; i < 12; i++) engine.MarkFailed(Zen, "m");
        clock.Now += RouterEngine.MaxCooldown + TimeSpan.FromSeconds(1);
        Assert.False(Resting(engine));
    }

    [Fact]
    public void RetryAfterWins()
    {
        var clock = new Clock();
        var engine = new RouterEngine(clock);
        engine.MarkFailed(Zen, "m", TimeSpan.FromSeconds(5));
        clock.Now += TimeSpan.FromSeconds(6);
        Assert.False(Resting(engine));
    }

    [Fact]
    public void ARefusedProviderStaysOutUntilCleared()
    {
        var clock = new Clock();
        var engine = new RouterEngine(clock);
        engine.MarkUnusable(Zen, "key or sign-in was refused (401)");
        clock.Now += TimeSpan.FromHours(3);
        Assert.Contains("401", engine.Unavailable(Zen, "any-model"));
        engine.ClearUnusable("zen");
        Assert.Null(engine.Unavailable(Zen, "any-model"));
    }

    [Fact]
    public void AForbiddenModelDoesNotTakeTheProviderDown()
    {
        // OpenCode's free tier answers 403 for its gated models; paid models still work.
        var engine = new RouterEngine();
        engine.MarkUnusable(Zen, "access was refused (403)", "big-pickle-free");
        Assert.NotNull(engine.Unavailable(Zen, "big-pickle-free"));
        Assert.Null(engine.Unavailable(Zen, "kimi-k3"));
        engine.ClearUnusable();
        Assert.Null(engine.Unavailable(Zen, "big-pickle-free"));
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, true)]
    [InlineData(HttpStatusCode.PaymentRequired, true)]
    [InlineData(HttpStatusCode.Forbidden, true)]
    [InlineData(HttpStatusCode.ServiceUnavailable, false)]
    [InlineData(HttpStatusCode.TooManyRequests, false)]
    public void OnlyRefusalsAndMissingCreditAreUnusable(HttpStatusCode status, bool unusable) =>
        Assert.Equal(unusable, RelayServer.UnusableReason(status) is not null);

    [Fact]
    public void RetryAfterIsReadAsDeltaOrDate()
    {
        using var delta = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        delta.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(30));
        Assert.Equal(TimeSpan.FromSeconds(30), RelayServer.RetryAfter(delta));

        using var none = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        Assert.Null(RelayServer.RetryAfter(none));
    }
}
