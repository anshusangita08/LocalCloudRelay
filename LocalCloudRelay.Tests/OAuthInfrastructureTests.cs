using System.Net;
using System.Security.Cryptography;
using System.Text;
using LocalCloudRelay;

namespace LocalCloudRelay.Tests;

public sealed class OAuthInfrastructureTests
{
    [Fact]
    public void PkceGeneratesUrlSafeVerifierChallengeAndState()
    {
        var generated = 0;
        var pair = OAuthPkce.Create(count => Enumerable.Range(++generated, count).Select(value => (byte)value).ToArray());

        Assert.Equal(2, generated);
        Assert.Equal(43, pair.Verifier.Length);
        Assert.Equal(43, pair.Challenge.Length);
        Assert.Equal(43, pair.State.Length);
        Assert.Matches("^[A-Za-z0-9_-]+$", pair.Verifier);
        Assert.Matches("^[A-Za-z0-9_-]+$", pair.Challenge);
        Assert.Matches("^[A-Za-z0-9_-]+$", pair.State);
        var expectedChallenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(pair.Verifier)));
        Assert.Equal(expectedChallenge, pair.Challenge);
    }

    [Fact]
    public async Task AuthorizationUriIncludesPkceScopesAndProviderExtensionParameters()
    {
        var pkce = OAuthPkce.Create(_ => Enumerable.Range(1, 32).Select(value => (byte)value).ToArray());
        await using var callback = LoopbackOAuthCallback.Start("/oauth", pkce.State, TimeSpan.FromSeconds(10));

        var uri = OAuthAuthorizationUri.Create(new Uri("https://issuer.example/authorize"), "client-id", callback,
            pkce, ["openid", "profile"], new Dictionary<string, string> { ["access_type"] = "offline", ["resource"] = "https://api.openai.com/v1" });
        var query = ParseQuery(uri.Query);

        Assert.Equal("code", query["response_type"]);
        Assert.Equal("client-id", query["client_id"]);
        Assert.Equal(callback.RedirectUri.ToString(), query["redirect_uri"]);
        Assert.Equal(pkce.Challenge, query["code_challenge"]);
        Assert.Equal("S256", query["code_challenge_method"]);
        Assert.Equal(pkce.State, query["state"]);
        Assert.Equal("openid profile", query["scope"]);
        Assert.Equal("offline", query["access_type"]);
        Assert.Equal("https://api.openai.com/v1", query["resource"]);
    }

    [Fact]
    public async Task LoopbackCallbackBindsToIpv4LoopbackAndDoesNotReturnCodeInResponse()
    {
        const string state = "state-value";
        await using var callback = LoopbackOAuthCallback.Start("/oauth/callback", state, TimeSpan.FromSeconds(10));
        var waiting = callback.WaitForCallbackAsync();
        using var client = new HttpClient();
        var response = await client.GetAsync(AddQuery(callback.RedirectUri, "state=state-value&code=secret-code&client_id=dynamic-client"));
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(IPAddress.Loopback, IPAddress.Parse(callback.RedirectUri.Host));
        Assert.InRange(callback.RedirectUri.Port, 1, 65535);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.DoesNotContain("secret-code", body, StringComparison.Ordinal);
        var result = await waiting.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal("secret-code", result.Code);
        Assert.Equal("dynamic-client", result.ClientId);
    }

    [Fact]
    public async Task LoopbackCallbackRejectsWrongPathAndStateBeforeAcceptingValidCallback()
    {
        await using var callback = LoopbackOAuthCallback.Start("/oauth/callback", "expected", TimeSpan.FromSeconds(10));
        var waiting = callback.WaitForCallbackAsync();
        using var client = new HttpClient();

        using var wrongPath = await client.GetAsync(new Uri(callback.RedirectUri, "/other?state=expected&code=ignored"));
        using var wrongState = await client.GetAsync(AddQuery(callback.RedirectUri, "state=attacker&code=ignored"));
        using var duplicateState = await client.GetAsync(AddQuery(callback.RedirectUri, "state=expected&state=attacker&code=ignored"));
        using var valid = await client.GetAsync(AddQuery(callback.RedirectUri, "state=expected&code=accepted"));

        Assert.Equal(HttpStatusCode.NotFound, wrongPath.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, wrongState.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, duplicateState.StatusCode);
        Assert.Equal(HttpStatusCode.OK, valid.StatusCode);
        Assert.Equal("accepted", (await waiting.WaitAsync(TimeSpan.FromSeconds(2))).Code);
    }

    [Fact]
    public async Task LoopbackCallbackTerminatesOnValidStateAuthorizationDenialAndSanitizesError()
    {
        const string secret = "provider-private-description";
        await using var callback = LoopbackOAuthCallback.Start("/oauth/callback", "expected", TimeSpan.FromSeconds(10));
        var waiting = callback.WaitForCallbackAsync();
        using var client = new HttpClient();

        using var response = await client.GetAsync(AddQuery(callback.RedirectUri,
            $"state=expected&error=access_denied&error_description={Uri.EscapeDataString(secret)}"));
        var body = await response.Content.ReadAsStringAsync();
        var error = await Assert.ThrowsAsync<OAuthAuthorizationException>(() => waiting.WaitAsync(TimeSpan.FromSeconds(2)));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("The OAuth provider returned an authorization error.", error.Message);
        Assert.DoesNotContain(secret, error.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(secret, body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LoopbackCallbackPropagatesCancellationWhileWaiting()
    {
        await using var callback = LoopbackOAuthCallback.Start("/oauth/callback", "expected", TimeSpan.FromMinutes(1));
        using var cancellation = new CancellationTokenSource();
        var waiting = callback.WaitForCallbackAsync(cancellation.Token);

        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
    }

    [Fact]
    public async Task LoopbackCallbackThrowsTimeoutWhenNoAuthorizationResponseArrives()
    {
        var timeProvider = new ManualTimerTimeProvider();
        await using var callback = LoopbackOAuthCallback.Start("/oauth/callback", "expected", TimeSpan.FromMinutes(1), timeProvider);
        var waiting = callback.WaitForCallbackAsync();

        timeProvider.FireTimers();

        await Assert.ThrowsAsync<TimeoutException>(() => waiting.WaitAsync(TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public async Task TokenExchangeUsesAuthorizationCodeFormAndParsesCredentials()
    {
        using var handler = new RecordingHandler((_, _) => Task.FromResult(Json(HttpStatusCode.OK,
            """{"access_token":"access-secret","refresh_token":"refresh-secret","expires_in":3600,"token_type":"Bearer"}""")));
        using var http = new HttpClient(handler);
        var now = new DateTimeOffset(2030, 1, 2, 3, 4, 5, TimeSpan.Zero);
        var client = new OAuthTokenClient(http, timeProvider: new FixedTimeProvider(now));

        var tokens = await client.ExchangeCodeAsync(new OAuthAuthorizationCodeGrant(
            new Uri("https://issuer.example/token"), "client-id", "client-secret", "auth-code", "http://127.0.0.1:4321/oauth", "verifier",
            new Dictionary<string, string> { ["resource"] = "https://api.openai.com/v1", ["scope"] = "model.read" }));

        Assert.Equal("access-secret", tokens.AccessToken);
        Assert.Equal("refresh-secret", tokens.RefreshToken);
        Assert.Equal("Bearer", tokens.TokenType);
        Assert.Equal(now.AddHours(1), tokens.ExpiresAtUtc);
        Assert.Equal("authorization_code", handler.FormValues["grant_type"]);
        Assert.Equal("auth-code", handler.FormValues["code"]);
        Assert.Equal("verifier", handler.FormValues["code_verifier"]);
        Assert.Equal("client-secret", handler.FormValues["client_secret"]);
        Assert.Equal("https://api.openai.com/v1", handler.FormValues["resource"]);
        Assert.Equal("model.read", handler.FormValues["scope"]);
    }

    [Fact]
    public async Task TokenExchangeSanitizesUnsuccessfulProviderResponse()
    {
        const string secret = "provider-echoed-refresh-secret";
        using var handler = new RecordingHandler((_, _) => Task.FromResult(Json(HttpStatusCode.BadRequest,
            $$"""{"error":"invalid_grant","description":"{{secret}}"}""")));
        using var http = new HttpClient(handler);
        var client = new OAuthTokenClient(http);

        var error = await Assert.ThrowsAsync<OAuthTokenException>(() => client.ExchangeCodeAsync(new OAuthAuthorizationCodeGrant(
            new Uri("https://issuer.example/token"), "client-id", null, "auth-code", "http://127.0.0.1:4321/oauth", "verifier")));

        Assert.DoesNotContain(secret, error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(secret, error.ToString(), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.BadRequest, error.StatusCode);
    }

    [Fact]
    public async Task RefreshForProfileSerializesRefreshAndReusesRotatedTokens()
    {
        var now = new DateTimeOffset(2030, 1, 2, 3, 4, 5, TimeSpan.Zero);
        var store = new MemoryOAuthTokenStore(new OAuthTokenSet("expired-access", "old-refresh", now.AddMinutes(-1)));
        var releaseResponse = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var requestSeen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var requestCount = 0;
        using var handler = new RecordingHandler(async (_, cancellationToken) =>
        {
            Interlocked.Increment(ref requestCount);
            requestSeen.TrySetResult();
            await releaseResponse.Task.WaitAsync(cancellationToken);
            return Json(HttpStatusCode.OK, """{"access_token":"rotated-access","refresh_token":"rotated-refresh","expires_in":3600}""");
        });
        using var http = new HttpClient(handler);
        var client = new OAuthTokenClient(http, store, new FixedTimeProvider(now));
        var endpoint = new Uri("https://issuer.example/token");

        var first = client.GetValidAccessTokenAsync("profile-a", endpoint, "client-id", null, TimeSpan.FromMinutes(1));
        await requestSeen.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var second = client.GetValidAccessTokenAsync("profile-a", endpoint, "client-id", null, TimeSpan.FromMinutes(1));
        releaseResponse.TrySetResult();

        Assert.Equal("rotated-access", await first.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal("rotated-access", await second.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal(1, requestCount);
        Assert.Equal("rotated-refresh", store.Tokens!.RefreshToken);
    }

    [Fact]
    public async Task RefreshPreservesGrantedScopesAndIdTokenWhenProviderOmitsThem()
    {
        var now = new DateTimeOffset(2030, 1, 2, 3, 4, 5, TimeSpan.Zero);
        var store = new MemoryOAuthTokenStore(new OAuthTokenSet("expired", "refresh", now.AddMinutes(-1),
            scope: "openid resource.invoke chatgpt.tokens.use.direct", idToken: "validated-id-token"));
        using var handler = new RecordingHandler((_, _) => Task.FromResult(Json(HttpStatusCode.OK,
            """{"access_token":"new-access","refresh_token":"new-refresh","expires_in":3600}""")));
        using var http = new HttpClient(handler);
        var client = new OAuthTokenClient(http, store, new FixedTimeProvider(now));

        _ = await client.GetValidAccessTokenAsync("profile", new Uri("https://issuer.example/token"), "client", null,
            TimeSpan.FromMinutes(2));

        Assert.Equal("openid resource.invoke chatgpt.tokens.use.direct", store.Tokens!.Scope);
        Assert.Equal("validated-id-token", store.Tokens.IdToken);
    }

    [Fact]
    public async Task RefreshForProfilePersistsRotatedRefreshTokenBeforeSurfacingCancellation()
    {
        var now = new DateTimeOffset(2030, 1, 2, 3, 4, 5, TimeSpan.Zero);
        using var cancellation = new CancellationTokenSource();
        var store = new CancelOnSaveOAuthTokenStore(
            new OAuthTokenSet("expired-access", "old-refresh", now.AddMinutes(-1)), cancellation);
        using var handler = new RecordingHandler((_, _) => Task.FromResult(Json(HttpStatusCode.OK,
            """{"access_token":"rotated-access","refresh_token":"rotated-refresh","expires_in":3600}""")));
        using var http = new HttpClient(handler);
        var client = new OAuthTokenClient(http, store, new FixedTimeProvider(now));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.GetValidAccessTokenAsync(
            "profile-cancel-after-response", new Uri("https://issuer.example/token"), "client-id", null,
            TimeSpan.FromMinutes(1), cancellationToken: cancellation.Token));

        Assert.Equal("rotated-refresh", store.Tokens!.RefreshToken);
    }

    [Fact]
    public async Task RefreshForProfileCancellationWhileWaitingDoesNotSendRequest()
    {
        var now = new DateTimeOffset(2030, 1, 2, 3, 4, 5, TimeSpan.Zero);
        var store = new MemoryOAuthTokenStore(new OAuthTokenSet("expired", "refresh", now.AddMinutes(-1)));
        using var handler = new RecordingHandler((_, _) => Task.FromResult(Json(HttpStatusCode.OK, "{}")));
        using var http = new HttpClient(handler);
        var client = new OAuthTokenClient(http, store, new FixedTimeProvider(now));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.GetValidAccessTokenAsync(
            "profile-cancel", new Uri("https://issuer.example/token"), "client-id", null,
            TimeSpan.FromMinutes(1), cancellationToken: cancellation.Token));
        Assert.Equal(0, handler.RequestCount);
    }

    private static Uri AddQuery(Uri uri, string query) => new($"{uri}?{query}");
    private static Dictionary<string, string> ParseQuery(string query) => query.TrimStart('?').Split('&')
        .Select(part => part.Split('=', 2))
        .ToDictionary(pair => Uri.UnescapeDataString(pair[0]), pair => Uri.UnescapeDataString(pair[1]), StringComparer.Ordinal);
    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private static HttpResponseMessage Json(HttpStatusCode status, string body) => new(status)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json")
    };

    private sealed class RecordingHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        private int _requestCount;
        public int RequestCount => Volatile.Read(ref _requestCount);
        public Dictionary<string, string> FormValues { get; private set; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _requestCount);
            if (request.Content is not null)
            {
                var content = await request.Content.ReadAsStringAsync(cancellationToken);
                FormValues = content.Split('&', StringSplitOptions.RemoveEmptyEntries)
                    .Select(part => part.Split('=', 2))
                    .ToDictionary(pair => Uri.UnescapeDataString(pair[0]), pair => Uri.UnescapeDataString(pair[1]), StringComparer.Ordinal);
            }
            return await respond(request, cancellationToken);
        }
    }

    private sealed class MemoryOAuthTokenStore(OAuthTokenSet tokens) : IOAuthTokenStore
    {
        public OAuthTokenSet? Tokens { get; private set; } = tokens;
        public ValueTask<OAuthTokenSet?> GetAsync(string profileId, CancellationToken cancellationToken = default) => ValueTask.FromResult(Tokens);
        public ValueTask SetAsync(string profileId, OAuthTokenSet value, CancellationToken cancellationToken = default)
        {
            Tokens = value;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class CancelOnSaveOAuthTokenStore(OAuthTokenSet tokens, CancellationTokenSource cancellation) : IOAuthTokenStore
    {
        public OAuthTokenSet? Tokens { get; private set; } = tokens;

        public ValueTask<OAuthTokenSet?> GetAsync(string profileId, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(Tokens);

        public ValueTask SetAsync(string profileId, OAuthTokenSet value, CancellationToken cancellationToken = default)
        {
            cancellation.Cancel();
            cancellationToken.ThrowIfCancellationRequested();
            Tokens = value;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class ManualTimerTimeProvider : TimeProvider
    {
        private readonly List<ManualTimer> _timers = [];

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ManualTimer(callback, state);
            _timers.Add(timer);
            return timer;
        }

        public void FireTimers()
        {
            foreach (var timer in _timers.ToArray()) timer.Fire();
        }

        private sealed class ManualTimer(TimerCallback callback, object? state) : ITimer
        {
            private bool _disposed;

            public bool Change(TimeSpan dueTime, TimeSpan period) => !_disposed;
            public void Dispose() => _disposed = true;
            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }
            public void Fire()
            {
                if (!_disposed) callback(state);
            }
        }
    }
}
