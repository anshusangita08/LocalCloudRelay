using System.Text.Json;

namespace LocalCloudRelay;

/// <summary>
/// Today's spend per router, for the optional daily budget. An agent stuck in a loop on a
/// paid model is the most expensive failure the relay can see, and it already knows the
/// cost of every request, so it can stop one instead of only reporting it.
///
/// "Today" is the local calendar day: the budget resets at local midnight. Kept in
/// spend.json so a restart in the middle of the day does not hand out a second budget.
/// </summary>
public sealed class RouterSpend
{
    private readonly object _gate = new();
    private readonly string? _path;
    private readonly TimeProvider _time;
    private DateOnly _day;
    private Dictionary<string, decimal> _spent = new(StringComparer.OrdinalIgnoreCase);

    private sealed record Snapshot(DateOnly Day, Dictionary<string, decimal> Spent);

    /// <param name="path">Where to keep the day's totals; null keeps them in memory only.</param>
    public RouterSpend(string? path = null, TimeProvider? time = null)
    {
        _path = path;
        _time = time ?? TimeProvider.System;
        _day = Today();
        if (path is null) return;
        try
        {
            if (!File.Exists(path)) return;
            var snapshot = JsonSerializer.Deserialize<Snapshot>(File.ReadAllText(path));
            // Yesterday's totals are worth nothing today.
            if (snapshot is not null && snapshot.Day == _day)
                _spent = new Dictionary<string, decimal>(snapshot.Spent, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            // An unreadable file means starting the day's count from zero.
        }
    }

    private DateOnly Today() => DateOnly.FromDateTime(_time.GetLocalNow().DateTime);

    private void RollOver()
    {
        var today = Today();
        if (today == _day) return;
        _day = today;
        _spent = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>What a router has spent today.</summary>
    public decimal SpentToday(string router)
    {
        lock (_gate)
        {
            RollOver();
            return _spent.TryGetValue(router, out var spent) ? spent : 0m;
        }
    }

    /// <summary>Adds a request's cost to its router's total for today.</summary>
    public void Add(string router, decimal usd)
    {
        if (usd <= 0m) return;
        lock (_gate)
        {
            RollOver();
            _spent[router] = (_spent.TryGetValue(router, out var spent) ? spent : 0m) + usd;
        }
    }

    /// <summary>
    /// The refusal message when a router has used its budget, or null while it may still
    /// spend. A request already in flight can take a router slightly past its budget; the
    /// next one is refused.
    /// </summary>
    public string? OverBudget(RouterRule rule)
    {
        if (rule.DailyBudgetUsd is not { } budget) return null;
        var spent = SpentToday(rule.Key);
        return spent < budget ? null
            : $"Router '{rule.Key}' has reached its daily budget of ${budget:0.00} (spent ${spent:0.00} today). " +
              "It resets at local midnight, or raise the budget in the router's settings.";
    }

    public void Save()
    {
        if (_path is null) return;
        Snapshot snapshot;
        lock (_gate)
        {
            RollOver();
            snapshot = new Snapshot(_day, new Dictionary<string, decimal>(_spent, StringComparer.OrdinalIgnoreCase));
        }
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);
            var temp = _path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(snapshot));
            File.Move(temp, _path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Losing the file only loses today's count after a restart.
        }
    }
}
