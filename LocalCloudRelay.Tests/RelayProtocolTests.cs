using LocalCloudRelay;

namespace LocalCloudRelay.Tests;

public sealed class RelayProtocolTests
{
    [Fact]
    public void AcceptsBothSupportedCredentialHeaders()
    {
        const string key = "local-test";
        Assert.True(RelayProtocol.IsAuthorized(key, RelayProtocol.StripBearer("Bearer local-test")));
        Assert.True(RelayProtocol.IsAuthorized(key, "local-test"));
        Assert.False(RelayProtocol.IsAuthorized(key, RelayProtocol.StripBearer("Bearer wrong")));
        Assert.False(RelayProtocol.IsAuthorized(key, null, null));
    }

    [Fact]
    public void AcceptsGeminiAndAzureCredentialHeaders()
    {
        const string key = "local-test";
        Assert.Contains("x-goog-api-key", RelayProtocol.CredentialHeaders);
        Assert.Contains("api-key", RelayProtocol.CredentialHeaders);
        Assert.True(RelayProtocol.IsAuthorized(key, "local-test"));
    }

    [Fact]
    public void AnEmptyLocalKeyNeverAuthorizes()
    {
        // Regression guard: with an empty key and an empty candidate a naive equality
        // check would match and hand out access to an unconfigured relay.
        Assert.False(RelayProtocol.IsAuthorized(string.Empty, string.Empty));
        Assert.False(RelayProtocol.IsAuthorized(null, "anything"));
        Assert.False(RelayProtocol.IsAuthorized(string.Empty, null, string.Empty));
    }

    [Fact]
    public void StripsBearerPrefixCaseInsensitively()
    {
        Assert.Equal("abc", RelayProtocol.StripBearer("Bearer abc"));
        Assert.Equal("abc", RelayProtocol.StripBearer("bearer  abc"));
        Assert.Equal("abc", RelayProtocol.StripBearer("abc"));
        Assert.Null(RelayProtocol.StripBearer(null));
    }

    [Theory]
    // One advertised base URL - the bare host - has to serve clients that append
    // different things to it, and also clients that are handed a /v1 base. These are
    // the exact paths each shape produces, and what the upstream should see.
    // Base URL = http://host:8787
    [InlineData("/chat/completions", "/v1/chat/completions")]                    // OpenAI SDK, OpenCode, Cline, Aider
    [InlineData("/models", "/v1/models")]                                         // OpenAI SDK model list
    [InlineData("/embeddings", "/v1/embeddings")]
    [InlineData("/responses", "/v1/responses")]
    [InlineData("/v1/messages", "/v1/messages")]                                  // Claude Code
    [InlineData("/v1beta/models/gemini-3-pro:generateContent", "/v1beta/models/gemini-3-pro:generateContent")]
    [InlineData("/v1/models", "/v1/models")]                                      // already canonical
    // Base URL = http://host:8787/v1 - still has to work
    [InlineData("/v1/chat/completions", "/v1/chat/completions")]
    [InlineData("/v1/v1/messages", "/v1/messages")]
    [InlineData("/v1/v1beta/models/gemini-3-pro:generateContent", "/v1beta/models/gemini-3-pro:generateContent")]
    [InlineData("/v1/v1", "/v1")]
    public void NormalizesBothBaseUrlFormsToTheSameUpstreamPath(string input, string expected)
    {
        Assert.Equal(expected, RelayProtocol.NormalizeRequestPath(input));
    }

    [Fact]
    public void LeavesAVersionedPathAlone()
    {
        // Anything already carrying a version segment is passed through untouched,
        // including v1beta and v2, and including a base path that legitimately
        // contains v1 twice.
        Assert.Equal("/v1beta/models", RelayProtocol.NormalizeRequestPath("/v1beta/models"));
        Assert.Equal("/v2/chat/completions", RelayProtocol.NormalizeRequestPath("/v2/chat/completions"));
        Assert.Equal("/api/v1/v1/models", RelayProtocol.NormalizeRequestPath("/api/v1/v1/models"));
    }

    [Fact]
    public void NormalizationIsIdempotent()
    {
        // HandleAsync re-normalizes a path the middleware already normalized.
        foreach (var path in new[] { "/chat/completions", "/v1/messages", "/models", "/v1/v1/messages", "/v1beta/models/x" })
        {
            var once = RelayProtocol.NormalizeRequestPath(path);
            Assert.Equal(once, RelayProtocol.NormalizeRequestPath(once));
        }
    }

    [Fact]
    public void JoinsPathsWithoutDroppingBasePathOrQuery()
    {
        var baseUri = new Uri("https://gateway.example/api/");
        Assert.Equal("https://gateway.example/api/v1/messages?stream=true", RelayProtocol.Join(baseUri, "/v1/messages?stream=true").ToString());
    }

    [Fact]
    public void RejectsAbsoluteUrlPathToPreventSSRF()
    {
        var baseUri = new Uri("https://gateway.example/v1/");
        // SSRF attempt: absolute URL gets passed as a path
        Assert.Throws<ArgumentException>(() => RelayProtocol.Join(baseUri, "http://evil.example/v1/x"));
        Assert.Throws<ArgumentException>(() => RelayProtocol.Join(baseUri, "https://attacker.example/v1/messages"));
    }

    [Fact]
    public void RejectsPathsWithDoubleSlashToPreventSSRF()
    {
        var baseUri = new Uri("https://gateway.example/v1/");
        // Protocol-relative URL attack: //evil.example/v1/x
        Assert.Throws<ArgumentException>(() => RelayProtocol.Join(baseUri, "//evil.example/v1/x"));
    }

    [Fact]
    public void RejectsPathsWithSchemeToPreventSSRF()
    {
        var baseUri = new Uri("https://gateway.example/v1/");
        // Path containing scheme before first slash
        Assert.Throws<ArgumentException>(() => RelayProtocol.Join(baseUri, "ftp://evil.example/v1/x"));
    }

    [Fact]
    public void ProbesModelsWithoutDoublingTheV1Segment()
    {
        Assert.Equal("https://gw.example/v1/models",
            RelayProtocol.ModelsUri(new Uri("https://gw.example/")).ToString());
        Assert.Equal("https://gw.example/v1/models",
            RelayProtocol.ModelsUri(new Uri("https://gw.example/v1")).ToString());
        Assert.Equal("https://gw.example/v1/models",
            RelayProtocol.ModelsUri(new Uri("https://gw.example/v1/")).ToString());
    }

    [Fact]
    public void LocalKeyHasExpectedPrefixAndEntropy()
    {
        var first = RelayProtocol.CreateLocalKey();
        var second = RelayProtocol.CreateLocalKey();
        Assert.StartsWith("local-", first);
        Assert.NotEqual(first, second);
        Assert.True(first.Length >= 30);
    }

    [Fact]
    public void IdentifiesHopByHopHeaders()
    {
        Assert.True(RelayProtocol.IsHopByHop("Connection"));
        Assert.True(RelayProtocol.IsHopByHop("transfer-encoding"));
        Assert.True(RelayProtocol.IsHopByHop("Proxy-Connection"));
        Assert.False(RelayProtocol.IsHopByHop("Content-Type"));
    }

    [Fact]
    public void IdentifiesClientSuppliedForwardedHeaders()
    {
        Assert.True(RelayProtocol.IsForwarded("X-Forwarded-For"));
        Assert.True(RelayProtocol.IsForwarded("x-forwarded-host"));
        Assert.False(RelayProtocol.IsForwarded("X-Request-Id"));
    }
}
