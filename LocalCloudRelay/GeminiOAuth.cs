using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace LocalCloudRelay;

/// <summary>Google OAuth endpoints and least-privilege scopes for Gemini API access.</summary>
public static class GeminiOAuth
{
    public const string AuthorizationEndpoint = "https://accounts.google.com/o/oauth2/v2/auth";
    public const string TokenEndpoint = "https://oauth2.googleapis.com/token";
    public const string CloudPlatformScope = "https://www.googleapis.com/auth/cloud-platform";
    public const string GenerativeLanguageScope = "https://www.googleapis.com/auth/generative-language.retriever";
    public const string Issuer = "https://accounts.google.com";

    public static readonly Uri ApiBaseUri = new("https://generativelanguage.googleapis.com");
    public static readonly Uri ModelsEndpoint = new("https://generativelanguage.googleapis.com/v1beta/models");

    public static GeminiOAuthAuthorizationAttempt CreateAuthorizationAttempt(
        string clientId, TimeSpan? timeout = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clientId);
        var pkce = OAuthPkce.Create();
        // Google installed-app OAuth only accepts the loopback IP redirect form with
        // a dynamically assigned port and no callback path.
        var callback = LoopbackOAuthCallback.Start("/", pkce.State,
            timeout ?? TimeSpan.FromMinutes(5));
        try
        {
            var uri = OAuthAuthorizationUri.Create(new Uri(AuthorizationEndpoint), clientId, callback, pkce,
                [CloudPlatformScope, GenerativeLanguageScope], new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["access_type"] = "offline",
                    ["prompt"] = "consent"
                });
            return new GeminiOAuthAuthorizationAttempt(uri, callback, pkce);
        }
        catch
        {
            callback.DisposeAsync().AsTask().GetAwaiter().GetResult();
            throw;
        }
    }
}

public sealed class GeminiOAuthAuthorizationAttempt : IAsyncDisposable
{
    private readonly LoopbackOAuthCallback _callback;

    internal GeminiOAuthAuthorizationAttempt(Uri authorizationUri, LoopbackOAuthCallback callback, OAuthPkceParameters pkce)
    {
        AuthorizationUri = authorizationUri;
        _callback = callback;
        Pkce = pkce;
    }

    public Uri AuthorizationUri { get; }
    public string RedirectUri => _callback.RedirectUri.ToString();
    public OAuthPkceParameters Pkce { get; }

    public Task<OAuthCallbackResult> WaitForCallbackAsync(CancellationToken cancellationToken = default) =>
        _callback.WaitForCallbackAsync(cancellationToken);

    public ValueTask DisposeAsync() => _callback.DisposeAsync();

    public override string ToString() => "Gemini OAuth authorization attempt (redacted)";
}

/// <summary>DPAPI-backed profile operations required by the Gemini OAuth flow.</summary>
public interface IGeminiAccountStore : IOAuthTokenStore
{
    ProviderSettings? FindProvider(string id);
    void SaveGeminiCredentials(string profileId, string clientId, string clientSecret,
        string projectId, OAuthTokenSet tokens);
}

public static class GeminiOAuthSignIn
{
    public static async Task<OAuthTokenSet> CompleteAsync(
        string profileId,
        ProviderSettings profile,
        string clientId,
        string clientSecret,
        string projectId,
        GeminiOAuthAuthorizationAttempt attempt,
        OAuthCallbackResult callback,
        OAuthTokenClient tokenClient,
        IGeminiAccountStore settingsStore,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileId);
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentException.ThrowIfNullOrWhiteSpace(clientId);
        ArgumentException.ThrowIfNullOrWhiteSpace(clientSecret);
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        ArgumentNullException.ThrowIfNull(attempt);
        ArgumentNullException.ThrowIfNull(callback);
        ArgumentNullException.ThrowIfNull(tokenClient);
        ArgumentNullException.ThrowIfNull(settingsStore);

        if (!profile.Id.Equals(profileId, StringComparison.Ordinal) ||
            !profile.Kind.Equals(ProviderKinds.Gemini, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Gemini OAuth credentials can only be saved to the selected Gemini profile.");

        var savedProfile = settingsStore.FindProvider(profileId);
        if (savedProfile is null || !savedProfile.Kind.Equals(ProviderKinds.Gemini, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The Gemini provider profile is unavailable.");

        var previousTokens = await settingsStore.GetAsync(profileId, cancellationToken).ConfigureAwait(false);
        var tokens = await tokenClient.ExchangeCodeAsync(new OAuthAuthorizationCodeGrant(
            new Uri(GeminiOAuth.TokenEndpoint), clientId, clientSecret, callback.Code, attempt.RedirectUri,
            attempt.Pkce.Verifier), cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(tokens.RefreshToken) && !string.IsNullOrWhiteSpace(previousTokens?.RefreshToken))
            tokens = new OAuthTokenSet(tokens.AccessToken, previousTokens.RefreshToken, tokens.ExpiresAtUtc,
                tokens.TokenType ?? previousTokens.TokenType, tokens.Scope ?? previousTokens.Scope,
                tokens.IdToken ?? previousTokens.IdToken);
        settingsStore.SaveGeminiCredentials(profileId, clientId, clientSecret, projectId, tokens);
        return tokens;
    }
}

/// <summary>Fetches the Gemini models that this Google OAuth project can actually generate with.</summary>
public sealed class GeminiOAuthModelClient(HttpClient httpClient, OAuthTokenClient tokenClient)
{
    private static readonly Uri TokenEndpoint = new(GeminiOAuth.TokenEndpoint);
    private readonly HttpClient _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
    private readonly OAuthTokenClient _tokenClient = tokenClient ?? throw new ArgumentNullException(nameof(tokenClient));

    public async Task<IReadOnlyList<string>> FetchModelsAsync(ProviderSettings provider,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(provider);
        cancellationToken.ThrowIfCancellationRequested();
        if (provider.AuthMode != ProviderAuthMode.OAuth ||
            !provider.Kind.Equals(ProviderKinds.Gemini, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Account model discovery requires a Gemini OAuth provider profile.");
        if (string.IsNullOrWhiteSpace(provider.OAuthClientId) ||
            string.IsNullOrWhiteSpace(provider.OAuthClientSecret) ||
            string.IsNullOrWhiteSpace(provider.ProjectId))
            throw new InvalidOperationException("Gemini OAuth requires a Google desktop client ID, client secret, and Cloud project ID.");

        var token = await _tokenClient.GetValidAccessTokenAsync(provider.Id, TokenEndpoint,
            provider.OAuthClientId, provider.OAuthClientSecret, TimeSpan.FromMinutes(2), cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        var result = new List<string>();
        string? pageToken = null;
        var seenPageTokens = new HashSet<string>(StringComparer.Ordinal);
        do
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (pageToken is not null && !seenPageTokens.Add(pageToken))
                throw new GeminiOAuthException("The Gemini model catalog returned a repeated page token.", null);
            var query = "pageSize=1000" + (pageToken is null ? string.Empty : "&pageToken=" + Uri.EscapeDataString(pageToken));
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(GeminiOAuth.ModelsEndpoint + "?" + query));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Headers.TryAddWithoutValidation("x-goog-user-project", provider.ProjectId);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            HttpResponseMessage response;
            try
            {
                response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch (HttpRequestException)
            {
                throw new GeminiOAuthException("The Gemini account model request failed.", null);
            }

            using (response)
            {
                if (!response.IsSuccessStatusCode)
                    throw new GeminiOAuthException($"The Gemini account model request failed (HTTP {(int)response.StatusCode}).", response.StatusCode);
                var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                var page = ParseModelPage(body);
                result.AddRange(page.Models);
                pageToken = page.NextPageToken;
            }
        }
        while (!string.IsNullOrWhiteSpace(pageToken));

        return result.Distinct(StringComparer.Ordinal).ToArray();
    }

    public static GeminiModelPage ParseModelPage(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return new GeminiModelPage([], null);
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("models", out var models) ||
                models.ValueKind != JsonValueKind.Array)
                return new GeminiModelPage([], ReadString(root, "nextPageToken"));

            var ids = new List<string>();
            foreach (var model in models.EnumerateArray())
            {
                if (model.ValueKind != JsonValueKind.Object) continue;
                var name = ReadString(model, "name");
                if (string.IsNullOrWhiteSpace(name)) continue;
                if (name.StartsWith("models/", StringComparison.Ordinal)) name = name["models/".Length..];
                if (string.IsNullOrWhiteSpace(name) || !SupportsGeneration(model)) continue;
                ids.Add(name);
            }
            return new GeminiModelPage(ids.Distinct(StringComparer.Ordinal).ToArray(), ReadString(root, "nextPageToken"));
        }
        catch (JsonException) { return new GeminiModelPage([], null); }
    }

    private static bool SupportsGeneration(JsonElement model)
    {
        if (!model.TryGetProperty("supportedGenerationMethods", out var methods) || methods.ValueKind != JsonValueKind.Array)
            return false;
        return methods.EnumerateArray().Any(method => method.ValueKind == JsonValueKind.String &&
            method.GetString() is "generateContent" or "streamGenerateContent");
    }

    private static string? ReadString(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) &&
        value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}

public sealed record GeminiModelPage(IReadOnlyList<string> Models, string? NextPageToken);

public sealed class GeminiOAuthException(string message, HttpStatusCode? statusCode) : Exception(message)
{
    public HttpStatusCode? StatusCode { get; } = statusCode;
}
