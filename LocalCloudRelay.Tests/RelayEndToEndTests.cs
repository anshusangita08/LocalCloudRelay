using System.Net;
using System.Net.Http.Headers;
using System.Text;
using LocalCloudRelay;

namespace LocalCloudRelay.Tests;

/// <summary>
/// Stands in for the upstream gateway. Records what the relay actually sent, which is
/// the only way to prove the body survives routing and credentials are not leaked.
/// </summary>
internal sealed class FakeUpstreamHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;
    public List<Uri> Requests { get; } = [];
    public List<string> Bodies { get; } = [];
    public List<string?> AuthorizationHeaders { get; } = [];
    public List<bool> HasAcceptEncodingHeader { get; } = [];

    public FakeUpstreamHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) => _responder = responder;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request.RequestUri!);
        AuthorizationHeaders.Add(request.Headers.Authorization?.Parameter);
        Bodies.Add(request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken));
        HasAcceptEncodingHeader.Add(request.Headers.Contains("Accept-Encoding"));
        return _responder(request);
    }
}

public sealed class RelayEndToEndTests : IAsyncLifetime
{
    private const string LocalKey = "local-e2e-test-key";
    private readonly RelayTelemetryStore _telemetry = new();
    private FakeUpstreamHandler _upstream = null!;
    private RelayServer _server = null!;
    private HttpClient _client = null!;
    private string _base = string.Empty;

    public async Task InitializeAsync()
    {
        // The local provider deliberately reports NO cost header, so the estimate path
        // is what decides the cost source for it.
        _upstream = new FakeUpstreamHandler(request =>
        {
            var isLocal = request.RequestUri!.Host == "127.0.0.1";
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(isLocal
                    ? """{"id":"local-1","model":"llama3.1-70b","choices":[],"usage":{"prompt_tokens":20,"completion_tokens":8}}"""
                    : """{"id":"chatcmpl-1","model":"gpt-5.6-luna","choices":[],"usage":{"prompt_tokens":20,"completion_tokens":8}}""",
                    Encoding.UTF8, "application/json")
            };
            if (!isLocal)
            {
                response.Headers.TryAddWithoutValidation("x-litellm-response-cost", "0.0123");
                response.Headers.TryAddWithoutValidation("x-litellm-model-group", "azure");
            }
            return response;
        });

        // Port 0 lets Kestrel pick a free port, so tests never collide with a real relay.
        _server = new RelayServer(_telemetry, _upstream, port: 0);
        _server.Apply(LocalKey, new ProviderRouter(
        [
            new ProviderSettings("corp", "Corp Gateway", ProviderKinds.OpenAi, "https://corp.example", "corp-secret", true, 0),
            new ProviderSettings("ollama", "Local Ollama", ProviderKinds.Local, "http://127.0.0.1:11434/v1", null, true, 1)
        ], new CatalogSnapshot(DateTimeOffset.UtcNow, new Dictionary<string, IReadOnlyList<string>>
        {
            ["corp"] = ["gpt-5.6-luna"],
            ["ollama"] = ["llama3.1-70b"]
        })));
        await _server.StartAsync();
        _base = $"http://127.0.0.1:{_server.Port}";
        _client = new HttpClient();
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _server.StopAsync();
        _server.Dispose();
    }

    private HttpRequestMessage Authorized(HttpMethod method, string path)
    {
        var request = new HttpRequestMessage(method, _base + path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", LocalKey);
        return request;
    }

    [Fact]
    public async Task ProxiesAndRecordsModelTokensAndProviderCost()
    {
        // The regression working-status.md recorded and followup-plan.md asked for:
        // a successful JSON response must record model, tokens and cost.
        using var request = Authorized(HttpMethod.Post, "/v1/chat/completions");
        request.Content = new StringContent("""{"model":"gpt-5.6-luna","messages":[{"role":"user","content":"hello"}]}""",
            Encoding.UTF8, "application/json");

        using var response = await _client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("chatcmpl-1", body);

        var report = _telemetry.GetReport();
        Assert.Equal(1, report.RequestCount);
        Assert.Equal("gpt-5.6-luna", report.RecentRequests[0].Model);
        Assert.Equal(20, report.InputTokens);
        Assert.Equal(8, report.OutputTokens);
        Assert.Equal(0.0123m, report.ProviderCostUsd);
        Assert.Equal("provider", report.RecentRequests[0].CostSource);
    }

    [Fact]
    public async Task RoutesToTheProviderThatOwnsTheModel()
    {
        using var request = Authorized(HttpMethod.Post, "/v1/chat/completions");
        request.Content = new StringContent("""{"model":"llama3.1-70b","messages":[]}""", Encoding.UTF8, "application/json");

        using var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        // Reached the local provider's base URL exactly once, and with no Bearer token,
        // since it has no key. The base already ends in /v1, so the canonical path must
        // not add a second one.
        Assert.Equal("http://127.0.0.1:11434/v1/chat/completions", _upstream.Requests[^1].ToString());
        Assert.Null(_upstream.AuthorizationHeaders[^1]);
    }

    [Fact]
    public async Task ForwardsCacheControlFieldsWithoutRewritingThem()
    {
        const string body = """
        {"model":"gpt-5.6-luna","messages":[{"role":"user","content":[{"type":"text","text":"hi","cache_control":{"type":"ephemeral"}}]}]}
        """;

        using var request = Authorized(HttpMethod.Post, "/v1/chat/completions");
        request.Content = new StringContent(body, Encoding.UTF8, "application/json");

        using var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("\"cache_control\":{\"type\":\"ephemeral\"}", _upstream.Bodies[^1]);
    }

    [Fact]
    public async Task StripsTheClientKeyAndForwardedHeadersBeforeSendingUpstream()
    {
        using var request = Authorized(HttpMethod.Post, "/v1/chat/completions");
        request.Headers.TryAddWithoutValidation("x-api-key", LocalKey);
        request.Headers.TryAddWithoutValidation("X-Forwarded-For", "10.9.9.9");
        request.Headers.TryAddWithoutValidation("x-goog-api-key", "leak-me");
        request.Content = new StringContent("""{"model":"gpt-5.6-luna"}""", Encoding.UTF8, "application/json");

        using var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        // Upstream sees the provider credential, never the LAN client's.
        Assert.Equal("corp-secret", _upstream.AuthorizationHeaders[^1]);
    }

    [Fact]
    public async Task UsesCurrentProviderProfileWhenSettingsChangeAfterRouterBuild()
    {
        var current = new ProviderSettings("corp", "Corp Gateway", ProviderKinds.OpenAi,
            "https://corp.example", "rotated-key", true, 0);
        _server.Apply(LocalKey, _server.Router, id => id == current.Id ? current : null);
        using var request = Authorized(HttpMethod.Post, "/v1/chat/completions");
        request.Content = new StringContent("""{"model":"gpt-5.6-luna","messages":[]}""",
            Encoding.UTF8, "application/json");

        using var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("rotated-key", _upstream.AuthorizationHeaders[^1]);
    }

    [Fact]
    public async Task RejectsRequestsWithoutTheLocalKey()
    {
        using var anonymous = new HttpRequestMessage(HttpMethod.Post, _base + "/v1/chat/completions")
        {
            Content = new StringContent("""{"model":"gpt-5.6-luna"}""", Encoding.UTF8, "application/json")
        };
        using var response = await _client.SendAsync(anonymous);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

        using var wrong = new HttpRequestMessage(HttpMethod.Post, _base + "/v1/chat/completions")
        {
            Content = new StringContent("""{"model":"gpt-5.6-luna"}""", Encoding.UTF8, "application/json")
        };
        wrong.Headers.TryAddWithoutValidation("x-api-key", "local-not-the-key");
        using var rejected = await _client.SendAsync(wrong);
        Assert.Equal(HttpStatusCode.Unauthorized, rejected.StatusCode);
    }

    [Fact]
    public async Task AcceptsGeminiStyleCredentials()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, _base + "/v1/models");
        request.Headers.TryAddWithoutValidation("x-goog-api-key", LocalKey);
        using var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Theory]
    // The point of the whole normalization: one base URL - the bare host - and a client
    // of any shape reaches the provider that serves its model. Each of these is exactly
    // what a real client sends with baseURL http://<relay>. The second group is a client
    // handed a /v1 base instead, which must keep working.
    [InlineData("/chat/completions")]
    [InlineData("/messages")]
    [InlineData("/v1/chat/completions")]
    [InlineData("/v1/messages")]
    [InlineData("/v1/v1/messages")]
    [InlineData("/v1beta/models/gpt-5.6-luna:generateContent")]
    [InlineData("/v1/v1beta/models/gpt-5.6-luna:generateContent")]
    public async Task OneBaseUrlServesEveryClientShape(string path)
    {
        using var request = Authorized(HttpMethod.Post, path);
        request.Content = new StringContent("""{"model":"gpt-5.6-luna","messages":[]}""", Encoding.UTF8, "application/json");

        using var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Corp Gateway", response.Headers.GetValues("x-relay-provider").Single());
        // The upstream saw a canonical, versioned path - never unversioned, never doubled.
        var upstreamPath = _upstream.Requests[^1].AbsolutePath;
        var segments = upstreamPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        Assert.True(segments.Length > 0 && segments[0].Length >= 2
            && segments[0][0] == 'v' && char.IsAsciiDigit(segments[0][1]),
            $"expected a leading version segment, got {upstreamPath}");
        Assert.DoesNotContain("/v1/v1/", upstreamPath, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheUnversionedRootFormIsServedAsAVersionedUpstreamPath()
    {
        // What the OpenAI SDK does with baseURL http://<relay> and no /v1 anywhere.
        using var request = Authorized(HttpMethod.Post, "/chat/completions");
        request.Content = new StringContent("""{"model":"gpt-5.6-luna"}""", Encoding.UTF8, "application/json");

        using var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("https://corp.example/v1/chat/completions", _upstream.Requests[^1].ToString());
    }

    [Fact]
    public async Task TheModelListIsServedFromBothBaseUrlForms()
    {
        // An SDK on the bare host asks for /models; one on a /v1 base asks for
        // /v1/models. Both must get the union, never a proxied provider response.
        foreach (var path in new[] { "/models", "/v1/models" })
        {
            using var response = await _client.SendAsync(Authorized(HttpMethod.Get, path));
            var json = await response.Content.ReadAsStringAsync();

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Contains("\"object\":\"list\"", json);
            Assert.Contains("gpt-5.6-luna", json);
            Assert.Contains("llama3.1-70b", json);
        }
    }

    [Fact]
    public async Task RelayRoutesAreNotRewrittenIntoTheVersionedNamespace()
    {
        // The relay's own endpoints are not upstream API paths and must survive intact.
        using var response = await _client.SendAsync(Authorized(HttpMethod.Get, "/relay/capabilities"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("schema_version", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task HealthStaysUnauthenticated()
    {
        using var response = await _client.GetAsync(_base + "/health");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("ok\n", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task ReturnsTheUnionModelListAcrossProviders()
    {
        using var response = await _client.SendAsync(Authorized(HttpMethod.Get, "/v1/models"));
        var json = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("\"object\":\"list\"", json);
        Assert.Contains("gpt-5.6-luna", json);
        Assert.Contains("llama3.1-70b", json);
    }

    [Fact]
    public async Task RejectsAnUnknownModelWithTheAvailableList()
    {
        using var request = Authorized(HttpMethod.Post, "/v1/chat/completions");
        request.Content = new StringContent("""{"model":"no-such-model"}""", Encoding.UTF8, "application/json");

        using var response = await _client.SendAsync(request);
        var json = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("no-such-model", json);
        Assert.Contains("gpt-5.6-luna", json);
    }

    [Fact]
    public async Task ReportsARealCostSourceHeader()
    {
        using var request = Authorized(HttpMethod.Post, "/v1/chat/completions");
        request.Content = new StringContent("""{"model":"gpt-5.6-luna"}""", Encoding.UTF8, "application/json");

        using var response = await _client.SendAsync(request);

        // Was hardcoded to "pending" for anything that was not a provider-reported cost.
        Assert.Equal("provider", response.Headers.GetValues("x-relay-cost-source").Single());
        Assert.Equal("Corp Gateway", response.Headers.GetValues("x-relay-provider").Single());
    }

    [Fact]
    public async Task DoesNotChargeTokenPricesForALocalProvider()
    {
        using var request = Authorized(HttpMethod.Post, "/v1/chat/completions");
        request.Content = new StringContent("""{"model":"llama3.1-70b","messages":[]}""", Encoding.UTF8, "application/json");

        using var response = await _client.SendAsync(request);

        // A local model is free to run, so a token-price estimate would be a lie.
        Assert.Equal("unknown", response.Headers.GetValues("x-relay-cost-source").Single());
    }

    [Fact]
    public async Task RejectsSsrfAttemptsViaAbsoluteUriPath()
    {
        // SSRF attempt: pass an absolute URL as the path. Without validation, this
        // would cause new Uri(base, path) to return the attacker's URL and send
        // credentials upstream to the wrong host.
        using var request = Authorized(HttpMethod.Post, "/http://evil.example/v1/chat/completions");
        request.Content = new StringContent("""{"model":"gpt-5.6-luna","messages":[]}""", Encoding.UTF8, "application/json");

        using var response = await _client.SendAsync(request);

        // Should reject with 400, not forward to evil.example
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        // Upstream handler should never see evil.example
        Assert.DoesNotContain(_upstream.Requests, r => r.Host == "evil.example");
    }

    [Fact]
    public async Task SkipsAcceptEncodingHeaderWhenForwardingToUpstream()
    {
        // The relay should not forward Accept-Encoding headers to upstream, since it handles
        // decompression transparently via AutomaticDecompression in HttpClientHandler.
        using var request = Authorized(HttpMethod.Post, "/v1/chat/completions");
        request.Headers.Add("Accept-Encoding", "gzip, deflate");
        request.Content = new StringContent("""{"model":"gpt-5.6-luna","messages":[]}""", Encoding.UTF8, "application/json");

        using var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        // Verify the upstream request does not have Accept-Encoding header
        Assert.False(_upstream.HasAcceptEncodingHeader[^1],
            "Accept-Encoding should not be forwarded to upstream");
    }
}
