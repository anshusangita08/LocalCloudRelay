using System.Text;
using LocalCloudRelay;

namespace LocalCloudRelay.Tests;

/// <summary>
/// Coding agents send tools on every turn, and a CLI account can only answer text, so a
/// router must not hand a tool-using request to one while a capable model is in its pool.
/// </summary>
public sealed class ToolAwareRoutingTests
{
    private static readonly ProviderSettings Cli = new("cli", "Claude", ProviderKinds.ClaudeCode, "https://unused.invalid", null, true, 0,
        AuthMode: ProviderAuthMode.CliAccount, ImportedModels: ["claude-sonnet-5"]);
    private static readonly ProviderSettings Api = new("api", "Zen", ProviderKinds.OpenAi, "https://zen.example/v1", "k", true, 1);

    private static ProviderRouter Router() => new([Cli, Api],
        new CatalogSnapshot(DateTimeOffset.UtcNow, new Dictionary<string, IReadOnlyList<string>> { ["api"] = ["kimi-k3"] }),
        [RouterRule.Create("code", RouterStrategies.Sticky, ["claude-sonnet-5", "kimi-k3"])]);

    [Fact]
    public void ARequestWithToolsSkipsTextOnlyAccounts()
    {
        var router = Router();
        for (var i = 0; i < 4; i++)
        {
            Assert.True(router.TryResolve("code", out var route, new RouterEngine(), $"s{i}", null, null, null, needsTools: true));
            Assert.Equal("api", route.Provider.Id);
        }
    }

    [Fact]
    public void ATextOnlyRequestCanStillUseTheAccount()
    {
        var router = Router();
        var engine = new RouterEngine();
        var providers = Enumerable.Range(0, 4)
            .Select(i => router.TryResolve("code", out var route, engine, $"t{i}", null, null, null, needsTools: false) ? route.Provider.Id : null)
            .ToHashSet();
        Assert.Contains("cli", providers);
    }

    [Fact]
    public void ThePassedOverAccountIsExplained()
    {
        var router = Router();
        var engine = new RouterEngine();
        Assert.True(router.TryResolve("code", out var route, engine, "s", null, null, null, needsTools: true));
        var decision = router.ExplainRule("code", route, engine, null, warm: false, needsTools: true);
        Assert.Contains("Claude/claude-sonnet-5 (text-only account, request uses tools)", decision);
    }

    [Theory]
    [InlineData("""{"tools":[{"name":"read"}],"messages":[]}""", true)]
    [InlineData("""{"tools":[],"messages":[]}""", false)]
    [InlineData("""{"messages":[{"role":"user","content":"tools"}]}""", false)]
    public void ToolsAreDetectedInTheBody(string body, bool expected) =>
        Assert.Equal(expected, RelaySession.HasTools(Encoding.UTF8.GetBytes(body)));
}
