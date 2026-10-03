using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using LocalCloudRelay;

namespace LocalCloudRelay.Tests;

public sealed class GeminiOAuthRelayTests : IAsyncLifetime
{
    private const string LocalKey = "gemini-relay-test-key";
    private readonly RelayTelemetryStore _telemetry = new();
    private readonly GeminiTokenStore _tokens = new();
    private RecordingHandler _upstream = null!;
    private RelayServer _server = null!;
    private HttpClient _client = null!;
    private ProviderSettings[] _providers = [];
    private string _base = string.Empty;

    public async Task InitializeAsync()
    {
        _upstream = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("data: {\"candidates\":[{\"content\":{\"parts\":[{\"text\":\"hello\"}]}}]}\n\n", Encoding.UTF8, "text/event-stream")
        });
        _tokens.Set("one", new OAuthTokenSet("account-one-access", "refresh-one", DateTimeOffset.UtcNow.AddHours(1)));
        _tokens.Set("two", new OAuthTokenSet("account-two-access", "refresh-two", DateTimeOffset.UtcNow.AddHours(1)));
        _server = new RelayServer(_telemetry, _upstream, port: 0, oauthTokenStore: _tokens);
        _providers = [GeminiProvider("one"), GeminiProvider("two")];
        _server.Apply(LocalKey, new ProviderRouter(_providers, CatalogSnapshot.Empty),
            id => _providers.SingleOrDefault(provider => provider.Id == id));
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
    public async Task GeminiCatalogUsesNativeModelsShapeAndQualifiedExactAccountIds()
    {
        using var request = Authorized(HttpMethod.Get, "/v1beta/models");
        using var response = await _client.SendAsync(request);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var names = json.RootElement.GetProperty("models").EnumerateArray()
            .Select(model => model.GetProperty("name").GetString()!).ToArray();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(["models/account:one/gemini-2.5-flash", "models/account:two/gemini-2.5-flash"], names);
        Assert.Contains("generateContent", json.RootElement.GetProperty("models")[0]
            .GetProperty("supportedGenerationMethods").EnumerateArray().Select(method => method.GetString()));
    }

    [Theory]
    [InlineData("/v1beta/models")]
    [InlineData("/v1/v1beta/models")]
    public async Task GeminiCatalogNormalizesVersionPrefixedRoute(string path)
    {
        using var request = Authorized(HttpMethod.Get, path);
        using var response = await _client.SendAsync(request);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, json.RootElement.GetProperty("models").GetArrayLength());
        Assert.Empty(_upstream.Requests);
    }

    [Theory]
    [InlineData("generateContent")]
    [InlineData("streamGenerateContent")]
    public async Task RoutesGeminiAliasToExactAccountModelWithOAuthAndQuotaProject(string action)
    {
        const string requestBody = """{"contents":[{"role":"user","parts":[{"text":"hello"}]}],"generationConfig":{"temperature":0.2}}""";
        var query = action == "streamGenerateContent" ? "?alt=sse" : string.Empty;
        using var request = Authorized(HttpMethod.Post,
            $"/v1beta/models/account:one/gemini-2.5-flash:{action}{query}");
        request.Content = new StringContent(requestBody, Encoding.UTF8, "application/json");

        using var response = await _client.SendAsync(request);
        var responseBody = await response.Content.ReadAsStringAsync();
        var upstream = Assert.Single(_upstream.Requests);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal($"https://generativelanguage.googleapis.com/v1beta/models/gemini-2.5-flash:{action}{query}",
            upstream.Uri.AbsoluteUri);
        Assert.Equal("Bearer account-one-access", upstream.Authorization);
        Assert.Equal("cloud-project-one", upstream.ProjectId);
        Assert.Null(upstream.GoogleApiKey);
        Assert.Equal(requestBody, upstream.Body);
        Assert.Contains("data: {\"candidates\"", responseBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GeminiOAuthRejectsMissingProjectInsteadOfSendingUnscopedRequest()
    {
        _providers = [GeminiProvider("one") with { ProjectId = null }];
        _server.Apply(LocalKey, new ProviderRouter(_providers, CatalogSnapshot.Empty),
            id => _providers.SingleOrDefault(provider => provider.Id == id));
        using var request = Authorized(HttpMethod.Post, "/v1beta/models/gemini-2.5-flash:generateContent");
        request.Content = new StringContent("{}", Encoding.UTF8, "application/json");

        using var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(_upstream.Requests);
    }

    [Fact]
    public async Task GeminiOAuthPinsUpstreamOriginInsteadOfSendingCredentialsToConfiguredBaseUrl()
    {
        _providers = [GeminiProvider("one") with { BaseUrl = "https://attacker.example/collect" }];
        _server.Apply(LocalKey, new ProviderRouter(_providers, CatalogSnapshot.Empty),
            id => _providers.SingleOrDefault(provider => provider.Id == id));
        using var request = Authorized(HttpMethod.Post, "/v1beta/models/gemini-2.5-flash:generateContent");
        request.Content = new StringContent("{}", Encoding.UTF8, "application/json");

        using var response = await _client.SendAsync(request);
        var upstream = Assert.Single(_upstream.Requests);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("https://generativelanguage.googleapis.com/v1beta/models/gemini-2.5-flash:generateContent",
            upstream.Uri.AbsoluteUri);
        Assert.Equal("Bearer account-one-access", upstream.Authorization);
        Assert.Equal("cloud-project-one", upstream.ProjectId);
    }

    private HttpRequestMessage Authorized(HttpMethod method, string path)
    {
        var request = new HttpRequestMessage(method, _base + path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", LocalKey);
        return request;
    }

    private static ProviderSettings GeminiProvider(string id) => new(id, "Gemini " + id,
        ProviderKinds.Gemini, "https://generativelanguage.googleapis.com", null, true, 0,
        AuthMode: ProviderAuthMode.OAuth,
        ProjectId: "cloud-project-" + id,
        ImportedModels: ["gemini-2.5-flash"],
        OAuthTokens: new ProviderOAuthTokens("account-" + id + "-access", "refresh-" + id,
            DateTimeOffset.UtcNow.AddHours(1)),
        OAuthClientId: "desktop-client-" + id,
        OAuthClientSecret: "desktop-secret-" + id);

    private sealed class GeminiTokenStore : IOAuthTokenStore
    {
        private readonly Dictionary<string, OAuthTokenSet> _values = new(StringComparer.Ordinal);
        public ValueTask<OAuthTokenSet?> GetAsync(string profileId, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(_values.TryGetValue(profileId, out var value) ? value : null);
        public ValueTask SetAsync(string profileId, OAuthTokenSet tokens, CancellationToken cancellationToken = default)
        {
            _values[profileId] = tokens;
            return ValueTask.CompletedTask;
        }
        public void Set(string profileId, OAuthTokenSet tokens) => _values[profileId] = tokens;
    }

    private sealed record CapturedRequest(Uri Uri, string? Authorization, string? ProjectId,
        string? GoogleApiKey, string Body);

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> reply) : HttpMessageHandler
    {
        public List<CapturedRequest> Requests { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var captured = new CapturedRequest(request.RequestUri!, request.Headers.Authorization?.ToString(),
                request.Headers.TryGetValues("x-goog-user-project", out var project) ? project.Single() : null,
                request.Headers.TryGetValues("x-goog-api-key", out var apiKey) ? apiKey.Single() : null,
                request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken));
            Requests.Add(captured);
            return reply(request);
        }
    }
}
