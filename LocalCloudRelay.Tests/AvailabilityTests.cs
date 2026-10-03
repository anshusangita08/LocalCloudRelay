using LocalCloudRelay;

namespace LocalCloudRelay.Tests;

/// <summary>
/// One check decides whether a model takes traffic: a failure, a provider rate limit or
/// low headroom, or a used-up request allowance all take it out of rotation.
/// </summary>
public sealed class AvailabilityTests
{
    private static readonly ProviderSettings Go =
        new("go", "OpenCode Go", ProviderKinds.OpenAi, "https://opencode.ai/zen/go/v1", "k", true, 0);

    [Fact]
    public void AModelStepsAsideAtNinetyPercentOfItsFiveHourAllowanceAndReturnsAfterTheWindow()
    {
        var clock = new Clock();
        var engine = new RouterEngine(clock) { AllowanceFor = (_, _) => 10 };

        for (var i = 0; i < 8; i++) engine.RecordRequest(Go, "glm-5.1");
        Assert.Null(engine.Unavailable(Go, "glm-5.1"));

        engine.RecordRequest(Go, "glm-5.1");
        Assert.Contains("9 of 10", engine.Unavailable(Go, "glm-5.1"), StringComparison.Ordinal);

        clock.Now += RouterEngine.AllowanceWindow;
        Assert.Null(engine.Unavailable(Go, "glm-5.1"));
    }

    [Fact]
    public void ModelsWithoutAnAllowanceAreNotCounted()
    {
        var engine = new RouterEngine { AllowanceFor = (_, _) => null };

        for (var i = 0; i < 1000; i++) engine.RecordRequest(Go, "glm-5.1");

        Assert.Null(engine.Unavailable(Go, "glm-5.1"));
        Assert.Equal(0, engine.RequestsInWindow(Go, "glm-5.1", engine.Now));
    }

    [Fact]
    public void EveryReasonIsReportedByTheSameCheck()
    {
        var engine = new RouterEngine { AllowanceFor = (_, _) => null };

        engine.MarkFailed(Go, "glm-5.1");
        Assert.Contains("failed", engine.Unavailable(Go, "glm-5.1"), StringComparison.Ordinal);
        Assert.Null(engine.Unavailable(Go, "kimi-k3"));

        engine.MarkProviderBusy(Go, TimeSpan.FromMinutes(1));
        Assert.Contains("rate-limited", engine.Unavailable(Go, "kimi-k3"), StringComparison.Ordinal);
        Assert.True(engine.IsCooling(Go, "kimi-k3"));
    }

    [Fact]
    public void ARouterSkipsAModelWhoseAllowanceIsUsedUp()
    {
        var router = new ProviderRouter([Go],
            new CatalogSnapshot(DateTimeOffset.UtcNow, new Dictionary<string, IReadOnlyList<string>> { ["go"] = ["glm-5.1", "kimi-k3"] }),
            [RouterRule.Create("go", RouterStrategies.Sticky, ["glm-5.1", "kimi-k3"])]);
        var engine = new RouterEngine { AllowanceFor = (_, model) => model == "glm-5.1" ? 1 : null };
        engine.RecordRequest(Go, "glm-5.1");

        for (var i = 0; i < 4; i++)
        {
            Assert.True(router.TryResolve("go", out var route, engine, $"chat-{i}"));
            Assert.Equal("kimi-k3", route.Model);
        }
    }

    [Theory]
    [InlineData("Claude AI usage limit reached|1759500000", true)]
    [InlineData("429 Too Many Requests", true)]
    [InlineData("RESOURCE_EXHAUSTED: quota exceeded", true)]
    [InlineData("Overloaded", true)]
    [InlineData("prompt is too long: context length limit reached", false)]
    [InlineData("invalid model id", false)]
    public void CliErrorTextIsRecognisedAsALimitOnlyWhenItSaysSo(string text, bool limit) =>
        Assert.Equal(limit, RateLimitSignal.LooksLikeLimit(text));

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 10, 3, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
