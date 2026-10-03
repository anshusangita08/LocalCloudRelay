using System.Globalization;
using System.Text.RegularExpressions;

namespace LocalCloudRelay;

/// <summary>
/// Reads a provider's rate-limit headers to decide whether to send it less traffic.
///
/// OpenAI-style gateways send x-ratelimit-limit-* / x-ratelimit-remaining-* with a reset
/// such as "6m0s"; Anthropic sends anthropic-ratelimit-*-limit / -remaining with an
/// RFC 3339 reset time. A 429 carries Retry-After in seconds or as an HTTP date.
/// </summary>
public static class RateLimitSignal
{
    /// <summary>Below this share of a limit left, new turns go elsewhere until the reset.</summary>
    public const decimal LowHeadroom = 0.10m;

    public static readonly TimeSpan DefaultBackoff = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan MaxBackoff = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan MinBackoff = TimeSpan.FromSeconds(5);

    private static readonly (string Limit, string Remaining, string Reset)[] Pairs =
    [
        ("x-ratelimit-limit-requests", "x-ratelimit-remaining-requests", "x-ratelimit-reset-requests"),
        ("x-ratelimit-limit-tokens", "x-ratelimit-remaining-tokens", "x-ratelimit-reset-tokens"),
        ("anthropic-ratelimit-requests-limit", "anthropic-ratelimit-requests-remaining", "anthropic-ratelimit-requests-reset"),
        ("anthropic-ratelimit-tokens-limit", "anthropic-ratelimit-tokens-remaining", "anthropic-ratelimit-tokens-reset"),
        ("anthropic-ratelimit-input-tokens-limit", "anthropic-ratelimit-input-tokens-remaining", "anthropic-ratelimit-input-tokens-reset"),
        ("anthropic-ratelimit-output-tokens-limit", "anthropic-ratelimit-output-tokens-remaining", "anthropic-ratelimit-output-tokens-reset")
    ];

    private static readonly Regex GoDuration = new(@"^(?:(\d+(?:\.\d+)?)h)?(?:(\d+(?:\.\d+)?)m(?!s))?(?:(\d+(?:\.\d+)?)s)?(?:(\d+(?:\.\d+)?)ms)?$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>
    /// How long a plan account (Claude Code, Antigravity, Gemini CLI) stays out of rotation
    /// after reporting a usage limit. Plan limits reset over hours and the CLIs rarely say
    /// when, so this is a recheck interval rather than a known reset.
    /// </summary>
    public static readonly TimeSpan PlanLimitBackoff = TimeSpan.FromMinutes(30);

    private static readonly string[] LimitPhrases =
    [
        "rate limit", "rate-limit", "ratelimit", "usage limit", "quota", "too many requests",
        "resource_exhausted", "resource exhausted", "429", "overloaded"
    ];

    /// <summary>True when a CLI's own error text says it hit a rate or usage limit.</summary>
    public static bool LooksLikeLimit(string? text) =>
        !string.IsNullOrWhiteSpace(text) &&
        LimitPhrases.Any(phrase => text.Contains(phrase, StringComparison.OrdinalIgnoreCase));

    private static readonly string[] SignedOutPhrases =
    [
        "failed to authenticate", "not logged in", "please run /login", "session expired",
        "invalid api key", "login required", "sign in again", "unauthenticated", "authentication required"
    ];

    /// <summary>
    /// True when a CLI's own error text says it is signed out or its sign-in expired, for
    /// example Claude Code's "Failed to authenticate: OAuth session expired and could not be
    /// refreshed". Retrying cannot fix that; the operator has to sign in again.
    /// </summary>
    public static bool LooksSignedOut(string? text) =>
        !string.IsNullOrWhiteSpace(text) &&
        SignedOutPhrases.Any(phrase => text.Contains(phrase, StringComparison.OrdinalIgnoreCase));

    /// <summary>How long to keep traffic off a provider that answered 429.</summary>
    public static TimeSpan BackoffFor429(IReadOnlyDictionary<string, string> headers, DateTimeOffset now) =>
        Clamp(headers.TryGetValue("retry-after", out var value) ? ParseRetryAfter(value, now) : null) ?? DefaultBackoff;

    /// <summary>
    /// How long to keep new turns off a provider whose headers say a limit is nearly used
    /// up, or null when there is headroom on every limit it reports.
    /// </summary>
    public static TimeSpan? LowHeadroomFor(IReadOnlyDictionary<string, string> headers, DateTimeOffset now)
    {
        TimeSpan? longest = null;
        foreach (var (limitName, remainingName, resetName) in Pairs)
        {
            if (!TryNumber(headers, limitName, out var limit) || limit <= 0 ||
                !TryNumber(headers, remainingName, out var remaining)) continue;
            if (remaining / limit >= LowHeadroom) continue;
            var wait = headers.TryGetValue(resetName, out var reset) ? ParseReset(reset, now) : null;
            var bounded = Clamp(wait) ?? DefaultBackoff;
            if (longest is null || bounded > longest) longest = bounded;
        }
        return longest;
    }

    internal static TimeSpan? ParseRetryAfter(string value, DateTimeOffset now)
    {
        value = value.Trim();
        if (decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var seconds))
            return TimeSpan.FromSeconds((double)seconds);
        return DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at)
            ? at - now
            : null;
    }

    internal static TimeSpan? ParseReset(string value, DateTimeOffset now)
    {
        value = value.Trim();
        var go = GoDuration.Match(value);
        if (go.Success && value.Length > 0)
        {
            double Part(int i) => go.Groups[i].Success ? double.Parse(go.Groups[i].Value, CultureInfo.InvariantCulture) : 0;
            return TimeSpan.FromHours(Part(1)) + TimeSpan.FromMinutes(Part(2)) + TimeSpan.FromSeconds(Part(3)) +
                   TimeSpan.FromMilliseconds(Part(4));
        }
        return ParseRetryAfter(value, now);
    }

    private static TimeSpan? Clamp(TimeSpan? wait) => wait is not { } value
        ? null
        : value < MinBackoff ? MinBackoff : value > MaxBackoff ? MaxBackoff : value;

    private static bool TryNumber(IReadOnlyDictionary<string, string> headers, string name, out decimal value)
    {
        value = 0;
        return headers.TryGetValue(name, out var text) &&
               decimal.TryParse(text.Split(',')[0].Trim(), NumberStyles.Number, CultureInfo.InvariantCulture, out value);
    }
}
