using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Net.Http.Headers;

namespace LocalCloudRelay;

/// <summary>OpenAI's public Sign in with ChatGPT authorization values for local apps.</summary>
public static class OpenAiChatGptOAuth
{
    public const string DynamicClientId = "dynamic_agent_client";
    public const string Issuer = "https://auth.openai.com";
    public const string AuthorizationEndpoint = "https://auth.openai.com/api/accounts/authorize";
    public const string TokenEndpoint = "https://auth.openai.com/api/accounts/oauth/token";
    public const string JwksEndpoint = "https://auth.openai.com/.well-known/jwks.json";
    public const string Resource = "https://api.openai.com/v1";
    public const string DirectUsageScope = "chatgpt.tokens.use.direct";
    private const string Scope = "openid profile email offline_access resource.invoke chatgpt.tokens.use.direct";

    public static OpenAiOAuthAuthorizationAttempt CreateAuthorizationAttempt(
        string? registeredClientId,
        string hostId,
        string? idTokenHint = null,
        string? loginHint = null,
        TimeSpan? timeout = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hostId);
        var pkce = OAuthPkce.Create();
        var nonce = Base64Url(RandomNumberGenerator.GetBytes(32));
        var isRegistration = string.IsNullOrWhiteSpace(registeredClientId);
        var clientId = isRegistration ? DynamicClientId : registeredClientId!;
        var callback = LoopbackOAuthCallback.Start("/auth/callback", pkce.State,
            timeout ?? TimeSpan.FromMinutes(5));
        var fields = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["response_type"] = "code",
            ["client_id"] = clientId,
            ["redirect_uri"] = callback.RedirectUri.ToString(),
            ["code_challenge"] = pkce.Challenge,
            ["code_challenge_method"] = "S256",
            ["state"] = pkce.State,
            ["nonce"] = nonce,
            ["scope"] = Scope,
            ["resource"] = Resource,
            ["ext_agent_host_id"] = hostId
        };
        if (isRegistration) fields["agent_name_hint"] = "Local Cloud Relay";
        else
        {
            if (!string.IsNullOrWhiteSpace(idTokenHint)) fields["id_token_hint"] = idTokenHint;
            if (!string.IsNullOrWhiteSpace(loginHint)) fields["login_hint"] = loginHint;
        }
        var query = string.Join('&', fields.Select(pair =>
            $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}"));
        return new OpenAiOAuthAuthorizationAttempt(
            new Uri($"{AuthorizationEndpoint}?{query}", UriKind.Absolute),
            clientId, callback.RedirectUri.ToString(), pkce, nonce, isRegistration, callback);
    }

    public static bool HasDirectUsagePermission(string? scopes) =>
        !string.IsNullOrWhiteSpace(scopes) && scopes.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Contains(DirectUsageScope, StringComparer.Ordinal);

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

public sealed class OpenAiOAuthAuthorizationAttempt : IAsyncDisposable
{
    private readonly LoopbackOAuthCallback _callback;

    internal OpenAiOAuthAuthorizationAttempt(Uri authorizationUri, string requestedClientId, string redirectUri,
        OAuthPkceParameters pkce, string nonce, bool isNewRegistration, LoopbackOAuthCallback callback)
    {
        AuthorizationUri = authorizationUri;
        RequestedClientId = requestedClientId;
        RedirectUri = redirectUri;
        Pkce = pkce;
        Nonce = nonce;
        IsNewRegistration = isNewRegistration;
        _callback = callback;
    }

    public Uri AuthorizationUri { get; }
    public string RequestedClientId { get; }
    public string RedirectUri { get; }
    public OAuthPkceParameters Pkce { get; }
    public string Nonce { get; }
    public bool IsNewRegistration { get; }

    public Task<OAuthCallbackResult> WaitForCallbackAsync(CancellationToken cancellationToken = default) =>
        _callback.WaitForCallbackAsync(cancellationToken);

    public ValueTask DisposeAsync() => _callback.DisposeAsync();

    public override string ToString() => "OpenAI OAuth authorization attempt (redacted)";
}

public sealed record OpenAiAccountIdentity(string Subject, string? Email, string Issuer);

public interface IOpenAiAccountStore : IOAuthTokenStore
{
    ProviderSettings? FindProvider(string id);
    void SaveOpenAiRegistration(string profileId, string clientId);
    void SaveOpenAiIdentity(string profileId, string clientId, OpenAiAccountIdentity identity, OAuthTokenSet tokens);
}

public interface IOpenAiIdTokenValidator
{
    Task<OpenAiAccountIdentity> ValidateAsync(string idToken, string clientId, string expectedNonce,
        CancellationToken cancellationToken = default);
}

/// <summary>Verifies OpenAI's OIDC identity token against its published RSA JWKS.</summary>
public sealed class OpenAiIdTokenValidator(HttpClient httpClient, TimeProvider? timeProvider = null) : IOpenAiIdTokenValidator
{
    private readonly HttpClient _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public async Task<OpenAiAccountIdentity> ValidateAsync(string idToken, string clientId, string expectedNonce,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var parts = idToken.Split('.');
            if (parts.Length != 3) throw InvalidIdentity();
            var header = JsonDocument.Parse(Decode(parts[0]));
            var payload = JsonDocument.Parse(Decode(parts[1]));
            using (header)
            using (payload)
            {
                var headerRoot = header.RootElement;
                var claims = payload.RootElement;
                if (Read(headerRoot, "alg") != "RS256" || string.IsNullOrWhiteSpace(Read(headerRoot, "kid")))
                    throw InvalidIdentity();
                using var jwks = await ReadJwksAsync(cancellationToken).ConfigureAwait(false);
                if (!TryGetRsaKey(jwks, Read(headerRoot, "kid")!, out var parameters)) throw InvalidIdentity();
                using var rsa = RSA.Create();
                rsa.ImportParameters(parameters);
                if (!rsa.VerifyData(Encoding.ASCII.GetBytes(parts[0] + "." + parts[1]), Decode(parts[2]),
                    HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)) throw InvalidIdentity();

                var issuer = Read(claims, "iss");
                if (!string.Equals(issuer, OpenAiChatGptOAuth.Issuer, StringComparison.Ordinal) || !AudienceMatches(claims, clientId))
                    throw InvalidIdentity();
                var now = _timeProvider.GetUtcNow().ToUnixTimeSeconds();
                if (!claims.TryGetProperty("exp", out var expiration) || !expiration.TryGetInt64(out var exp) || exp <= now)
                    throw InvalidIdentity();
                if (claims.TryGetProperty("nbf", out var notBefore) && notBefore.TryGetInt64(out var nbf) && nbf > now)
                    throw InvalidIdentity();
                if (!FixedEquals(Read(claims, "nonce"), expectedNonce)) throw InvalidIdentity();
                var subject = Read(claims, "sub");
                if (string.IsNullOrWhiteSpace(subject)) throw InvalidIdentity();
                return new OpenAiAccountIdentity(subject, Read(claims, "email"), issuer!);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (OpenAiChatGptException) { throw; }
        catch (Exception ex) when (ex is JsonException or FormatException or CryptographicException or HttpRequestException or InvalidOperationException or ArgumentException)
        {
            throw InvalidIdentity();
        }
    }

    private async Task<JsonDocument> ReadJwksAsync(CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, OpenAiChatGptOAuth.JwksEndpoint);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) throw InvalidIdentity();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    private static bool TryGetRsaKey(JsonDocument jwks, string keyId, out RSAParameters parameters)
    {
        parameters = default;
        if (!jwks.RootElement.TryGetProperty("keys", out var keys) || keys.ValueKind != JsonValueKind.Array) return false;
        foreach (var key in keys.EnumerateArray())
        {
            if (Read(key, "kid") != keyId || Read(key, "kty") != "RSA" || Read(key, "use") is { } usage && usage != "sig") continue;
            var modulus = Read(key, "n");
            var exponent = Read(key, "e");
            if (modulus is null || exponent is null) return false;
            parameters = new RSAParameters { Modulus = Decode(modulus), Exponent = Decode(exponent) };
            return true;
        }
        return false;
    }

    private static bool AudienceMatches(JsonElement claims, string clientId)
    {
        if (!claims.TryGetProperty("aud", out var audience)) return false;
        return audience.ValueKind == JsonValueKind.String
            ? audience.GetString() == clientId
            : audience.ValueKind == JsonValueKind.Array && audience.EnumerateArray().Any(item => item.ValueKind == JsonValueKind.String && item.GetString() == clientId);
    }

    private static string? Read(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static bool FixedEquals(string? actual, string expected) => actual is not null &&
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(actual), Encoding.UTF8.GetBytes(expected));

    private static byte[] Decode(string value)
    {
        var normalized = value.Replace('-', '+').Replace('_', '/');
        normalized += new string('=', (4 - normalized.Length % 4) % 4);
        return Convert.FromBase64String(normalized);
    }

    private static OpenAiChatGptException InvalidIdentity() =>
        new("OpenAI account identity could not be verified.", null);
}

public static class OpenAiChatGptSignIn
{
    public static async Task<OpenAiAccountIdentity> CompleteAsync(
        string profileId,
        string hostId,
        ProviderSettings profile,
        OpenAiOAuthAuthorizationAttempt attempt,
        OAuthCallbackResult callback,
        OAuthTokenClient tokenClient,
        IOpenAiAccountStore settingsStore,
        IOpenAiIdTokenValidator identityValidator,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(attempt);
        ArgumentNullException.ThrowIfNull(callback);
        ArgumentNullException.ThrowIfNull(tokenClient);
        ArgumentNullException.ThrowIfNull(settingsStore);
        ArgumentNullException.ThrowIfNull(identityValidator);
        ArgumentException.ThrowIfNullOrWhiteSpace(profileId);
        ArgumentException.ThrowIfNullOrWhiteSpace(hostId);

        string exchangeClientId;
        if (attempt.IsNewRegistration)
        {
            exchangeClientId = callback.ClientId ?? string.Empty;
            if (string.IsNullOrWhiteSpace(exchangeClientId) || exchangeClientId == OpenAiChatGptOAuth.DynamicClientId)
                throw new OpenAiChatGptException("OpenAI did not return the issued account registration ID.", null);
            settingsStore.SaveOpenAiRegistration(profileId, exchangeClientId);
        }
        else
        {
            exchangeClientId = attempt.RequestedClientId;
            if (callback.ClientId is not null && !string.Equals(callback.ClientId, exchangeClientId, StringComparison.Ordinal))
                throw new OpenAiChatGptException("OpenAI returned a different account registration than the one selected.", null);
        }

        var tokens = await tokenClient.ExchangeCodeAsync(new OAuthAuthorizationCodeGrant(
            new Uri(OpenAiChatGptOAuth.TokenEndpoint), exchangeClientId, null, callback.Code, attempt.RedirectUri,
            attempt.Pkce.Verifier,
            new Dictionary<string, string>(StringComparer.Ordinal) { ["resource"] = OpenAiChatGptOAuth.Resource }), cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(tokens.IdToken))
            throw new OpenAiChatGptException("OpenAI sign-in did not return an identity token.", null);
        if (!OpenAiChatGptOAuth.HasDirectUsagePermission(tokens.Scope) ||
            !(tokens.Scope?.Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains("resource.invoke", StringComparer.Ordinal) ?? false))
            throw new OpenAiChatGptException("This ChatGPT account did not grant direct plan usage to this app.", null);

        var identity = await identityValidator.ValidateAsync(tokens.IdToken, exchangeClientId, attempt.Nonce, cancellationToken).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(profile.AccountId) && !string.Equals(profile.AccountId, identity.Subject, StringComparison.Ordinal))
            throw new OpenAiChatGptException("The signed-in ChatGPT identity does not match the selected account profile.", null);
        var current = settingsStore.FindProvider(profileId);
        if (current is null) throw new InvalidOperationException("Provider profile settings are unavailable.");
        if (!attempt.IsNewRegistration && !string.Equals(current.OAuthClientId, exchangeClientId, StringComparison.Ordinal))
            throw new OpenAiChatGptException("The selected OpenAI account registration changed during sign-in.", null);
        settingsStore.SaveOpenAiIdentity(profileId, exchangeClientId, identity, tokens);
        return identity;
    }
}
