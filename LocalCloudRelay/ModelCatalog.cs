namespace LocalCloudRelay;

public sealed record ModelPricing(string Model, string Provider, string Mode, decimal InputPerMillion, decimal OutputPerMillion);

public static class ModelCatalog
{
    private static readonly IReadOnlyList<ModelPricing> Entries =
    [
        new("gemini-embedding-001", "vertex_ai", "embedding", .15m, 0m),
        new("text-embedding-3-large", "azure", "embedding", .13m, 0m),
        new("text-embedding-3-small", "azure", "embedding", .02m, 0m),
        new("gpt-5.6-luna", "azure", "chat", .20m, 1.20m), new("gemini-3.1-flash-lite", "vertex_ai", "chat", .25m, 1.50m),
        new("gpt-4.1-mini", "azure", "chat", .40m, 1.60m), new("gpt-5-mini", "azure", "chat", .25m, 2m),
        new("gemini-3-flash", "vertex_ai", "chat", .50m, 3m), new("o3-mini", "azure", "chat", 1.10m, 4.40m),
        new("o4-mini", "azure", "chat", 1.10m, 4.40m), new("gpt-5.4-mini", "azure", "chat", .75m, 4.50m),
        new("claude-haiku-4-5", "vertex_ai", "chat", 1m, 5m), new("claude-haiku-4-5-20251001", "vertex_ai", "chat", 1m, 5m),
        new("gpt-4.1", "azure", "chat", 2m, 8m), new("claude-sonnet-5", "vertex_ai", "chat", 2m, 10m),
        new("gemini-2.5-pro", "vertex_ai", "chat", 1.25m, 10m), new("gpt-4o", "azure", "chat", 2.50m, 10m),
        new("gpt-5.1", "azure", "chat", 1.25m, 10m), new("gemini-3.1-pro", "vertex_ai", "chat", 2m, 12m),
        new("gpt-5.6-terra", "azure", "chat", 2m, 12m), new("gpt-5.2", "azure", "chat", 1.75m, 14m),
        new("claude-sonnet-4-5", "vertex_ai", "chat", 3m, 15m), new("claude-sonnet-4-6", "vertex_ai", "chat", 3m, 15m),
        new("gpt-5.4", "azure", "chat", 2.50m, 15m), new("claude-opus-4-5", "vertex_ai", "chat", 5m, 25m),
        new("claude-opus-4-6", "vertex_ai", "chat", 5m, 25m), new("claude-opus-4-7", "vertex_ai", "chat", 5m, 25m),
        new("claude-opus-4-8", "vertex_ai", "chat", 5m, 25m), new("claude-opus-5", "vertex_ai", "chat", 5m, 25m),
        new("gpt-5.5", "azure", "chat", 5m, 30m), new("gpt-5.6-sol", "azure", "chat", 5m, 30m)
    ];

    public static IReadOnlyList<ModelPricing> All => Entries;
    public static ModelPricing? Find(string? model) => Entries.FirstOrDefault(x => x.Model.Equals(model, StringComparison.OrdinalIgnoreCase));
    public static decimal? Estimate(string? model, long? input, long? output, long? cacheRead = null)
    {
        var p = Find(model); if (p is null || (!input.HasValue && !output.HasValue && !cacheRead.HasValue)) return null;
        // ModelCatalog doesn't track cache rates, so cache tokens are ignored
        return ((input ?? 0) * p.InputPerMillion + (output ?? 0) * p.OutputPerMillion) / 1_000_000m;
    }
}
