using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LocalCloudRelay;

namespace LocalCloudRelay.Tests;

public sealed class OpenAiChatGptOAuthTests
{
    [Fact]
    public async Task CompleteSignInPersistsIssuedRegistrationBeforeExchangingAndSavesVerifiedIdentity()
    {
        var profile = OpenAiProfile() with { OAuthClientId = null, AccountId = null };
        var store = new MemoryAccountStore(profile);
        using var handler = new OAuthResponseHandler(async (request, _) =>
        {
            Assert.Equal(OpenAiChatGptOAuth.TokenEndpoint, request.RequestUri?.ToString());
            Assert.Equal("issued-client", store.Provider.OAuthClientId);
            var fields = await ReadFormAsync(request);
            Assert.Equal("issued-client", fields["client_id"]);
            Assert.Equal("https://api.openai.com/v1", fields["resource"]);
            Assert.Equal("authorization_code", fields["grant_type"]);
            return Json(HttpStatusCode.OK, """
            {"access_token":"access-value","refresh_token":"refresh-value","id_token":"signed-id-token","expires_in":3600,"scope":"openid profile email offline_access resource.invoke chatgpt.tokens.use.direct"}
            """);
        });
        using var http = new HttpClient(handler);
        var tokenClient = new OAuthTokenClient(http);
        var validator = new FakeIdentityValidator(new OpenAiAccountIdentity("verified-subject", "user@example.com", OpenAiChatGptOAuth.Issuer));
        await using var attempt = OpenAiChatGptOAuth.CreateAuthorizationAttempt(null, "urn:uuid:host");
        var callback = await SimulateCallbackAsync(attempt, "issued-client");

        var identity = await OpenAiChatGptSignIn.CompleteAsync(profile.Id, "urn:uuid:host", profile,
            attempt, callback, tokenClient, store, validator);

        Assert.Equal("verified-subject", identity.Subject);
        Assert.Equal("user@example.com", store.Provider.OAuthEmail);
        Assert.Equal("issued-client", store.Provider.OAuthClientId);
        Assert.Equal("verified-subject", store.Provider.AccountId);
        Assert.Equal("access-value", store.Tokens!.AccessToken);
        Assert.Equal("signed-id-token", store.Tokens.IdToken);
        Assert.Equal(attempt.Nonce, validator.ExpectedNonce);
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task CompleteSignInRejectsACallbackForAnotherSavedRegistration()
    {
        var profile = OpenAiProfile() with { OAuthClientId = "selected-client", AccountId = "selected-subject" };
        var store = new MemoryAccountStore(profile);
        using var handler = new OAuthResponseHandler((_, _) => throw new InvalidOperationException("Token exchange must not occur."));
        using var http = new HttpClient(handler);
        var tokenClient = new OAuthTokenClient(http);
        await using var attempt = OpenAiChatGptOAuth.CreateAuthorizationAttempt("selected-client", "urn:uuid:host");
        var callback = await SimulateCallbackAsync(attempt, "different-client");

        var error = await Assert.ThrowsAsync<OpenAiChatGptException>(() => OpenAiChatGptSignIn.CompleteAsync(
            profile.Id, "urn:uuid:host", profile, attempt, callback, tokenClient, store,
            new FakeIdentityValidator(new OpenAiAccountIdentity("selected-subject", null, OpenAiChatGptOAuth.Issuer))));

        Assert.Equal("OpenAI returned a different account registration than the one selected.", error.Message);
        Assert.Equal(0, handler.RequestCount);
    }

    [Fact]
    public async Task IdTokenValidatorChecksSignatureIssuerAudienceExpiryAndNonce()
    {
        using var rsa = RSA.Create(2048);
        var now = DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        const string nonce = "nonce-value";
        var token = CreateIdToken(rsa, "signing-key", now.AddMinutes(5).ToUnixTimeSeconds(), nonce);
        var jwks = new JsonObjectBuilder()
            .Add("keys", new[]
            {
                new Dictionary<string, string>
                {
                    ["kid"] = "signing-key", ["kty"] = "RSA", ["use"] = "sig",
                    ["n"] = Base64Url(rsa.ExportParameters(false).Modulus!),
                    ["e"] = Base64Url(rsa.ExportParameters(false).Exponent!)
                }
            }).Build();
        using var http = new HttpClient(new OAuthResponseHandler((request, _) =>
        {
            Assert.Equal(OpenAiChatGptOAuth.JwksEndpoint, request.RequestUri?.ToString());
            return Task.FromResult(Json(HttpStatusCode.OK, jwks));
        }));
        var validator = new OpenAiIdTokenValidator(http, new FixedTimeProvider(now));

        var identity = await validator.ValidateAsync(token, "issued-client", nonce);

        Assert.Equal("verified-subject", identity.Subject);
        Assert.Equal("user@example.com", identity.Email);
        Assert.Equal(OpenAiChatGptOAuth.Issuer, identity.Issuer);
        await Assert.ThrowsAsync<OpenAiChatGptException>(() => validator.ValidateAsync(token, "issued-client", "wrong-nonce"));
        await Assert.ThrowsAsync<OpenAiChatGptException>(() => validator.ValidateAsync(token, "other-client", nonce));
        await Assert.ThrowsAsync<OpenAiChatGptException>(() => validator.ValidateAsync(
            CreateIdToken(rsa, "signing-key", now.AddMinutes(-1).ToUnixTimeSeconds(), nonce), "issued-client", nonce));
    }

    private static async Task<OAuthCallbackResult> SimulateCallbackAsync(OpenAiOAuthAuthorizationAttempt attempt, string clientId)
    {
        using var client = new HttpClient();
        var callbackTask = attempt.WaitForCallbackAsync();
        var uri = new Uri(attempt.RedirectUri + $"?state={Uri.EscapeDataString(attempt.Pkce.State)}&code=one-time-code&client_id={Uri.EscapeDataString(clientId)}");
        using var response = await client.GetAsync(uri);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await callbackTask.WaitAsync(TimeSpan.FromSeconds(2));
    }

    private static async Task<Dictionary<string, string>> ReadFormAsync(HttpRequestMessage request)
    {
        var body = await request.Content!.ReadAsStringAsync();
        return body.Split('&', StringSplitOptions.RemoveEmptyEntries).Select(part => part.Split('=', 2))
            .ToDictionary(pair => Uri.UnescapeDataString(pair[0]), pair => Uri.UnescapeDataString(pair[1]), StringComparer.Ordinal);
    }

    private static string CreateIdToken(RSA rsa, string keyId, long expires, string nonce)
    {
        var header = Base64Url(Encoding.UTF8.GetBytes("""{"alg":"RS256","kid":"signing-key","typ":"JWT"}"""));
        var payload = Base64Url(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            iss = OpenAiChatGptOAuth.Issuer,
            aud = "issued-client",
            exp = expires,
            nonce,
            sub = "verified-subject",
            email = "user@example.com"
        })));
        var data = Encoding.ASCII.GetBytes(header + "." + payload);
        var signature = rsa.SignData(data, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return header + "." + payload + "." + Base64Url(signature);
    }

    private static string Base64Url(byte[] value) =>
        Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static HttpResponseMessage Json(HttpStatusCode status, string body) => new(status)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json")
    };

    private static ProviderSettings OpenAiProfile() => new(
        "openai-profile", "OpenAI Account", ProviderKinds.OpenAi, "https://api.openai.com/v1", null, true, 0,
        AuthMode: ProviderAuthMode.OAuth);

    private sealed class OAuthResponseHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            return respond(request, cancellationToken);
        }
    }

    private sealed class MemoryAccountStore(ProviderSettings provider) : IOpenAiAccountStore
    {
        public ProviderSettings Provider { get; private set; } = provider;
        public OAuthTokenSet? Tokens { get; private set; }

        public ProviderSettings? FindProvider(string id) => Provider.Id == id ? Provider : null;

        public void SaveOpenAiRegistration(string profileId, string clientId) =>
            Provider = Provider with { OAuthClientId = clientId };

        public void SaveOpenAiIdentity(string profileId, string clientId, OpenAiAccountIdentity identity, OAuthTokenSet tokens)
        {
            Provider = Provider with
            {
                AuthMode = ProviderAuthMode.OAuth,
                OAuthClientId = clientId,
                AccountId = identity.Subject,
                OAuthEmail = identity.Email,
                OAuthIssuer = identity.Issuer
            };
            Tokens = tokens;
        }

        public ValueTask<OAuthTokenSet?> GetAsync(string profileId, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(Tokens);

        public ValueTask SetAsync(string profileId, OAuthTokenSet tokens, CancellationToken cancellationToken = default)
        {
            Tokens = tokens;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeIdentityValidator(OpenAiAccountIdentity identity) : IOpenAiIdTokenValidator
    {
        public string? ExpectedNonce { get; private set; }

        public Task<OpenAiAccountIdentity> ValidateAsync(string idToken, string clientId, string expectedNonce,
            CancellationToken cancellationToken = default)
        {
            Assert.Equal("signed-id-token", idToken);
            Assert.Equal("issued-client", clientId);
            ExpectedNonce = expectedNonce;
            return Task.FromResult(identity);
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class JsonObjectBuilder
    {
        private readonly object _value;
        public JsonObjectBuilder() => _value = new Dictionary<string, object>();
        public JsonObjectBuilder Add(string key, object value)
        {
            ((Dictionary<string, object>)_value)[key] = value;
            return this;
        }
        public string Build() => JsonSerializer.Serialize(_value);
    }
}
