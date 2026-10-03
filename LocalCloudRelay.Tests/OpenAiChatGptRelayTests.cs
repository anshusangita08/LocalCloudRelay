using System.Net;
using System.Net.Http.Headers;
using System.Text;
using LocalCloudRelay;

namespace LocalCloudRelay.Tests;

public sealed class OpenAiChatGptRelayTests : IAsyncLifetime
{
    private const string LocalKey = "openai-relay-test-key";
    private readonly RelayTelemetryStore _telemetry = new();
    private FakeUpstreamHandler _upstream = null!;
    private RelayServer _server = null!;
    private HttpClient _client = null!;
    private string _base = string.Empty;
    private ProviderSettings[] _providers = [];
    private string _streamBody = string.Empty;

    public async Task InitializeAsync()
    {
        const string stream = """
        event: response.created
        data: {"response":{"id":"resp-1","model":"gpt-5.6","status":"in_progress"}}

        event: response.output_text.delta
        data: {"delta":"hello"}

        event: response.completed
        data: {"response":{"id":"resp-1","model":"gpt-5.6","status":"completed","output":[{"type":"message","content":[{"type":"output_text","text":"hello"}]}],"usage":{"input_tokens":2,"output_tokens":1}}}

        """;
        _streamBody = stream;
        _upstream = new FakeUpstreamHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(_streamBody, Encoding.UTF8, "text/event-stream")
        });
        var tokens = new TestOAuthTokenStore(new OAuthTokenSet("account-access", "account-refresh",
            DateTimeOffset.UtcNow.AddHours(1), scope: "openid resource.invoke chatgpt.tokens.use.direct"));
        _server = new RelayServer(_telemetry, _upstream, port: 0, oauthTokenStore: tokens);
        _providers =
        [
            OpenAiProvider("one", "account-one"),
            OpenAiProvider("two", "account-two")
        ];
        var router = new ProviderRouter(_providers, new CatalogSnapshot(DateTimeOffset.UtcNow,
            new Dictionary<string, IReadOnlyList<string>>()));
        _server.Apply(LocalKey, router, id => _providers.SingleOrDefault(provider => provider.Id == id));
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

    [Fact]
    public async Task RoutesAccountAliasToResponsesWithUnderlyingModelAndOAuthToken()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, _base + "/v1/chat/completions")
        {
            Content = new StringContent("""{"model":"account:one/gpt-5.6","stream":true,"messages":[{"role":"user","content":"hello"}]}""",
                Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", LocalKey);

        using var response = await _client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        var upstreamRequest = _upstream.Requests[^1];
        using var upstreamBody = System.Text.Json.JsonDocument.Parse(_upstream.Bodies[^1]);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("https://api.openai.com/v1/responses", upstreamRequest.ToString());
        Assert.Equal("account-access", _upstream.AuthorizationHeaders[^1]);
        Assert.Equal("gpt-5.6", upstreamBody.RootElement.GetProperty("model").GetString());
        Assert.False(upstreamBody.RootElement.GetProperty("store").GetBoolean());
        Assert.True(upstreamBody.RootElement.GetProperty("stream").GetBoolean());
        Assert.Contains("\"content\":\"hello\"", body, StringComparison.Ordinal);
        Assert.Contains("data: [DONE]", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ConvertsNonStreamingAnthropicMessagesFromResponsesCompletion()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, _base + "/v1/messages")
        {
            Content = new StringContent("""{"model":"account:one/gpt-5.6","stream":false,"max_tokens":64,"system":"Be concise","messages":[{"role":"user","content":"hello"}]}""",
                Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", LocalKey);

        using var response = await _client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        using var responseJson = System.Text.Json.JsonDocument.Parse(body);
        using var upstreamJson = System.Text.Json.JsonDocument.Parse(_upstream.Bodies[^1]);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("assistant", responseJson.RootElement.GetProperty("role").GetString());
        Assert.Equal("hello", responseJson.RootElement.GetProperty("content")[0].GetProperty("text").GetString());
        Assert.Equal("Be concise", upstreamJson.RootElement.GetProperty("instructions").GetString());
        Assert.Equal("gpt-5.6", upstreamJson.RootElement.GetProperty("model").GetString());
    }

    [Fact]
    public async Task RejectsAccountInferenceWhenDirectUsageConsentIsMissing()
    {
        _providers = _providers.Select(provider => provider with
        {
            OAuthTokens = provider.OAuthTokens! with { Scope = "openid profile email" }
        }).ToArray();
        _server.Apply(LocalKey, new ProviderRouter(_providers, CatalogSnapshot.Empty),
            id => _providers.SingleOrDefault(provider => provider.Id == id));
        using var request = new HttpRequestMessage(HttpMethod.Post, _base + "/v1/chat/completions")
        {
            Content = new StringContent("""{"model":"account:one/gpt-5.6","messages":[{"role":"user","content":"hello"}]}""",
                Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", LocalKey);

        using var response = await _client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains("not authorized for ChatGPT plan usage", body, StringComparison.Ordinal);
        Assert.Empty(_upstream.Requests);
    }

    private static ProviderSettings OpenAiProvider(string id, string accountId) => new(
        id, "OpenAI " + id, ProviderKinds.OpenAi, "https://api.openai.com/v1", null, true, 0,
        AuthMode: ProviderAuthMode.OAuth,
        AccountId: accountId,
        ImportedModels: ["gpt-5.6"],
        OAuthTokens: new ProviderOAuthTokens("account-access", "account-refresh", DateTimeOffset.UtcNow.AddHours(1),
            "openid resource.invoke chatgpt.tokens.use.direct"),
        OAuthClientId: "issued-client-" + id);

    private sealed class TestOAuthTokenStore(OAuthTokenSet tokens) : IOAuthTokenStore
    {
        public ValueTask<OAuthTokenSet?> GetAsync(string profileId, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<OAuthTokenSet?>(tokens);

        public ValueTask SetAsync(string profileId, OAuthTokenSet value, CancellationToken cancellationToken = default)
        {
            tokens = value;
            return ValueTask.CompletedTask;
        }
    }
}
