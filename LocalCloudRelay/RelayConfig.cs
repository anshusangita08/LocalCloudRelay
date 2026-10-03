using System.Text.Json;
using System.Text.Json.Serialization;

namespace LocalCloudRelay;

public static class ProviderKinds
{
    public const string OpenAi = "openai";
    public const string Anthropic = "anthropic";
    public const string Gemini = "gemini";
    public const string Local = "local";
    public const string ClaudeCode = "claude-code";
    public const string Antigravity = "antigravity";
    public const string GeminiCli = "gemini-cli";

    /// <summary>Local models are free to run, so a token-price estimate would be a lie.</summary>
    public static bool IsFree(string? kind) =>
        string.Equals(kind, Local, StringComparison.OrdinalIgnoreCase);
}

[JsonConverter(typeof(JsonStringEnumConverter<ProviderAuthMode>))]
public enum ProviderAuthMode
{
    ApiKey,
    OAuth,
    CliAccount
}

/// <summary>App-managed OAuth credentials; settings storage protects this with DPAPI.</summary>
public sealed record ProviderOAuthTokens(
    string AccessToken,
    string? RefreshToken = null,
    DateTimeOffset? ExpiresAtUtc = null,
    string? Scope = null,
    string? TokenType = null,
    string? IdToken = null)
{
    public override string ToString() => "Provider OAuth credentials (redacted)";
}

public sealed record ProviderSettings(
    string Id,
    string Name,
    string Kind,
    string BaseUrl,
    string? ApiKey,
    bool Enabled,
    int Priority,
    // Models the operator has switched off. Absent or empty means serve everything the
    // provider advertises, which is the behaviour before this field existed.
    IReadOnlyList<string>? DisabledModels = null,
    ProviderAuthMode AuthMode = ProviderAuthMode.ApiKey,
    string? AccountId = null,
    string? ProjectId = null,
    string? CliExecutable = null,
    IReadOnlyList<string>? ImportedModels = null,
    DateTimeOffset? ModelsFetchedAt = null,
    ProviderOAuthTokens? OAuthTokens = null,
    string? OAuthClientId = null,
    string? OAuthClientSecret = null,
    string? OAuthEmail = null,
    string? OAuthIssuer = null)
{
    public override string ToString() =>
        $"ProviderSettings {{ Id = {Id}, Name = {Name}, Kind = {Kind}, BaseUrl = {BaseUrl}, Enabled = {Enabled}, Priority = {Priority}, AuthMode = {AuthMode}, AccountId = {AccountId}, ProjectId = {ProjectId}, ApiKey = [redacted], OAuthTokens = {(OAuthTokens is null ? "null" : "[redacted]")}, OAuthClientSecret = [redacted] }}";

    public static ProviderSettings Create(string name, string kind, string baseUrl, string? apiKey = null) =>
        new(Guid.NewGuid().ToString("N")[..8], name, kind, baseUrl, apiKey, true, 0);

    public bool ServesModel(string model) =>
        DisabledModels is null || !DisabledModels.Contains(model, StringComparer.OrdinalIgnoreCase);

    /// <summary>Account-backed profiles must route only their imported exact model IDs.</summary>
    public bool RequiresExactModelId => AuthMode is ProviderAuthMode.OAuth or ProviderAuthMode.CliAccount;

    /// <summary>
    /// Sets whether a model is served, and normalises the list either way. Both the
    /// de-duplication and the case-insensitive removal are load-bearing: a
    /// DataGridView checkbox column delivers this event for its own population, again
    /// on a sort, and again on a rebuild, so a naive append grew the list without
    /// bound. De-duplicating on the enable path as well repairs a list that is
    /// already inflated instead of leaving it that way forever.
    /// </summary>
    public ProviderSettings WithModel(string model, bool serve)
    {
        if (DisabledModels is null)
            return serve ? this : this with { DisabledModels = [model] };

        var kept = DisabledModels
            .Where(m => !m.Equals(model, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        // An emptied list becomes [] rather than null. ServesModel treats both as
        // "serve everything", and [] keeps the property non-null for callers.
        return serve
            ? this with { DisabledModels = kept }
            : this with { DisabledModels = [.. kept, model] };
    }
}

public sealed record RelayConfig(
    int SchemaVersion,
    string LocalApiKey,
    IReadOnlyList<ProviderSettings> Providers,
    IReadOnlyList<RouterRule>? RouterRules = null,
    string? OAuthHostId = null)
{
    public const int CurrentSchemaVersion = 5;

    public IReadOnlyList<ProviderSettings> EnabledProviders =>
        Providers.Where(p => p.Enabled).OrderBy(p => p.Priority).ToArray();

    /// <summary>Rules a client can actually ask for: enabled, named and of a known strategy.</summary>
    public IReadOnlyList<RouterRule> UsableRules =>
        (RouterRules ?? []).Where(r => r.IsUsable).ToArray();
}

/// <summary>
/// Reads either the v2 provider list or the legacy single-gateway blob and returns a
/// normalized RelayConfig. The legacy shape was
/// {"UpstreamUrl":..,"UpstreamApiKey":..,"LocalApiKey":..} and becomes a one-provider
/// v2 config, which the caller re-saves on next successful start.
/// </summary>
public static class RelayConfigReader
{
    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };

    private sealed class Document
    {
        public int SchemaVersion { get; set; }
        public string? LocalApiKey { get; set; }
        public string? UpstreamUrl { get; set; }
        public string? UpstreamApiKey { get; set; }
        public List<Entry>? Providers { get; set; }
        public List<RuleEntry>? RouterRules { get; set; }
        public string? OAuthHostId { get; set; }
    }

    private sealed class RuleEntry
    {
        public string? Name { get; set; }
        public string? Strategy { get; set; }
        public List<string>? Models { get; set; }
        public bool Enabled { get; set; } = true;
        public decimal? DailyBudgetUsd { get; set; }
    }

    private sealed class Entry
    {
        public string? Id { get; set; }
        public string? Name { get; set; }
        public string? Kind { get; set; }
        public string? BaseUrl { get; set; }
        public string? ApiKey { get; set; }
        public bool Enabled { get; set; } = true;
        public int Priority { get; set; }
        public List<string>? DisabledModels { get; set; }
        public string? AuthMode { get; set; }
        public string? AccountId { get; set; }
        public string? ProjectId { get; set; }
        public string? CliExecutable { get; set; }
        public List<string>? ImportedModels { get; set; }
        public DateTimeOffset? ModelsFetchedAt { get; set; }
        public ProviderOAuthTokens? OAuthTokens { get; set; }
        public string? OAuthClientId { get; set; }
        public string? OAuthClientSecret { get; set; }
        public string? OAuthEmail { get; set; }
        public string? OAuthIssuer { get; set; }
    }

    public static RelayConfig? FromJson(byte[] utf8)
    {
        Document? document;
        try { document = JsonSerializer.Deserialize<Document>(utf8, Options); }
        catch (JsonException) { return null; }
        if (document is null) return null;

        var key = document.LocalApiKey;
        if (string.IsNullOrWhiteSpace(key)) return null;

        var rules = (document.RouterRules ?? [])
            .Where(r => !string.IsNullOrWhiteSpace(r.Name) && RouterStrategies.IsKnown(r.Strategy))
            .Select(r => new RouterRule(
                r.Name!.Trim(),
                // Canonicalised on read, so a rule written before a strategy was renamed
                // keeps working rather than quietly becoming unusable.
                RouterStrategies.Canonical(r.Strategy),
                [.. (r.Models ?? []).Where(m => !string.IsNullOrWhiteSpace(m)).Select(m => m.Trim()).Distinct(StringComparer.OrdinalIgnoreCase)],
                r.Enabled,
                r.DailyBudgetUsd is > 0 ? r.DailyBudgetUsd : null))
            // A duplicate name would make resolution depend on list order, which is not
            // something the operator can see or control.
            .GroupBy(r => r.Key, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToArray();

        if (document.Providers is not null)
        {
            // A present-but-empty list is a legitimate state: the key exists and no
            // provider has been added yet. Treating it as "no config" would mint a new
            // key on every launch and throw away the one already handed to clients.
            var providers = document.Providers
                .Where(p => !string.IsNullOrWhiteSpace(p.BaseUrl))
                .Select((p, index) => new ProviderSettings(
                    string.IsNullOrWhiteSpace(p.Id) ? Guid.NewGuid().ToString("N")[..8] : p.Id,
                    string.IsNullOrWhiteSpace(p.Name) ? $"Provider {index + 1}" : p.Name,
                    string.IsNullOrWhiteSpace(p.Kind) ? ProviderKinds.OpenAi : p.Kind,
                    p.BaseUrl!.TrimEnd('/'),
                    p.ApiKey,
                    p.Enabled,
                    p.Priority,
                    p.DisabledModels,
                    ParseAuthMode(p.AuthMode),
                    p.AccountId,
                    p.ProjectId,
                    p.CliExecutable,
                    p.ImportedModels,
                    p.ModelsFetchedAt,
                    p.OAuthTokens,
                    p.OAuthClientId,
                    p.OAuthClientSecret,
                    p.OAuthEmail,
                    p.OAuthIssuer))
                .ToArray();
            return new RelayConfig(RelayConfig.CurrentSchemaVersion, key, providers, rules, document.OAuthHostId);
        }

        // Legacy single-gateway blob.
        if (string.IsNullOrWhiteSpace(document.UpstreamUrl)) return null;
        return new RelayConfig(RelayConfig.CurrentSchemaVersion, key,
        [
            ProviderSettings.Create("Primary gateway", ProviderKinds.OpenAi,
                document.UpstreamUrl.TrimEnd('/'), document.UpstreamApiKey)
        ], rules, document.OAuthHostId);
    }

    private static ProviderAuthMode ParseAuthMode(string? value) =>
        Enum.TryParse<ProviderAuthMode>(value, ignoreCase: true, out var mode) && Enum.IsDefined(mode)
            ? mode
            : ProviderAuthMode.ApiKey;
}
