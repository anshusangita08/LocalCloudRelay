using System.Text;
using LocalCloudRelay;

namespace LocalCloudRelay.Tests;

/// <summary>
/// A long conversation must not be routed to a model whose context window cannot hold
/// it; that only ends in a 400 the client has to recover from.
/// </summary>
[Collection("PricingState")]
public sealed class ContextFitTests : IDisposable
{
    private readonly LivePricing? _previous = LivePricing.Current;

    private const string Sample = """
        {"zen":{"api":"https://zen.example/v1","models":{
          "small":{"cost":{"input":1,"output":2},"limit":{"context":8000,"output":2000}},
          "large":{"cost":{"input":3,"output":6},"limit":{"context":200000}},
          "unknown":{"cost":{"input":1,"output":1}}}}}
        """;

    public ContextFitTests() => LivePricing.Use(LivePricing.Parse(Sample, DateTimeOffset.UtcNow));

    public void Dispose() => LivePricing.Use(_previous);

    private static readonly ProviderSettings Zen = new("zen", "Zen", ProviderKinds.OpenAi, "https://zen.example/v1", "k", true, 0);

    private static ProviderRouter Router(string strategy, params string[] pool) => new(
        [Zen],
        new CatalogSnapshot(DateTimeOffset.UtcNow, new Dictionary<string, IReadOnlyList<string>> { ["zen"] = ["small", "large", "unknown"] }),
        [RouterRule.Create("r", strategy, pool)]);

    [Fact]
    public void TheContextWindowIsReadFromModelsDev()
    {
        Assert.Equal(8000, ModelPricingTable.For(Zen, "small")!.ContextTokens);
        Assert.Null(ModelPricingTable.For(Zen, "unknown")!.ContextTokens);
    }

    [Fact]
    public void AModelTooSmallForTheRequestIsSkipped()
    {
        // Free first would pick the cheapest, "small"; at 50k tokens it cannot fit.
        var router = Router(RouterStrategies.FreeFirst, "small", "large");
        Assert.True(router.TryResolve("r", out var route, new RouterEngine(), "s", null, null, promptTokens: 50_000));
        Assert.Equal("large", route.Model);

        Assert.True(router.TryResolve("r", out var shortRoute, new RouterEngine(), "s2", null, null, promptTokens: 1_000));
        Assert.Equal("small", shortRoute.Model);
    }

    [Fact]
    public void UnknownWindowsStayInAndNothingFittingLeavesThePoolAlone()
    {
        var router = Router(RouterStrategies.FreeFirst, "small", "unknown");
        Assert.True(router.TryResolve("r", out var route, new RouterEngine(), "s", null, null, promptTokens: 50_000));
        Assert.Equal("unknown", route.Model);

        var tiny = Router(RouterStrategies.FreeFirst, "small");
        Assert.True(tiny.TryResolve("r", out var forced, new RouterEngine(), "s", null, null, promptTokens: 500_000));
        Assert.Equal("small", forced.Model);
    }

    [Fact]
    public void ThePromptEstimateCountsTextAndIgnoresImagePayloads()
    {
        var text = new string('x', 40_000);
        var plain = Encoding.UTF8.GetBytes($$"""{"model":"m","messages":[{"role":"user","content":"{{text}}"}]}""");
        Assert.InRange(RelaySession.EstimatePromptTokens(plain)!.Value, 10_000, 10_010);

        var base64 = new string('A', 1_000_000);
        var withImage = Encoding.UTF8.GetBytes($$$"""
            {"messages":[{"role":"user","content":[
              {"type":"image","source":{"type":"base64","media_type":"image/png","data":"{{{base64}}}"}},
              {"type":"text","text":"what is this"}]}]}
            """);
        var estimate = RelaySession.EstimatePromptTokens(withImage)!.Value;
        Assert.InRange(estimate, 1_600, 2_000);       // one image, not 250k tokens of base64
        Assert.Null(RelaySession.EstimatePromptTokens(null));
    }
}
