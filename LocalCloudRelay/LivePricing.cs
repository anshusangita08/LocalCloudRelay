using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace LocalCloudRelay;

/// <summary>
/// Live per-model prices, pulled from models.dev - the same public database OpenCode
/// publishes from.
///
/// This exists because the built-in <see cref="ModelPricingTable"/> is a snapshot and
/// snapshots drift: it had deepseek-v4-pro at 1.65/3.96 on Go when the published rate
/// was 0.66/1.98, and grok-4.7 at 5/30 against 2/6. A price shown beside a spend figure
/// has to be current or it is worse than no price.
///
/// Matching is by the provider's <c>api</c> base URL, which models.dev states
/// explicitly, so this is an exact key match rather than a guess from the model name.
/// The fetched data is projected down to what this app shows and cached to disk, so a
/// restart - or an offline start - still has prices.
/// </summary>
public sealed class LivePricing
{
    public const string Url = "https://models.dev/api.json";

    /// <summary>Host only, for a status line a person reads.</summary>
    public const string Host = "models.dev";

    /// <summary>
    /// Where OpenCode publishes its per-model monthly allowances. There is no JSON for
    /// these, so this is the docs page, and the parse below is written against its table
    /// structure.
    /// </summary>
    public const string GoPlanUrl = "https://opencode.ai/docs/go/";

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    // baseUrl (normalized) -> model id -> price
    private readonly Dictionary<string, Dictionary<string, ModelPrice>> _byBaseUrl;

    // OpenCode Go model id -> monthly allowance in USD, from the Go plan table.
    private readonly Dictionary<string, decimal> _goMonthlyLimits;

    // OpenCode Go model id -> requests per five hours, from the same page's usage table.
    private readonly Dictionary<string, RequestAllowance> _goRequestAllowance;

    private LivePricing(Dictionary<string, Dictionary<string, ModelPrice>> byBaseUrl, DateTimeOffset fetchedAt,
        Dictionary<string, decimal>? goMonthlyLimits = null,
        Dictionary<string, RequestAllowance>? goRequestAllowance = null)
    {
        _byBaseUrl = byBaseUrl;
        _goMonthlyLimits = goMonthlyLimits ?? [];
        _goRequestAllowance = goRequestAllowance ?? [];
        FetchedAt = fetchedAt;
    }

    public DateTimeOffset FetchedAt { get; }

    public int ProviderCount => _byBaseUrl.Count;

    public int ModelCount => _byBaseUrl.Values.Sum(m => m.Count);

    public int GoLimitCount => _goMonthlyLimits.Count;

    public int GoRequestAllowanceCount => _goRequestAllowance.Count;

    /// <summary>
    /// OpenCode Go's published monthly dollar allowance for a model, or null.
    ///
    /// Kept apart from the token rates on purpose: rates are the same on Go and Go Plus,
    /// only the allowances differ, so a price and a budget do not come from the same
    /// place and must not be dropped together.
    /// </summary>
    public decimal? GoMonthlyLimit(string? model) =>
        !string.IsNullOrWhiteSpace(model) && _goMonthlyLimits.TryGetValue(model, out var limit) ? limit : null;

    /// <summary>
    /// OpenCode Go's published requests-per-five-hours for a model, or null when the plan
    /// publishes none. This is the number the Models tab shows.
    /// </summary>
    public RequestAllowance? GoFiveHourRequests(string? model) =>
        !string.IsNullOrWhiteSpace(model) && _goRequestAllowance.TryGetValue(model, out var allowance) ? allowance : null;

    /// <summary>The live table the app is currently pricing from, or null if never loaded.</summary>
    public static LivePricing? Current { get; private set; }

    public static void Use(LivePricing? pricing) => Current = pricing;

    /// <summary>
    /// Loads the cached projection if one exists. Called at startup: prices should be
    /// there before any network call, and must survive an offline start.
    /// </summary>
    public static LivePricing? LoadCached(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var file = JsonSerializer.Deserialize<CachedDocument>(File.ReadAllText(path), Options);
            if (file?.Providers is null) return null;
            var pricing = new LivePricing(file.Providers, file.FetchedAt, file.GoMonthlyLimits, file.GoRequestAllowance);
            Use(pricing);
            return pricing;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Fetches the current database and caches the projection. Returns null on any
    /// failure, leaving whatever table was already loaded in place - a failed refresh
    /// must not take prices away.
    /// </summary>
    public static async Task<LivePricing?> RefreshAsync(string cachePath, HttpClient client, CancellationToken cancellationToken = default)
    {
        try
        {
            var json = await client.GetStringAsync(Url, cancellationToken);
            var pricing = Parse(json, DateTimeOffset.UtcNow);

            // Allowances are published on the Go docs page, not in the database, and the
            // page is the only source for them. A failure here must not cost the prices.
            // Preserve previous allowances if the fetch fails.
            var previousLimits = Current?._goMonthlyLimits;
            var previousRequests = Current?._goRequestAllowance;
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, GoPlanUrl);
                request.Headers.UserAgent.ParseAdd("LocalCloudRelay/1.0");
                using var response = await client.SendAsync(request, cancellationToken);
                if (response.IsSuccessStatusCode)
                {
                    var html = await response.Content.ReadAsStringAsync(cancellationToken);
                    var limits = ParseGoPlanLimits(html);
                    var requests = ParseGoRequestAllowance(html);
                    pricing = new LivePricing(pricing._byBaseUrl, pricing.FetchedAt, limits, requests);
                }
                else if (previousLimits is not null || previousRequests is not null)
                {
                    // Go plan fetch failed, reuse previous allowances
                    pricing = new LivePricing(pricing._byBaseUrl, pricing.FetchedAt, previousLimits, previousRequests);
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
            {
                // Keep the previous allowances on fetch failure
                if (previousLimits is not null || previousRequests is not null)
                    pricing = new LivePricing(pricing._byBaseUrl, pricing.FetchedAt, previousLimits, previousRequests);
            }

            if (pricing.ModelCount == 0) return null;

            Use(pricing);
            Save(cachePath, pricing);
            return pricing;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Reads the Go plan's per-model monthly allowances out of the docs page.
    ///
    /// The page lists two plans in tabs - Go and Go Plus - whose allowances differ by
    /// roughly 3x, and this takes the first one, which is the base Go plan. Getting that
    /// wrong triples a budget figure, so the parse is keyed off the table header rather
    /// than table order alone, and it joins the allowance rows to the model-id table, so
    /// "GLM-5.3-Flash" becomes "glm-5.3-flash" rather than being guessed at.
    /// </summary>
    public static Dictionary<string, decimal> ParseGoPlanLimits(string html)
    {
        var result = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(html)) return result;

        var limits = new List<(string Name, decimal Monthly)>();
        var idByName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (Match table in Regex.Matches(html, @"(?s)<table>(.*?)</table>"))
        {
            var rows = Regex.Matches(table.Groups[1].Value, @"(?s)<tr>(.*?)</tr>");
            if (rows.Count < 2) continue;

            var header = Cells(rows[0]);
            var body = rows.Skip(1).Select(Cells).Where(r => r.Count > 0).ToArray();

            // Model | Input | Output | Cached Read | Cached Write | Monthly limit
            if (limits.Count == 0 &&
                header.Count >= 6 &&
                header[0].Equals("Model", StringComparison.OrdinalIgnoreCase) &&
                header[^1].Contains("monthly limit", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var row in body)
                {
                    if (row.Count < 6) continue;
                    var monthly = Money(row[^1]);
                    if (monthly is not null) limits.Add((row[0], monthly.Value));
                }
                continue;
            }

            // Model | Model ID | Endpoint | AI SDK Package
            if (header.Count >= 2 &&
                header[0].Equals("Model", StringComparison.OrdinalIgnoreCase) &&
                header[1].Equals("Model ID", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var row in body)
                    if (row.Count >= 2 && row[0].Length > 0 && row[1].Length > 0)
                        idByName[row[0]] = row[1];
            }
        }

        foreach (var (name, monthly) in limits)
        {
            // The page spells some models with a variant suffix - "Grok 4.7 (> 200K
            // tokens)", "DeepSeek V4 Pro (Peak)" - while the id table lists the bare
            // name. The allowance is per model and the variants agree on it, so the
            // suffix is dropped to make the join. Verified against the published table:
            // every tiered and peak/off-peak pair carries the same monthly limit.
            if (idByName.TryGetValue(name, out var id))
                result[id] = monthly;
            else if (BaseName(name) is { } bare && idByName.TryGetValue(bare, out var bareId) && !result.ContainsKey(bareId))
                result[bareId] = monthly;
        }

        return result;
    }

    /// <summary>"Grok 4.7 (&gt; 200K tokens)" becomes "Grok 4.7".</summary>
    private static string? BaseName(string name)
    {
        var index = name.IndexOf('(');
        if (index <= 0) return null;
        var bare = name[..index].Trim();
        return bare.Length == 0 ? null : bare;
    }

    /// <summary>
    /// Reads the Go plan's per-model five-hour request allowances out of the docs page.
    ///
    /// The same page lists both plans in tabs with Go Plus allowing roughly four times as
    /// many, so this takes the first matching table, which is the base Go plan. The rows
    /// are joined to the model-id table the same way the spend allowances are, and
    /// "Unlimited" is kept as its own state rather than being folded into "unknown".
    /// </summary>
    public static Dictionary<string, RequestAllowance> ParseGoRequestAllowance(string html)
    {
        var result = new Dictionary<string, RequestAllowance>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(html)) return result;

        var requests = new List<(string Name, RequestAllowance Allowance)>();
        var idByName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (Match table in Regex.Matches(html, @"(?s)<table>(.*?)</table>"))
        {
            var rows = Regex.Matches(table.Groups[1].Value, @"(?s)<tr>(.*?)</tr>");
            if (rows.Count < 2) continue;

            var header = Cells(rows[0]);
            var body = rows.Skip(1).Select(Cells).Where(r => r.Count > 0).ToArray();

            // Model | Requests per 5 hours | Requests per week | Requests per month
            if (requests.Count == 0 &&
                header.Count >= 2 &&
                header[0].Equals("Model", StringComparison.OrdinalIgnoreCase) &&
                header[1].Contains("5 hours", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var row in body)
                {
                    if (row.Count < 2) continue;
                    var allowance = Allowance(row[1]);
                    if (allowance is not null) requests.Add((row[0], allowance));
                }
                continue;
            }

            // Model | Model ID | Endpoint | AI SDK Package
            if (header.Count >= 2 &&
                header[0].Equals("Model", StringComparison.OrdinalIgnoreCase) &&
                header[1].Equals("Model ID", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var row in body)
                    if (row.Count >= 2 && row[0].Length > 0 && row[1].Length > 0)
                        idByName[row[0]] = row[1];
            }
        }

        foreach (var (name, allowance) in requests)
        {
            if (idByName.TryGetValue(name, out var id))
                result[id] = allowance;
            else if (BaseName(name) is { } bare && idByName.TryGetValue(bare, out var bareId) && !result.ContainsKey(bareId))
                result[bareId] = allowance;
        }

        return result;
    }

    /// <summary>"6,320" becomes 6320, "Unlimited" becomes the unlimited state, "-" nothing.</summary>
    private static RequestAllowance? Allowance(string text)
    {
        var cleaned = text.Replace(",", string.Empty).Trim();
        if (cleaned.Equals("unlimited", StringComparison.OrdinalIgnoreCase)) return RequestAllowance.UnlimitedValue;
        return long.TryParse(cleaned, NumberStyles.Integer, CultureInfo.InvariantCulture, out var count)
            ? new RequestAllowance(count, false)
            : null;
    }

    private static List<string> Cells(Match row) =>
        Regex.Matches(row.Groups[1].Value, @"(?s)<t[hd][^>]*>(.*?)</t[hd]>")
            .Select(m => Regex.Replace(m.Groups[1].Value, "<[^>]+>", string.Empty).Trim())
            .ToList();

    /// <summary>Reads "$60" or "60" as 60, and "-" or "" as nothing.</summary>
    private static decimal? Money(string text)
    {
        var cleaned = text.Replace("$", string.Empty).Replace(",", string.Empty).Trim();
        return decimal.TryParse(cleaned, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : null;
    }

    /// <summary>
    /// Keeps only providers that state a base URL and models that state a cost, so the
    /// cache is small and a missing price stays missing rather than becoming a zero.
    /// </summary>
    public static LivePricing Parse(string json, DateTimeOffset fetchedAt)
    {
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Object) return new LivePricing([], fetchedAt);

        var byBaseUrl = new Dictionary<string, Dictionary<string, ModelPrice>>(StringComparer.OrdinalIgnoreCase);

        foreach (var provider in document.RootElement.EnumerateObject())
        {
            // First-party vendors (openai, anthropic, google) publish no api URL because
            // their SDKs know it. They are kept under a models.dev key so account
            // providers, which call those vendors directly, can still be priced.
            var baseUrl = provider.Value.TryGetProperty("api", out var apiElement) && apiElement.ValueKind == JsonValueKind.String
                ? Normalize(apiElement.GetString())
                : VendorKey(provider.Name);
            if (baseUrl is null) continue;

            if (!provider.Value.TryGetProperty("models", out var modelsElement) ||
                modelsElement.ValueKind != JsonValueKind.Object) continue;

            var prices = new Dictionary<string, ModelPrice>(StringComparer.OrdinalIgnoreCase);
            foreach (var model in modelsElement.EnumerateObject())
            {
                if (!model.Value.TryGetProperty("cost", out var cost) || cost.ValueKind != JsonValueKind.Object) continue;
                var input = Number(cost, "input");
                var output = Number(cost, "output");
                // A cost block with neither rate says nothing about price.
                if (input is null && output is null) continue;
                long? context = model.Value.TryGetProperty("limit", out var limit) && limit.ValueKind == JsonValueKind.Object &&
                    limit.TryGetProperty("context", out var ctx) && ctx.ValueKind == JsonValueKind.Number && ctx.TryGetInt64(out var tokens) && tokens > 0
                    ? tokens : null;
                prices[model.Name] = new ModelPrice(input, output, Number(cost, "cache_read"), Number(cost, "cache_write"), null, context);
            }

            if (prices.Count > 0) byBaseUrl[baseUrl] = prices;
        }

        return new LivePricing(byBaseUrl, fetchedAt);
    }

    /// <summary>
    /// The price for a model on a provider, matched on the provider's base URL. Falls
    /// back to host+path so an http/https difference does not lose the match.
    /// </summary>
    public ModelPrice? For(string? baseUrl, string? model)
    {
        if (string.IsNullOrWhiteSpace(model)) return null;
        var normalized = Normalize(baseUrl);
        if (normalized is null) return null;

        if (_byBaseUrl.TryGetValue(normalized, out var prices) && prices.TryGetValue(model, out var price))
            return price;

        var withoutScheme = WithoutScheme(normalized);
        if (withoutScheme is null) return null;

        foreach (var (key, candidate) in _byBaseUrl)
            if (string.Equals(WithoutScheme(key), withoutScheme, StringComparison.OrdinalIgnoreCase) &&
                candidate.TryGetValue(model, out var match))
                return match;

        return null;
    }

    /// <summary>Key for a models.dev provider that has no api URL of its own.</summary>
    public static string VendorKey(string vendor) => "https://models.dev/providers/" + vendor;

    /// <summary>Published price for a model at a first-party vendor such as "anthropic".</summary>
    public ModelPrice? ForVendor(string vendor, string? model) =>
        !string.IsNullOrWhiteSpace(model) && _byBaseUrl.TryGetValue(VendorKey(vendor), out var prices) &&
        prices.TryGetValue(model, out var price) ? price : null;

    /// <summary>Every model id models.dev lists for a first-party vendor.</summary>
    public IReadOnlyList<string> VendorModels(string vendor) =>
        _byBaseUrl.TryGetValue(VendorKey(vendor), out var prices) ? [.. prices.Keys] : [];

    public static string? Normalize(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        var text = url.Trim().TrimEnd('/');
        return text.Length == 0 ? null : text;
    }

    private static string? WithoutScheme(string url)
    {
        var index = url.IndexOf("://", StringComparison.Ordinal);
        return index < 0 ? url : url[(index + 3)..];
    }

    private static decimal? Number(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value)) return null;
        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetDecimal(out var number) => number,
            JsonValueKind.String when decimal.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => null
        };
    }

    private static void Save(string path, LivePricing pricing)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        var document = new CachedDocument
        {
            FetchedAt = pricing.FetchedAt,
            Providers = pricing._byBaseUrl,
            GoMonthlyLimits = pricing._goMonthlyLimits,
            GoRequestAllowance = pricing._goRequestAllowance
        };
        var temporaryPath = path + ".tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(document, Options));
        File.Move(temporaryPath, path, true);
    }

    private sealed class CachedDocument
    {
        public DateTimeOffset FetchedAt { get; set; }
        public Dictionary<string, Dictionary<string, ModelPrice>>? Providers { get; set; }
        public Dictionary<string, decimal>? GoMonthlyLimits { get; set; }
        public Dictionary<string, RequestAllowance>? GoRequestAllowance { get; set; }
    }
}
