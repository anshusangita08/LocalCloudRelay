using LocalCloudRelay;

namespace LocalCloudRelay.Tests;

/// <summary>
/// A gateway behind a path prefix - opencode.ai/zen/go/v1, a LiteLLM on /proxy - used
/// to be unconfigurable, because the editor reduced a base URL to its origin and then
/// probed the wrong path. These pin the path-preserving behaviour end to end.
/// </summary>
public sealed class PathPrefixedProviderTests
{
    [Fact]
    public void ProbesTheModelsPathUnderAPathPrefixWithoutDoublingV1()
    {
        Assert.Equal("https://opencode.ai/zen/v1/models",
            RelayProtocol.ModelsUri(new Uri("https://opencode.ai/zen/v1")).ToString());
        Assert.Equal("https://opencode.ai/zen/go/v1/models",
            RelayProtocol.ModelsUri(new Uri("https://opencode.ai/zen/go/v1")).ToString());
        Assert.Equal("https://opencode.ai/zen/go/v1/models",
            RelayProtocol.ModelsUri(new Uri("https://opencode.ai/zen/go/v1/")).ToString());
        // A prefix that is not itself a version segment still gets /v1/models.
        Assert.Equal("https://gw.example/proxy/v1/models",
            RelayProtocol.ModelsUri(new Uri("https://gw.example/proxy")).ToString());
    }

    [Fact]
    public void JoinsUpstreamPathsUnderAPathPrefixWithoutDoublingTheVersion()
    {
        // The shape that produced the 404: a base ending in /v1 joined with a
        // canonical /v1/... path must not become /v1/v1/...
        var go = new Uri("https://opencode.ai/zen/go/v1/");
        Assert.Equal("https://opencode.ai/zen/go/v1/chat/completions",
            RelayProtocol.Join(go, "/v1/chat/completions").ToString());
        Assert.Equal("https://opencode.ai/zen/go/v1/messages",
            RelayProtocol.Join(go, "/v1/messages").ToString());
        Assert.Equal("https://opencode.ai/zen/go/v1/responses",
            RelayProtocol.Join(go, "/v1/responses").ToString());

        // A base with no version keeps the version from the path.
        Assert.Equal("https://gw.example/v1/chat/completions",
            RelayProtocol.Join(new Uri("https://gw.example"), "/v1/chat/completions").ToString());
        Assert.Equal("https://gw.example/proxy/v1/chat/completions",
            RelayProtocol.Join(new Uri("https://gw.example/proxy"), "/v1/chat/completions").ToString());

        // A genuinely different version is the upstream's business, not ours.
        Assert.Equal("https://opencode.ai/zen/go/v1/v1beta/models/x",
            RelayProtocol.Join(go, "/v1beta/models/x").ToString());
    }

    [Fact]
    public void NormalizationAndJoiningComposeToOneVersionSegment()
    {
        var go = new Uri("https://opencode.ai/zen/go/v1/");
        foreach (var clientPath in new[] { "/v1/chat/completions", "/chat/completions", "/v1/v1/chat/completions" })
        {
            var joined = RelayProtocol.Join(go, RelayProtocol.NormalizeRequestPath(clientPath)).ToString();
            Assert.Equal("https://opencode.ai/zen/go/v1/chat/completions", joined);
        }
    }

    [Fact]
    public void ThePresetsPointAtRealDocumentedEndpoints()
    {
        var zen = ProviderPresets.Find("OpenCode Zen")!;
        var go = ProviderPresets.Find("OpenCode Go")!;

        // Two different services. Collapsing them to one host+path was the original bug.
        Assert.Equal("https://opencode.ai/zen/v1", zen.BaseUrl);
        Assert.Equal("https://opencode.ai/zen/go/v1", go.BaseUrl);
        Assert.NotEqual(zen.BaseUrl, go.BaseUrl);

        // Both must survive a models probe.
        Assert.Equal("https://opencode.ai/zen/v1/models",
            RelayProtocol.ModelsUri(new Uri(zen.BaseUrl)).ToString());
        Assert.Equal("https://opencode.ai/zen/go/v1/models",
            RelayProtocol.ModelsUri(new Uri(go.BaseUrl)).ToString());

        // Local presets must be marked local so they are never charged a token price.
        Assert.Equal(ProviderKinds.Local, ProviderPresets.Find("Ollama")!.Kind);
        Assert.True(ProviderKinds.IsFree(ProviderPresets.Find("Ollama")!.Kind));
    }

    [Fact]
    public void PresetLabelsAreUniqueAndTheLastOneIsCustom()
    {
        var labels = ProviderPresets.All.Select(p => p.Label).ToArray();
        Assert.Equal(labels.Length, labels.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal("Custom", ProviderPresets.All[^1].Label);
        // Custom carries no defaults, so selecting it cannot clobber typed values.
        Assert.Equal("", ProviderPresets.All[^1].BaseUrl);
    }

    [Fact]
    public void DiscoveryKeepsAPathPrefixInsteadOfReducingToTheOrigin()
    {
        var home = Path.Combine(Path.GetTempPath(), $"pfx-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(Path.Combine(home, ".config", "opencode"));
            File.WriteAllText(
                Path.Combine(home, ".config", "opencode", "opencode.json"),
                """{ "provider": { "zen": { "options": { "baseURL": "https://opencode.ai/zen/go/v1" } } } }""");

            var found = Assert.Single(ClientDiscovery.Scan(home, new Dictionary<string, string?>()).Found);

            Assert.Equal("https://opencode.ai/zen/go/v1", found.BaseUrl);
        }
        finally { try { Directory.Delete(home, true); } catch { } }
    }

    [Fact]
    public void DiscoveryDeduplicatesOnTheFullUrlNotTheHost()
    {
        var home = Path.Combine(Path.GetTempPath(), $"pfx2-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(Path.Combine(home, ".claude"));
            File.WriteAllText(
                Path.Combine(home, ".claude", "settings.json"),
                """{ "env": { "ANTHROPIC_BASE_URL": "https://opencode.ai/zen/v1" } }""");

            // Same host, different path prefix: these are two different upstreams and
            // must both survive. Deduping on origin would have thrown one away.
            var found = ClientDiscovery.Scan(home, new Dictionary<string, string?>
            {
                ["ANTHROPIC_BASE_URL"] = "https://opencode.ai/zen/go/v1"
            }).Found;

            Assert.Equal(2, found.Count);
        }
        finally { try { Directory.Delete(home, true); } catch { } }
    }
}
