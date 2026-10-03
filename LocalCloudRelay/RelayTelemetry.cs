using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace LocalCloudRelay;

public sealed record RelayUsageSnapshot(
    long? InputTokens = null,
    long? OutputTokens = null,
    long? TotalTokens = null,
    long? CacheReadInputTokens = null,
    long? CacheCreationInputTokens = null,
    long? ReasoningTokens = null,
    decimal? CostUsd = null,
    string? Model = null);

public sealed record RelayHeaderSnapshot(
    string? UpstreamRequestId,
    string? LiteLlmCallId,
    string? Model,
    string? Provider,
    decimal? CostUsd,
    decimal? InputCostUsd,
    decimal? OutputCostUsd,
    decimal? CacheReadCostUsd,
    decimal? CacheCreationCostUsd,
    IReadOnlyDictionary<string, string> RateLimits);

public sealed record RelayRequestRecord(
    DateTimeOffset StartedAt,
    string RequestId,
    string SessionId,
    string Method,
    string Path,
    int StatusCode,
    long DurationMs,
    string? Model,
    string? Provider,
    string? UpstreamRequestId,
    string? LiteLlmCallId,
    long? InputTokens,
    long? OutputTokens,
    long? TotalTokens,
    long? CacheReadInputTokens,
    long? CacheCreationInputTokens,
    long? ReasoningTokens,
    decimal? ProviderCostUsd,
    string CostSource,
    IReadOnlyDictionary<string, string> RateLimits,
    // How a client is identified for the "connected" count. The session header is
    // ours, so most clients never send it - Claude Code included - which made a
    // running client look like nobody was connected. The remote address always exists.
    string? ClientAddress = null,
    string? UserAgent = null,
    // What the request would have cost at the vendor's API rates, recorded for plan
    // accounts (Claude Code, ChatGPT, Antigravity, Gemini sign-in), whose real cost is
    // the flat subscription: ProviderCostUsd is 0 for them and CostSource is "plan".
    decimal? ApiEquivalentUsd = null,
    // The x-relay-decision text for a routed request, and the exception type and message
    // for one that failed inside the relay. Kept for the diagnostic log; never a prompt.
    string? Decision = null,
    string? Error = null);

public sealed record RelayUsageBreakdown(
    string Key,
    int Requests,
    long InputTokens,
    long OutputTokens,
    long TotalTokens,
    long CacheReadInputTokens,
    decimal ProviderCostUsd,
    decimal EstimatedCostUsd,
    int UnknownCostRequests,
    long CacheCreationInputTokens = 0,
    decimal ApiEquivalentUsd = 0m)
{
    /// <summary>Share of prompt tokens read from cache, or null before any prompt tokens.</summary>
    public double? CacheHitRate => RelayTelemetryStore.CacheHitRate(InputTokens, CacheReadInputTokens, CacheCreationInputTokens);
}

public sealed record RelayTelemetryReport(
    DateTimeOffset GeneratedAt,
    string SessionId,
    int RequestCount,
    int FailedRequestCount,
    long InputTokens,
    long OutputTokens,
    long TotalTokens,
    long CacheReadInputTokens,
    long CacheCreationInputTokens,
    long ReasoningTokens,
    decimal ProviderCostUsd,
    decimal EstimatedCostUsd,
    decimal TotalConsumedCostUsd,
    int RequestsWithUnknownCost,
    double AverageDurationMs,
    IReadOnlyList<RelayUsageBreakdown> Models,
    IReadOnlyList<RelayUsageBreakdown> Providers,
    IReadOnlyList<RelayRequestRecord> RecentRequests)
{
    /// <summary>What plan-account traffic would have cost on the vendors' APIs. Not spend.</summary>
    public decimal ApiEquivalentUsd => Models.Sum(model => model.ApiEquivalentUsd);

    /// <summary>Share of prompt tokens read from cache, or null before any prompt tokens.</summary>
    public double? CacheHitRate => RelayTelemetryStore.CacheHitRate(InputTokens, CacheReadInputTokens, CacheCreationInputTokens);
}

public static class RelayUsageParser
{
    public static RelayUsageSnapshot? Parse(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;
        try
        {
            using var document = JsonDocument.Parse(body);
            var values = new List<RelayUsageSnapshot>();
            AddUsageValues(document.RootElement, values);
            return Combine(values);
        }
        catch (JsonException)
        {
            return ParseServerSentEvents(body);
        }
    }

    public static RelayUsageSnapshot? ParseServerSentEvents(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;
        var values = new List<RelayUsageSnapshot>();
        foreach (var rawLine in body.Split('\n'))
        {
            var line = rawLine.Trim();
            if (!line.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) continue;
            var json = line[5..].Trim();
            if (json.Length == 0 || json.Equals("[DONE]", StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                using var document = JsonDocument.Parse(json);
                AddUsageValues(document.RootElement, values);
            }
            catch (JsonException) { }
        }
        return Combine(values);
    }

    private static void AddUsageValues(JsonElement root, ICollection<RelayUsageSnapshot> values)
    {
        if (root.ValueKind != JsonValueKind.Object) return;
        if (TryGetProperty(root, "usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
            values.Add(ParseUsageObject(root, usage));
        if (TryGetProperty(root, "message", out var message) && message.ValueKind == JsonValueKind.Object &&
            TryGetProperty(message, "usage", out var messageUsage) && messageUsage.ValueKind == JsonValueKind.Object)
            values.Add(ParseUsageObject(message, messageUsage));
        if (TryGetProperty(root, "response", out var response) && response.ValueKind == JsonValueKind.Object &&
            TryGetProperty(response, "usage", out var responseUsage) && responseUsage.ValueKind == JsonValueKind.Object)
            values.Add(ParseUsageObject(response, responseUsage));
    }

    private static RelayUsageSnapshot ParseUsageObject(JsonElement root, JsonElement usage)
    {
        var input = FirstLong(usage, "input_tokens", "inputTokens", "prompt_tokens", "total_input_tokens");
        var output = FirstLong(usage, "output_tokens", "outputTokens", "completion_tokens", "total_output_tokens");
        var total = FirstLong(usage, "total_tokens", "totalTokens");
        var cacheRead = FirstLong(usage, "cache_read_input_tokens", "cacheReadInputTokens", "cache_read_tokens");
        var cacheCreation = FirstLong(usage, "cache_creation_input_tokens", "cacheCreationInputTokens", "cache_creation_tokens", "cache_write_tokens");
        var reasoning = FirstLong(usage, "reasoning_tokens", "total_thought_tokens");

        // Two conventions. Anthropic reports cache reads and writes beside input_tokens,
        // which leaves them out. OpenAI (prompt_tokens_details) and the Responses API
        // (input_tokens_details) count them inside the input total. Input is kept as the
        // uncached part either way, so pricing never charges a cached token twice.
        long? inclusiveRead = null, inclusiveWrite = null;
        foreach (var detailsName in new[] { "prompt_tokens_details", "input_tokens_details" })
        {
            if (!TryGetProperty(usage, detailsName, out var details) || details.ValueKind != JsonValueKind.Object) continue;
            inclusiveRead ??= FirstLong(details, "cached_tokens", "cache_read_input_tokens");
            inclusiveWrite ??= FirstLong(details, "cache_creation_tokens", "cache_write_tokens");
        }
        if (cacheRead is null && cacheCreation is null && (inclusiveRead is not null || inclusiveWrite is not null))
        {
            cacheRead = inclusiveRead;
            cacheCreation = inclusiveWrite;
            if (input is { } inclusive)
                input = Math.Max(0, inclusive - (inclusiveRead ?? 0) - (inclusiveWrite ?? 0));
        }
        if (TryGetProperty(usage, "completion_tokens_details", out var completionDetails) && completionDetails.ValueKind == JsonValueKind.Object)
            reasoning ??= FirstLong(completionDetails, "reasoning_tokens");
        if (TryGetProperty(usage, "output_tokens_details", out var outputDetails) && outputDetails.ValueKind == JsonValueKind.Object)
            reasoning ??= FirstLong(outputDetails, "thinking_tokens", "reasoning_tokens");

        total ??= input.HasValue && output.HasValue
            ? input.Value + output.Value + (cacheRead ?? 0) + (cacheCreation ?? 0)
            : null;
        var cost = FirstDecimal(root, "cost", "cost_usd", "total_cost", "estimated_cost") ??
                   FirstDecimal(usage, "cost", "cost_usd", "total_cost", "estimated_cost");
        return new RelayUsageSnapshot(input, output, total, cacheRead, cacheCreation, reasoning,
            cost, FirstString(root, "model", "model_id"));
    }

    private static RelayUsageSnapshot? Combine(IReadOnlyCollection<RelayUsageSnapshot> values)
    {
        if (values.Count == 0) return null;
        return new RelayUsageSnapshot(
            Max(values.Select(v => v.InputTokens)), Max(values.Select(v => v.OutputTokens)),
            Max(values.Select(v => v.TotalTokens)), Max(values.Select(v => v.CacheReadInputTokens)),
            Max(values.Select(v => v.CacheCreationInputTokens)), Max(values.Select(v => v.ReasoningTokens)),
            values.Select(v => v.CostUsd).FirstOrDefault(v => v.HasValue),
            values.Select(v => v.Model).FirstOrDefault(v => !string.IsNullOrWhiteSpace(v)));
    }

    private static long? Max(IEnumerable<long?> values)
    {
        var present = values.Where(v => v.HasValue).Select(v => v!.Value).ToArray();
        return present.Length == 0 ? null : present.Max();
    }

    private static long? Sum(IEnumerable<long?> values)
    {
        var present = values.Where(v => v.HasValue).Select(v => v!.Value).ToArray();
        return present.Length == 0 ? null : present.Sum();
    }

    public static RelayHeaderSnapshot ReadHeaders(IEnumerable<KeyValuePair<string, IEnumerable<string>>> headers)
    {
        var values = headers
            .GroupBy(h => h.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => string.Join(",", g.SelectMany(h => h.Value)), StringComparer.OrdinalIgnoreCase);
        string? First(params string[] names) => names.Select(name => values.TryGetValue(name, out var value) ? value : null)
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
        decimal? Decimal(params string[] names) => names.Select(name => values.TryGetValue(name, out var value) ? ParseDecimal(value) : null)
            .FirstOrDefault(value => value.HasValue);
        var rateLimits = values.Where(pair => pair.Key.StartsWith("x-ratelimit-", StringComparison.OrdinalIgnoreCase) ||
                                               pair.Key.StartsWith("anthropic-ratelimit-", StringComparison.OrdinalIgnoreCase) ||
                                               pair.Key.Equals("retry-after", StringComparison.OrdinalIgnoreCase))
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
        return new RelayHeaderSnapshot(
            First("request-id", "x-request-id", "x-litellm-request-id"), First("x-litellm-call-id"),
            First("x-litellm-model-id", "x-model", "model"), First("x-litellm-model-group", "x-litellm-provider", "x-provider"),
            Decimal("x-litellm-response-cost", "x-response-cost", "x-cost-usd"),
            Decimal("x-litellm-response-cost-input"), Decimal("x-litellm-response-cost-output"),
            Decimal("x-litellm-response-cost-cache-read"), Decimal("x-litellm-response-cost-cache-creation"), rateLimits);
    }

    private static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
    {
        if (element.TryGetProperty(name, out value)) return true;
        foreach (var property in element.EnumerateObject())
        {
            if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) { value = property.Value; return true; }
        }
        value = default;
        return false;
    }

    private static long? FirstLong(JsonElement element, params string[] names) =>
        names.Select(name => TryGetProperty(element, name, out var value) ? ToLong(value) : null).FirstOrDefault(v => v.HasValue);
    private static decimal? FirstDecimal(JsonElement element, params string[] names) =>
        names.Select(name => TryGetProperty(element, name, out var value) ? ToDecimal(value) : null).FirstOrDefault(v => v.HasValue);
    private static string? FirstString(JsonElement element, params string[] names) =>
        names.Select(name => TryGetProperty(element, name, out var value) ? value.ToString() : null)
            .FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
    private static long? ToLong(JsonElement value) => value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number)
        ? number : long.TryParse(value.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out number) ? number : null;
    private static decimal? ToDecimal(JsonElement value) => value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var number)
        ? number : ParseDecimal(value.ToString());
    private static decimal? ParseDecimal(string? value) => decimal.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) ? number : null;
}

/// <summary>
/// Tee that forwards a streaming response to the client while keeping a bounded tail of
/// it for usage parsing. It keeps the TAIL, not the head: an SSE stream carries its
/// usage block in the final message_delta, so a head-truncating capture silently
/// reported zero tokens and zero cost on exactly the longest, most expensive requests.
/// </summary>
public sealed class RelayBodyCaptureStream : Stream
{
    private readonly Stream _inner;
    private readonly int _limit;
    private readonly MemoryStream _capture;

    public RelayBodyCaptureStream(Stream inner, int limit = 2 * 1024 * 1024)
    {
        _inner = inner;
        _limit = limit;
        _capture = new MemoryStream();
    }

    public string CapturedText => Encoding.UTF8.GetString(_capture.GetBuffer(), 0, (int)_capture.Length);

    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => _inner.CanWrite;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() => _inner.Flush();
    public override Task FlushAsync(CancellationToken cancellationToken) => _inner.FlushAsync(cancellationToken);
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count)
    {
        _inner.Write(buffer, offset, count);
        Capture(buffer.AsSpan(offset, count));
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        await _inner.WriteAsync(buffer, cancellationToken);
        Capture(buffer.Span);
    }

    private void Capture(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty) return;
        _capture.Write(bytes);
        // Trim in one hop once the buffer doubles, so this is not per-write overhead.
        if (_capture.Length > (long)_limit * 2) TrimTo(_limit);
    }

    private void TrimTo(int keep)
    {
        var buffer = _capture.GetBuffer();
        var drop = (int)_capture.Length - keep;
        if (drop <= 0) return;
        Buffer.BlockCopy(buffer, drop, buffer, 0, keep);
        _capture.SetLength(keep);
        _capture.Position = keep;
    }

    // _inner is the live Kestrel response body and is not ours to dispose; only the
    // capture buffer is. Safe to dispose this, so callers should.
    public override ValueTask DisposeAsync()
    {
        _capture.Dispose();
        return base.DisposeAsync();
    }
    protected override void Dispose(bool disposing) { if (disposing) _capture.Dispose(); base.Dispose(disposing); }
}

public sealed class RelayTelemetryStore
{
    private const int MaximumRecords = 2_000;
    private readonly object _gate = new();
    private readonly Queue<RelayRequestRecord> _records = new();

    /// <summary>
    /// Raised after each record is kept, on the request's thread, so the usage history and
    /// diagnostic log see every request without the server knowing they exist.
    /// </summary>
    public event Action<RelayRequestRecord>? Recorded;

    public void Record(RelayRequestRecord record)
    {
        lock (_gate) { _records.Enqueue(record); while (_records.Count > MaximumRecords) _records.Dequeue(); }
        try { Recorded?.Invoke(record); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // History and diagnostics are best effort; a full disk must not fail a request.
        }
    }
    public IReadOnlyList<RelayRequestRecord> Records(string? sessionId = null)
    {
        lock (_gate)
        {
            var records = _records.ToArray();
            return string.IsNullOrWhiteSpace(sessionId) ? records : records.Where(r => r.SessionId.Equals(sessionId, StringComparison.Ordinal)).ToArray();
        }
    }

    public RelayTelemetryReport GetReport(string? sessionId = null)
    {
        RelayRequestRecord[] records;
        lock (_gate) records = _records.ToArray();
        if (!string.IsNullOrWhiteSpace(sessionId)) records = records.Where(r => r.SessionId.Equals(sessionId, StringComparison.Ordinal)).ToArray();
        var providerCost = records.Sum(r => r.CostSource == "provider" ? r.ProviderCostUsd ?? 0m : 0m);
        var estimatedCost = records.Sum(r => r.CostSource == "estimated" ? r.ProviderCostUsd ?? 0m : 0m);
        var breakdown = records.GroupBy(r => r.Model ?? "unknown", StringComparer.OrdinalIgnoreCase)
            .Select(g => Breakdown(g.Key, g)).OrderByDescending(x => x.ProviderCostUsd + x.EstimatedCostUsd).ToArray();
        var providers = records.GroupBy(r => r.Provider ?? "unknown", StringComparer.OrdinalIgnoreCase)
            .Select(g => Breakdown(g.Key, g)).OrderByDescending(x => x.ProviderCostUsd + x.EstimatedCostUsd).ToArray();
        return new RelayTelemetryReport(DateTimeOffset.UtcNow, sessionId ?? "relay-process", records.Length,
            records.Count(r => r.StatusCode >= 400), Sum(records.Select(r => r.InputTokens)), Sum(records.Select(r => r.OutputTokens)),
            Sum(records.Select(r => r.TotalTokens)), Sum(records.Select(r => r.CacheReadInputTokens)), Sum(records.Select(r => r.CacheCreationInputTokens)),
            Sum(records.Select(r => r.ReasoningTokens)), providerCost, estimatedCost, providerCost + estimatedCost,
            records.Count(r => r.CostSource == "unknown"), records.Length == 0 ? 0 : records.Average(r => r.DurationMs),
            breakdown, providers, records.TakeLast(50).Reverse().ToArray());
    }
    private static RelayUsageBreakdown Breakdown(string key, IEnumerable<RelayRequestRecord> group)
    {
        var records = group.ToArray();
        return new RelayUsageBreakdown(key, records.Length, Sum(records.Select(r => r.InputTokens)), Sum(records.Select(r => r.OutputTokens)),
            Sum(records.Select(r => r.TotalTokens)), Sum(records.Select(r => r.CacheReadInputTokens)),
            records.Sum(r => r.CostSource == "provider" ? r.ProviderCostUsd ?? 0m : 0m),
            records.Sum(r => r.CostSource == "estimated" ? r.ProviderCostUsd ?? 0m : 0m),
            records.Count(r => r.CostSource == "unknown"),
            Sum(records.Select(r => r.CacheCreationInputTokens)),
            records.Sum(r => r.ApiEquivalentUsd ?? 0m));
    }

    /// <summary>
    /// Cache reads over every prompt token: uncached input, cache reads and cache writes.
    /// Input is already stored as the uncached part (see ParseUsageObject), so the three
    /// add up to the whole prompt in both the Anthropic and the OpenAI convention.
    /// </summary>
    public static double? CacheHitRate(long input, long cacheRead, long cacheWrite)
    {
        var prompt = input + cacheRead + cacheWrite;
        return prompt <= 0 ? null : (double)cacheRead / prompt;
    }

    private static long Sum(IEnumerable<long?> values) => values.Where(v => v.HasValue).Sum(v => v!.Value);
}

public sealed record RelayThinkingProfile(string ProviderFamily, IReadOnlyList<string> Levels, IReadOnlyList<string> RequestFields, bool ChangesMayInvalidateCache, string Notes);

public static class RelayCapabilityCatalog
{
    public static RelayThinkingProfile Resolve(string? model)
    {
        var value = model ?? string.Empty;
        if (value.Contains("claude", StringComparison.OrdinalIgnoreCase))
            return new("anthropic", ["default", "off", "low", "medium", "high"], ["reasoning_effort", "thinking", "output_config.effort"], true, "Use adaptive thinking and output_config.effort on supported current models; legacy models use a stable thinking.budget_tokens value.");
        if (value.Contains("gemini", StringComparison.OrdinalIgnoreCase) || value.Contains("vertex", StringComparison.OrdinalIgnoreCase))
            return new("gemini-vertex", ["default", "off", "minimal", "low", "medium", "high"], ["reasoning_effort", "thinking_level", "thinkingConfig"], false, "Thinking values are model-dependent. LiteLLM/provider support must be checked before selecting a non-default level.");
        if (value.Contains("gpt", StringComparison.OrdinalIgnoreCase) || value.Contains("o1", StringComparison.OrdinalIgnoreCase) || value.Contains("o3", StringComparison.OrdinalIgnoreCase) || value.Contains("o4", StringComparison.OrdinalIgnoreCase))
            return new("openai-azure", ["default", "off", "low", "medium", "high"], ["reasoning_effort"], false, "Use reasoning_effort only for a model/deployment that declares reasoning support.");
        return new("unknown", ["default"], ["provider-specific"], false, "The relay will not invent provider-specific thinking fields for an unknown model.");
    }
    public static object Manifest() => new
    {
        schema_version = 1, default_behavior = "pass-through", session_header = "x-relay-session-id", thinking_header = "x-relay-thinking-level",
        pricing = ModelCatalog.All,
        profiles = new[] { ToManifest("anthropic", Resolve("claude")), ToManifest("gemini-vertex", Resolve("gemini")), ToManifest("openai-azure", Resolve("gpt")), ToManifest("unknown", Resolve("custom")) },
        cache_note = "The relay does not remove, reorder, or rewrite MCP, skill, tool, cache-control, thought-signature, or provider cache fields. Keep thinking settings stable within a cached conversation."
    };
    private static object ToManifest(string name, RelayThinkingProfile profile) => new { provider_family = name, levels = profile.Levels, request_fields = profile.RequestFields, changes_may_invalidate_cache = profile.ChangesMayInvalidateCache, notes = profile.Notes };
}

/// <summary>
/// "Is a client connected", counted by remote address rather than by our own session
/// header. Claude Code, Cline and most clients never set x-relay-session-id, so keying
/// on it reported zero for a relay that was actively serving traffic. Read on demand,
/// never streamed.
/// </summary>
public static class RelayStatus
{
    public static readonly TimeSpan ClientWindow = TimeSpan.FromMinutes(5);

    public static int RecentClientCount(RelayTelemetryStore store, TimeSpan? window = null)
    {
        var cutoff = DateTimeOffset.UtcNow - (window ?? ClientWindow);
        return store.Records()
            .Where(r => r.StartedAt >= cutoff && !string.IsNullOrWhiteSpace(r.ClientAddress))
            .Select(r => r.ClientAddress!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();
    }

    /// <summary>What connected, for the tooltip: address plus user agent when known.</summary>
    public static IReadOnlyList<string> RecentClients(RelayTelemetryStore store, TimeSpan? window = null)
    {
        var cutoff = DateTimeOffset.UtcNow - (window ?? ClientWindow);
        return store.Records()
            .Where(r => r.StartedAt >= cutoff && !string.IsNullOrWhiteSpace(r.ClientAddress))
            .GroupBy(r => r.ClientAddress!, StringComparer.OrdinalIgnoreCase)
            .Select(g => $"{g.Key}  {(g.Select(r => r.UserAgent).FirstOrDefault(u => !string.IsNullOrWhiteSpace(u)) ?? "unknown client")}")
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }
}

internal static class RelayIds
{
    public static string Create(string prefix) => $"{prefix}-{Convert.ToHexString(RandomNumberGenerator.GetBytes(12)).ToLowerInvariant()}";
}
