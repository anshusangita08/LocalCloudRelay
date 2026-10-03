using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.NetworkInformation;
using System.Text.Json;

namespace LocalCloudRelay;

public sealed record DiscoveredEndpoint(
    string Source,
    string SourcePath,
    string Name,
    string BaseUrl,
    string? ApiKey,
    string Kind);

public sealed record DiscoveryResult(
    IReadOnlyList<DiscoveredEndpoint> Found,
    IReadOnlyList<string> Checked)
{
    public bool AnythingFound => Found.Count > 0;
}

/// <summary>
/// Reads endpoints these tools are already pointed at, so a provider can be created
/// from an existing configuration instead of retyped.
///
/// It only ever READS. Keys are copied into the DPAPI store like any other credential
/// and are never logged or written anywhere else. Nothing here contacts a network.
/// </summary>
public static class ClientDiscovery
{
    public static DiscoveryResult Scan(string? userProfile = null, IReadOnlyDictionary<string, string?>? environment = null)
    {
        var home = userProfile ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var env = environment ?? ReadEnvironment();
        var found = new List<DiscoveredEndpoint>();
        var checkedPaths = new List<string>();

        foreach (var path in Candidate(home, ".claude/settings.json")) ScanClaudeCode(path, env, found, checkedPaths);
        foreach (var path in Candidate(home, ".gemini/settings.json")) ScanGeminiSettings(path, env, found, checkedPaths);
        foreach (var path in OpenCodeCandidates(home)) ScanOpenCode(path, env, found, checkedPaths);
        ScanEnvironment(env, found, checkedPaths);

        return new DiscoveryResult(
            found.GroupBy(f => f.BaseUrl, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).ToArray(),
            checkedPaths);
    }

    private static IEnumerable<string> Candidate(string home, string relative)
    {
        var path = Path.Combine(home, relative.Replace('/', Path.DirectorySeparatorChar));
        if (File.Exists(path)) yield return path;
    }

    private static IEnumerable<string> OpenCodeCandidates(string home)
    {
        // opencode writes JSONC, and the extension is not always .json, so try both.
        var roots = new[]
        {
            Path.Combine(home, ".config", "opencode"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "opencode"),
            Path.Combine(home, ".opencode"),
            Directory.GetCurrentDirectory()        };
        foreach (var root in roots)
            foreach (var name in new[] { "opencode.jsonc", "opencode.json" })
            {
                var path = Path.Combine(root, name);
                if (File.Exists(path)) yield return path;
            }
    }

    private static void ScanClaudeCode(string path, IReadOnlyDictionary<string, string?> environment, List<DiscoveredEndpoint> found, List<string> checkedPaths)
    {
        checkedPaths.Add(path);
        if (TryReadJson(path, out var root) && root.ValueKind == JsonValueKind.Object)
        {
            // Claude Code keeps overrides under env; a user-set base URL lives there.
            if (TryGetProperty(root, "env", out var env) && env.ValueKind == JsonValueKind.Object)
            {
                var url = String(env, "ANTHROPIC_BASE_URL");
                if (IsUsable(url) && !IsRelayEndpoint(url))
                    found.Add(new("Claude Code", path, "Claude Code", Normalize(url), ResolveApiKey(String(env, "ANTHROPIC_API_KEY"), environment), ProviderKinds.Anthropic));
            }
        }
    }

    private static void ScanGeminiSettings(string path, IReadOnlyDictionary<string, string?> environment, List<DiscoveredEndpoint> found, List<string> checkedPaths)
    {
        checkedPaths.Add(path);
        if (!TryReadJson(path, out var root) || root.ValueKind != JsonValueKind.Object) return;
        if (!TryGetProperty(root, "env", out var env) || env.ValueKind != JsonValueKind.Object) return;
        var url = String(env, "GOOGLE_GEMINI_BASE_URL") ?? String(env, "GEMINI_BASE_URL");
        if (!IsUsable(url) || IsRelayEndpoint(url)) return;
        found.Add(new("Gemini CLI", path, "Gemini CLI", Normalize(url),
            ResolveApiKey(String(env, "GEMINI_API_KEY") ?? String(env, "GOOGLE_API_KEY"), environment), ProviderKinds.Gemini));
    }

    private static void ScanOpenCode(string path, IReadOnlyDictionary<string, string?> environment, List<DiscoveredEndpoint> found, List<string> checkedPaths)
    {
        checkedPaths.Add(path);
        if (!TryReadJson(path, out var root) || root.ValueKind != JsonValueKind.Object) return;
        if (!TryGetProperty(root, "provider", out var providers) || providers.ValueKind != JsonValueKind.Object) return;
        foreach (var entry in providers.EnumerateObject())
        {
            if (entry.Value.ValueKind != JsonValueKind.Object) continue;
            if (!TryGetProperty(entry.Value, "options", out var options) || options.ValueKind != JsonValueKind.Object) continue;
            var url = String(options, "baseURL") ?? String(options, "baseUrl");
            if (!IsUsable(url)) continue;
            if (IsRelayEndpoint(url)) continue;
            var host = SafeHost(url) ?? entry.Name;
            var kind = DetectKindFromNpm(entry.Value);
            var apiKey = ResolveApiKey(String(options, "apiKey"), environment);
            found.Add(new($"OpenCode ({entry.Name})", path, host, Normalize(url), apiKey, kind));
        }
    }

    private static void ScanEnvironment(IReadOnlyDictionary<string, string?> env, List<DiscoveredEndpoint> found, List<string> checkedPaths)
    {
        checkedPaths.Add("environment variables");
        void Add(string name, string? url, string? key, string kind)
        {
            if (!IsUsable(url) || IsRelayEndpoint(url)) return;
            found.Add(new($"{name} (env)", "environment", name, Normalize(url), ResolveApiKey(key, env), kind));
        }

        Add("ANTHROPIC", env.GetValueOrDefault("ANTHROPIC_BASE_URL"), env.GetValueOrDefault("ANTHROPIC_API_KEY"), ProviderKinds.Anthropic);
        Add("OpenAI", env.GetValueOrDefault("OPENAI_BASE_URL") ?? env.GetValueOrDefault("OPENAI_API_BASE"),
            env.GetValueOrDefault("OPENAI_API_KEY"), ProviderKinds.OpenAi);
        Add("GOOGLE_GEMINI", env.GetValueOrDefault("GOOGLE_GEMINI_BASE_URL"), env.GetValueOrDefault("GEMINI_API_KEY"), ProviderKinds.Gemini);
    }

    // ---------------------------------------------------------------- helpers

    private static IReadOnlyDictionary<string, string?> ReadEnvironment()
    {
        var result = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
            if (entry.Key is string key) result[key] = entry.Value as string;
        return result;
    }

    private static bool TryReadJson(string path, out JsonElement root)
    {
        root = default;
        try
        {
            using var stream = File.OpenRead(path);
            using var document = JsonDocument.Parse(stream, new JsonDocumentOptions
            {
                // opencode's config is JSONC: comments and trailing commas are legal there.
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true
            });
            root = document.RootElement.Clone();
            return true;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException) { return false; }
    }

    private static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
    {
        if (element.TryGetProperty(name, out value)) return true;
        foreach (var property in element.EnumerateObject())
            if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) { value = property.Value; return true; }
        value = default;
        return false;
    }

    private static string? String(JsonElement element, string name) =>
        TryGetProperty(element, name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    /// <summary>NotNullWhen so callers can pass the value straight to Normalize after the check.</summary>
    private static bool IsUsable([NotNullWhen(true)] string? url) =>
        !string.IsNullOrWhiteSpace(url)
        && Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && uri.Scheme is "http" or "https"
        && !string.IsNullOrEmpty(uri.Host);

    /// <summary>Trims a trailing slash but keeps any path prefix. A gateway behind a path
    /// (opencode.ai/zen/go/v1, a LiteLLM on /proxy) 404s if reduced to its origin.</summary>
    private static string Normalize(string url)
    {
        var uri = new Uri(url);
        var text = uri.GetLeftPart(UriPartial.Path).TrimEnd('/');
        return text.Length == 0 ? uri.GetLeftPart(UriPartial.Authority) : text;
    }

    private static string? SafeHost(string url)
    {
        try { return Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host : null; } catch { return null; }
    }

    /// <summary>
    /// Resolves {env:NAME} references to environment variables.
    /// If the environment variable is null or missing, returns empty string (not null).
    /// </summary>
    private static string? ResolveApiKey(string? value, IReadOnlyDictionary<string, string?> environment)
    {
        if (string.IsNullOrEmpty(value)) return value;
        if (value.StartsWith("{env:", StringComparison.OrdinalIgnoreCase) && value.EndsWith('}'))
        {
            var envName = value[5..^1];
            foreach (var entry in environment)
                if (entry.Key.Equals(envName, StringComparison.OrdinalIgnoreCase))
                    return entry.Value ?? string.Empty;
            return string.Empty;
        }
        return value;
    }

    /// <summary>
    /// Detects the provider kind from the npm package name in the provider config.
    /// Returns Anthropic for @ai-sdk/anthropic, otherwise defaults to OpenAi.
    /// </summary>
    private static string DetectKindFromNpm(JsonElement providerConfig)
    {
        if (TryGetProperty(providerConfig, "npm", out var npm) && npm.ValueKind == JsonValueKind.String)
        {
            var npmPackage = npm.GetString();
            if (npmPackage?.Equals("@ai-sdk/anthropic", StringComparison.OrdinalIgnoreCase) == true)
                return ProviderKinds.Anthropic;
        }
        return ProviderKinds.OpenAi;
    }

    /// <summary>
    /// Checks if a URL points at the relay itself (localhost, 127.0.0.1, or machine name on the relay port).
    /// </summary>
    private static bool IsRelayEndpoint(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
        var host = uri.Host.ToLowerInvariant();
        var port = uri.Port;

        // Check for localhost, 127.0.0.1, or machine name on relay port
        if (port == RelayProtocol.Port && (
            string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(host, "127.0.0.1", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(host, System.Environment.MachineName, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(host, System.Environment.MachineName + ".local", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        if (port == RelayProtocol.Port && IPAddress.TryParse(uri.Host, out var address))
        {
            try
            {
                return IPAddress.IsLoopback(address) || NetworkInterface.GetAllNetworkInterfaces()
                    .SelectMany(network => network.GetIPProperties().UnicastAddresses)
                    .Any(unicast => unicast.Address.Equals(address));
            }
            catch (NetworkInformationException)
            {
                return IPAddress.IsLoopback(address);
            }
        }

        return false;
    }
}
