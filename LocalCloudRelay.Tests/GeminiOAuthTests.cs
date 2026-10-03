using System.Net;
using System.Text;
using LocalCloudRelay;

namespace LocalCloudRelay.Tests;

public sealed class GeminiOAuthTests
{
    [Fact]
    public async Task CreateAuthorizationAttemptUsesGoogleScopesAndLoopbackPkce()
    {
        await using var attempt = GeminiOAuth.CreateAuthorizationAttempt("desktop-client", timeout: TimeSpan.FromMinutes(1));
        var query = ParseQuery(attempt.AuthorizationUri.Query);

        Assert.Equal("https://accounts.google.com/o/oauth2/v2/auth", attempt.AuthorizationUri.GetLeftPart(UriPartial.Path));
        Assert.Equal("desktop-client", query["client_id"]);
        Assert.Equal("code", query["response_type"]);
        Assert.Equal("S256", query["code_challenge_method"]);
        Assert.Equal(attempt.RedirectUri, query["redirect_uri"]);
        var redirect = new Uri(attempt.RedirectUri);
        Assert.Equal("http", redirect.Scheme);
        Assert.Equal("127.0.0.1", redirect.Host);
        Assert.True(redirect.Port > 0);
        Assert.Equal("/", redirect.AbsolutePath);
        Assert.Equal("offline", query["access_type"]);
        Assert.Contains("https://www.googleapis.com/auth/cloud-platform", query["scope"]);
        Assert.Contains("https://www.googleapis.com/auth/generative-language.retriever", query["scope"]);
    }

    [Fact]
    public async Task SignInExchangesCodeAndStoresClientProjectAndOAuthCredentials()
    {
        var store = new FakeGeminiAccountStore();
        var handler = new RecordingHandler(_ => JsonResponse("""{"access_token":"google-access","refresh_token":"google-refresh","expires_in":3600,"scope":"https://www.googleapis.com/auth/cloud-platform https://www.googleapis.com/auth/generative-language.retriever","token_type":"Bearer"}"""));
        using var http = new HttpClient(handler);
        var tokenClient = new OAuthTokenClient(http, store);
        var profile = GeminiProvider("gemini-profile") with { ProjectId = "cloud-project" };
        await using var attempt = GeminiOAuth.CreateAuthorizationAttempt("desktop-client", timeout: TimeSpan.FromMinutes(1));
        var callback = new OAuthCallbackResult("auth-code", new Dictionary<string, string>
        {
            ["state"] = attempt.Pkce.State
        });
        store.SetProfile(profile);

        var tokens = await GeminiOAuthSignIn.CompleteAsync(profile.Id, profile, "desktop-client", "desktop-secret",
            "cloud-project", attempt, callback, tokenClient, store);

        Assert.Equal("google-access", tokens.AccessToken);
        Assert.Equal("https://oauth2.googleapis.com/token", handler.Requests[0].Uri.AbsoluteUri);
        var form = handler.Requests[0].Body;
        Assert.Contains("client_secret=desktop-secret", form, StringComparison.Ordinal);
        Assert.Contains("code_verifier=", form, StringComparison.Ordinal);
        Assert.Equal("desktop-client", store.SavedProfile!.OAuthClientId);
        Assert.Equal("desktop-secret", store.SavedProfile.OAuthClientSecret);
        Assert.Equal("cloud-project", store.SavedProfile.ProjectId);
        Assert.Equal("google-access", store.SavedProfile.OAuthTokens!.AccessToken);
    }

    [Fact]
    public async Task SignInPreservesSavedRefreshTokenWhenGoogleDoesNotReturnAnother()
    {
        var store = new FakeGeminiAccountStore();
        var profile = GeminiProvider("gemini-profile") with { ProjectId = "cloud-project" };
        store.SetProfile(profile);
        store.SetTokens(new OAuthTokenSet("old-access", "existing-refresh", DateTimeOffset.UtcNow.AddMinutes(-1)));
        var handler = new RecordingHandler(_ => JsonResponse("""{"access_token":"new-access","expires_in":3600}"""));
        using var http = new HttpClient(handler);
        var tokenClient = new OAuthTokenClient(http, store);
        await using var attempt = GeminiOAuth.CreateAuthorizationAttempt("desktop-client", timeout: TimeSpan.FromMinutes(1));
        var callback = new OAuthCallbackResult("auth-code", new Dictionary<string, string> { ["state"] = attempt.Pkce.State });

        var tokens = await GeminiOAuthSignIn.CompleteAsync(profile.Id, profile, "desktop-client", "desktop-secret",
            "cloud-project", attempt, callback, tokenClient, store);

        Assert.Equal("existing-refresh", tokens.RefreshToken);
        Assert.Equal("existing-refresh", store.SavedProfile!.OAuthTokens!.RefreshToken);
    }

    [Fact]
    public void ParseModelPageKeepsExactCallableModelIdsAndDropsNonGenerativeModels()
    {
        const string json = """
        {"models":[
          {"name":"models/gemini-2.5-flash","displayName":"Gemini 2.5 Flash","supportedGenerationMethods":["generateContent","countTokens"]},
          {"name":"models/gemini-2.5-pro","supportedGenerationMethods":["generateContent"]},
          {"name":"models/text-embedding-004","supportedGenerationMethods":["embedContent"]},
          {"name":"models/no-methods"},
          {"name":"gemini-custom-case","supportedGenerationMethods":["generateContent"]}
        ],"nextPageToken":"page-2"}
        """;

        var page = GeminiOAuthModelClient.ParseModelPage(json);

        Assert.Equal(["gemini-2.5-flash", "gemini-2.5-pro", "gemini-custom-case"], page.Models);
        Assert.Equal("page-2", page.NextPageToken);
    }

    [Fact]
    public async Task FetchModelsUsesOAuthAndQuotaProjectAcrossPages()
    {
        var store = new FakeGeminiAccountStore();
        store.SetTokens(new OAuthTokenSet("access-token", "refresh-token", DateTimeOffset.UtcNow.AddHours(1)));
        var handler = new RecordingHandler(request =>
        {
            if (request.Uri.Query.Contains("pageToken", StringComparison.Ordinal))
                return JsonResponse("""{"models":[{"name":"models/gemini-2.5-pro","supportedGenerationMethods":["generateContent"]}]}""");
            return JsonResponse("""{"models":[{"name":"models/gemini-2.5-flash","supportedGenerationMethods":["generateContent"]}],"nextPageToken":"page-2"}""");
        });
        using var http = new HttpClient(handler);
        var client = new GeminiOAuthModelClient(http, new OAuthTokenClient(http, store));

        var models = await client.FetchModelsAsync(GeminiProvider("p") with
        {
            ProjectId = "cloud-project",
            OAuthClientId = "desktop-client",
            OAuthClientSecret = "desktop-secret",
            OAuthTokens = new ProviderOAuthTokens("access-token", "refresh-token", DateTimeOffset.UtcNow.AddHours(1))
        });

        Assert.Equal(["gemini-2.5-flash", "gemini-2.5-pro"], models);
        Assert.Equal(2, handler.Requests.Count);
        Assert.All(handler.Requests, request =>
        {
            Assert.Equal("Bearer access-token", request.Authorization);
            Assert.Equal("cloud-project", request.ProjectId);
        });
        Assert.Contains("pageToken=page-2", handler.Requests[1].Uri.Query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FetchModelsStopsWhenGoogleRepeatsPaginationToken()
    {
        var store = new FakeGeminiAccountStore();
        store.SetTokens(new OAuthTokenSet("access-token", "refresh-token", DateTimeOffset.UtcNow.AddHours(1)));
        var handler = new RecordingHandler(_ => JsonResponse("""{"models":[],"nextPageToken":"same-page"}"""));
        using var http = new HttpClient(handler);
        var client = new GeminiOAuthModelClient(http, new OAuthTokenClient(http, store));

        var error = await Assert.ThrowsAsync<GeminiOAuthException>(() => client.FetchModelsAsync(GeminiProvider("p") with
        {
            ProjectId = "cloud-project",
            OAuthClientId = "desktop-client",
            OAuthClientSecret = "desktop-secret"
        }));

        Assert.Contains("repeated page token", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task FetchModelsRejectsWrongProviderAndHonorsCancellation()
    {
        using var http = new HttpClient(new RecordingHandler(_ => throw new InvalidOperationException("unexpected request")));
        var client = new GeminiOAuthModelClient(http, new OAuthTokenClient(http));

        await Assert.ThrowsAsync<InvalidOperationException>(() => client.FetchModelsAsync(
            new ProviderSettings("p", "OpenAI", ProviderKinds.OpenAi, "https://example.test", null, true, 0)));
        var cancellation = new CancellationToken(canceled: true);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.FetchModelsAsync(GeminiProvider("p"), cancellation));
    }

    private static ProviderSettings GeminiProvider(string id) => new(id, "Gemini", ProviderKinds.Gemini,
        "https://generativelanguage.googleapis.com", null, true, 0, AuthMode: ProviderAuthMode.OAuth);

    private static HttpResponseMessage JsonResponse(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json")
    };

    private static Dictionary<string, string> ParseQuery(string query) => query.TrimStart('?')
        .Split('&', StringSplitOptions.RemoveEmptyEntries)
        .Select(field => field.Split('=', 2))
        .ToDictionary(pair => Uri.UnescapeDataString(pair[0]), pair => Uri.UnescapeDataString(pair[1]), StringComparer.Ordinal);

    private sealed class RecordingHandler(Func<RequestRecord, HttpResponseMessage> response) : HttpMessageHandler
    {
        public List<RequestRecord> Requests { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var record = new RequestRecord(request.RequestUri!, request.Headers.Authorization?.ToString(),
                request.Headers.TryGetValues("x-goog-user-project", out var values) ? values.Single() : null,
                request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken));
            Requests.Add(record);
            return response(record);
        }
    }

    private sealed record RequestRecord(Uri Uri, string? Authorization, string? ProjectId, string Body);

    private sealed class FakeGeminiAccountStore : IGeminiAccountStore
    {
        private OAuthTokenSet? _tokens;
        public ProviderSettings? SavedProfile { get; private set; }
        public ValueTask<OAuthTokenSet?> GetAsync(string profileId, CancellationToken cancellationToken = default) => ValueTask.FromResult(_tokens);
        public ValueTask SetAsync(string profileId, OAuthTokenSet tokens, CancellationToken cancellationToken = default) { _tokens = tokens; return ValueTask.CompletedTask; }
        public ProviderSettings? FindProvider(string id) => SavedProfile;
        public void SaveGeminiCredentials(string profileId, string clientId, string clientSecret, string projectId, OAuthTokenSet tokens)
        {
            _tokens = tokens;
            SavedProfile = SavedProfile! with
            {
                AuthMode = ProviderAuthMode.OAuth,
                OAuthClientId = clientId,
                OAuthClientSecret = clientSecret,
                ProjectId = projectId,
                OAuthTokens = new ProviderOAuthTokens(tokens.AccessToken, tokens.RefreshToken,
                    tokens.ExpiresAtUtc, tokens.Scope, tokens.TokenType, tokens.IdToken)
            };
        }

        public void SetTokens(OAuthTokenSet tokens) => _tokens = tokens;
        public void SetProfile(ProviderSettings profile) => SavedProfile = profile;
    }

}
