using System.Text.Json;
using System.Text.Json.Serialization;

namespace LocalCloudRelay;

public sealed record ProviderRoute(ProviderSettings Provider, string Model, string? ViaRouter = null,
    string? ViaAccountAlias = null);

public sealed record CatalogSnapshot(
    DateTimeOffset FetchedAt,
    IReadOnlyDictionary<string, IReadOnlyList<string>> ModelsByProviderId)
{
    public static readonly CatalogSnapshot Empty = new(DateTimeOffset.MinValue,
        new Dictionary<string, IReadOnlyList<string>>());

    public bool IsStale(TimeSpan ttl) => DateTimeOffset.UtcNow - FetchedAt > ttl;

    public int TotalModels => ModelsByProviderId.Values.Sum(v => v.Count);
}

/// <summary>
/// Resolves a model name to the provider that serves it. Downstream clients send a bare
/// model id and never learn which upstream it landed on, so the union catalog is the
/// whole contract: one /v1/models, one local key, N providers behind it.
/// </summary>
public sealed class ProviderRouter
{
    private static readonly string[] GeminiGenerationMethods = ["generateContent", "streamGenerateContent"];
    private readonly ProviderSettings[] _enabled;
    private readonly IReadOnlyDictionary<string, ProviderSettings> _apiKeyByModel;
    private readonly IReadOnlyList<(string Model, ProviderSettings Provider)> _candidates;
    private readonly IReadOnlyList<(string Model, ProviderSettings Provider)> _accountBareModels;
    private readonly Dictionary<string, (string Model, ProviderSettings Provider)> _accountAliases;
    private readonly HashSet<string> _ambiguousAccountModels;
    private readonly IReadOnlyList<RouterRule> _rules;
    private readonly Dictionary<string, RouterRule> _rulesByName;
    private readonly HashSet<string> _reservedNames;
    private readonly HashSet<string> _disabledNames;
    private readonly HashSet<string> _unhealthy;

    public ProviderRouter(IEnumerable<ProviderSettings> providers, CatalogSnapshot catalog)
        : this(providers, catalog, null, null) { }

    public ProviderRouter(IEnumerable<ProviderSettings> providers, CatalogSnapshot catalog, IReadOnlyList<RouterRule>? rules)
        : this(providers, catalog, rules, null) { }

    public ProviderRouter(IEnumerable<ProviderSettings> providers, CatalogSnapshot catalog,
        IReadOnlyList<RouterRule>? rules, IEnumerable<string>? unhealthyProviderIds)
    {
        _enabled = providers.Where(p => p.Enabled).OrderBy(p => p.Priority).ToArray();
        _unhealthy = new HashSet<string>(unhealthyProviderIds ?? [], StringComparer.Ordinal);

        var all = (rules ?? []).Where(r => !string.IsNullOrWhiteSpace(r.Name)).ToArray();
        _rules = all.Where(r => r.IsUsable).ToArray();

        _rulesByName = new Dictionary<string, RouterRule>(StringComparer.OrdinalIgnoreCase);
        foreach (var rule in _rules)
            if (!_rulesByName.ContainsKey(rule.Key)) _rulesByName[rule.Key] = rule;

        // Every configured name is reserved, including a disabled one. The name belongs to
        // the router namespace, so it must never fall through to the alias path and be
        // forwarded upstream as a model id - no gateway has heard of "free".
        _reservedNames = new HashSet<string>(all.Select(r => r.Key), StringComparer.OrdinalIgnoreCase);
        _disabledNames = new HashSet<string>(
            all.Where(r => !r.IsUsable).Select(r => r.Key), StringComparer.OrdinalIgnoreCase);

        var apiKeyByModel = new Dictionary<string, ProviderSettings>(StringComparer.OrdinalIgnoreCase);
        var candidates = new List<(string Model, ProviderSettings Provider)>();
        foreach (var provider in _enabled)
        {
            IReadOnlyList<string>? models;
            if (provider.RequiresExactModelId)
            {
                // OAuth and CLI accounts own the catalog fetched for that profile.
                // Never widen it with another profile's cached ids or guessed aliases.
                models = provider.ImportedModels;
            }
            else if (!catalog.ModelsByProviderId.TryGetValue(provider.Id, out models))
            {
                continue;
            }
            if (models is null) continue;
            foreach (var model in models)
            {
                // A model the operator switched off is neither listed nor routable.
                if (!provider.ServesModel(model)) continue;
                candidates.Add((model, provider));
                // First writer wins for API-key profiles, and _enabled is already priority-ordered.
                if (!provider.RequiresExactModelId && !apiKeyByModel.ContainsKey(model))
                    apiKeyByModel[model] = provider;
            }
        }
        _apiKeyByModel = apiKeyByModel;
        _candidates = candidates;

        var accountCandidates = candidates.Where(c => c.Provider.RequiresExactModelId)
            .GroupBy(c => (c.Provider.Id, c.Model))
            .Select(group => group.First())
            .ToArray();
        var accountGroups = accountCandidates
            .GroupBy(c => c.Model, StringComparer.Ordinal)
            .ToArray();
        var aliases = new Dictionary<string, (string Model, ProviderSettings Provider)>(StringComparer.Ordinal);
        var bareModels = new List<(string Model, ProviderSettings Provider)>();
        var ambiguousModels = new HashSet<string>(StringComparer.Ordinal);
        var needsAlias = new List<(string Model, ProviderSettings Provider)>();
        foreach (var group in accountGroups)
        {
            var profiles = group.ToArray();
            var hasApiKeyOwner = candidates.Any(c => !c.Provider.RequiresExactModelId &&
                string.Equals(c.Model, group.Key, StringComparison.Ordinal));
            var conflictsWithRouterName = _reservedNames.Contains(group.Key);
            if (profiles.Length > 1 || hasApiKeyOwner || conflictsWithRouterName)
            {
                if (profiles.Length > 1 && !hasApiKeyOwner)
                    ambiguousModels.Add(group.Key);
                needsAlias.AddRange(profiles);
            }
            else
            {
                bareModels.Add((profiles[0].Model, profiles[0].Provider));
            }
        }

        // Exact imported account ids take precedence over generated aliases. This matters
        // when an upstream model happens to be named "account:<profile>/<model>". Reserve
        // every raw id (including ids that themselves need an alias), API-key ids, and all
        // configured router names before generating qualified names. Then resolve any
        // remaining alias-to-alias collision in a stable provider/model order.
        var apiKeyModelNames = candidates
            .Where(c => !c.Provider.RequiresExactModelId)
            .Select(c => c.Model)
            .ToArray();
        var occupiedNames = new HashSet<string>(apiKeyModelNames, StringComparer.Ordinal);
        // API-key routing is case-insensitive. Keep all raw spellings above for exact
        // namespace allocation and also block case-only collisions, since aliases are
        // checked before the legacy API-key resolver.
        var occupiedApiKeyNames = new HashSet<string>(apiKeyModelNames, StringComparer.OrdinalIgnoreCase);
        occupiedNames.UnionWith(accountCandidates.Select(c => c.Model));
        foreach (var candidate in needsAlias
                     .OrderBy(c => c.Provider.Id, StringComparer.Ordinal)
                     .ThenBy(c => c.Model, StringComparer.Ordinal))
        {
            var preferred = AccountAlias(candidate.Provider.Id, candidate.Model);
            var alias = preferred;
            var suffix = 2;
            while (_reservedNames.Contains(alias) || occupiedApiKeyNames.Contains(alias) || !occupiedNames.Add(alias))
                alias = $"{preferred}~{suffix++}";
            aliases.Add(alias, (candidate.Model, candidate.Provider));
        }

        _accountAliases = aliases;
        _accountBareModels = bareModels;
        _ambiguousAccountModels = ambiguousModels;
    }

    public IReadOnlyList<ProviderSettings> Providers => _enabled;

    /// <summary>Router names a client can ask for, whether or not each can resolve today.</summary>
    public IReadOnlyList<string> RouterNames => _rules.Select(r => r.Key).ToArray();

    /// <summary>What a rule resolves to right now, or null when nothing in its pool qualifies.</summary>
    public ProviderRoute? ResolveRule(string? name, RouterEngine engine, string sessionKey, string? turnKey = null, TimeSpan? cacheLifetime = null, long? promptTokens = null, bool needsTools = false)
    {
        if (string.IsNullOrWhiteSpace(name) || !_rulesByName.TryGetValue(name.Trim(), out var rule)) return null;

        // A provider that failed its last probe is not available, so its models step aside
        // for the ones that are.
        var healthy = _candidates.Where(c => _unhealthy.Count == 0 || !_unhealthy.Contains(c.Provider.Id)).ToArray();
        var selected = RouterRuleSelector.Select(rule, healthy, engine, sessionKey, turnKey, cacheLifetime, promptTokens, needsTools);
        return selected is null ? null : new ProviderRoute(selected.Value.Provider, selected.Value.Model, rule.Key);
    }

    /// <summary>
    /// One line for the x-relay-decision header: router, strategy, the pick, whether the
    /// conversation's cache was warm, and every pool candidate passed over with its reason.
    /// Null when the model is not a router name.
    /// </summary>
    public string? ExplainRule(string? name, ProviderRoute chosen, RouterEngine engine, long? promptTokens, bool warm, int attempt = 1, bool needsTools = false)
    {
        if (string.IsNullOrWhiteSpace(name) || !_rulesByName.TryGetValue(name.Trim(), out var rule)) return null;
        var healthy = _candidates.Where(c => _unhealthy.Count == 0 || !_unhealthy.Contains(c.Provider.Id)).ToArray();
        var skipped = RouterRuleSelector.Skipped(rule, healthy, engine, promptTokens, needsTools)
            .Where(s => !s.Candidate.Equals($"{chosen.Provider.Name}/{chosen.Model}", StringComparison.OrdinalIgnoreCase))
            .Select(s => $"{s.Candidate} ({s.Reason})");
        var parts = new List<string>
        {
            $"router={rule.Key}",
            $"strategy={RouterStrategies.Canonical(rule.Strategy)}",
            $"chose={chosen.Provider.Name}/{chosen.Model}",
            $"cache={(warm ? "warm" : "cold")}"
        };
        if (attempt > 1) parts.Add($"attempt={attempt}");
        if (promptTokens is { } tokens) parts.Add($"prompt~{tokens}");
        var skippedText = string.Join(", ", skipped);
        if (skippedText.Length > 0) parts.Add($"skipped={skippedText}");
        return HeaderSafe(string.Join("; ", parts));
    }

    /// <summary>Printable ASCII only, at most 1,000 characters, so the header is always valid.</summary>
    internal static string HeaderSafe(string text)
    {
        var chars = text.Select(ch => ch is >= ' ' and <= '~' ? ch : '?').ToArray();
        var safe = new string(chars);
        return safe.Length <= 1000 ? safe : safe[..997] + "...";
    }

    /// <summary>The router rule a model name refers to, or null when it is not a router.</summary>
    public RouterRule? RuleFor(string? name) =>
        !string.IsNullOrWhiteSpace(name) && _rulesByName.TryGetValue(name.Trim(), out var rule) ? rule : null;

    public int ModelCount
    {
        get
        {
            var models = _apiKeyByModel.Keys.ToList();
            foreach (var candidate in _candidates.Where(c => c.Provider.RequiresExactModelId))
                if (!models.Contains(candidate.Model, StringComparer.Ordinal)) models.Add(candidate.Model);
            return models.Count;
        }
    }

    /// <summary>Distinct providers actually contributing at least one model, which is not
    /// the same as the enabled count - a provider can be up and offer nothing.</summary>
    public int ServingProviderCount =>
        _candidates.Select(c => c.Provider.Id).Distinct(StringComparer.Ordinal).Count();

    /// <summary>
    /// A router name resolves first. API-key profiles retain the legacy exact, prefix,
    /// and sole-provider alias handling; account profiles require their imported exact id.
    /// </summary>
    public bool TryResolve(string? model, out ProviderRoute route) =>
        TryResolve(model, out route, null, "unassigned");

    public bool TryResolve(string? model, out ProviderRoute route, RouterEngine? engine, string sessionKey, string? turnKey = null, TimeSpan? cacheLifetime = null, long? promptTokens = null, bool needsTools = false)
    {
        // A router rule outranks a real model of the same name. The operator named it
        // deliberately, and a coincidental collision with an upstream id is not something
        // they could have predicted; the UI shows the precedence.
        //
        // A configured router name that resolves to nothing stops here rather than falling
        // through: forwarding "free" upstream as if it were a model id is what the
        // sole-provider fallback would otherwise do, and no gateway has heard of it.
        if (IsReservedRouterName(model))
        {
            var resolved = ResolveRule(model, engine ?? new RouterEngine(), sessionKey, turnKey, cacheLifetime, promptTokens, needsTools);
            if (resolved is not null)
            {
                route = resolved;
                return true;
            }
            route = null!;
            return false;
        }

        if (!string.IsNullOrWhiteSpace(model))
        {
            if (_accountAliases.TryGetValue(model, out var accountAlias))
            {
                route = new ProviderRoute(accountAlias.Provider, accountAlias.Model,
                    ViaAccountAlias: model);
                return true;
            }

            // Imported account ids are ordinal and case-sensitive. Bare ids are only
            // safe where one account owns that exact spelling and no API-key profile
            // already owns the bare model name.
            var accountExact = _accountBareModels.FirstOrDefault(c =>
                string.Equals(c.Model, model, StringComparison.Ordinal));
            // Preserve the existing API-key duplicate winner for an exact bare id.
            if (_apiKeyByModel.TryGetValue(model, out var exactApiKey) &&
                _candidates.Any(c => !c.Provider.RequiresExactModelId &&
                    string.Equals(c.Model, model, StringComparison.Ordinal)))
            {
                route = new ProviderRoute(exactApiKey, model);
                return true;
            }

            if (accountExact.Model is not null)
            {
                route = new ProviderRoute(accountExact.Provider, accountExact.Model);
                return true;
            }

            // Duplicate account ids deliberately have no bare route. Do not let a later
            // case-insensitive or prefix path turn that ambiguity into a priority guess.
            if (_ambiguousAccountModels.Contains(model))
            {
                route = null!;
                return false;
            }

            if (_apiKeyByModel.TryGetValue(model, out var exact))
            {
                route = new ProviderRoute(exact, model);
                return true;
            }

            // If the requested id matches the regex for a dated model (e.g., claude-sonnet-4-5-20251001
            // was requested but gateways list the base version), try exact match on the base name.
            if (System.Text.RegularExpressions.Regex.IsMatch(model, @"^(.+)-\d{8}$"))
            {
                var baseModel = model[..model.LastIndexOf('-')];
                if (_apiKeyByModel.TryGetValue(baseModel, out var baseExact))
                {
                    route = new ProviderRoute(baseExact, baseModel);
                    return true;
                }
            }

            var prefix = _candidates
                .Where(c => !c.Provider.RequiresExactModelId)
                .Where(c => c.Model.Equals(model, StringComparison.OrdinalIgnoreCase) ||
                           c.Model.StartsWith(model + "-", StringComparison.OrdinalIgnoreCase))
                .OrderBy(c => c.Provider.Priority)
                .ThenBy(c => c.Model.Length)
                .FirstOrDefault();
            if (prefix.Model is not null)
            {
                route = new ProviderRoute(prefix.Provider, prefix.Model);
                return true;
            }
        }

        // A sole provider can absorb an alias, but not a model it has been told not to
        // serve - that would quietly undo the switch the operator just flipped.
        if (_enabled.Length == 1 && !_enabled[0].RequiresExactModelId &&
            !string.IsNullOrWhiteSpace(model) && _enabled[0].ServesModel(model))
        {
            route = new ProviderRoute(_enabled[0], model ?? string.Empty);
            return true;
        }

        route = null!;
        return false;
    }

    public IReadOnlyList<ProviderRoute> AllRoutes => _candidates
        .Select(c => new ProviderRoute(c.Provider, c.Model))
        .ToArray();

    /// <summary>Model/provider pairs a client could ask for, best provider first.</summary>
    public IReadOnlyList<(string Model, ProviderSettings Provider)> RoutesForPicker =>
        _candidates.OrderBy(c => c.Provider.Priority).ThenBy(c => c.Model, StringComparer.OrdinalIgnoreCase).ToArray();

    /// <summary>True when a rule by this name is configured, resolvable or not.</summary>
    public bool HasRule(string? name) =>
        !string.IsNullOrWhiteSpace(name) && _rulesByName.ContainsKey(name.Trim());

    /// <summary>True when the name belongs to the router namespace, even if switched off.</summary>
    public bool IsReservedRouterName(string? name) =>
        !string.IsNullOrWhiteSpace(name) && _reservedNames.Contains(name.Trim());

    /// <summary>True when a rule by this name exists but is switched off.</summary>
    public bool IsDisabledRouter(string? name) =>
        !string.IsNullOrWhiteSpace(name) && _disabledNames.Contains(name.Trim());

    /// <summary>
    /// The names a client may ask for: every served model, plus every configured router.
    ///
    /// A router is listed even when it cannot resolve right now. It is the contract the
    /// operator set up, and dropping it from the list would make an agent that had
    /// selected it look broken for reasons that change minute to minute; asking for it
    /// gets a message that says exactly what is wrong instead.
    /// </summary>
    public IReadOnlyList<string> AdvertisedModelNames
    {
        get
        {
            // API-key names retain their case-insensitive legacy union. Account names
            // are ordinal identifiers, so case-distinct ids are appended independently.
            var names = new List<string>(_apiKeyByModel.Keys);
            foreach (var accountModel in _accountBareModels)
                if (!names.Contains(accountModel.Model, StringComparer.Ordinal)) names.Add(accountModel.Model);
            foreach (var alias in _accountAliases.Keys)
                if (!names.Contains(alias, StringComparer.Ordinal)) names.Add(alias);
            foreach (var rule in _rules)
                if (!names.Contains(rule.Key, StringComparer.OrdinalIgnoreCase)) names.Add(rule.Key);
            names.Sort((left, right) =>
            {
                var insensitive = StringComparer.OrdinalIgnoreCase.Compare(left, right);
                return insensitive != 0 ? insensitive : StringComparer.Ordinal.Compare(left, right);
            });
            return names;
        }
    }

    /// <summary>OpenAI-shaped list, with owned_by carrying the provider name.</summary>
    public object UnionModelsDocument()
    {
        var owners = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var candidate in _candidates.Where(c => !c.Provider.RequiresExactModelId))
            if (!owners.ContainsKey(candidate.Model)) owners[candidate.Model] = candidate.Provider.Name;

        var entries = owners.Select(pair => (Model: pair.Key, Owner: pair.Value)).ToList();
        foreach (var accountModel in _accountBareModels)
            if (!entries.Any(entry => string.Equals(entry.Model, accountModel.Model, StringComparison.Ordinal)))
                entries.Add((accountModel.Model, accountModel.Provider.Name));
        foreach (var (alias, accountModel) in _accountAliases)
            entries.Add((alias, accountModel.Provider.Name));

        foreach (var rule in _rules)
            if (!entries.Any(entry => string.Equals(entry.Model, rule.Key, StringComparison.OrdinalIgnoreCase)))
                entries.Add((rule.Key, "Router"));

        return new
        {
            @object = "list",
            data = entries
                .OrderBy(pair => pair.Model, StringComparer.OrdinalIgnoreCase)
                .ThenBy(pair => pair.Model, StringComparer.Ordinal)
                .Select(pair => new { id = pair.Model, @object = "model", created = 0, owned_by = pair.Owner })
                .ToArray()
        };
    }

    /// <summary>Gemini REST-shaped catalog for native clients using /v1beta/models.</summary>
    public object GeminiModelsDocument()
    {
        var names = new List<string>();
        foreach (var (model, provider) in _apiKeyByModel)
            if (provider.Kind.Equals(ProviderKinds.Gemini, StringComparison.OrdinalIgnoreCase)) names.Add(model);
        foreach (var (model, provider) in _accountBareModels)
            if (provider.Kind.Equals(ProviderKinds.Gemini, StringComparison.OrdinalIgnoreCase) &&
                !names.Contains(model, StringComparer.Ordinal)) names.Add(model);
        foreach (var (alias, accountModel) in _accountAliases)
            if (accountModel.Provider.Kind.Equals(ProviderKinds.Gemini, StringComparison.OrdinalIgnoreCase) &&
                !names.Contains(alias, StringComparer.Ordinal)) names.Add(alias);

        return new
        {
            models = names
                .OrderBy(model => model, StringComparer.OrdinalIgnoreCase)
                .ThenBy(model => model, StringComparer.Ordinal)
                .Select(model => new
                {
                    name = "models/" + model,
                    displayName = model,
                    supportedGenerationMethods = GeminiGenerationMethods
                })
                .ToArray(),
            nextPageToken = (string?)null
        };
    }

    private static string AccountAlias(string profileId, string model) => $"account:{profileId}/{model}";
}

/// <summary>
/// Disk cache of the per-provider model lists. Plain JSON with no secrets, so the app
/// opens with a usable catalog even when every upstream is unreachable.
/// </summary>
public sealed class CatalogCache
{
    private readonly string _path;
    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    public CatalogCache(string? path = null)
    {
        _path = path ?? RelayPaths.File("catalog.json");
    }

    public string Path => _path;

    public CatalogSnapshot Load()
    {
        try
        {
            if (!File.Exists(_path)) return CatalogSnapshot.Empty;
            var snapshot = JsonSerializer.Deserialize<CatalogSnapshot>(File.ReadAllText(_path), Options);
            return snapshot ?? CatalogSnapshot.Empty;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException) { return CatalogSnapshot.Empty; }
    }

    public void Save(CatalogSnapshot snapshot)
    {
        try
        {
            var directory = System.IO.Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            File.WriteAllText(_path, JsonSerializer.Serialize(snapshot, Options));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    public void Delete()
    {
        try { if (File.Exists(_path)) File.Delete(_path); }
        catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
