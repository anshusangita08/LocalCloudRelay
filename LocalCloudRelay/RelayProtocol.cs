using System.Security.Cryptography;
using System.Text;

namespace LocalCloudRelay;

public static class RelayProtocol
{
    public const int Port = 8787;
    public const string ListenAddress = "0.0.0.0";

    public static string CreateLocalKey() => "local-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(18)).ToLowerInvariant();

    /// <summary>
    /// True for gateways that reject a request carrying no session id.
    ///
    /// OpenCode answers 400 "Request is missing x-opencode-session and cannot be routed
    /// efficiently" without one, which is fatal for a client like Claude Code that has no
    /// concept of the header. The relay fills it in from the session key it already
    /// derives, so the requirement never reaches the client.
    /// </summary>
    public static bool SpeaksOpenCodeSession(string? baseUrl) =>
        !string.IsNullOrWhiteSpace(baseUrl) &&
        baseUrl.Contains("opencode.ai", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// A short digest of the session key, safe to send upstream: the key can hold the
    /// client's LAN address, the digest cannot.
    /// </summary>
    public static string SessionHash(string sessionKey) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sessionKey)).AsSpan(0, 8)).ToLowerInvariant();

    /// <summary>
    /// True for upstreams known to accept OpenAI's prompt_cache_key. Other
    /// OpenAI-compatible gateways may reject an unknown field, so this is an allow list.
    /// </summary>
    public static bool AcceptsPromptCacheKey(string? baseUrl) =>
        Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) &&
        uri.Host.Equals("api.openai.com", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Adds prompt_cache_key to a JSON request body that has none. OpenAI routes requests
    /// with the same key to the same cache, so a conversation's turns hit the prefix the
    /// previous turn wrote. A key the client chose is kept; anything not a JSON object is
    /// returned unchanged.
    /// </summary>
    public static string WithPromptCacheKey(string json, string cacheKey)
    {
        try
        {
            if (System.Text.Json.Nodes.JsonNode.Parse(json) is not System.Text.Json.Nodes.JsonObject body ||
                body.ContainsKey("prompt_cache_key")) return json;
            body["prompt_cache_key"] = cacheKey;
            return body.ToJsonString();
        }
        catch (System.Text.Json.JsonException)
        {
            return json;
        }
    }

    /// <summary>Client credential headers we accept. Gemini CLI sends x-goog-api-key;
    /// Azure-style clients send api-key; the rest use x-api-key or a Bearer token.</summary>
    public static readonly string[] CredentialHeaders =
    [
        "x-api-key", "x-goog-api-key", "api-key"
    ];

    public static string? StripBearer(string? value) =>
        value is not null && value.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? value[7..].Trim() : value;

    /// <summary>Constant-time comparison so a wrong key cannot be recovered by timing.
    /// An empty local key never authorizes, even against an empty candidate.</summary>
    public static bool IsAuthorized(string? localKey, params string?[] candidates)
    {
        if (string.IsNullOrEmpty(localKey)) return false;
        var expected = Encoding.UTF8.GetBytes(localKey);
        var matched = false;
        foreach (var candidate in candidates)
        {
            if (string.IsNullOrEmpty(candidate)) continue;
            matched |= CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(candidate), expected);
        }
        return matched;
    }

    public static Uri NormalizeBaseUri(Uri uri) => uri.AbsoluteUri.EndsWith('/')
        ? uri
        : new Uri(uri.AbsoluteUri + "/");

    public static Uri Join(Uri baseUri, string pathAndQuery)
    {
        var baseNormalized = NormalizeBaseUri(baseUri);

        // SSRF check: reject paths that look like absolute URLs or protocol-relative URLs
        // Check BEFORE trimming so we catch "//" at the start
        if (pathAndQuery.StartsWith("//") || Uri.TryCreate(pathAndQuery, UriKind.Absolute, out _))
            throw new ArgumentException($"Path contains an absolute URI or protocol-relative URL, refusing SSRF: {pathAndQuery}");

        var path = pathAndQuery.TrimStart('/');

        // Check for scheme in path (e.g., "http://evil.example/v1")
        // Reject if ":" appears before "/" since that's likely a scheme
        if (path.Contains(':'))
        {
            var slashIdx = path.IndexOf('/');
            // If ":" appears before "/" or no "/" exists, it's likely a scheme. Paths like /v1/foo:bar are fine.
            if (slashIdx < 0 || path.IndexOf(':') < slashIdx)
                throw new ArgumentException($"Path contains a scheme, refusing SSRF: {path}");
        }

        // A provider base that already ends in a version segment, joined with a path
        // that starts with the same one, would double it - opencode.ai/zen/v1 plus
        // /v1/chat/completions becoming /zen/v1/v1/chat/completions. Only an exact
        // match is collapsed: a genuinely different version (v1 vs v1beta) is the
        // upstream's business, not ours to rewrite.
        var lastSegment = baseNormalized.AbsolutePath.TrimEnd('/').Split('/').LastOrDefault() ?? string.Empty;
        if (IsVersionSegment(lastSegment))
        {
            if (path.Equals(lastSegment, StringComparison.OrdinalIgnoreCase)) path = string.Empty;
            else if (path.StartsWith(lastSegment + "/", StringComparison.OrdinalIgnoreCase))
                path = path[(lastSegment.Length + 1)..];
        }

        return new Uri(baseNormalized, path);
    }

    private static bool IsVersionSegment(string segment) =>
        segment.Length >= 2 && (segment[0] is 'v' or 'V') && char.IsAsciiDigit(segment[1]);

    /// <summary>Probes a provider's model list. Tolerates a base URL that already ends in /v1,
    /// which would otherwise produce /v1/v1/models and a spurious 404.</summary>
    public static Uri ModelsUri(Uri baseUri)
    {
        var normalized = NormalizeBaseUri(baseUri);
        return normalized.AbsolutePath.TrimEnd('/').EndsWith("/v1", StringComparison.OrdinalIgnoreCase)
            ? new Uri(normalized, "models")
            : new Uri(normalized, "v1/models");
    }

    /// <summary>
    /// One advertised base URL has to serve clients that append different suffixes to
    /// it. An OpenAI client given http://host:8787/v1 posts /v1/chat/completions. Claude
    /// Code and Gemini CLI given the same string post /v1/v1/messages and
    /// /v1/v1beta/models/... - a doubled /v1. Collapsing it is what lets a single base
    /// URL and a single key work across every client, which is the whole point of the
    /// relay. Providers are selected by model, not by which client connected.
    /// </summary>
    public static string NormalizeRequestPath(string path)
    {
        // Two clients, two mistakes, both producing a non-canonical version segment.

        // 1. A /v1 base URL plus a client that appends its own version gives a
        //    doubled segment: /v1/v1/messages, /v1/v1beta/models/... Drop the first.
        //    Anchored, so a legitimate /api/v1/v1/... survives.
        if (path.StartsWith("/v1/v1", StringComparison.OrdinalIgnoreCase)) return path[3..];

        // 2. A root base URL plus a client that appends a bare endpoint name gives an
        //    unversioned path: /chat/completions, /models. Add the version, so the
        //    advertised base URL can be the bare host and still serve every client.
        //    ponytail: only fires when NO segment anywhere looks like a version, so a
        //    path we do not recognise - /api/v1/v1/models - is forwarded untouched
        //    rather than guessed at. Ceiling: an upstream that genuinely serves
        //    unversioned paths would 404. Per-provider opt-out if that ever bites.
        return ContainsVersionSegment(path) ? path : "/v1" + path;
    }

    private static bool ContainsVersionSegment(string path)
    {
        for (var start = 0; start < path.Length;)
        {
            var next = path.IndexOf('/', start);
            var length = (next < 0 ? path.Length : next) - start;
            if (length >= 2 && (path[start] is 'v' or 'V') && char.IsAsciiDigit(path[start + 1]))
                return true;
            if (next < 0) break;
            start = next + 1;
        }
        return false;
    }

    public static bool IsHopByHop(string name) => name.Equals("Connection", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("Keep-Alive", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("Proxy-Authenticate", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("Proxy-Authorization", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("TE", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("Trailer", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("Upgrade", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("Proxy-Connection", StringComparison.OrdinalIgnoreCase);

    public static bool IsForwarded(string name) => name.StartsWith("X-Forwarded-", StringComparison.OrdinalIgnoreCase);
}
