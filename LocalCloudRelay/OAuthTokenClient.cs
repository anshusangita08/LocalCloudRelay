using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace LocalCloudRelay;

/// <summary>OAuth credentials returned by a token endpoint. ToString intentionally redacts all values.</summary>
public sealed class OAuthTokenSet
{
    public OAuthTokenSet(string accessToken, string? refreshToken, DateTimeOffset? expiresAtUtc,
        string? tokenType = null, string? scope = null, string? idToken = null)
    {
        AccessToken = accessToken;
        RefreshToken = refreshToken;
        ExpiresAtUtc = expiresAtUtc;
        TokenType = tokenType;
        Scope = scope;
        IdToken = idToken;
    }

    public string AccessToken { get; }
    public string? RefreshToken { get; }
    public DateTimeOffset? ExpiresAtUtc { get; }
    public string? TokenType { get; }
    public string? Scope { get; }
    public string? IdToken { get; }

    public override string ToString() => "OAuth token set (redacted)";
}

/// <summary>
/// Minimal persistence boundary used by OAuthTokenClient. Implementations should read the latest
/// profile value and atomically update only that profile's OAuth tokens in the DPAPI-protected config.
/// </summary>
public interface IOAuthTokenStore
{
    ValueTask<OAuthTokenSet?> GetAsync(string profileId, CancellationToken cancellationToken = default);
    ValueTask SetAsync(string profileId, OAuthTokenSet tokens, CancellationToken cancellationToken = default);
}

/// <summary>Authorization-code grant data. AdditionalParameters are form fields such as OpenAI's resource.</summary>
public sealed class OAuthAuthorizationCodeGrant
{
    public OAuthAuthorizationCodeGrant(
        Uri tokenEndpoint,
        string clientId,
        string? clientSecret,
        string code,
        string redirectUri,
        string codeVerifier,
        IReadOnlyDictionary<string, string>? additionalParameters = null)
    {
        TokenEndpoint = tokenEndpoint;
        ClientId = clientId;
        ClientSecret = clientSecret;
        Code = code;
        RedirectUri = redirectUri;
        CodeVerifier = codeVerifier;
        AdditionalParameters = additionalParameters ?? new Dictionary<string, string>();
    }

    public Uri TokenEndpoint { get; }
    public string ClientId { get; }
    public string? ClientSecret { get; }
    public string Code { get; }
    public string RedirectUri { get; }
    public string CodeVerifier { get; }
    public IReadOnlyDictionary<string, string> AdditionalParameters { get; }

    public override string ToString() => "OAuth authorization-code grant (redacted)";
}

/// <summary>Provider-neutral OAuth token POSTs and serialized profile token refresh.</summary>
public sealed class OAuthTokenClient
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> ProfileGates = new(StringComparer.Ordinal);
    private static readonly HashSet<string> ReservedGrantFields = new(StringComparer.Ordinal)
    {
        "grant_type", "client_id", "client_secret", "code", "redirect_uri", "code_verifier", "refresh_token"
    };
    private readonly HttpClient _httpClient;
    private readonly IOAuthTokenStore? _tokenStore;
    private readonly TimeProvider _timeProvider;

    public OAuthTokenClient(HttpClient httpClient, IOAuthTokenStore? tokenStore = null, TimeProvider? timeProvider = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _tokenStore = tokenStore;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<OAuthTokenSet> ExchangeCodeAsync(OAuthAuthorizationCodeGrant grant, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(grant);
        ValidateTokenEndpoint(grant.TokenEndpoint);
        ValidateRequired(grant.ClientId, nameof(grant.ClientId));
        ValidateRequired(grant.Code, nameof(grant.Code));
        ValidateRequired(grant.RedirectUri, nameof(grant.RedirectUri));
        ValidateRequired(grant.CodeVerifier, nameof(grant.CodeVerifier));
        var form = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["grant_type"] = "authorization_code",
            ["client_id"] = grant.ClientId,
            ["code"] = grant.Code,
            ["redirect_uri"] = grant.RedirectUri,
            ["code_verifier"] = grant.CodeVerifier
        };
        if (!string.IsNullOrEmpty(grant.ClientSecret)) form["client_secret"] = grant.ClientSecret;
        AddAdditionalParameters(form, grant.AdditionalParameters);
        return await PostTokenAsync(grant.TokenEndpoint, form, "authorization-code exchange", null, null, null, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Returns a sufficiently fresh access token or refreshes the profile's latest saved credentials.
    /// The token-store read occurs under the per-profile gate so a second caller observes rotated tokens.
    /// </summary>
    public async Task<string> GetValidAccessTokenAsync(
        string profileId,
        Uri tokenEndpoint,
        string clientId,
        string? clientSecret,
        TimeSpan minimumValidity,
        IReadOnlyDictionary<string, string>? additionalParameters = null,
        CancellationToken cancellationToken = default)
    {
        if (_tokenStore is null) throw new InvalidOperationException("OAuth token refresh requires a token store.");
        ValidateRequired(profileId, nameof(profileId));
        ValidateRequired(clientId, nameof(clientId));
        ValidateTokenEndpoint(tokenEndpoint);
        ArgumentOutOfRangeException.ThrowIfLessThan(minimumValidity, TimeSpan.Zero);

        var gate = ProfileGates.GetOrAdd(profileId, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var existing = await _tokenStore.GetAsync(profileId, cancellationToken).ConfigureAwait(false);
            if (existing is null || string.IsNullOrWhiteSpace(existing.AccessToken))
                throw new OAuthTokenException("No OAuth credentials are saved for this profile.", null);
            if (IsSufficientlyFresh(existing, minimumValidity)) return existing.AccessToken;
            if (string.IsNullOrWhiteSpace(existing.RefreshToken))
                throw new OAuthTokenException("The OAuth access token expired and no refresh token is available.", null);

            var form = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["grant_type"] = "refresh_token",
                ["client_id"] = clientId,
                ["refresh_token"] = existing.RefreshToken
            };
            if (!string.IsNullOrEmpty(clientSecret)) form["client_secret"] = clientSecret;
            if (additionalParameters is not null) AddAdditionalParameters(form, additionalParameters);
            var refreshed = await PostTokenAsync(tokenEndpoint, form, "token refresh", existing.RefreshToken,
                existing.Scope, existing.IdToken, cancellationToken).ConfigureAwait(false);
            await _tokenStore.SetAsync(profileId, refreshed, CancellationToken.None).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return refreshed.AccessToken;
        }
        finally
        {
            gate.Release();
        }
    }

    private bool IsSufficientlyFresh(OAuthTokenSet tokens, TimeSpan minimumValidity) =>
        tokens.ExpiresAtUtc is null || tokens.ExpiresAtUtc > _timeProvider.GetUtcNow() + minimumValidity;

    private async Task<OAuthTokenSet> PostTokenAsync(
        Uri endpoint,
        Dictionary<string, string> fields,
        string operation,
        string? previousRefreshToken,
        string? previousScope,
        string? previousIdToken,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new FormUrlEncodedContent(fields)
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (HttpRequestException)
        {
            throw new OAuthTokenException($"The OAuth {operation} request failed.", null);
        }
        using (response)
        {
            if (!response.IsSuccessStatusCode)
                throw new OAuthTokenException($"The OAuth {operation} failed (HTTP {(int)response.StatusCode}).", response.StatusCode);

            try
            {
                await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
                var root = document.RootElement;
                if (!root.TryGetProperty("access_token", out var access) || access.ValueKind != JsonValueKind.String ||
                    string.IsNullOrWhiteSpace(access.GetString()))
                    throw new OAuthTokenException($"The OAuth {operation} returned an invalid token response.", response.StatusCode);

                var refresh = ReadString(root, "refresh_token") ?? previousRefreshToken;
                var tokenType = ReadString(root, "token_type");
                var scope = ReadString(root, "scope") ?? previousScope;
                var idToken = ReadString(root, "id_token") ?? previousIdToken;
                DateTimeOffset? expires = null;
                if (root.TryGetProperty("expires_in", out var expiresIn) && expiresIn.TryGetInt64(out var seconds))
                    expires = _timeProvider.GetUtcNow().AddSeconds(Math.Clamp(seconds, 0, 315_360_000));
                return new OAuthTokenSet(access.GetString()!, refresh, expires, tokenType, scope, idToken);
            }
            catch (OAuthTokenException) { throw; }
            catch (Exception ex) when (ex is JsonException or IOException or InvalidOperationException or ArgumentOutOfRangeException)
            {
                throw new OAuthTokenException($"The OAuth {operation} returned an invalid token response.", response.StatusCode);
            }
        }
    }

    private static string? ReadString(JsonElement root, string property) =>
        root.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static void AddAdditionalParameters(Dictionary<string, string> form, IReadOnlyDictionary<string, string> parameters)
    {
        foreach (var pair in parameters)
        {
            if (string.IsNullOrWhiteSpace(pair.Key) || ReservedGrantFields.Contains(pair.Key) || form.ContainsKey(pair.Key))
                throw new ArgumentException("An OAuth extension parameter is empty or duplicates a standard field.", nameof(parameters));
            form.Add(pair.Key, pair.Value ?? string.Empty);
        }
    }

    private static void ValidateTokenEndpoint(Uri endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        if (!endpoint.IsAbsoluteUri || !string.Equals(endpoint.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            !string.IsNullOrEmpty(endpoint.UserInfo) || !string.IsNullOrEmpty(endpoint.Fragment))
            throw new ArgumentException("The OAuth token endpoint must be an absolute HTTPS URI without user information or a fragment.", nameof(endpoint));
    }

    private static void ValidateRequired(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException("A required OAuth value is missing.", name);
    }
}

public sealed class OAuthTokenException : Exception
{
    public OAuthTokenException(string message, HttpStatusCode? statusCode) : base(message) => StatusCode = statusCode;

    public HttpStatusCode? StatusCode { get; }
}
