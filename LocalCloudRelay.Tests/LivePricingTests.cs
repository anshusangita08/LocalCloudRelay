using System.Text.Json;
using LocalCloudRelay;
using Xunit;

namespace LocalCloudRelay.Tests;

/// <summary>
/// Live prices come from models.dev, matched to a provider by the base URL that
/// database states for it. These pin the parsing and the matching, because a wrong
/// match would print another plan's rates as if they were yours.
/// </summary>
[Collection("PricingState")]
public sealed class LivePricingTests
{
    private const string Sample = """
    {
      "opencode": {
        "id": "opencode",
        "name": "OpenCode Zen",
        "api": "https://opencode.ai/zen/v1",
        "models": {
          "gpt-5.4": { "id": "gpt-5.4", "cost": { "input": 2.5, "output": 15, "cache_read": 0.25 } },
          "no-cost-model": { "id": "no-cost-model" },
          "free-model": { "id": "free-model", "cost": { "input": 0, "output": 0, "cache_read": 0 } },
          "partial": { "id": "partial", "cost": { "input": 1 } }
        }
      },
      "opencode-go": {
        "id": "opencode-go",
        "name": "OpenCode Go",
        "api": "https://opencode.ai/zen/go/v1",
        "models": {
          "gpt-5.4": { "id": "gpt-5.4", "cost": { "input": 9, "output": 90 } }
        }
      },
      "no-api": { "id": "no-api", "name": "No API field", "models": { "x": { "cost": { "input": 1 } } } }
    }
    """;

    private static LivePricing Parsed() => LivePricing.Parse(Sample, DateTimeOffset.UtcNow);

    [Fact]
    public void TheSameModelIdIsPricedPerPlan()
    {
        // The reason matching is by base URL and not by model name. gpt-5.4 costs
        // different amounts on Zen and Go, so a name-only lookup would report one
        // plan's rates for a request that went to the other.
        var pricing = Parsed();

        Assert.Equal(2.5m, pricing.For("https://opencode.ai/zen/v1", "gpt-5.4")!.InputPerMillion);
        Assert.Equal(9m, pricing.For("https://opencode.ai/zen/go/v1", "gpt-5.4")!.InputPerMillion);
    }

    [Fact]
    public void ProvidersThatDoNotStateAnApiUrlAreNeverMatchedByUrl()
    {
        // No base URL means no way to match it to a configured gateway, and guessing
        // from the name is how you get another plan's prices. It is kept only under its
        // vendor key, for account providers that call that vendor directly.
        var pricing = Parsed();

        Assert.Equal(3, pricing.ProviderCount);
        Assert.Null(pricing.For("https://no-api.example/v1", "x"));
    }

    [Fact]
    public void AModelWithNoCostBlockStaysUnpriced()
    {
        // Absent is not zero. A zero would read as free, which is a claim.
        var pricing = Parsed();

        Assert.Null(pricing.For("https://opencode.ai/zen/v1", "no-cost-model"));
        Assert.Null(pricing.For("https://opencode.ai/zen/v1", "not-a-model-at-all"));
    }

    [Fact]
    public void AnExplicitZeroIsKeptAsFree()
    {
        // Unlike an absent cost, a published 0/0 is the provider saying it is free.
        var price = Parsed().For("https://opencode.ai/zen/v1", "free-model");

        Assert.NotNull(price);
        Assert.Equal(0m, price!.InputPerMillion);
        Assert.True(price.IsFree);
    }

    [Fact]
    public void AHalfSpecifiedCostKeepsWhatItHasAndLeavesTheRestBlank()
    {
        var price = Parsed().For("https://opencode.ai/zen/v1", "partial");

        Assert.NotNull(price);
        Assert.Equal(1m, price!.InputPerMillion);
        Assert.Null(price.OutputPerMillion);
    }

    [Fact]
    public void ABaseUrlMatchesDespiteATrailingSlashOrSchemeDifference()
    {
        var pricing = Parsed();

        Assert.NotNull(pricing.For("https://opencode.ai/zen/v1/", "gpt-5.4"));
        Assert.NotNull(pricing.For("https://opencode.ai/zen/v1", "GPT-5.4"));   // id case
        // http vs https: the host and path still identify the plan.
        Assert.NotNull(pricing.For("http://opencode.ai/zen/v1", "gpt-5.4"));
        // A different path is a different plan and must not match.
        Assert.Null(pricing.For("https://opencode.ai/zen/v2", "gpt-5.4"));
    }

    [Fact]
    public void MalformedOrEmptyInputYieldsNoPricesRatherThanThrowing()
    {
        Assert.Equal(0, LivePricing.Parse("{}", DateTimeOffset.UtcNow).ModelCount);
        Assert.Equal(0, LivePricing.Parse("[]", DateTimeOffset.UtcNow).ModelCount);

        var pricing = Parsed();
        Assert.Null(pricing.For(null, "gpt-5.4"));
        Assert.Null(pricing.For("   ", "gpt-5.4"));
        Assert.Null(pricing.For("https://opencode.ai/zen/v1", null));
        Assert.Null(pricing.For("https://opencode.ai/zen/v1", ""));
    }

    [Fact]
    public void TheCachedProjectionRoundTripsWithItsPricesIntact()
    {
        // The cache is what makes a restart and an offline start show real prices, so a
        // serialization bug here would silently empty every price column.
        var path = Path.Combine(Path.GetTempPath(), "pricing-" + Guid.NewGuid().ToString("N")[..8] + ".json");
        try
        {
            var original = Parsed();
            var pricing = typeof(LivePricing)
                .GetMethod("Save", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
            pricing.Invoke(null, [path, original]);

            // LoadCached sets Current as a side effect, so read the returned value.
            var loaded = LivePricing.LoadCached(path);

            Assert.NotNull(loaded);
            Assert.Equal(original.ModelCount, loaded!.ModelCount);
            Assert.Equal(2.5m, loaded.For("https://opencode.ai/zen/v1", "gpt-5.4")!.InputPerMillion);
            Assert.Equal(9m, loaded.For("https://opencode.ai/zen/go/v1", "gpt-5.4")!.InputPerMillion);
            Assert.Null(loaded.For("https://opencode.ai/zen/v1", "no-cost-model"));
        }
        finally
        {
            LivePricing.Use(null);
            File.Delete(path);
        }
    }

    [Fact]
    public void ALoadedCacheIsWhatThePriceLookupsActuallyUse()
    {
        // The startup path in one assertion: load the cache, then ask the price table.
        // This is what makes the grid show current rates on launch, without pressing
        // Update prices - and it must beat the built-in snapshot, which has drifted.
        var path = Path.Combine(Path.GetTempPath(), "pricing-" + Guid.NewGuid().ToString("N")[..8] + ".json");
        try
        {
            var go = new ProviderSettings("go", "OpenCode Go", ProviderKinds.OpenAi, "https://opencode.ai/zen/go/v1", "k", true, 0);

            // Before loading anything, the lookup comes from the built-in snapshot.
            LivePricing.Use(null);
            var before = ModelPricingTable.For(go, "deepseek-v4-pro");
            Assert.Equal(1.65m, before!.InputPerMillion);

            var save = typeof(LivePricing)
                .GetMethod("Save", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
            save.Invoke(null, [path, LivePricing.Parse("""
            { "opencode-go": { "api": "https://opencode.ai/zen/go/v1",
              "models": { "deepseek-v4-pro": { "cost": { "input": 0.66, "output": 1.98 } } } } }
            """, DateTimeOffset.UtcNow)]);

            Assert.NotNull(LivePricing.LoadCached(path));

            // And now it comes from the live database instead.
            var after = ModelPricingTable.For(go, "deepseek-v4-pro");
            Assert.Equal(0.66m, after!.InputPerMillion);
            Assert.Equal(1.98m, after.OutputPerMillion);
        }
        finally
        {
            LivePricing.Use(null);
            File.Delete(path);
        }
    }

    [Fact]
    public void AProviderWithNoLiveMatchStillFallsBackAndAnUnknownOneStaysBlank()
    {
        var path = Path.Combine(Path.GetTempPath(), "pricing-" + Guid.NewGuid().ToString("N")[..8] + ".json");
        try
        {
            var save = typeof(LivePricing)
                .GetMethod("Save", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
            save.Invoke(null, [path, Parsed()]);
            LivePricing.LoadCached(path);

            // Zen is in the live data, so it is priced from it.
            var zen = new ProviderSettings("z", "Zen", ProviderKinds.OpenAi, "https://opencode.ai/zen/v1", "k", true, 0);
            Assert.Equal(2.5m, ModelPricingTable.For(zen, "gpt-5.4")!.InputPerMillion);

            // A gateway nobody publishes: blank, not zero.
            var privateGateway = new ProviderSettings("p", "Private", ProviderKinds.OpenAi, "https://llm.corp.internal/v1", "k", true, 0);
            Assert.Null(ModelPricingTable.For(privateGateway, "some-model"));

            // A local server is free, and that is a fact rather than a lookup.
            var local = new ProviderSettings("l", "Ollama", ProviderKinds.Local, "http://127.0.0.1:11434/v1", null, true, 0);
            Assert.True(ModelPricingTable.For(local, "llama3")!.IsFree);
        }
        finally
        {
            LivePricing.Use(null);
            File.Delete(path);
        }
    }

    [Fact]
    public void TheGoPlanAllowanceTableIsReadAndJoinedToModelIds()
    {
        // The docs page states the allowance table by display name ("GLM-5.3-Flash") and
        // the model ids in a separate table. Reading either alone is not enough: the rows
        // would not join to anything the relay can route.
        var limits = LivePricing.ParseGoPlanLimits(GoDocs);

        Assert.Equal(60m, limits["glm-5.3-flash"]);
        Assert.Equal(15m, limits["glm-5.3"]);
        Assert.Equal(15m, limits["kimi-k3"]);
        Assert.Equal(3, limits.Count);
    }

    [Fact]
    public void TheBaseGoPlanIsTakenNotGoPlus()
    {
        // The page carries both plans in tabs and Go Plus allows roughly 3x more. Reading
        // the wrong one would tell someone they have triple the budget they have.
        var limits = LivePricing.ParseGoPlanLimits(GoDocs);

        // Go Plus publishes 180 for GLM-5.3-Flash; the base Go plan publishes 60.
        Assert.Equal(60m, limits["glm-5.3-flash"]);
        Assert.DoesNotContain(180m, limits.Values);
    }

    [Fact]
    public void ADocsPageThatChangedShapeYieldsNothingRatherThanWrongNumbers()
    {
        // A parse failure has to be inert: the caller keeps the snapshot allowances, and
        // the column keeps showing what it showed before.
        Assert.Empty(LivePricing.ParseGoPlanLimits(""));
        Assert.Empty(LivePricing.ParseGoPlanLimits("<html><body>no tables here</body></html>"));
        Assert.Empty(LivePricing.ParseGoPlanLimits("<table><tr><th>Wrong</th></tr><tr><td>1</td></tr></table>"));

        // A limits table with no id table cannot be joined, so nothing is claimed.
        Assert.Empty(LivePricing.ParseGoPlanLimits("""
        <table><tr><th>Model</th><th>Input</th><th>Output</th><th>Cached Read</th><th>Cached Write</th><th>Monthly limit</th></tr>
        <tr><td>GLM-5.3</td><td>$1.40</td><td>$4.40</td><td>$0.26</td><td>-</td><td>$15</td></tr></table>
        """));
    }

    /// <summary>
    /// The two tables that matter, in the shape the page actually publishes them:
    /// the Go panel first, then Go Plus, plus the id table the allowances join through.
    /// </summary>
    private const string GoDocs = """
    <table><thead><tr><th>Plan</th><th>Price</th><th>Included usage</th></tr></thead><tbody>
    <tr><td><strong>Go</strong></td><td><strong>$10/month</strong></td><td>Lower-cost access</td></tr></tbody></table>
    <div id="tab-panel-0"><table><thead><tr><th>Model</th><th>Input</th><th>Output</th><th>Cached Read</th><th>Cached Write</th><th>Monthly limit</th></tr></thead><tbody>
    <tr><td>GLM-5.3-Flash</td><td>$0.15</td><td>$0.50</td><td>$0.03</td><td>-</td><td>$60</td></tr>
    <tr><td>GLM-5.3</td><td>$1.40</td><td>$4.40</td><td>$0.26</td><td>-</td><td>$15</td></tr>
    <tr><td>Kimi K3</td><td>$3.00</td><td>$15.00</td><td>$0.30</td><td>-</td><td>$15</td></tr></tbody></table></div>
    <div id="tab-panel-1"><table><thead><tr><th>Model</th><th>Input</th><th>Output</th><th>Cached Read</th><th>Cached Write</th><th>Monthly limit</th></tr></thead><tbody>
    <tr><td>GLM-5.3-Flash</td><td>$0.15</td><td>$0.50</td><td>$0.03</td><td>-</td><td>$180</td></tr>
    <tr><td>GLM-5.3</td><td>$1.40</td><td>$4.40</td><td>$0.26</td><td>-</td><td>$120</td></tr>
    <tr><td>Kimi K3</td><td>$3.00</td><td>$15.00</td><td>$0.30</td><td>-</td><td>$60</td></tr></tbody></table></div>
    <table><thead><tr><th>Model</th><th>Model ID</th><th>Endpoint</th><th>AI SDK Package</th></tr></thead><tbody>
    <tr><td>GLM-5.3-Flash</td><td>glm-5.3-flash</td><td>https://opencode.ai/zen/go/v1/responses</td><td>@ai-sdk/openai</td></tr>
    <tr><td>GLM-5.3</td><td>glm-5.3</td><td>https://opencode.ai/zen/go/v1/responses</td><td>@ai-sdk/openai</td></tr>
    <tr><td>Kimi K3</td><td>kimi-k3</td><td>https://opencode.ai/zen/go/v1/responses</td><td>@ai-sdk/openai</td></tr></tbody></table>
    """;

    [Fact]
    public void AVariantSuffixStillJoinsToTheBaseModelId()
    {
        // The page writes "Grok 4.7 (> 200K tokens)" and "DeepSeek V4 Pro (Peak)" in the
        // allowance table while the id table lists the bare name. Dropping the suffix is
        // safe because every variant pair publishes the same monthly limit.
        var limits = LivePricing.ParseGoPlanLimits(GoDocsWithVariants);

        Assert.Equal(15m, limits["grok-4.7"]);
        Assert.Equal(15m, limits["deepseek-v4-pro"]);
        Assert.Equal(15m, limits["gpt-6-luna"]);
        Assert.Equal(30m, limits["deepseek-v4-flash"]);
    }

    private const string GoDocsWithVariants = """
    <table><thead><tr><th>Model</th><th>Input</th><th>Output</th><th>Cached Read</th><th>Cached Write</th><th>Monthly limit</th></tr></thead><tbody>
    <tr><td>Grok 4.7 (&lt; 200K tokens)</td><td>$2.00</td><td>$6.00</td><td>-</td><td>-</td><td>$15</td></tr>
    <tr><td>Grok 4.7 (&gt; 200K tokens)</td><td>$4.00</td><td>$12.00</td><td>-</td><td>-</td><td>$15</td></tr>
    <tr><td>DeepSeek V4 Pro (Off-Peak)</td><td>$0.66</td><td>$1.98</td><td>-</td><td>-</td><td>$15</td></tr>
    <tr><td>DeepSeek V4 Pro (Peak)</td><td>$1.32</td><td>$3.96</td><td>-</td><td>-</td><td>$15</td></tr>
    <tr><td>GPT 6 Luna (&lt; 272K tokens)</td><td>$0.10</td><td>$0.50</td><td>-</td><td>-</td><td>$15</td></tr>
    <tr><td>DeepSeek V4 Flash (Peak)</td><td>$0.30</td><td>$1.20</td><td>-</td><td>-</td><td>$30</td></tr></tbody></table>
    <table><thead><tr><th>Model</th><th>Model ID</th><th>Endpoint</th><th>AI SDK Package</th></tr></thead><tbody>
    <tr><td>Grok 4.7</td><td>grok-4.7</td><td>https://opencode.ai/zen/go/v1/responses</td><td>@ai-sdk/openai</td></tr>
    <tr><td>DeepSeek V4 Pro</td><td>deepseek-v4-pro</td><td>https://opencode.ai/zen/go/v1/responses</td><td>@ai-sdk/openai</td></tr>
    <tr><td>GPT 6 Luna</td><td>gpt-6-luna</td><td>https://opencode.ai/zen/go/v1/responses</td><td>@ai-sdk/openai</td></tr>
    <tr><td>DeepSeek V4 Flash</td><td>deepseek-v4-flash</td><td>https://opencode.ai/zen/go/v1/responses</td><td>@ai-sdk/openai</td></tr></tbody></table>
    """;

    [Fact]
    public void TheGoRequestAllowanceTableIsReadAndJoinedToModelIds()
    {
        // The docs state requests per 5 hours per model, which is the figure the Models tab
        // shows. Rows are named by display name and joined to the id table, exactly as the
        // spend allowances are.
        var requests = LivePricing.ParseGoRequestAllowance(GoDocsWithRequests);

        Assert.Equal(6320, requests["glm-5.3-flash"].Requests);
        Assert.Equal(110, requests["kimi-k3"].Requests);
        Assert.Equal(30100, requests["mimo-v2.6-flash"].Requests);
        Assert.Equal(4, requests.Count);
    }

    [Fact]
    public void UnlimitedIsItsOwnStateAndNotANumber()
    {
        // "Unlimited" is a published state. Folding it into a large number would be a
        // different claim, and folding it into null would lose it.
        var requests = LivePricing.ParseGoRequestAllowance(GoDocsWithRequests);

        Assert.True(requests["space-bunny-free"].Unlimited);
        Assert.Null(requests["space-bunny-free"].Requests);
        Assert.Equal("unlimited", requests["space-bunny-free"].Label);
    }

    [Fact]
    public void TheBaseGoPlanIsTakenNotGoPlusForRequestsToo()
    {
        // Go Plus allows roughly four times as many requests, and reading the wrong tab
        // would tell someone they had quadrupled their allowance.
        var requests = LivePricing.ParseGoRequestAllowance(GoDocsWithRequests);

        Assert.Equal(6320, requests["glm-5.3-flash"].Requests);
        Assert.DoesNotContain(18960, requests.Values.Select(v => v.Requests));
    }

    [Fact]
    public void ARequestsTableThatChangedShapeYieldsNothingRatherThanWrongNumbers()
    {
        Assert.Empty(LivePricing.ParseGoRequestAllowance(""));
        Assert.Empty(LivePricing.ParseGoRequestAllowance("<html>nothing here</html>"));
        Assert.Empty(LivePricing.ParseGoRequestAllowance("<table><tr><th>Wrong</th></tr><tr><td>1</td></tr></table>"));

        // Requests with no id table to join through cannot be attributed to a model.
        Assert.Empty(LivePricing.ParseGoRequestAllowance("""
        <table><tr><th>Model</th><th>Requests per 5 hours</th><th>Requests per week</th><th>Requests per month</th></tr>
        <tr><td>GLM-5.3</td><td>220</td><td>540</td><td>1,080</td></tr></table>
        """));
    }

    private const string GoDocsWithRequests = """
    <table><thead><tr><th>Model</th><th>Requests per 5 hours</th><th>Requests per week</th><th>Requests per month</th></tr></thead><tbody>
    <tr><td>GLM-5.3-Flash</td><td>6,320</td><td>15,790</td><td>31,580</td></tr>
    <tr><td>Kimi K3</td><td>110</td><td>250</td><td>490</td></tr>
    <tr><td>MiMo-V2.6-Flash</td><td>30,100</td><td>75,200</td><td>150,400</td></tr>
    <tr><td>Space Bunny Free</td><td>Unlimited</td><td>Unlimited</td><td>Unlimited</td></tr></tbody></table>
    <div id="tab-panel-1"><table><thead><tr><th>Model</th><th>Requests per 5 hours</th><th>Requests per week</th><th>Requests per month</th></tr></thead><tbody>
    <tr><td>GLM-5.3-Flash</td><td>18,960</td><td>47,370</td><td>94,740</td></tr></tbody></table></div>
    <table><thead><tr><th>Model</th><th>Model ID</th><th>Endpoint</th><th>AI SDK Package</th></tr></thead><tbody>
    <tr><td>GLM-5.3-Flash</td><td>glm-5.3-flash</td><td>https://opencode.ai/zen/go/v1/responses</td><td>@ai-sdk/openai</td></tr>
    <tr><td>Kimi K3</td><td>kimi-k3</td><td>https://opencode.ai/zen/go/v1/responses</td><td>@ai-sdk/openai</td></tr>
    <tr><td>MiMo-V2.6-Flash</td><td>mimo-v2.6-flash</td><td>https://opencode.ai/zen/go/v1/responses</td><td>@ai-sdk/openai</td></tr>
    <tr><td>Space Bunny Free</td><td>space-bunny-free</td><td>https://opencode.ai/zen/go/v1/responses</td><td>@ai-sdk/openai</td></tr></tbody></table>
    """;

    [Fact]
    public void TheCacheFileIsSmallBecauseItKeepsOnlyPrices()
    {
        // The source document is over 5 MB. Storing the projection rather than the raw
        // payload is what keeps the cache cheap to write on every refresh.
        var path = Path.Combine(Path.GetTempPath(), "pricing-" + Guid.NewGuid().ToString("N")[..8] + ".json");
        try
        {
            var save = typeof(LivePricing)
                .GetMethod("Save", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
            save.Invoke(null, [path, Parsed()]);

            Assert.True(new FileInfo(path).Length < 4096, $"cache was {new FileInfo(path).Length} bytes");
            // And it is valid JSON, not a partial write.
            Assert.NotNull(JsonDocument.Parse(File.ReadAllText(path)));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
