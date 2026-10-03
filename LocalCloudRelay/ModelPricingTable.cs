using System.Globalization;
using System.Text.RegularExpressions;

namespace LocalCloudRelay;

public sealed record ModelPrice(
    decimal? InputPerMillion,
    decimal? OutputPerMillion,
    decimal? CachedReadPerMillion,
    decimal? CachedWritePerMillion,
    decimal? MonthlyLimitUsd,
    // Context window in tokens, from models.dev's limit.context; null when unpublished.
    long? ContextTokens = null)
{
    public bool IsFree => InputPerMillion == 0m && OutputPerMillion == 0m;
}

/// <summary>
/// How many requests a plan allows in a five-hour window.
///
/// <see cref="Unlimited"/> is a published state, not a missing number, so it is kept apart
/// from null rather than folded into it or into a huge number that would read as a
/// different claim.
/// </summary>
public sealed record RequestAllowance(long? Requests, bool Unlimited)
{
    public static readonly RequestAllowance UnlimitedValue = new(null, true);

    public bool IsKnown => Unlimited || Requests is not null;

    public string Label => Unlimited ? "unlimited"
        : Requests is null ? "-"
        : Requests.Value.ToString("n0", CultureInfo.InvariantCulture);
}

/// <summary>
/// Published prices for OpenCode Zen and Go.
///
/// NOT fetched from the provider. /zen/v1/models returns ids only - object, created,
/// owned_by - and there is no pricing or limits endpoint (probed /zen/pricing,
/// /zen/v1/pricing, /zen/go/v1/pricing, /zen/v1/limits and others: all 404). These
/// numbers are transcribed from the published docs, so they are a dated snapshot, not
/// live data. If OpenCode ever adds a pricing endpoint, replace this table with it.
///
/// The same model can cost different amounts on each plan - deepseek-v4-pro is
/// 1.74/3.48 on Zen and 1.65/3.96 on Go - so this is keyed by plan, not by model.
/// </summary>
public static class ModelPricingTable
{
    public const string SourceDate = "2026-09-28";

    /// <summary>OpenCode Go publishes a 5-hour window as 20% of the monthly limit.</summary>
    public const decimal FiveHourWindowFraction = 0.20m;

    /// <summary>
    /// OpenCode Go's five-hour allowance for a model, in requests, or null when the plan
    /// publishes none.
    ///
    /// Requests rather than dollars because that is what Go actually publishes per model
    /// on its usage-limits table, and it is the number a person can act on: the dollar
    /// figure was derived from the monthly spend limit and told you nothing about how many
    /// calls it buys you.
    ///
    /// Deliberately independent of where the token rates came from. The rates come from a
    /// live database and the allowances from the published plan page; when the two were
    /// fetched together, going live silently blanked this column, because a live price
    /// carries no allowance.
    /// </summary>
    public static RequestAllowance? FiveHourRequests(ProviderSettings provider, string? model)
    {
        if (string.IsNullOrWhiteSpace(model)) return null;
        if (LivePricing.Current?.GoFiveHourRequests(model) is { } live) return live;
        return SnapshotRequests(provider, model);
    }

    /// <summary>The allowance recorded in the built-in table, used when no live one loaded.</summary>
    private static RequestAllowance? SnapshotRequests(ProviderSettings provider, string? model)
    {
        if (string.IsNullOrWhiteSpace(model)) return null;
        if (PlanFor(provider.BaseUrl) != "g") return null;
        return ByRequestAllowance.TryGetValue(model, out var allowance) ? allowance : null;
    }

    private const string Data = """
        # plan model in out cacheRead cacheWrite monthlyLimit   (USD per 1M tokens; - = not published)
        # z = OpenCode Zen (https://opencode.ai/zen/v1)
        z claude-fable-5-1 10.00 50.00 0.25 12.50 -
        z claude-fable-5 10.00 50.00 1.00 -
        z claude-opus-5-5 4.00 20.00 0.20 5.00 -
        z claude-opus-5 5.00 25.00 0.50 6.25 -
        z claude-opus-4-8 5.00 25.00 0.50 -
        z claude-opus-4-7 5.00 25.00 0.50 -
        z claude-opus-4-6 5.00 25.00 0.50 -
        z claude-opus-4-5 5.00 25.00 0.50 -
        z claude-sonnet-5 2.00 10.00 0.20 2.50 -
        z claude-sonnet-4-6 3.00 15.00 0.30 3.75 -
        z claude-sonnet-4-5 3.00 15.00 0.30 3.75 -
        z claude-haiku-4-5 1.00 5.00 0.10 1.25 -
        z gpt-6-astra 10.00 50.00 1.00 12.50 -
        z gpt-6-sol 2.00 10.00 0.20 2.50 -
        z gpt-6-luna 0.10 0.50 0.01 0.125 -
        z gpt-5.6-sol 0.20 1.20 0.02 0.25 -
        z gpt-5.6-terra 2.00 10.00 0.20 2.50 -
        z gpt-5.6-luna 0.20 1.20 0.02 0.25 -
        z gpt-5.5 5.00 30.00 0.50 -
        z gpt-5.5-pro 30.00 180.00 30.00 -
        z gpt-5.4 2.50 15.00 0.25 -
        z gpt-5.4-pro 30.00 180.00 30.00 -
        z gpt-5.4-mini 0.75 3.00 0.075 -
        z gpt-5.4-nano 0.20 1.25 0.02 -
        z gpt-5.3-codex-spark 1.75 14.00 0.175 -
        z gpt-5.3-codex 1.75 14.00 0.175 -
        z gpt-5.3 1.75 14.00 0.175 -
        z gpt-5.2 1.75 14.00 0.175 -
        z gpt-5.2-codex 1.75 14.00 0.175 -
        z gpt-5.1 1.07 8.50 0.107 -
        z gpt-5.1-codex 1.07 8.50 0.107 -
        z gpt-5.1-codex-max 1.25 10.00 0.125 -
        z gpt-5.1-codex-mini 0.25 2.00 0.025 -
        z gpt-5 1.07 8.50 0.107 -
        z gpt-5-codex 1.07 8.50 0.107 -
        z gpt-5-nano 0.05 0.40 0.005 -
        z gemini-3.8-flash 1.50 7.50 0.15 -
        z gemini-3.7-flash 1.50 7.50 0.15 -
        z gemini-3.6-flash 1.50 7.50 0.15 -
        z gemini-3.5-flash 1.50 9.00 0.15 -
        z gemini-3.5-flash-lite 0.30 2.50 0.03 -
        z gemini-3.1-pro 2.00 12.00 0.20 -
        z gemini-3-flash 0.50 3.00 0.05 -
        z grok-4.7 5.00 30.00 1.00 -
        z grok-4.6 5.00 30.00 1.00 -
        z grok-4.5 5.00 30.00 1.00 -
        z grok-build-0.1 1.00 2.00 0.20 -
        z muse-spark-1.3 1.25 4.25 0.15 -
        z muse-spark-1.2 1.25 4.25 0.15 -
        z qwen3.8-max 2.00 6.00 0.25 2.50 -
        z qwen3.8-flash 0.15 0.47 0.016 0.20 -
        z qwen3.7-max 2.50 7.50 0.50 3.125 -
        z qwen3.7-plus 0.40 1.60 0.04 0.50 -
        z qwen3.6-plus 0.50 3.00 0.05 0.625 -
        z qwen3.5-plus 0.20 1.20 0.02 -
        z deepseek-v4.1-flash 0.30 1.20 0.006 -
        z deepseek-v4-pro 1.74 3.48 0.145 -
        z deepseek-v4-flash 0.14 0.28 0.028 -
        z deepseek-v4-flash-vision-exp 0.14 0.28 0.028 -
        z minimax-m3 0.30 1.20 0.06 -
        z minimax-m2.7 0.30 1.20 0.06 -
        z minimax-m2.5 0.30 1.20 0.06 -
        z glm-5.3-flash 0.15 0.50 0.03 -
        z glm-5.3 1.40 4.40 0.26 -
        z glm-5.2 1.40 4.40 0.26 -
        z glm-5.1 1.40 4.40 0.26 -
        z glm-5 1.00 3.20 0.20 -
        z kimi-k2.5 0.60 3.00 0.10 -
        z kimi-k2.6 0.95 4.00 0.16 -
        z kimi-k2.7-code 0.95 4.00 0.19 -
        z kimi-k3 3.00 15.00 0.30 -
        z jev-1.13 0.042 - - -
        z big-pickle 0 0 0 0 -
        z space-bunny-free 0 0 0 0 -
        z longcat-2.5-preview-free 0 0 0 0 -
        z mimo-v2.6-flash-free 0 0 0 0 -
        z mimo-v2.5-free 0 0 0 0 -
        z ling-3.0-flash-fin-free 0 0 0 0 -
        z nemotron-3-ultra-free 0 0 0 0 -
        z nemotron-3.5-lightning-free 0 0 0 0 -
        z muse-spark-1.3-contributor-free 0 0 0 0 -
        z jev-1.13-free 0 0 0 0 -
        # g = OpenCode Go (https://opencode.ai/zen/go/v1)
        g grok-4.7 5.00 30.00 1.00 - 15
        g grok-4.6 5.00 30.00 1.00 - 15
        g gpt-6-luna 0.10 0.50 0.01 0.125 15
        g gpt-5.6-luna 0.20 1.20 0.02 0.25 15
        g glm-5.3-flash 0.15 0.50 0.03 - 60
        g glm-5.3 1.40 4.40 0.26 - 15
        g glm-5.2 1.40 4.40 0.26 - 60
        g kimi-k3 3.00 15.00 0.30 - 15
        g kimi-k2.7-code 0.95 4.00 0.19 - 60
        g kimi-k2.6 0.95 4.00 0.19 - 60
        g longcat-2.0 0.30 1.20 0.006 - 60
        g deepseek-v4.1-flash 0.30 1.20 0.006 - 60
        g deepseek-v4-pro 1.65 3.96 0.165 - 15
        g deepseek-v4-flash 0.14 0.28 0.028 - 60
        g deepseek-v4-flash-vision-exp 0.14 0.28 0.028 - 60
        g mimo-v2.6-flash 0.14 0.28 0.0028 - 60
        g mimo-v2.6-pro 0.435 0.87 0.003625 - 15
        g mimo-v2.5 0.14 0.28 0.0028 - 60
        g mimo-v2.5-pro 0.435 0.87 0.003625 - 15
        g minimax-m3 0.30 1.20 0.06 0.375 60
        g minimax-m2.7 0.30 1.20 0.06 - 60
        g muse-spark-1.3-contributor 0.10 0.20 0.002 - 60
        g muse-spark-1.2-contributor 0.10 0.20 0.002 - 60
        g qwen3.8-max 2.00 6.00 0.25 2.50 15
        g qwen3.8-flash 0.15 0.47 0.016 0.30 30
        g qwen3.7-plus 0.40 1.60 0.04 - 60
        g hy4-preview 0.834 2.501 0.042 - 30
        g hy3 0.14 0.58 0.035 - 60
        g space-bunny-free 0 0 0 0 -
        g longcat-2.5-preview-free 0 0 0 0 -
        """;

    private static readonly Dictionary<string, ModelPrice> ByPlan = Parse();

    /// <summary>
    /// OpenCode Go's published requests-per-five-hours, as a dated snapshot for the same
    /// reason the prices are: the plan page is the only source and it is fetched live, so
    /// this is what a start with no network shows.
    /// </summary>
    private static readonly Dictionary<string, RequestAllowance> ByRequestAllowance = ParseRequests();

    private const string RequestData = """
        # OpenCode Go, requests per 5 hours. -1 = published as Unlimited.
        glm-5.3-flash 6320
        glm-5.3 220
        glm-5.2 880
        kimi-k3 110
        kimi-k2.7-code 1350
        kimi-k2.6 1150
        longcat-2.0 11400
        longcat-2.5-preview-free -1
        mimo-v2.6-flash 30100
        mimo-v2.6-pro 3250
        mimo-v2.5 30100
        mimo-v2.5-pro 3250
        minimax-m3 3200
        minimax-m2.7 3400
        muse-spark-1.3-contributor 45300
        muse-spark-1.2-contributor 45300
        qwen3.8-max 160
        qwen3.8-flash 5400
        qwen3.7-plus 4300
        deepseek-v4.1-flash 26000
        deepseek-v4-pro 1050
        deepseek-v4-flash 13000
        deepseek-v4-flash-vision-exp 6500
        hy4-preview 1350
        hy3 4300
        space-bunny-free -1
        grok-4.7 169
        grok-4.6 169
        gpt-6-luna 4230
        gpt-5.6-luna 2050
        """;

    private static Dictionary<string, RequestAllowance> ParseRequests()
    {
        var result = new Dictionary<string, RequestAllowance>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in RequestData.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (raw.StartsWith('#')) continue;
            var parts = raw.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length < 2) continue;
            if (!long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var count)) continue;
            result[parts[0]] = count < 0 ? RequestAllowance.UnlimitedValue : new RequestAllowance(count, false);
        }
        return result;
    }

    private static Dictionary<string, ModelPrice> Parse()
    {
        var result = new Dictionary<string, ModelPrice>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in Data.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (raw.StartsWith('#')) continue;
            var parts = raw.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length < 3) continue;
            decimal? N(int i) => i < parts.Length && decimal.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;
            result[$"{parts[0]}/{parts[1]}"] = new(N(2), N(3), N(4), N(5), N(6));
        }
        return result;
    }

    /// <summary>Which published plan a provider base URL belongs to, or null if neither.</summary>
    public static string? PlanFor(string? baseUrl)
    {
        if (string.IsNullOrWhiteSpace(baseUrl)) return null;
        if (baseUrl.Contains("/zen/go/", StringComparison.OrdinalIgnoreCase)) return "g";
        if (baseUrl.Contains("/zen", StringComparison.OrdinalIgnoreCase)) return "z";
        return null;
    }

    /// <summary>
    /// Published price for a model on a specific provider. Local providers are free,
    /// which is a fact rather than an estimate.
    ///
    /// Live prices go first: models.dev is what OpenCode publishes from, and this
    /// snapshot has drifted (it had deepseek-v4-pro at 1.65/3.96 on Go against a
    /// published 0.66/1.98). The snapshot below is the offline fallback, not the
    /// primary source.
    /// </summary>
    public static ModelPrice? For(ProviderSettings provider, string? model)
    {
        if (string.IsNullOrWhiteSpace(model)) return null;
        if (ProviderKinds.IsFree(provider.Kind)) return Free;
        if (LivePricing.Current?.For(provider.BaseUrl, model) is { } live) return live;
        if (VendorPrice(provider, model) is { } vendor) return vendor;
        var plan = PlanFor(provider.BaseUrl);
        return plan is not null && ByPlan.TryGetValue($"{plan}/{model}", out var price) ? price : null;
    }

    /// <summary>
    /// Account providers call the vendor itself, so they are priced at the vendor's
    /// published API rate. On a subscription that is what the usage would cost on the
    /// API, not a charge. Antigravity names variants ("gemini-3.1-pro-high",
    /// "claude-opus-4-6-thinking"); the variant suffix is dropped to find the model.
    /// </summary>
    private static ModelPrice? VendorPrice(ProviderSettings provider, string model)
    {
        if (LivePricing.Current is not { } live || VendorFor(provider, model) is not { } vendor) return null;
        foreach (var candidate in PriceCandidates(model))
            if (live.ForVendor(vendor, candidate) is { } price) return price;
        return null;
    }

    internal static string? VendorFor(ProviderSettings provider, string model)
    {
        var kind = provider.Kind.ToLowerInvariant();
        if (kind is ProviderKinds.Anthropic or ProviderKinds.ClaudeCode) return "anthropic";
        if (kind is ProviderKinds.Gemini or ProviderKinds.GeminiCli) return "google";
        if (kind == ProviderKinds.OpenAi &&
            (provider.AuthMode == ProviderAuthMode.OAuth || provider.BaseUrl.Contains("api.openai.com", StringComparison.OrdinalIgnoreCase)))
            return "openai";
        if (kind == ProviderKinds.Antigravity)
        {
            if (model.StartsWith("gemini", StringComparison.OrdinalIgnoreCase)) return "google";
            if (model.StartsWith("claude", StringComparison.OrdinalIgnoreCase)) return "anthropic";
            if (model.StartsWith("gpt", StringComparison.OrdinalIgnoreCase)) return "openai";
        }
        return null;
    }

    private static readonly Regex VariantSuffix = new("-(?:high|medium|low|thinking)$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    internal static IEnumerable<string> PriceCandidates(string model)
    {
        var current = model;
        while (true)
        {
            yield return current;
            yield return current + "-preview";
            var stripped = VariantSuffix.Replace(current, string.Empty);
            if (stripped == current) yield break;
            current = stripped;
        }
    }

    /// <summary>Local providers cost nothing to run.</summary>
    public static readonly ModelPrice Free = new(0m, 0m, 0m, 0m, null);

    /// <summary>
    /// Cost of a request, using the plan's published rates. Preferred over
    /// <see cref="ModelCatalog"/> wherever the provider is a known plan, because the
    /// same model can cost differently on Zen and Go - an estimate that ignores the
    /// plan is wrong for half of them.
    /// </summary>
    /// <param name="input">Uncached input tokens only; the parser keeps cache reads and writes apart.</param>
    public static decimal? Estimate(ProviderSettings provider, string? model, long? input, long? output,
        long? cacheRead = null, long? cacheWrite = null)
    {
        var price = For(provider, model);
        if (price?.InputPerMillion is not { } rateIn || price.OutputPerMillion is not { } rateOut) return null;
        if (!input.HasValue && !output.HasValue && !cacheRead.HasValue) return null;
        var total = (input ?? 0) * rateIn + (output ?? 0) * rateOut;
        // A cache rate the source does not publish falls back to the plain input rate:
        // the tokens were still processed, and leaving them out would understate cost.
        if (cacheRead is > 0)
            total += cacheRead.Value * (price.CachedReadPerMillion ?? rateIn);
        if (cacheWrite is > 0)
            total += cacheWrite.Value * (price.CachedWritePerMillion ?? rateIn);
        return total / 1_000_000m;
    }

    public static int KnownModelCount => ByPlan.Count;
}
