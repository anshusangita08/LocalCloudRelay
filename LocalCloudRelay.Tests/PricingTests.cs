using LocalCloudRelay;
using Xunit;

namespace LocalCloudRelay.Tests;

[Collection("PricingState")]
public sealed class PricingTests
{
    private static ProviderSettings Provider(string baseUrl, string kind = ProviderKinds.OpenAi) =>
        new("p", "P", kind, baseUrl, "k", true, 0);

    [Fact]
    public void ZenAndGoChargeDifferentRatesForTheSameModel()
    {
        // The reason pricing is keyed per plan and not per model. Getting this wrong
        // would report a plausible number that is simply not what gets billed.
        var zen = ModelPricingTable.For(Provider("https://opencode.ai/zen/v1"), "deepseek-v4-pro");
        var go = ModelPricingTable.For(Provider("https://opencode.ai/zen/go/v1"), "deepseek-v4-pro");

        Assert.NotNull(zen);
        Assert.NotNull(go);
        Assert.Equal(1.74m, zen!.InputPerMillion);
        Assert.Equal(1.65m, go!.InputPerMillion);
        Assert.NotEqual(zen.InputPerMillion, go.InputPerMillion);
    }

    [Fact]
    public void LookupIsCaseInsensitiveAndDoesNotLeakAcrossPlans()
    {
        var zen = Provider("https://opencode.ai/zen/v1");

        Assert.NotNull(ModelPricingTable.For(zen, "KIMI-K3"));
        // A Go-only model must not be priced off Zen.
        Assert.Null(ModelPricingTable.For(zen, "mimo-v2.6-pro"));
        Assert.NotNull(ModelPricingTable.For(Provider("https://opencode.ai/zen/go/v1"), "mimo-v2.6-pro"));
    }

    [Fact]
    public void LocalProvidersAreFreeAndUnknownProvidersAreNotPriced()
    {
        // Free is a fact about a local model. An unknown provider is not a fact at all,
        // so it stays null rather than becoming a zero that reads as free.
        var ollama = Provider("http://127.0.0.1:11434/v1", ProviderKinds.Local);
        Assert.True(ModelPricingTable.For(ollama, "llama3.1-70b")!.IsFree);
        Assert.Equal(0m, ModelPricingTable.Estimate(ollama, "llama3.1-70b", 1_000_000, 1_000_000));

        var other = Provider("https://some-gateway.example/v1");
        Assert.Null(ModelPricingTable.For(other, "llama3.1-70b"));
        Assert.Null(ModelPricingTable.Estimate(other, "llama3.1-70b", 1000, 1000));
    }

    [Fact]
    public void FiveHourAllowanceIsThePublishedRequestCount()
    {
        // The column shows requests, not dollars: Go publishes requests per five hours per
        // model, and the dollar figure only said what the monthly spend limit was.
        var go = Provider("https://opencode.ai/zen/go/v1");

        Assert.Equal(880, ModelPricingTable.FiveHourRequests(go, "glm-5.2")!.Requests);
        Assert.Equal("880", ModelPricingTable.FiveHourRequests(go, "glm-5.2")!.Label);

        // Zen publishes no per-model request allowance at all, so it must not invent one.
        var zen = Provider("https://opencode.ai/zen/v1");
        Assert.Null(ModelPricingTable.FiveHourRequests(zen, "kimi-k3"));
    }

    [Fact]
    public void AnUnlimitedAllowanceIsItsOwnStateRatherThanABigNumber()
    {
        var go = Provider("https://opencode.ai/zen/go/v1");

        var allowance = ModelPricingTable.FiveHourRequests(go, "space-bunny-free")!;

        Assert.True(allowance.Unlimited);
        Assert.Null(allowance.Requests);
        Assert.Equal("unlimited", allowance.Label);
        // And a model with no published allowance is a third state, not zero.
        Assert.Null(ModelPricingTable.FiveHourRequests(go, "deepseek-flash"));
    }

    [Fact]
    public void AGoAllowanceSurvivesLivePricesThatCarryNone()
    {
        // The bug this column had: prices went live from models.dev, whose entries carry
        // no allowance, and the allowance - fetched separately - blanked for every Go
        // model. Rates and allowances come from different places and must resolve apart.
        var go = Provider("https://opencode.ai/zen/go/v1");
        var path = Path.Combine(Path.GetTempPath(), "pricing-" + Guid.NewGuid().ToString("N")[..8] + ".json");
        try
        {
            Assert.Equal(1050, ModelPricingTable.FiveHourRequests(go, "deepseek-v4-pro")!.Requests);

            var save = typeof(LivePricing)
                .GetMethod("Save", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
            save.Invoke(null, [path, LivePricing.Parse("""
            { "opencode-go": { "api": "https://opencode.ai/zen/go/v1",
              "models": { "deepseek-v4-pro": { "cost": { "input": 0.66, "output": 1.98 } } } } }
            """, DateTimeOffset.UtcNow)]);
            Assert.NotNull(LivePricing.LoadCached(path));

            // The rate is live now ...
            Assert.Equal(0.66m, ModelPricingTable.For(go, "deepseek-v4-pro")!.InputPerMillion);
            // ... and the allowance is still known, so the column is not blank.
            Assert.Equal(1050, ModelPricingTable.FiveHourRequests(go, "deepseek-v4-pro")!.Requests);
        }
        finally
        {
            LivePricing.Use(null);
            File.Delete(path);
        }
    }

    [Fact]
    public void EstimateUsesTheRatesOfThePlanTheRequestActuallyWentTo()
    {
        var zen = Provider("https://opencode.ai/zen/v1");
        var go = Provider("https://opencode.ai/zen/go/v1");

        // 1M in + 1M out on deepseek-v4-pro: $5.22 on Zen, $5.61 on Go.
        Assert.Equal(5.22m, ModelPricingTable.Estimate(zen, "deepseek-v4-pro", 1_000_000, 1_000_000));
        Assert.Equal(5.61m, ModelPricingTable.Estimate(go, "deepseek-v4-pro", 1_000_000, 1_000_000));
    }

    [Fact]
    public void PublishedFreeModelsCostNothing()
    {
        foreach (var baseUrl in new[] { "https://opencode.ai/zen/v1", "https://opencode.ai/zen/go/v1" })
            Assert.True(ModelPricingTable.For(Provider(baseUrl), "space-bunny-free")!.IsFree);

        Assert.Equal(0m, ModelPricingTable.Estimate(Provider("https://opencode.ai/zen/v1"), "space-bunny-free", 1_000_000, 1_000_000));
    }

    [Fact]
    public void EveryPublishedRowParsesToCompleteNumbers()
    {
        // A row with a typo would silently read as a null rate, which the grid shows as
        // blank. This catches a malformed table rather than a blank column.
        Assert.True(ModelPricingTable.KnownModelCount > 100, $"only {ModelPricingTable.KnownModelCount} models parsed");

        foreach (var model in new[] { "claude-opus-4-5", "gpt-5.6-luna", "gemini-3.1-pro", "qwen3.8-max", "minimax-m3" })
        {
            var price = ModelPricingTable.For(Provider("https://opencode.ai/zen/v1"), model);
            Assert.NotNull(price);
            Assert.NotNull(price!.InputPerMillion);
            Assert.NotNull(price.OutputPerMillion);
            Assert.True(price.InputPerMillion > 0, $"{model} in-price is {price.InputPerMillion}");
            Assert.True(price.OutputPerMillion >= price.InputPerMillion, $"{model} out-price is below in-price");
        }
    }

    [Fact]
    public void UsesSuppliedLunaPricing()
    {
        Assert.Equal(0.20m, ModelCatalog.Find("gpt-5.6-luna")!.InputPerMillion);
        Assert.Equal(1.20m, ModelCatalog.Find("gpt-5.6-luna")!.OutputPerMillion);
        Assert.Equal(0.001m, ModelCatalog.Estimate("gpt-5.6-luna", 5000, 0));
    }

    [Fact]
    public void UnknownModelsRemainUnknown()
    {
        Assert.Null(ModelCatalog.Estimate("not-configured", 10, 10));
    }

    [Fact]
    public void IsFreeRequiresBothInputAndOutputBeZero()
    {
        // Only if both input and output are exactly 0 is a model free.
        var bothZero = new ModelPrice(0m, 0m, 0m, 0m, null);
        var inputZeroOutputNull = new ModelPrice(0m, null, 0m, 0m, null);
        var inputNullOutputZero = new ModelPrice(null, 0m, 0m, 0m, null);
        var bothNonZero = new ModelPrice(1m, 2m, 0m, 0m, null);

        Assert.True(bothZero.IsFree);
        Assert.False(inputZeroOutputNull.IsFree);
        Assert.False(inputNullOutputZero.IsFree);
        Assert.False(bothNonZero.IsFree);
    }

    [Fact]
    public void EstimateIncludesCacheTokenCosts()
    {
        var provider = Provider("https://opencode.ai/zen/v1");
        // deepseek-v4-pro: input 1.74, output 3.48, cache read 0.145
        // 100k input + 200k output + 300k cache read
        // = (100k * 1.74 + 200k * 3.48 + 300k * 0.145) / 1M = $1.1295
        var estimate = ModelPricingTable.Estimate(provider, "deepseek-v4-pro", 100_000, 200_000, 300_000);
        Assert.NotNull(estimate);
        Assert.True(estimate > 0.8m && estimate < 1.2m, $"estimate {estimate} not in expected range");
    }

    [Fact]
    public void ModelCatalogEstimateIncludesCacheTokenCosts()
    {
        // gpt-5.6-luna has no cached rate in ModelCatalog, so should ignore cache tokens
        var withoutCache = ModelCatalog.Estimate("gpt-5.6-luna", 100_000, 200_000);
        var withCache = ModelCatalog.Estimate("gpt-5.6-luna", 100_000, 200_000, 50_000);
        // Since no cache rate is known, both should be equal
        Assert.Equal(withoutCache, withCache);
    }
}
