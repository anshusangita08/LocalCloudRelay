using LocalCloudRelay;
using System.Net;
using System.Net.NetworkInformation;

namespace LocalCloudRelay.Tests;

/// <summary>
/// Discovery reads other tools' config files, so these tests build a fake home directory
/// and assert against that. Nothing here touches the real %USERPROFILE%.
/// </summary>
public sealed class ClientDiscoveryTests : IDisposable
{
    private readonly string _home = Path.Combine(Path.GetTempPath(), $"discover-{Guid.NewGuid():N}");

    public ClientDiscoveryTests() => Directory.CreateDirectory(_home);

    public void Dispose()
    {
        try { Directory.Delete(_home, true); } catch { }
        GC.SuppressFinalize(this);
    }

    private void Write(string relative, string content)
    {
        var path = Path.Combine(_home, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private DiscoveryResult Scan() => ClientDiscovery.Scan(_home, new Dictionary<string, string?>());

    [Fact]
    public void ReadsClaudeCodeBaseUrlAndKey()
    {
        Write(".claude/settings.json", """
        {
          "env": {
            "ANTHROPIC_BASE_URL": "https://gw.corp.example:8443/api/",
            "ANTHROPIC_API_KEY": "sk-ant-123"
          },
          "model": "opus"
        }
        """);

        var found = Assert.Single(Scan().Found);
        Assert.Equal("Claude Code", found.Source);
        // The path prefix survives; only the trailing slash is trimmed. A gateway on
        // /api 404s if reduced to its origin.
        Assert.Equal("https://gw.corp.example:8443/api", found.BaseUrl);
        Assert.Equal("sk-ant-123", found.ApiKey);
        Assert.Equal(ProviderKinds.Anthropic, found.Kind);
    }

    [Fact]
    public void ResolvesEnvironmentKeyReferencesFromTheScanEnvironment()
    {
        Write(".claude/settings.json", """
        { "env": { "ANTHROPIC_BASE_URL": "https://gw.example", "ANTHROPIC_API_KEY": "{env:CLAUDE_TEST_KEY}" } }
        """);

        var found = Assert.Single(ClientDiscovery.Scan(_home, new Dictionary<string, string?>
        {
            ["CLAUDE_TEST_KEY"] = "resolved-secret"
        }).Found);

        Assert.Equal("resolved-secret", found.ApiKey);
    }

    [Fact]
    public void DetectsAnthropicOpenCodeProviderKind()
    {
        Write(".config/opencode/opencode.json", """
        {
          "provider": {
            "anthropic": {
              "npm": "@ai-sdk/anthropic",
              "options": { "baseURL": "https://anthropic.example/v1" }
            }
          }
        }
        """);

        var found = Assert.Single(ClientDiscovery.Scan(_home, new Dictionary<string, string?>()).Found,
            endpoint => endpoint.BaseUrl == "https://anthropic.example/v1");

        Assert.Equal(ProviderKinds.Anthropic, found.Kind);
    }

    [Fact]
    public void IgnoresRelayEndpointAtALocalNetworkAddress()
    {
        var address = NetworkInterface.GetAllNetworkInterfaces()
            .SelectMany(network => network.GetIPProperties().UnicastAddresses)
            .Select(unicast => unicast.Address)
            .FirstOrDefault(candidate => candidate.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
                && !IPAddress.IsLoopback(candidate));
        Assert.NotNull(address);

        var relayUrl = new UriBuilder("http", address!.ToString(), RelayProtocol.Port, "/v1").Uri.ToString();
        Write(".config/opencode/opencode.json", $$"""
        { "provider": { "relay": { "options": { "baseURL": "{{relayUrl}}" } } } }
        """);

        var found = ClientDiscovery.Scan(_home, new Dictionary<string, string?>()).Found;

        Assert.DoesNotContain(found, endpoint => endpoint.BaseUrl == relayUrl.TrimEnd('/'));
    }

    [Fact]
    public void ReadsGeminiCliFromEitherEnvName()
    {
        Write(".gemini/settings.json", """
        { "env": { "GEMINI_BASE_URL": "https://gemini.example/v1beta", "GOOGLE_API_KEY": "goog-9" } }
        """);

        var found = Assert.Single(Scan().Found);
        Assert.Equal("Gemini CLI", found.Source);
        Assert.Equal("https://gemini.example/v1beta", found.BaseUrl);
        Assert.Equal("goog-9", found.ApiKey);
    }

    [Fact]
    public void ReadsEveryOpenCodeProviderFromAJsoncConfig()
    {
        // opencode writes JSONC, so comments and trailing commas must not break parsing.
        Write(".config/opencode/opencode.jsonc", """
        {
          // a comment
          "provider": {
            "corp": {
              "options": { "baseURL": "https://corp.example/v1", "apiKey": "sk-1" },
            },
            "local": {
              "options": { "baseURL": "http://127.0.0.1:11434/v1" },
            },
            "no-options": { "npm": "@ai-sdk/openai-compatible" }
          }
        }
        """);

        var found = Scan().Found;
        Assert.Equal(2, found.Count);
        Assert.Contains(found, f => f.BaseUrl == "https://corp.example/v1" && f.ApiKey == "sk-1");
        // A provider with no key stays usable, so ApiKey is null rather than empty.
        var local = Assert.Single(found, f => f.BaseUrl == "http://127.0.0.1:11434/v1");
        Assert.Null(local.ApiKey);
    }

    [Fact]
    public void ReadsEndpointsFromEnvironmentVariables()
    {
        var env = new Dictionary<string, string?>
        {
            ["ANTHROPIC_BASE_URL"] = "https://env-anthropic.example",
            ["ANTHROPIC_API_KEY"] = "env-key-1",
            ["OPENAI_BASE_URL"] = "https://env-openai.example/v1",
            ["OPENAI_API_KEY"] = "env-key-2"
        };

        var found = ClientDiscovery.Scan(_home, env).Found;

        Assert.Equal(2, found.Count);
        Assert.Contains(found, f => f.BaseUrl == "https://env-anthropic.example" && f.ApiKey == "env-key-1");
        Assert.Contains(found, f => f.BaseUrl == "https://env-openai.example/v1" && f.ApiKey == "env-key-2");
    }

    [Fact]
    public void ReportsNothingFoundRatherThanThrowingWhenNoConfigExists()
    {
        var result = Scan();
        Assert.False(result.AnythingFound);
        Assert.Empty(result.Found);
    }

    [Fact]
    public void IgnoresUnusableValuesInsteadOfImportingThem()
    {
        Write(".claude/settings.json", """
        {
          "env": {
            "ANTHROPIC_BASE_URL": "not-a-url",
            "ANTHROPIC_API_KEY": "k"
          }
        }
        """);
        var env = new Dictionary<string, string?> { ["OPENAI_BASE_URL"] = "ftp://wrong-scheme.example" };

        var result = ClientDiscovery.Scan(_home, env);

        Assert.Empty(result.Found);
    }

    [Fact]
    public void DeduplicatesTheSameEndpointReachedTwoWays()
    {
        Write(".claude/settings.json", """
        { "env": { "ANTHROPIC_BASE_URL": "https://same.example" } }
        """);
        var env = new Dictionary<string, string?> { ["ANTHROPIC_BASE_URL"] = "https://same.example/" };

        Assert.Single(ClientDiscovery.Scan(_home, env).Found);
    }

    [Fact]
    public void SurvivesAMalformedConfigFile()
    {
        Write(".claude/settings.json", "{ this is not json");
        Write(".config/opencode/opencode.jsonc", "]]]not json[");

        var result = Scan();

        Assert.Empty(result.Found);
        Assert.Contains(result.Checked, p => p.EndsWith("settings.json", StringComparison.Ordinal));
    }

    [Fact]
    public void CatalogCacheDeleteRemovesTheFileAndLoadToleratesItsAbsence()
    {
        var path = Path.Combine(_home, "catalog.json");
        var cache = new CatalogCache(path);
        cache.Save(new CatalogSnapshot(DateTimeOffset.UtcNow,
            new Dictionary<string, IReadOnlyList<string>> { ["p"] = ["m"] }));
        Assert.True(File.Exists(path));

        cache.Delete();

        Assert.False(File.Exists(path));
        Assert.Equal(0, cache.Load().TotalModels);
    }
}
