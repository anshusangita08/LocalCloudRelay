using LocalCloudRelay;

namespace LocalCloudRelay.Tests;

/// <summary>
/// Among equally ranked models, the one with more of its request allowance left goes
/// first, so load spreads before any model reaches its limit.
/// </summary>
public sealed class HeadroomTests
{
    private static readonly ProviderSettings A = new("a", "A", ProviderKinds.OpenAi, "https://a.example/v1", "k", true, 0);
    private static readonly ProviderSettings B = new("b", "B", ProviderKinds.OpenAi, "https://b.example/v1", "k", true, 0);

    [Fact]
    public void HeadroomIsTheUnusedShareOfTheAllowance()
    {
        var engine = new RouterEngine { AllowanceFor = (_, _) => 10 };
        Assert.Equal(1.0, engine.Headroom(A, "m"));
        for (var i = 0; i < 4; i++) engine.RecordRequest(A, "m");
        Assert.Equal(0.6, engine.Headroom(A, "m"), 3);
        Assert.Equal(1.0, new RouterEngine().Headroom(A, "m"));   // no allowance, no limit
    }

    [Fact]
    public void AnEquallyPricedModelWithMoreHeadroomIsPreferred()
    {
        // The same unpriced model on two providers of equal priority: without headroom the
        // id and priority tie, and A (first) would always win.
        var engine = new RouterEngine { AllowanceFor = (_, _) => 100 };
        for (var i = 0; i < 50; i++) engine.RecordRequest(A, "m");
        var router = new ProviderRouter([A, B],
            new CatalogSnapshot(DateTimeOffset.UtcNow, new Dictionary<string, IReadOnlyList<string>> { ["a"] = ["m"], ["b"] = ["m"] }),
            [RouterRule.Create("r", RouterStrategies.Premium, ["m"])]);

        Assert.True(router.TryResolve("r", out var route, engine, "chat"));
        Assert.Equal("b", route.Provider.Id);
    }
}
