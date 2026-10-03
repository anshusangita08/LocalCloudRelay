using System.Text;
using System.Text.Json;

namespace LocalCloudRelay;

/// <summary>Totals for one period of the usage history.</summary>
public sealed record UsagePeriod(
    string Label,
    int Requests,
    int Failed,
    long InputTokens,
    long OutputTokens,
    long CacheReadTokens,
    long CacheWriteTokens,
    decimal CostUsd,
    decimal ApiEquivalentUsd)
{
    public double? CacheHitRate => RelayTelemetryStore.CacheHitRate(InputTokens, CacheReadTokens, CacheWriteTokens);
}

/// <summary>
/// Every request record, appended to one JSON-lines file per local day, so spend and
/// cache figures survive a restart. Files older than <see cref="RetentionDays"/> are
/// deleted. Records hold counts, costs, model and provider names, the client address and
/// the routing decision - never a prompt or a key.
/// </summary>
public sealed class UsageHistory
{
    public const int RetentionDays = 30;
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = false };

    private readonly string _directory;
    private readonly TimeProvider _time;
    private readonly object _gate = new();

    public UsageHistory(string directory, TimeProvider? time = null)
    {
        _directory = directory;
        _time = time ?? TimeProvider.System;
    }

    private DateOnly Today => DateOnly.FromDateTime(_time.GetLocalNow().DateTime);

    private string FileFor(DateOnly day) => Path.Combine(_directory, day.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture) + ".jsonl");

    public void Append(RelayRequestRecord record)
    {
        var line = JsonSerializer.Serialize(record, Options) + "\n";
        lock (_gate)
        {
            Directory.CreateDirectory(_directory);
            File.AppendAllText(FileFor(DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(record.StartedAt, _time.LocalTimeZone).DateTime)), line, Encoding.UTF8);
        }
    }

    /// <summary>Deletes day files older than the retention window.</summary>
    public void Prune()
    {
        if (!Directory.Exists(_directory)) return;
        var oldest = Today.AddDays(-RetentionDays + 1);
        foreach (var file in Directory.EnumerateFiles(_directory, "*.jsonl"))
        {
            if (DateOnly.TryParseExact(Path.GetFileNameWithoutExtension(file), "yyyy-MM-dd",
                    System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var day) &&
                day < oldest)
            {
                try { File.Delete(file); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }
    }

    /// <summary>Today, the last 7 days and the last 30 days, each including today.</summary>
    public IReadOnlyList<UsagePeriod> Summaries()
    {
        var today = Today;
        var byDay = new Dictionary<DateOnly, List<RelayRequestRecord>>();
        for (var offset = 0; offset < RetentionDays; offset++)
        {
            var day = today.AddDays(-offset);
            byDay[day] = Read(day);
        }

        UsagePeriod Sum(string label, int days)
        {
            var records = Enumerable.Range(0, days).SelectMany(offset => byDay[today.AddDays(-offset)]).ToArray();
            return new UsagePeriod(label, records.Length, records.Count(r => r.StatusCode >= 400),
                records.Sum(r => r.InputTokens ?? 0), records.Sum(r => r.OutputTokens ?? 0),
                records.Sum(r => r.CacheReadInputTokens ?? 0), records.Sum(r => r.CacheCreationInputTokens ?? 0),
                records.Sum(r => r.CostSource is "provider" or "estimated" ? r.ProviderCostUsd ?? 0m : 0m),
                records.Sum(r => r.ApiEquivalentUsd ?? 0m));
        }

        return [Sum("Today", 1), Sum("Last 7 days", 7), Sum("Last 30 days", RetentionDays)];
    }

    private List<RelayRequestRecord> Read(DateOnly day)
    {
        var path = FileFor(day);
        var records = new List<RelayRequestRecord>();
        string[] lines;
        lock (_gate)
        {
            if (!File.Exists(path)) return records;
            try { lines = File.ReadAllLines(path, Encoding.UTF8); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return records; }
        }
        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                if (JsonSerializer.Deserialize<RelayRequestRecord>(line, Options) is { } record) records.Add(record);
            }
            catch (JsonException)
            {
                // A line cut short by a crash is skipped, not fatal.
            }
        }
        return records;
    }
}

/// <summary>
/// One line per failed or retried request: time, request id, status, router, provider and
/// model, the routing decision and the error. Enough to explain "that turn failed at
/// 11:40" after the fact. Never a prompt or a key. Kept to about a megabyte, with one
/// previous file, so it cannot grow over a long day.
/// </summary>
public sealed class DiagnosticLog(string path, long maxBytes = 1_048_576)
{
    private readonly object _gate = new();

    public string FilePath => path;

    /// <summary>Failed requests (status 400 and up, client-closed excluded) and retried ones.</summary>
    public static bool Worth(RelayRequestRecord record) =>
        (record.StatusCode >= 400 && record.StatusCode != 499) ||
        (record.Decision?.Contains("attempt=", StringComparison.Ordinal) ?? false);

    public void Write(RelayRequestRecord record)
    {
        if (!Worth(record)) return;
        var line = string.Join(" | ",
            record.StartedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture),
            record.RequestId,
            record.StatusCode.ToString(System.Globalization.CultureInfo.InvariantCulture),
            $"{record.Provider ?? "-"}/{record.Model ?? "-"}",
            $"{record.DurationMs} ms",
            record.Decision ?? "-",
            record.Error ?? "-") + Environment.NewLine;
        lock (_gate)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            if (File.Exists(path) && new FileInfo(path).Length + line.Length > maxBytes)
                File.Move(path, path + ".1", overwrite: true);
            File.AppendAllText(path, line, Encoding.UTF8);
        }
    }
}
