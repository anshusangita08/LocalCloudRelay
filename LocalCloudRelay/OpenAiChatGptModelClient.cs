using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace LocalCloudRelay;

/// <summary>Fetches the models visible to a signed-in ChatGPT account, not the public API catalog.</summary>
public sealed class OpenAiChatGptModelClient(HttpClient httpClient, OAuthTokenClient tokenClient)
{
    // The account catalog hides every model whose minimal_client_version is newer than
    // the caller's. Without client_version it answers as a very old client and leaves out
    // the newest models, so a version well above any current client is sent.
    public static readonly Uri ModelsEndpoint = new("https://api.openai.com/v1/models?client_version=9.0.0");
    public static readonly Uri ResponsesEndpoint = new("https://api.openai.com/v1/responses");
    private static readonly Uri TokenEndpoint = new(OpenAiChatGptOAuth.TokenEndpoint);
    private static readonly IReadOnlyDictionary<string, string> TokenParameters =
        new Dictionary<string, string>(StringComparer.Ordinal) { ["resource"] = OpenAiChatGptOAuth.Resource };

    private readonly HttpClient _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
    private readonly OAuthTokenClient _tokenClient = tokenClient ?? throw new ArgumentNullException(nameof(tokenClient));

    public async Task<IReadOnlyList<string>> FetchModelsAsync(ProviderSettings provider, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(provider);
        if (provider.AuthMode != ProviderAuthMode.OAuth || !provider.Kind.Equals(ProviderKinds.OpenAi, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Account model discovery requires an OpenAI OAuth provider profile.");
        if (string.IsNullOrWhiteSpace(provider.OAuthClientId))
            throw new InvalidOperationException("The OpenAI account registration is incomplete. Sign in again to finish account registration.");
        if (!OpenAiChatGptOAuth.HasDirectUsagePermission(provider.OAuthTokens?.Scope))
            throw new OpenAiChatGptException("This ChatGPT account has not granted direct plan usage to this app.", HttpStatusCode.Forbidden);

        var token = await _tokenClient.GetValidAccessTokenAsync(provider.Id, TokenEndpoint,
            provider.OAuthClientId, null, TimeSpan.FromMinutes(2), TokenParameters, cancellationToken).ConfigureAwait(false);
        using var request = new HttpRequestMessage(HttpMethod.Get, ModelsEndpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (HttpRequestException)
        {
            throw new OpenAiChatGptException("The OpenAI account model request failed.", null);
        }
        using (response)
        {
            if (!response.IsSuccessStatusCode)
                throw new OpenAiChatGptException($"The OpenAI account model request failed (HTTP {(int)response.StatusCode}).", response.StatusCode);
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return ParseEligibleModelIds(body);
        }
    }

    public static IReadOnlyList<string> ParseEligibleModelIds(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return [];
        try
        {
            using var document = JsonDocument.Parse(body);
            if (!document.RootElement.TryGetProperty("models", out var models) || models.ValueKind != JsonValueKind.Array) return [];
            return models.EnumerateArray()
                .Where(model => model.ValueKind == JsonValueKind.Object &&
                    model.TryGetProperty("visibility", out var visibility) &&
                    visibility.ValueKind == JsonValueKind.String &&
                    visibility.GetString() == "list")
                .Select(model => model.TryGetProperty("slug", out var slug) && slug.ValueKind == JsonValueKind.String ? slug.GetString() : null)
                .Where(slug => !string.IsNullOrWhiteSpace(slug))
                .Select(slug => slug!)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
        }
        catch (JsonException) { return []; }
    }
}

public sealed class OpenAiChatGptException(string message, HttpStatusCode? statusCode) : Exception(message)
{
    public HttpStatusCode? StatusCode { get; } = statusCode;
}
