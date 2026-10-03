using LocalCloudRelay;
using Xunit;

namespace LocalCloudRelay.Tests;

/// <summary>
/// Account providers call OpenAI, Anthropic and Google directly. models.dev lists those
/// vendors without an api URL, so they are priced from the vendor entry instead.
/// </summary>
[Collection("PricingState")]
public sealed class AccountPricingTests : IDisposable
{
    private const string Sample = """
    {
      "openai": { "id": "openai", "models": { "gpt-6-sol": { "cost": { "input": 2, "output": 10, "cache_read": 0.2 } } } },
      "anthropic": { "id": "anthropic", "models": {
        "claude-opus-5-5": { "cost": { "input": 4, "output": 20 } },
        "claude-opus-4-6": { "cost": { "input": 5, "output": 25 } },
        "claude-haiku-4-5": { "cost": { "input": 1, "output": 5 } },
        "claude-haiku-4-5-20251001": { "cost": { "input": 1, "output": 5 } } } },
      "google": { "id": "google", "models": {
        "gemini-3.8-flash": { "cost": { "input": 0.75, "output": 3.75 } },
        "gemini-3.1-pro-preview": { "cost": { "input": 2, "output": 12 } } } }
    }
    """;

    private readonly LivePricing? _previous = LivePricing.Current;

    public AccountPricingTests() => LivePricing.Use(LivePricing.Parse(Sample, DateTimeOffset.UtcNow));

    public void Dispose() => LivePricing.Use(_previous);

    [Theory]
    [InlineData(ProviderKinds.OpenAi, ProviderAuthMode.OAuth, "gpt-6-sol", 2)]
    [InlineData(ProviderKinds.ClaudeCode, ProviderAuthMode.CliAccount, "claude-opus-5-5", 4)]
    [InlineData(ProviderKinds.Antigravity, ProviderAuthMode.CliAccount, "gemini-3.8-flash-high", 0.75)]
    [InlineData(ProviderKinds.Antigravity, ProviderAuthMode.CliAccount, "gemini-3.1-pro-low", 2)]
    [InlineData(ProviderKinds.Antigravity, ProviderAuthMode.CliAccount, "claude-opus-4-6-thinking", 5)]
    public void AccountProvidersArePricedAtTheVendorRate(string kind, ProviderAuthMode mode, string model, double input)
    {
        var provider = new ProviderSettings("a", "Account", kind, "https://example.invalid", null, true, 0, AuthMode: mode);

        var price = ModelPricingTable.For(provider, model);

        Assert.Equal((decimal)input, price?.InputPerMillion);
    }

    [Fact]
    public void AGatewayIsNotPricedAtTheVendorRate()
    {
        // OpenCode resells OpenAI models at its own rates; only api.openai.com is OpenAI.
        var gateway = new ProviderSettings("g", "Gateway", ProviderKinds.OpenAi, "https://gateway.example/v1", "k", true, 0);

        Assert.Null(ModelPricingTable.For(gateway, "gpt-6-sol"));
    }

    [Fact]
    public void ClaudeCatalogAddsEveryPublishedModelAndDropsDatedTwins()
    {
        var models = ClaudeCodeGateway.MergeCatalog(
            ["claude-opus-5-5", "claude-haiku-4-5-20251001"],
            LivePricing.Current!.VendorModels("anthropic"));

        Assert.Equal(["claude-opus-5-5", "claude-opus-4-6", "claude-haiku-4-5"], models);
    }
}
