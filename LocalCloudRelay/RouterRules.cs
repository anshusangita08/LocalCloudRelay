using System.Collections.Concurrent;

namespace LocalCloudRelay;

public static class RouterStrategies
{
    /// <summary>Stay on one model while it keeps working, for as long as the session lasts.</summary>
    public const string Sticky = "sticky";

    /// <summary>Rotate providers one user turn at a time.</summary>
    public const string RoundRobin = "round-robin";

    /// <summary>Free models first, then the cheapest of the rest.</summary>
    public const string FreeFirst = "free-first";

    /// <summary>Dearest first, as a stand-in for the most capable tier.</summary>
    public const string Premium = "premium";

    /// <summary>Models that advertise themselves as coding models first.</summary>
    public const string Coding = "coding";

    public static readonly IReadOnlyList<string> All = [Sticky, RoundRobin, FreeFirst, Premium, Coding];

    private static readonly Dictionary<string, string> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["cheapest-first"] = FreeFirst,
        ["quality-first"] = Premium
    };

    public static bool IsKnown(string? strategy) =>
        strategy is not null && (All.Contains(strategy, StringComparer.OrdinalIgnoreCase) || Aliases.ContainsKey(strategy));

    /// <summary>Canonical name, so a config written before a rename still resolves.</summary>
    public static string Canonical(string? strategy) => strategy switch
    {
        null => string.Empty,
        _ when Aliases.TryGetValue(strategy, out var mapped) => mapped,
        _ => strategy.ToLowerInvariant()
    };

    public static string Describe(string strategy) => Canonical(strategy) switch
    {
        Sticky => "Sticky - one model per session",
        RoundRobin => "Round robin - rotate providers, keep warm caches",
        FreeFirst => "Free first - free, then cheapest",
        Premium => "Premium - dearest first",
        Coding => "Coding - coding models first",
        _ => strategy
    };

    /// <summary>Short name shown in the strategy picker.</summary>
    public static string Title(string strategy) => Canonical(strategy) switch
    {
        Sticky => "Sticky - one model per conversation",
        RoundRobin => "Round robin - spread turns across providers",
        FreeFirst => "Free first - cheapest that works",
        Premium => "Premium - most capable that works",
        Coding => "Coding - coding-named models first",
        _ => strategy
    };

    /// <summary>
    /// What the strategy does and when to pick it, shown under the picker. Every claim
    /// here is what <see cref="RouterRuleSelector"/> actually does.
    /// </summary>
    public static string Explain(string strategy) => Canonical(strategy) switch
    {
        Sticky =>
            "Each new conversation gets one model from the pool, taking turns across the pool, and keeps it " +
            "until the conversation ends. It moves only if that model fails.\n" +
            "Use for coding agents such as Claude Code or OpenCode: staying on one model keeps its prompt " +
            "cache and context, so long sessions are faster and cheaper. The best default.",
        RoundRobin =>
            "Each conversation goes to the next provider in the pool and stays there while its prompt cache " +
            "is warm. It moves to the next provider after 5 minutes idle (the cache has expired anyway), " +
            "after 20 minutes on one signed-in plan account, or as soon as that provider fails, rate-limits, or reports " +
            "less than 10% of its limit left. With several models on one provider, it rotates those too.\n" +
            "Use to share load across several subscriptions or accounts, so no single one hits its rate " +
            "limit, without paying for a cold cache on every turn.",
        FreeFirst =>
            "Uses a free model when one is ticked, otherwise the cheapest priced one. If it fails, the next " +
            "cheapest answers, and the conversation stays there while its cache is warm.\n" +
            "Use for background work where cost matters more than quality: titles, summaries, commit " +
            "messages, quick questions.",
        Premium =>
            "Uses the most expensive ticked model, taking price as the stand-in for the most capable, and " +
            "drops to the next one down if it fails. A conversation stays on the model that answered while " +
            "its cache is warm, and returns to the top choice after 5 minutes idle.\n" +
            "Use when you want the strongest answer and a fallback behind it, for example a top model with " +
            "a cheaper one ticked as backup.",
        Coding =>
            "Prefers models whose id says code, coder or codex, most expensive first. If none of those are " +
            "ticked it behaves exactly like Premium.\n" +
            "Use only when the pool holds dedicated coding models such as Qwen Coder. With general " +
            "models (GPT, Claude, Gemini) pick Premium or Sticky instead.",
        _ => string.Empty
    };
}

/// <summary>
/// A name a client can ask for, backed by a pool of models the operator chose.
///
/// The point is that the agent asks for one name and the relay decides which of *your*
/// models answers, so changing the pool or the order is a change here rather than in
/// every agent's config. Nothing is picked from outside the pool: a router can only ever
/// resolve to a model that was explicitly selected for it.
/// </summary>
public sealed record RouterRule(string Name, string Strategy, IReadOnlyList<string> Models, bool Enabled,
    // Most this router may spend per local day, in USD; null means no limit. Plan-account
    // traffic costs $0 and never counts toward it.
    decimal? DailyBudgetUsd = null)
{
    public const int MaxNameLength = 64;

    public static RouterRule Create(string name, string strategy, IEnumerable<string>? models = null, bool enabled = true) =>
        new(name, strategy, [.. (models ?? []).Where(m => !string.IsNullOrWhiteSpace(m))], enabled);

    /// <summary>Trimmed, because it is matched against what a client sends.</summary>
    public string Key => Name.Trim();

    public IReadOnlyList<string> Pool => Models ?? [];

    public bool IsUsable =>
        Enabled &&
        !string.IsNullOrWhiteSpace(Name) &&
        RouterStrategies.IsKnown(Strategy) &&
        Pool.Count > 0;
}

/// <summary>
/// What the strategies need to remember between requests, and what "available" means.
///
/// Availability is learned, not assumed: a provider that failed its last probe contributes
/// nothing, and a model whose request just failed is put in a short cooldown so the next
/// request moves on instead of retrying something that is not answering.
/// </summary>
public sealed class RouterEngine
{
    /// <summary>How long a model stays out of rotation after a request to it failed.</summary>
    public static readonly TimeSpan Cooldown = TimeSpan.FromSeconds(60);

    /// <summary>
    /// How long a provider keeps a conversation's prompt cache after its last request.
    /// Anthropic's default cache lives five minutes and is refreshed on every hit; OpenAI's
    /// lasts at least as long. Inside this window, moving a conversation throws the cache away.
    /// </summary>
    public static readonly TimeSpan CacheWarmth = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Longest a round-robin conversation stays on one provider, warm cache or not. Plan
    /// accounts (Claude Code, Antigravity, ChatGPT) publish no rate-limit headers, so time
    /// is the only way to keep one long session from using up a single account. One cold
    /// cache every 20 minutes is the price.
    /// </summary>
    public static readonly TimeSpan MaxHold = TimeSpan.FromMinutes(20);

    private readonly ConcurrentDictionary<string, long> _rotation = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> _sticky = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _stickyAt = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, TimeSpan> _warmFor = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, (int Streak, DateTimeOffset At)> _failures = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _unusable = new(StringComparer.Ordinal);
    private readonly object _evictLock = new();
    private readonly ConcurrentDictionary<string, DateTimeOffset> _cooling = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastUsed = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _heldSince = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _providerBusy = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, List<DateTimeOffset>> _requests = new(StringComparer.Ordinal);

    /// <summary>OpenCode Go counts its published request allowances over five hours.</summary>
    public static readonly TimeSpan AllowanceWindow = TimeSpan.FromHours(5);

    /// <summary>Share of an allowance kept in reserve: at 90% used the model steps aside.</summary>
    public const decimal AllowanceReserve = 0.10m;

    /// <summary>
    /// Requests a model may make per <see cref="AllowanceWindow"/> on a provider, or null
    /// when no allowance applies. Defaults to OpenCode Go's published table.
    /// </summary>
    public Func<ProviderSettings, string, long?> AllowanceFor { get; init; } = GoAllowance;

    private static long? GoAllowance(ProviderSettings provider, string model) =>
        ModelPricingTable.PlanFor(provider.BaseUrl) == "g" &&
        LivePricing.Current?.GoFiveHourRequests(model) is { Unlimited: false, Requests: { } requests }
            ? requests
            : null;
    private readonly TimeProvider _time;

    public RouterEngine(TimeProvider? time = null) => _time = time ?? TimeProvider.System;

    public DateTimeOffset Now => _time.GetUtcNow();

    /// <summary>Longest a repeatedly failing model rests between attempts.</summary>
    public static readonly TimeSpan MaxCooldown = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Rests a model after a failure. The rest doubles with each failure in a row (60 s,
    /// 2 min, 4 min ... up to <see cref="MaxCooldown"/>) so a model that keeps failing is
    /// tried less and less often, and resets on its next success. A provider's own
    /// Retry-After wins when it gives one.
    /// </summary>
    public void MarkFailed(ProviderSettings provider, string model, TimeSpan? retryAfter = null)
    {
        var key = CoolKey(provider, model);
        var now = Now;
        var streak = _failures.AddOrUpdate(key, (1, now), (_, last) => (Math.Min(last.Streak + 1, 16), now)).Streak;
        var backoff = retryAfter is { } asked && asked > TimeSpan.Zero
            ? (asked > MaxCooldown ? MaxCooldown : asked)
            : TimeSpan.FromTicks(Math.Min(MaxCooldown.Ticks, Cooldown.Ticks << (streak - 1)));
        _cooling[key] = now + backoff;
    }

    public void MarkSucceeded(ProviderSettings provider, string model)
    {
        var key = CoolKey(provider, model);
        _cooling.TryRemove(key, out _);
        _failures.TryRemove(key, out _);
    }

    /// <summary>
    /// Takes a whole provider out until the operator acts: its sign-in or key was refused
    /// (401/403) or it has no credit left (402). Retrying on a timer cannot fix either, so
    /// it stays out until <see cref="ClearUnusable"/> runs on Refresh all or Edit.
    /// </summary>
    public void MarkUnusable(ProviderSettings provider, string reason, string? model = null) =>
        _unusable[model is null ? provider.Id : CoolKey(provider, model)] = reason;

    /// <summary>Forgets every "until the operator acts" verdict; called on Refresh all and Edit.</summary>
    public void ClearUnusable(string? providerId = null)
    {
        if (providerId is null) _unusable.Clear();
        else foreach (var key in _unusable.Keys.Where(k => k == providerId || k.StartsWith(providerId + "|", StringComparison.Ordinal)).ToArray())
            _unusable.TryRemove(key, out _);
    }

    /// <summary>
    /// The one availability check every strategy uses. Null means the model can take
    /// traffic; otherwise the reason it is out of rotation for now:
    /// its last request failed, its provider rate-limited or is close to a rate limit,
    /// or it has used 90% of a published request allowance.
    /// </summary>
    public string? Unavailable(ProviderSettings provider, string model)
    {
        var now = Now;
        if (_unusable.TryGetValue(provider.Id, out var reason) || _unusable.TryGetValue(CoolKey(provider, model), out reason))
            return $"{provider.Name}: {reason} (press Refresh all or Edit once fixed)";
        if (_providerBusy.TryGetValue(provider.Id, out var busy) && busy > now)
            return $"{provider.Name} is rate-limited or near a limit until {busy.ToLocalTime():HH:mm}";
        if (_cooling.TryGetValue(CoolKey(provider, model), out var until) && until > now)
            return $"{model} failed and rests until {until.ToLocalTime():HH:mm:ss}";
        if (AllowanceFor(provider, model) is { } allowance && allowance > 0)
        {
            var used = RequestsInWindow(provider, model, now);
            var ceiling = Math.Max(1, (long)Math.Floor(allowance * (1 - AllowanceReserve)));
            if (used >= ceiling)
                return $"{model} used {used} of {allowance} requests in 5 hours";
        }
        return null;
    }

    public bool IsCooling(ProviderSettings provider, string model) => Unavailable(provider, model) is not null;

    /// <summary>
    /// Share of a published request allowance still unused in the current window, from 1
    /// (untouched) to 0 (used up). Models without an allowance report 1: nothing limits them.
    /// </summary>
    public double Headroom(ProviderSettings provider, string model)
    {
        if (AllowanceFor(provider, model) is not { } allowance || allowance <= 0) return 1.0;
        var used = RequestsInWindow(provider, model, Now);
        return Math.Clamp(1.0 - (double)used / allowance, 0.0, 1.0);
    }


    /// <summary>Counts a request against any allowance the model has.</summary>
    public void RecordRequest(ProviderSettings provider, string model)
    {
        if (AllowanceFor(provider, model) is null) return;
        var list = _requests.GetOrAdd(CoolKey(provider, model), _ => []);
        lock (list) list.Add(Now);
    }

    public int RequestsInWindow(ProviderSettings provider, string model, DateTimeOffset now)
    {
        if (!_requests.TryGetValue(CoolKey(provider, model), out var list)) return 0;
        lock (list)
        {
            list.RemoveAll(at => now - at >= AllowanceWindow);
            return list.Count;
        }
    }

    /// <summary>
    /// Keeps every model on a provider out of rotation for a while: it answered 429, or its
    /// headers say a rate limit is nearly used up. Limits are per account, not per model.
    /// </summary>
    public void MarkProviderBusy(ProviderSettings provider, TimeSpan duration)
    {
        var until = Now + duration;
        _providerBusy.AddOrUpdate(provider.Id, until, (_, current) => current > until ? current : until);
    }

    /// <summary>When the conversation was placed on its current provider.</summary>
    public DateTimeOffset? HeldSince(string key) => _heldSince.TryGetValue(key, out var since) ? since : null;

    public void SetHeldSince(string key) => _heldSince[key] = Now;

    /// <summary>Next position in the rotation for a rule, wrapping at the pool size.</summary>
    public int NextPosition(string ruleName, int poolSize) =>
        poolSize <= 0 ? 0 : (int)(_rotation.AddOrUpdate(ruleName.ToLowerInvariant(), 0, (_, current) => current + 1) % poolSize);

    /// <summary>Most remembered conversations kept before the least recently used are dropped.</summary>
    public int StickyCapacity { get; init; } = 10_000;

    public string? StickyFor(string sessionKey)
    {
        if (!_sticky.TryGetValue(sessionKey, out var model)) return null;
        _stickyAt[sessionKey] = Now;
        return model;
    }

    public void SetSticky(string sessionKey, string model)
    {
        _sticky[sessionKey] = model;
        _stickyAt[sessionKey] = Now;
        if (_sticky.Count > StickyCapacity) Evict();
    }

    /// <summary>
    /// Drops what no live conversation needs. Clearing everything at the cap sent every
    /// active chat to a cold cache at once; this keeps the recently used ones.
    /// First anything idle past <see cref="MaxHold"/>, whose cache is long gone, then the
    /// least recently used until the map is back under capacity.
    /// </summary>
    private void Evict() => Prune();

    /// <summary>
    /// Drops everything that has gone stale: idle conversations, spent cooldowns and
    /// rate-limit rests, and allowance timestamps outside the window. Runs at capacity and
    /// on every routing-memory save, so an all-day session never accumulates dead entries.
    /// </summary>
    public void Prune()
    {
        lock (_evictLock)
        {
            var now = Now;
            foreach (var (key, until) in _cooling)
                if (until <= now) _cooling.TryRemove(key, out _);
            // A failure streak older than the longest rest is history, not a pattern.
            foreach (var key in _failures.Keys)
                if (_failures.TryGetValue(key, out var failure) && now - failure.At >= MaxCooldown + MaxCooldown) _failures.TryRemove(key, out _);
            foreach (var (key, until) in _providerBusy)
                if (until <= now) _providerBusy.TryRemove(key, out _);
            foreach (var (key, at) in _stickyAt)
                if (now - at >= IdleLimit(key)) Forget(key);

            var excess = _sticky.Count - StickyCapacity;
            if (excess > 0)
                foreach (var key in _stickyAt.OrderBy(e => e.Value).Take(excess).Select(e => e.Key).ToArray())
                    Forget(key);

            foreach (var (key, at) in _lastUsed)
                if (now - at >= IdleLimit(key) && !_sticky.ContainsKey(key)) _lastUsed.TryRemove(key, out _);
            foreach (var (key, _) in _warmFor)
                if (!_lastUsed.ContainsKey(key)) _warmFor.TryRemove(key, out _);
            foreach (var (key, _) in _heldSince)
                if (!_sticky.ContainsKey(key)) _heldSince.TryRemove(key, out _);
            foreach (var (key, list) in _requests)
            {
                bool empty;
                lock (list)
                {
                    list.RemoveAll(at => now - at >= AllowanceWindow);
                    empty = list.Count == 0;
                }
                if (empty) _requests.TryRemove(key, out _);
            }
        }
    }

    /// <summary>
    /// What the engine has learned that is still worth something after a restart: which
    /// provider and model each conversation is on (and how warm its cache is), rotation
    /// positions, rate-limit rests, and allowance counts. Failure cooldowns last a minute
    /// and are left out.
    /// </summary>
    public RouterMemory Export()
    {
        var conversations = _sticky.Select(entry => new RouterMemory.Conversation(
                entry.Key, entry.Value,
                _stickyAt.TryGetValue(entry.Key, out var at) ? at : Now,
                _lastUsed.TryGetValue(entry.Key, out var last) ? last : null,
                _heldSince.TryGetValue(entry.Key, out var held) ? held : null,
                _warmFor.TryGetValue(entry.Key, out var warm) ? warm : null))
            .ToArray();
        var requests = _requests.Select(entry =>
        {
            lock (entry.Value) return new RouterMemory.Allowance(entry.Key, entry.Value.ToArray());
        }).Where(a => a.At.Length > 0).ToArray();
        return new RouterMemory(Now, conversations,
            _rotation.ToDictionary(e => e.Key, e => e.Value, StringComparer.OrdinalIgnoreCase),
            _providerBusy.Where(e => e.Value > Now).ToDictionary(e => e.Key, e => e.Value, StringComparer.Ordinal),
            requests);
    }

    /// <summary>Restores <see cref="Export"/> output, dropping anything that has expired since.</summary>
    public void Import(RouterMemory memory)
    {
        var now = Now;
        foreach (var conversation in memory.Conversations ?? [])
        {
            if (string.IsNullOrEmpty(conversation.Key) || string.IsNullOrEmpty(conversation.Model)) continue;
            var limit = conversation.WarmFor is { } w && w > MaxHold ? w : MaxHold;
            if (now - conversation.StickyAt >= limit) continue;
            _sticky[conversation.Key] = conversation.Model;
            _stickyAt[conversation.Key] = conversation.StickyAt;
            if (conversation.LastUsed is { } last) _lastUsed[conversation.Key] = last;
            if (conversation.HeldSince is { } held) _heldSince[conversation.Key] = held;
            if (conversation.WarmFor is { } warm) _warmFor[conversation.Key] = warm;
        }
        foreach (var (rule, position) in memory.Rotation ?? new Dictionary<string, long>())
            _rotation[rule] = position;
        foreach (var (provider, until) in memory.ProviderBusy ?? new Dictionary<string, DateTimeOffset>())
            if (until > now) _providerBusy[provider] = until;
        foreach (var allowance in memory.Requests ?? [])
        {
            var recent = allowance.At.Where(at => now - at < AllowanceWindow).ToList();
            if (recent.Count > 0) _requests[allowance.Key] = recent;
        }
    }

    /// <summary>How long a conversation may sit idle before its memory is worth nothing.</summary>
    private TimeSpan IdleLimit(string key) =>
        _warmFor.TryGetValue(key, out var warm) && warm > MaxHold ? warm : MaxHold;

    private void Forget(string key)
    {
        _sticky.TryRemove(key, out _);
        _warmFor.TryRemove(key, out _);
        _stickyAt.TryRemove(key, out _);
        _lastUsed.TryRemove(key, out _);
        _heldSince.TryRemove(key, out _);
    }

    /// <summary>Diagnostics: how many entries each side table holds.</summary>
    internal (int Sticky, int LastUsed, int HeldSince, int Requests) TableSizes =>
        (_sticky.Count, _lastUsed.Count, _heldSince.Count, _requests.Count);

    internal (int Cooling, int ProviderBusy, int WarmFor) ShortLivedTableSizes =>
        (_cooling.Count, _providerBusy.Count, _warmFor.Count);

    /// <summary>
    /// True when the key was used within its cache lifetime; records this use.
    ///
    /// The lifetime is <see cref="CacheWarmth"/> unless a request in the conversation asked
    /// for longer (Anthropic's <c>ttl: "1h"</c>). The longest asked for is remembered,
    /// because that cache entry outlives the shorter requests that follow it.
    /// </summary>
    public bool TouchIsWarm(string key, TimeSpan? cacheLifetime = null)
    {
        var now = Now;
        var lifetime = cacheLifetime is { } asked && asked > CacheWarmth ? asked : CacheWarmth;
        lifetime = _warmFor.AddOrUpdate(key, lifetime, (_, current) => current > lifetime ? current : lifetime);
        var warm = _lastUsed.TryGetValue(key, out var last) && now - last < lifetime;
        _lastUsed[key] = now;
        return warm;
    }

    /// <summary>Testing and diagnostics: forget everything learned.</summary>
    public void Reset()
    {
        _rotation.Clear();
        _sticky.Clear();
        _stickyAt.Clear();
        _warmFor.Clear();
        _failures.Clear();
        _unusable.Clear();
        _cooling.Clear();
        _lastUsed.Clear();
        _heldSince.Clear();
        _providerBusy.Clear();
        _requests.Clear();
    }

    private static string CoolKey(ProviderSettings provider, string model) => provider.Id + "|" + model;
}

/// <summary>A serializable snapshot of <see cref="RouterEngine"/> state.</summary>
public sealed record RouterMemory(
    DateTimeOffset SavedAt,
    RouterMemory.Conversation[] Conversations,
    Dictionary<string, long> Rotation,
    Dictionary<string, DateTimeOffset> ProviderBusy,
    RouterMemory.Allowance[] Requests)
{
    public sealed record Conversation(string Key, string Model, DateTimeOffset StickyAt,
        DateTimeOffset? LastUsed, DateTimeOffset? HeldSince, TimeSpan? WarmFor);

    public sealed record Allowance(string Key, DateTimeOffset[] At);
}

/// <summary>
/// Keeps routing memory across restarts in a small JSON file next to the catalog, so a
/// relay restart does not move every live conversation to a new model and a cold cache.
/// Written atomically; a missing or unreadable file just means starting fresh.
/// </summary>
public sealed class RouterMemoryStore(string path)
{
    private static readonly System.Text.Json.JsonSerializerOptions Options = new() { WriteIndented = false };
    private readonly object _gate = new();

    /// <summary>
    /// Memory older than this on start is discarded and rebuilt from live traffic. An
    /// overnight gap means every cache is long expired, and the first chats of the day
    /// should be placed fresh rather than on yesterday's choices.
    /// </summary>
    public static readonly TimeSpan MaxAgeOnStart = TimeSpan.FromHours(4);

    /// <summary>The saved memory, or null when there is none, it is unreadable, or it is
    /// older than <see cref="MaxAgeOnStart"/> (the file is then deleted).</summary>
    public RouterMemory? Load(DateTimeOffset? now = null)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var memory = System.Text.Json.JsonSerializer.Deserialize<RouterMemory>(File.ReadAllText(path), Options);
            if (memory is null || (now ?? DateTimeOffset.UtcNow) - memory.SavedAt > MaxAgeOnStart)
            {
                File.Delete(path);
                return null;
            }
            return memory;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or NotSupportedException)
        {
            return null;
        }
    }

    public void Save(RouterMemory memory)
    {
        lock (_gate)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
                var temp = path + ".tmp";
                File.WriteAllText(temp, System.Text.Json.JsonSerializer.Serialize(memory, Options));
                File.Move(temp, path, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Routing memory is an optimisation; failing to save it must not fail a request.
            }
        }
    }
}

/// <summary>
/// Chooses which model in a rule's pool answers. Kept apart from the router so the
/// ordering rules can be tested against a fixed pool without a live catalog.
/// </summary>
public static class RouterRuleSelector
{
    public static (string Model, ProviderSettings Provider)? Select(
        RouterRule rule,
        IReadOnlyList<(string Model, ProviderSettings Provider)> candidates,
        RouterEngine engine,
        string sessionKey,
        string? turnKey = null,
        TimeSpan? cacheLifetime = null,
        long? promptTokens = null,
        bool needsTools = false)
    {
        if (!rule.IsUsable) return null;

        // Only the pool, and only models the operator has not switched off. Every provider
        // serving a selected model is a candidate: round robin balances across them, and
        // the other strategies can move a conversation to the same model elsewhere when its
        // provider fails instead of to a different model.
        var pool = rule.Pool.SelectMany(model => ResolveAll(model, candidates))
            .DistinctBy(c => c.Provider.Id + "\0" + c.Model, StringComparer.OrdinalIgnoreCase).ToArray();
        if (pool.Length == 0) return null;

        // A model whose published context window cannot hold the request would only
        // answer 400. Unknown windows stay in. If nothing fits, the pool is left as it
        // was: the estimate is rough, and the provider's own answer is better than none.
        var fitting = pool.Where(c => Fits(c, promptTokens)).ToArray();
        if (fitting.Length > 0) pool = fitting;

        // Coding agents send tools on every turn. A CLI account (Claude Code, Antigravity,
        // Gemini CLI) answers text only, so it is left out for such a request; if nothing
        // else is in the pool, it stays, and its own error says why.
        var toolCapable = pool.Where(c => !needsTools || !IsTextOnly(c.Provider)).ToArray();
        if (toolCapable.Length > 0) pool = toolCapable;

        var available = pool.Where(c => !engine.IsCooling(c.Provider, c.Model)).ToArray();

        // Everything in the pool is cooling: offer the least recently failed rather than
        // refusing outright, because a stale cooldown should not take a name offline.
        if (available.Length == 0) available = pool;

        return RouterStrategies.Canonical(rule.Strategy) switch
        {
            RouterStrategies.Sticky => Sticky(rule, available, engine, sessionKey),
            RouterStrategies.RoundRobin => RoundRobin(rule, available, engine, sessionKey, cacheLifetime),
            RouterStrategies.FreeFirst => WithCacheAffinity(rule, available, engine, sessionKey, cacheLifetime, () => FreeFirst(available, engine)),
            RouterStrategies.Premium => WithCacheAffinity(rule, available, engine, sessionKey, cacheLifetime, () => ByPrice(available, descending: true, engine)),
            RouterStrategies.Coding => WithCacheAffinity(rule, available, engine, sessionKey, cacheLifetime, () => CodingFirst(available, engine)),
            _ => available[0]
        };
    }

    private static (string Model, ProviderSettings Provider) RoundRobin(
        RouterRule rule,
        (string Model, ProviderSettings Provider)[] available,
        RouterEngine engine,
        string sessionKey,
        TimeSpan? cacheLifetime)
    {
        // Keyed on the conversation alone. A body with no user turn (a bare completion or
        // a probe) used to rotate on every request, starting a cold cache each time.
        // A conversation stays where its prompt cache is while the cache is warm, so a
        // long session is not charged a cold start on every turn. It moves to the next
        // provider once it has been idle past the cache lifetime, or when its model fails
        // (a cooling model is not in `available`). Load still spreads: across conversations,
        // and across the idle gaps inside one.
        var key = rule.Key + "|" + sessionKey;
        var warm = engine.TouchIsWarm(key, cacheLifetime);
        var remembered = engine.StickyFor(key);
        // Even a busy, warm conversation moves on after MaxHold so one account is not
        // carrying a whole long session.
        var match = remembered is null || !warm
            ? default
            : available.FirstOrDefault(c => RoundRobinIdentity(c).Equals(remembered, StringComparison.OrdinalIgnoreCase));
        // The hold limit protects plan accounts, which publish no rate-limit headers. A
        // pay-per-token provider reports its limits, so moving it only throws away a warm cache.
        var heldTooLong = match.Model is not null && IsPlanAccount(match.Provider) &&
            engine.HeldSince(key) is { } since && engine.Now - since >= RouterEngine.MaxHold;
        if (match.Model is not null && !heldTooLong) return match;

        var providers = available.GroupBy(c => c.Provider.Id, StringComparer.OrdinalIgnoreCase)
            .OrderBy(group => group.First().Provider.Priority)
            .ThenBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var provider = providers[engine.NextPosition(rule.Key, providers.Length)];
        var models = provider.ToArray();
        var chosen = models[engine.NextPosition(rule.Key + "|" + provider.Key, models.Length)];
        engine.SetSticky(key, RoundRobinIdentity(chosen));
        engine.SetHeldSince(key);
        return chosen;
    }

    /// <summary>
    /// Why each pool candidate the selector passed over was not chosen: out of rotation
    /// (with the engine's reason) or too small for the request. Uses the same checks as
    /// <see cref="Select"/>, so it explains the decision rather than re-deciding it.
    /// </summary>
    public static IReadOnlyList<(string Candidate, string Reason)> Skipped(
        RouterRule rule, IReadOnlyList<(string Model, ProviderSettings Provider)> candidates,
        RouterEngine engine, long? promptTokens, bool needsTools = false)
    {
        var skipped = new List<(string, string)>();
        foreach (var candidate in rule.Pool.SelectMany(model => ResolveAll(model, candidates))
                     .DistinctBy(c => c.Provider.Id + "\0" + c.Model, StringComparer.OrdinalIgnoreCase))
        {
            var name = $"{candidate.Provider.Name}/{candidate.Model}";
            if (needsTools && IsTextOnly(candidate.Provider))
                skipped.Add((name, "text-only account, request uses tools"));
            else if (!Fits(candidate, promptTokens))
                skipped.Add((name, $"context {ContextWindow(candidate.Provider, candidate.Model)} < {promptTokens}"));
            else if (engine.Unavailable(candidate.Provider, candidate.Model) is { } reason)
                skipped.Add((name, reason));
        }
        return skipped;
    }

    /// <summary>The model's published context window in tokens, or null when unknown.</summary>
    internal static long? ContextWindow(ProviderSettings provider, string model) =>
        ModelPricingTable.For(provider, model)?.ContextTokens;

    /// <summary>True unless the model's known context window is smaller than the prompt.</summary>
    internal static bool Fits((string Model, ProviderSettings Provider) candidate, long? promptTokens) =>
        promptTokens is not { } tokens || ContextWindow(candidate.Provider, candidate.Model) is not { } window || tokens <= window;

    /// <summary>
    /// CLI accounts relay text only: the CLI runs its own agent loop, so a client's tool
    /// definitions cannot be handed to it and tool calls cannot come back.
    /// </summary>
    internal static bool IsTextOnly(ProviderSettings provider) =>
        provider.AuthMode == ProviderAuthMode.CliAccount;

    /// <summary>Signed-in subscription accounts (CLI or OAuth) rather than API-key billing.</summary>
    internal static bool IsPlanAccount(ProviderSettings provider) =>
        provider.AuthMode is ProviderAuthMode.CliAccount or ProviderAuthMode.OAuth;

    private static string RoundRobinIdentity((string Model, ProviderSettings Provider) candidate) =>
        candidate.Provider.Id + "\0" + candidate.Model;

    /// <summary>
    /// Free models first, then the rest cheapest-first. Free is a fact rather than a
    /// ranking, so it is a separate pass: a pool of one free model and nine paid ones
    /// answers with the free one until it stops working.
    /// </summary>
    private static (string Model, ProviderSettings Provider) FreeFirst(
        (string Model, ProviderSettings Provider)[] available, RouterEngine? engine = null)
    {
        var free = available
            .Where(c => ModelPricingTable.For(c.Provider, c.Model)?.IsFree == true)
            .OrderBy(c => c.Provider.Priority)
            .ThenByDescending(c => engine?.Headroom(c.Provider, c.Model) ?? 1.0)
            .ThenBy(c => c.Model, StringComparer.OrdinalIgnoreCase);

        return free.Concat(ByPriceOrdered(available.Where(c => ModelPricingTable.For(c.Provider, c.Model)?.IsFree != true)
            .ToArray(), descending: false, engine)).First();
    }

    /// <summary>
    /// Models whose own id says they are for code - code, codex, coder, coding - ahead of
    /// the rest of the pool.
    ///
    /// A name is all this is: the model databases carry no "good at code" field, and
    /// inventing one would be a guess dressed as a fact. Anything the provider did not
    /// name that way stays eligible, so a pool of general models still answers.
    /// </summary>
    private static (string Model, ProviderSettings Provider) CodingFirst(
        (string Model, ProviderSettings Provider)[] available, RouterEngine? engine = null)
    {
        static bool IsCoding(string model) =>
            model.Contains("code", StringComparison.OrdinalIgnoreCase) ||
            model.Contains("coder", StringComparison.OrdinalIgnoreCase) ||
            model.Contains("coding", StringComparison.OrdinalIgnoreCase);

        var coding = available.Where(c => IsCoding(c.Model)).ToArray();
        if (coding.Length == 0) return ByPrice(available, descending: true, engine);

        return ByPriceOrdered(coding, descending: true, engine).First();
    }

    /// <summary>
    /// Keeps a session on the same model so an agent's context does not jump between
    /// upstreams mid-conversation, and only moves when that model stops being available.
    /// </summary>
    /// <summary>
    /// Keeps a conversation on the model that answered it last while that model's prompt
    /// cache is warm, then lets the strategy choose again.
    ///
    /// Without this, a preference strategy re-ranks on every request. After a failure the
    /// conversation moves to the next model, then jumps back as soon as the 60-second
    /// cooldown ends, paying a cold cache both ways, and a price refresh mid-conversation
    /// can move it too. Re-ranking once the conversation has been idle past the cache
    /// lifetime costs nothing, because the cache is gone by then anyway.
    /// </summary>
    private static (string Model, ProviderSettings Provider) WithCacheAffinity(
        RouterRule rule, (string Model, ProviderSettings Provider)[] available, RouterEngine engine, string sessionKey,
        TimeSpan? cacheLifetime, Func<(string Model, ProviderSettings Provider)> pick)
    {
        var key = "affinity|" + rule.Key + "|" + sessionKey;
        var warm = engine.TouchIsWarm(key, cacheLifetime);
        if (warm && engine.StickyFor(key) is { } remembered)
        {
            // A cooling model is not in `available`, so a failure still moves the conversation.
            var match = available.FirstOrDefault(c => RoundRobinIdentity(c).Equals(remembered, StringComparison.OrdinalIgnoreCase));
            if (match.Model is not null) return match;
        }
        var chosen = pick();
        engine.SetSticky(key, RoundRobinIdentity(chosen));
        return chosen;
    }

    private static (string Model, ProviderSettings Provider) Sticky(
        RouterRule rule, (string Model, ProviderSettings Provider)[] available, RouterEngine engine, string sessionKey)
    {
        var key = rule.Key + "|" + sessionKey;
        var remembered = engine.StickyFor(key);
        if (remembered is not null)
        {
            // Same provider and model first: the same id on another provider has none of
            // this conversation's cache. Same model elsewhere is the next best thing when
            // the first provider is out of rotation.
            var match = available.FirstOrDefault(c => RoundRobinIdentity(c).Equals(remembered, StringComparison.OrdinalIgnoreCase));
            if (match.Model is null)
            {
                var model = remembered[(remembered.IndexOf('\0') + 1)..];
                match = available.FirstOrDefault(c => c.Model.Equals(model, StringComparison.OrdinalIgnoreCase));
                if (match.Model is not null) engine.SetSticky(key, RoundRobinIdentity(match));
            }
            if (match.Model is not null) return match;
        }

        // For a new session, use round-robin on deterministically-ordered models to spread
        // load evenly across new chats instead of always picking the most expensive.
        var ordered = ByPriceOrdered(available, descending: true, engine).ToArray();
        var chosen = ordered[engine.NextPosition(rule.Key, ordered.Length)];
        engine.SetSticky(key, RoundRobinIdentity(chosen));
        return chosen;
    }

    /// <summary>
    /// Ordered by output price, with unpriced models last in both directions: an unknown
    /// price is not evidence of high quality, and it is not evidence of being cheap.
    /// Provider priority breaks a tie, then the id, so the choice is stable rather than
    /// depending on dictionary order.
    /// </summary>
    private static (string Model, ProviderSettings Provider) ByPrice(
        (string Model, ProviderSettings Provider)[] candidates, bool descending, RouterEngine? engine = null) =>
        ByPriceOrdered(candidates, descending, engine).First();

    /// <summary>
    /// Price order. Among equally priced models, the one with the most of its published
    /// request allowance left goes first (OpenCode Go's 5-hour counts), so load spreads
    /// before any one model reaches its limit; then provider priority, then the id.
    /// </summary>
    private static IEnumerable<(string Model, ProviderSettings Provider)> ByPriceOrdered(
        (string Model, ProviderSettings Provider)[] candidates, bool descending, RouterEngine? engine = null)
    {
        double Headroom((string Model, ProviderSettings Provider) c) => engine?.Headroom(c.Provider, c.Model) ?? 1.0;

        static decimal? Rate((string Model, ProviderSettings Provider) candidate) =>
            ModelPricingTable.For(candidate.Provider, candidate.Model) is { } price
                ? price.OutputPerMillion ?? price.InputPerMillion
                : null;

        var priced = candidates.Where(c => Rate(c) is not null);
        var unpriced = candidates.Where(c => Rate(c) is null)
            .OrderByDescending(Headroom)
            .ThenBy(c => c.Provider.Priority)
            .ThenBy(c => c.Model, StringComparer.OrdinalIgnoreCase);

        var ranked = descending
            ? priced.OrderByDescending(c => Rate(c))
            : priced.OrderBy(c => Rate(c));

        return ranked
            .ThenByDescending(Headroom)
            .ThenBy(c => c.Provider.Priority)
            .ThenBy(c => c.Model, StringComparer.OrdinalIgnoreCase)
            .Concat(unpriced);
    }

    /// <summary>Highest-priority provider serving the model, which is the one a plain request would get.</summary>
    private static IEnumerable<(string Model, ProviderSettings Provider)> ResolveAll(
        string model, IReadOnlyList<(string Model, ProviderSettings Provider)> candidates) =>
        candidates.Where(c => c.Model.Equals(model, StringComparison.OrdinalIgnoreCase))
            .OrderBy(c => c.Provider.Priority)
            .ThenBy(c => c.Provider.Id, StringComparer.OrdinalIgnoreCase);
}
