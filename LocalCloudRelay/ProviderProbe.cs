using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text.Json;

namespace LocalCloudRelay;

public sealed record ProviderProbeResult(
    ProviderSettings Provider,
    IReadOnlyList<string> Models,
    bool Healthy,
    string? Error,
    long ElapsedMs);

/// <summary>
/// Fetches each provider's model list. A provider that fails is reported unhealthy and
/// simply contributes no models - it never blocks the ones that work.
/// </summary>
public static class ProviderProbe
{
    public static async Task<ProviderProbeResult> ProbeAsync(ProviderSettings provider, HttpClient client, CancellationToken cancellationToken = default)
    {
        var started = Stopwatch.StartNew();
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, RelayProtocol.ModelsUri(new Uri(provider.BaseUrl)));
            if (!string.IsNullOrWhiteSpace(provider.ApiKey))
            {
                if (string.Equals(provider.Kind, ProviderKinds.Gemini, StringComparison.OrdinalIgnoreCase))
                {
                    // Gemini uses x-goog-api-key, not Bearer
                    request.Headers.TryAddWithoutValidation("x-goog-api-key", provider.ApiKey);
                }
                else
                {
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", provider.ApiKey);
                    request.Headers.TryAddWithoutValidation("x-api-key", provider.ApiKey);
                    if (string.Equals(provider.Kind, ProviderKinds.Anthropic, StringComparison.OrdinalIgnoreCase))
                        request.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01");
                }
            }
            using var response = await client.SendAsync(request, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            started.Stop();
            if (!response.IsSuccessStatusCode)
                return new(provider, [], false, $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}", started.ElapsedMilliseconds);
            return new(provider, ParseModelIds(body), true, null, started.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            started.Stop();
            return new(provider, [], false, ex.Message, started.ElapsedMilliseconds);
        }
    }

    /// <summary>
    /// Tolerant model-id extraction. OpenAI-compatible and Anthropic gateways use
    /// data[].id; Gemini uses models[].name prefixed with "models/"; Ollama uses
    /// models[].name unqualified.
    /// </summary>
    public static IReadOnlyList<string> ParseModelIds(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return [];
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return [];

            foreach (var arrayName in new[] { "data", "models" })
            {
                if (!TryGetProperty(root, arrayName, out var array) || array.ValueKind != JsonValueKind.Array) continue;
                var ids = new List<string>();
                foreach (var item in array.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object) continue;
                    var id = FirstString(item, "id", "name", "model");
                    if (string.IsNullOrWhiteSpace(id)) continue;
                    // Gemini reports "models/gemini-3-pro"; clients expect the bare id.
                    ids.Add(id.StartsWith("models/", StringComparison.OrdinalIgnoreCase) ? id["models/".Length..] : id);
                }
                if (ids.Count > 0) return ids.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();
            }
            return [];
        }
        catch (JsonException) { return []; }
    }

    private static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
    {
        if (element.TryGetProperty(name, out value)) return true;
        foreach (var property in element.EnumerateObject())
            if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) { value = property.Value; return true; }
        value = default;
        return false;
    }

    private static string? FirstString(JsonElement element, params string[] names)
    {
        foreach (var name in names)
            if (TryGetProperty(element, name, out var value) && value.ValueKind == JsonValueKind.String)
            {
                var text = value.GetString();
                if (!string.IsNullOrWhiteSpace(text)) return text;
            }
        return null;
    }
}

public sealed record CatalogRefresh(
    ProviderRouter Router,
    CatalogSnapshot Snapshot,
    IReadOnlyList<ProviderProbeResult> Probes)
{
    public bool FromCache => Probes.Count == 0;
    public int HealthyProviders => Probes.Count(p => p.Healthy);
    public int FailedProviders => Probes.Count(p => !p.Healthy);
}

/// <summary>
/// Produces a live router. Loads the cached catalog first so the app opens with a
/// usable model list even when every upstream is unreachable, and re-probes when the
/// cache is stale or empty. A provider that fails simply contributes no models.
/// </summary>
public static class CatalogRefreshRunner
{
    public static readonly TimeSpan TimeToLive = TimeSpan.FromMinutes(10);

    public static async Task<CatalogRefresh> RunAsync(
        RelayConfig config, CatalogCache cache, bool force = false, HttpClient? client = null)
    {
        var snapshot = cache.Load();
        if (!force && !snapshot.IsStale(TimeToLive) && snapshot.TotalModels > 0)
            return new CatalogRefresh(new ProviderRouter(config.EnabledProviders, snapshot, config.RouterRules), snapshot, []);

        HttpClient? owned = null;
        try
        {
            client ??= owned = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            // Account profiles own their catalog and need their own sign-in; an anonymous
            // GET /models against them only ever reports a misleading 401.
            var probes = await Task.WhenAll(config.EnabledProviders.Where(p => !p.RequiresExactModelId)
                .Select(p => ProviderProbe.ProbeAsync(p, client)));
            var models = probes
                .Where(p => p.Healthy && p.Models.Count > 0)
                .GroupBy(p => p.Provider.Id)
                .ToDictionary(g => g.Key,
                    g => (IReadOnlyList<string>)g.SelectMany(p => p.Models).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
                    StringComparer.Ordinal);

            // For each enabled provider that didn't get models (probe failed or returned empty),
            // carry over its cached models if available. This prevents wiping the model list
            // when all probes fail.
            foreach (var provider in config.EnabledProviders)
            {
                if (!models.ContainsKey(provider.Id) && snapshot.ModelsByProviderId.TryGetValue(provider.Id, out var cachedModels))
                {
                    models[provider.Id] = cachedModels;
                }
            }

            // Only save if at least one probe succeeded. If all failed, keep the old cache.
            if (probes.Any(p => p.Healthy))
            {
                snapshot = new CatalogSnapshot(DateTimeOffset.UtcNow, models);
                cache.Save(snapshot);
            }

            return new CatalogRefresh(new ProviderRouter(config.EnabledProviders, snapshot, config.RouterRules), snapshot, probes);
        }
        catch
        {
            // A probe failure must never stop the relay from coming up on cached models.
            return new CatalogRefresh(new ProviderRouter(config.EnabledProviders, snapshot, config.RouterRules), snapshot, []);
        }
        finally
        {
            owned?.Dispose();
        }
    }
}
