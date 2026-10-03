using System.Text.Json.Nodes;
using LocalCloudRelay;

namespace LocalCloudRelay.Tests;

/// <summary>
/// An OpenAI client reaching an Anthropic upstream sends no cache_control, and Anthropic
/// caches nothing without it. The bridge adds the breakpoints so later turns are read
/// from cache instead of paid for again.
/// </summary>
[Collection("PricingState")]
public sealed class PromptCacheTests
{
    private static JsonObject Translate(string openAiBody) =>
        JsonNode.Parse(ProtocolBridge.TranslateRequest(WireProtocol.OpenAi, WireProtocol.Anthropic, openAiBody))!.AsObject();

    private static int Breakpoints(JsonNode node) => node switch
    {
        JsonObject obj => (obj.ContainsKey("cache_control") ? 1 : 0) + obj.Sum(p => p.Value is null ? 0 : Breakpoints(p.Value)),
        JsonArray array => array.Sum(item => item is null ? 0 : Breakpoints(item)),
        _ => 0
    };

    [Fact]
    public void AConversationGetsBreakpointsOnToolsSystemAndItsLatestTurn()
    {
        var request = Translate("""
            {"model":"claude-sonnet-5","messages":[
              {"role":"system","content":"You are a coding agent."},
              {"role":"user","content":"Fix the bug."},
              {"role":"assistant","content":"Looking."},
              {"role":"user","content":"Go on."}],
             "tools":[{"type":"function","function":{"name":"read","parameters":{"type":"object"}}},
                      {"type":"function","function":{"name":"edit","parameters":{"type":"object"}}}]}
            """);

        Assert.Equal("ephemeral", request["tools"]![1]!["cache_control"]!["type"]!.GetValue<string>());
        Assert.Null(request["tools"]![0]!["cache_control"]);
        Assert.Equal("ephemeral", request["system"]![0]!["cache_control"]!["type"]!.GetValue<string>());
        Assert.Equal("You are a coding agent.", request["system"]![0]!["text"]!.GetValue<string>());
        var last = request["messages"]!.AsArray()[^1]!["content"]!.AsArray()[^1]!;
        Assert.Equal("ephemeral", last["cache_control"]!["type"]!.GetValue<string>());
        // The previous user turn carries the fourth, so a long tool loop still finds it.
        var previous = request["messages"]!.AsArray()[0]!["content"]!.AsArray()[^1]!;
        Assert.Equal("ephemeral", previous["cache_control"]!["type"]!.GetValue<string>());
        // Anthropic allows four; all four are used.
        Assert.Equal(4, Breakpoints(request));
    }

    [Fact]
    public void ALongToolLoopKeepsABreakpointOnTheTurnBeforeIt()
    {
        var calls = string.Join(",", Enumerable.Range(0, 15).Select(i => $$$"""
            {"role":"assistant","tool_calls":[{"id":"c{{{i}}}","type":"function","function":{"name":"read","arguments":"{}"}}]},
            {"role":"tool","tool_call_id":"c{{{i}}}","content":"result {{{i}}}"}
            """));
        var request = Translate($$"""
            {"model":"claude-sonnet-5","messages":[
              {"role":"user","content":"Earlier ask."},
              {"role":"assistant","content":"Done."},
              {"role":"user","content":"Now refactor."},
              {{calls}}]}
            """);

        var messages = request["messages"]!.AsArray();
        Assert.NotNull(messages[^1]!["content"]!.AsArray()[^1]!["cache_control"]);
        // The second-to-last tool result is a user turn too; it carries the fourth mark.
        var marked = messages.Where(m => m!["content"] is JsonArray blocks && blocks.Any(b => b?["cache_control"] is not null)).ToArray();
        Assert.Equal(2, marked.Length);
        Assert.All(marked, m => Assert.Equal("user", m!["role"]!.GetValue<string>()));
        Assert.True(Breakpoints(request) <= 4);
    }

    [Fact]
    public void AToolResultTurnCachesThroughTheToolResult()
    {
        var request = Translate("""
            {"model":"claude-sonnet-5","messages":[
              {"role":"user","content":"Read it."},
              {"role":"assistant","tool_calls":[{"id":"c1","type":"function","function":{"name":"read","arguments":"{}"}}]},
              {"role":"tool","tool_call_id":"c1","content":"file contents"}]}
            """);

        var block = request["messages"]!.AsArray()[^1]!["content"]!.AsArray()[^1]!;
        Assert.Equal("tool_result", block["type"]!.GetValue<string>());
        Assert.NotNull(block["cache_control"]);
    }

    [Fact]
    public void AOneOffQuestionWithoutToolsIsNotMarked()
    {
        // Nothing would be reused, and writing the cache costs a quarter more.
        var request = Translate("""{"model":"claude-sonnet-5","messages":[{"role":"user","content":"Hi"}]}""");

        Assert.Equal(0, Breakpoints(request));
    }

    [Fact]
    public void TranslatedUsageCountsCachedTokensTheOpenAiWay()
    {
        var body = ProtocolBridge.TranslateResponse(WireProtocol.Anthropic, WireProtocol.OpenAi, """
            {"id":"m1","type":"message","role":"assistant","model":"claude-sonnet-5","content":[{"type":"text","text":"ok"}],
             "stop_reason":"end_turn","usage":{"input_tokens":50,"output_tokens":10,"cache_read_input_tokens":900,"cache_creation_input_tokens":40}}
            """);
        var usage = JsonNode.Parse(body)!["usage"]!;

        Assert.Equal(990, usage["prompt_tokens"]!.GetValue<long>());
        Assert.Equal(900, usage["prompt_tokens_details"]!["cached_tokens"]!.GetValue<long>());

        // And the relay's own parser reads it back without double counting.
        var parsed = RelayUsageParser.Parse(body)!;
        Assert.Equal(50, parsed.InputTokens);
        Assert.Equal(900, parsed.CacheReadInputTokens);
        Assert.Equal(40, parsed.CacheCreationInputTokens);
    }

    [Fact]
    public void CachedTokensArePricedAtTheCacheRate()
    {
        var provider = new ProviderSettings("z", "Zen", ProviderKinds.OpenAi, "https://opencode.ai/zen/v1", "k", true, 0);
        var previous = LivePricing.Current;
        try
        {
            LivePricing.Use(LivePricing.Parse("""
                {"opencode":{"api":"https://opencode.ai/zen/v1","models":{"m":{"cost":{"input":10,"output":20,"cache_read":1,"cache_write":12.5}}}}}
                """, DateTimeOffset.UtcNow));

            // 100k uncached at $10, 1M cache read at $1, 10k cache write at $12.50 → $1 + $1 + $0.125
            var cost = ModelPricingTable.Estimate(provider, "m", 100_000, 0, 1_000_000, 10_000);

            Assert.Equal(2.125m, cost);
        }
        finally { LivePricing.Use(previous); }
    }
}
