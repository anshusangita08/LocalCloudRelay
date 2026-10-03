using System.Text.Json;

namespace LocalCloudRelay;

/// <summary>What the relay has seen from one provider, and which of those models are new.</summary>
public sealed record KnownProviderModels(
    IReadOnlyList<string> Known,
    IReadOnlyList<string> New,
    int AddedLastRefresh = 0,
    DateTimeOffset? LastAddedAt = null);

/// <summary>Every provider's known models, keyed by provider id.</summary>
public sealed record KnownModels(IReadOnlyDictionary<string, KnownProviderModels> Providers)
{
    public static readonly KnownModels Empty = new(new Dictionary<string, KnownProviderModels>(StringComparer.Ordinal));

    public IReadOnlyList<string> NewFor(string providerId) =>
        Providers.TryGetValue(providerId, out var known) ? known.New : [];

    public bool IsNew(string providerId, string model) =>
        NewFor(providerId).Contains(model, StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// Spots models a provider started offering since the last refresh, so a provider change
/// is visible instead of silent. New models are flagged and switched off until the
/// operator turns them on: a new model joining every router unasked could change what
/// a client gets, or what it costs.
///
/// A provider seen for the first time is a baseline, not a change: everything it lists
/// is known, nothing is new, and nothing is switched off. A provider that returned no
/// models (offline, signed out) is left as it was, so a failed refresh never makes the
/// whole catalog look new on the next one.
/// </summary>
public static class ModelChangeTracker
{
    public sealed record Result(KnownModels Known, IReadOnlyDictionary<string, IReadOnlyList<string>> Added)
    {
        public int AddedCount => Added.Values.Sum(models => models.Count);
    }

    public static Result Track(KnownModels previous, IReadOnlyDictionary<string, IReadOnlyList<string>> current, DateTimeOffset now)
    {
        var providers = new Dictionary<string, KnownProviderModels>(previous.Providers, StringComparer.Ordinal);
        var added = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);

        foreach (var (providerId, listed) in current)
        {
            var models = listed.Where(m => !string.IsNullOrWhiteSpace(m))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (models.Length == 0) continue;

            if (!providers.TryGetValue(providerId, out var known))
            {
                providers[providerId] = new KnownProviderModels(models, []);
                continue;
            }

            var fresh = models.Where(m => !known.Known.Contains(m, StringComparer.OrdinalIgnoreCase)).ToArray();
            // A flag stays until the operator acts on the model, or the provider drops it.
            var stillNew = known.New.Where(m => models.Contains(m, StringComparer.OrdinalIgnoreCase));
            providers[providerId] = new KnownProviderModels(
                [.. known.Known.Concat(fresh)],
                [.. stillNew.Concat(fresh).Distinct(StringComparer.OrdinalIgnoreCase)],
                fresh.Length,
                fresh.Length > 0 ? now : known.LastAddedAt);
            if (fresh.Length > 0) added[providerId] = fresh;
        }

        return new Result(new KnownModels(providers), added);
    }

    /// <summary>Clears the new flag once the operator has decided about a model.</summary>
    public static KnownModels Acknowledge(KnownModels known, string providerId, IEnumerable<string> models)
    {
        if (!known.Providers.TryGetValue(providerId, out var entry)) return known;
        var seen = models.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!entry.New.Any(seen.Contains)) return known;
        var providers = new Dictionary<string, KnownProviderModels>(known.Providers, StringComparer.Ordinal)
        {
            [providerId] = entry with { New = [.. entry.New.Where(m => !seen.Contains(m))] }
        };
        return new KnownModels(providers);
    }

    /// <summary>Drops providers that no longer exist, so removing one leaves nothing behind.</summary>
    public static KnownModels Retain(KnownModels known, IEnumerable<string> providerIds)
    {
        var keep = providerIds.ToHashSet(StringComparer.Ordinal);
        if (known.Providers.Keys.All(keep.Contains)) return known;
        return new KnownModels(known.Providers.Where(e => keep.Contains(e.Key))
            .ToDictionary(e => e.Key, e => e.Value, StringComparer.Ordinal));
    }
}

/// <summary>Persists <see cref="KnownModels"/> between launches in known-models.json.</summary>
public sealed class KnownModelsStore(string path)
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = false };

    public KnownModels Load()
    {
        try
        {
            if (!File.Exists(path)) return KnownModels.Empty;
            var providers = JsonSerializer.Deserialize<Dictionary<string, KnownProviderModels>>(File.ReadAllText(path), Options);
            return providers is null ? KnownModels.Empty
                : new KnownModels(new Dictionary<string, KnownProviderModels>(providers, StringComparer.Ordinal));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            return KnownModels.Empty;
        }
    }

    public void Save(KnownModels known)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            var temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(known.Providers, Options));
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Losing the list only means the next refresh treats today's models as the baseline.
        }
    }
}
