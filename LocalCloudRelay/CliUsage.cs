using System.Text.Json;

namespace LocalCloudRelay;

/// <summary>
/// Token usage reported back to the client by the CLI gateways, in each dialect's shape.
///
/// Claude Code reads the usage on each reply to track how full the context is and to
/// decide when to compact. Reporting zeros made a CLI-backed conversation look empty
/// forever, so it could run past the window instead of compacting in time. The CLI's own
/// counts include its system prompt, which is part of the real context too.
/// </summary>
public static class CliUsage
{
    /// <summary>Anthropic usage: input excludes cache reads and writes, which are listed beside it.</summary>
    public static object Anthropic(RelayUsageSnapshot? usage) => new
    {
        input_tokens = usage?.InputTokens ?? 0,
        output_tokens = usage?.OutputTokens ?? 0,
        cache_creation_input_tokens = usage?.CacheCreationInputTokens ?? 0,
        cache_read_input_tokens = usage?.CacheReadInputTokens ?? 0
    };

    /// <summary>OpenAI usage: prompt_tokens includes cached tokens, which are broken out in the details.</summary>
    public static object OpenAi(RelayUsageSnapshot? usage)
    {
        var prompt = (usage?.InputTokens ?? 0) + (usage?.CacheReadInputTokens ?? 0) + (usage?.CacheCreationInputTokens ?? 0);
        var completion = usage?.OutputTokens ?? 0;
        return new
        {
            prompt_tokens = prompt,
            completion_tokens = completion,
            total_tokens = prompt + completion,
            prompt_tokens_details = new { cached_tokens = usage?.CacheReadInputTokens ?? 0 }
        };
    }

    /// <summary>
    /// The final usage-only chunk of an OpenAI stream, as sent for
    /// <c>stream_options.include_usage</c>: empty choices, usage set.
    /// </summary>
    public static string OpenAiUsageChunk(string id, long created, string model, RelayUsageSnapshot usage) =>
        "data: " + JsonSerializer.Serialize(new
        {
            id, @object = "chat.completion.chunk", created, model,
            choices = Array.Empty<object>(),
            usage = OpenAi(usage)
        }) + "\n\n";
}
