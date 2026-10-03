using System.Text;
using System.Text.Json.Nodes;
using LocalCloudRelay;

namespace LocalCloudRelay.Tests;

public sealed class StreamCompletionTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void InterleavedToolsKeepArgumentsInsideTheirOwnOpenBlock(bool hasFinishReason)
    {
        var translator = ProtocolStreamTranslator.For(WireProtocol.OpenAi, WireProtocol.Anthropic)!;
        var output = new List<string>();
        void Feed(string payload)
        {
            output.AddRange(translator.Transform("data: " + payload));
            output.AddRange(translator.Transform(""));
        }
        Feed("""{"choices":[{"delta":{"tool_calls":[{"index":0,"id":"call_","function":{"name":"get_","arguments":"{\"city\":"}},{"index":1,"id":"other_","function":{"name":"find_","arguments":"{\"count\":"}}]}}]}""");
        Feed("""{"choices":[{"delta":{"tool_calls":[{"index":0,"id":"1","function":{"name":"weather","arguments":"\"Paris\"}"}},{"index":1,"id":"2","function":{"name":"items","arguments":"2}"}}]}}]}""");
        if (hasFinishReason) Feed("""{"choices":[{"delta":{},"finish_reason":"tool_calls"}]}""");
        Feed("[DONE]");
        output.AddRange(translator.Finish());
        Assert.True(translator.IsTerminal);
        Assert.Empty(translator.Finish());

        int? open = null;
        var arguments = new Dictionary<int, string>();
        var names = new List<string>();
        var ids = new List<string>();
        foreach (var line in output.Where(line => line.StartsWith("data: {")))
        {
            var data = JsonNode.Parse(line[6..])!;
            switch (data["type"]!.GetValue<string>())
            {
                case "content_block_start":
                    Assert.Null(open);
                    open = data["index"]!.GetValue<int>();
                    arguments.Add(open.Value, "");
                    names.Add(data["content_block"]!["name"]!.GetValue<string>());
                    ids.Add(data["content_block"]!["id"]!.GetValue<string>());
                    break;
                case "content_block_delta":
                    Assert.Equal(open, data["index"]!.GetValue<int>());
                    arguments[open!.Value] += data["delta"]!["partial_json"]!.GetValue<string>();
                    break;
                case "content_block_stop":
                    Assert.Equal(open, data["index"]!.GetValue<int>());
                    open = null;
                    break;
            }
        }
        Assert.Null(open);
        Assert.Equal(["get_weather", "find_items"], names);
        Assert.Equal(["call_1", "other_2"], ids);
        Assert.Equal("Paris", JsonNode.Parse(arguments[0])!["city"]!.GetValue<string>());
        Assert.Equal(2, JsonNode.Parse(arguments[1])!["count"]!.GetValue<int>());
        Assert.Single(output, line => line == "event: message_stop");
    }

    [Theory]
    [InlineData(WireProtocol.OpenAi, WireProtocol.Anthropic, "data: [DONE]\n\n", "event: message_stop")]
    [InlineData(WireProtocol.Anthropic, WireProtocol.OpenAi, "data: {\"type\":\"message_stop\"}\n\n", "data: [DONE]")]
    [InlineData(WireProtocol.OpenAi, WireProtocol.Anthropic, "data: {\"error\":{\"message\":\"failed\"}}\n\n", "event: error")]
    [InlineData(WireProtocol.Anthropic, WireProtocol.OpenAi, "data: {\"type\":\"error\",\"error\":{\"message\":\"failed\"}}\n\n", "failed")]
    public async Task PumpReturnsAfterTerminalEventWithoutReadingAgain(
        WireProtocol from, WireProtocol to, string input, string expected)
    {
        using var source = new TerminalThenStallStream(input);
        using var destination = new MemoryStream();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var pump = RelaySseTranslator.PumpAsync(source, destination, from, to, cancellation.Token);
        var first = await Task.WhenAny(pump, source.ReadPastTerminal.Task);
        if (first != pump) cancellation.Cancel();
        await pump;
        Assert.False(source.ReadPastTerminal.Task.IsCompleted);
        Assert.Contains(expected, Encoding.UTF8.GetString(destination.ToArray()));
    }

    [Fact]
    public void FinishClosesACompletedChoiceWithoutDoneExactlyOnce()
    {
        var translator = ProtocolStreamTranslator.For(WireProtocol.OpenAi, WireProtocol.Anthropic)!;
        translator.Transform("""data: {"choices":[{"delta":{"content":"hello"},"finish_reason":"stop"}]}""");
        var output = translator.Finish();
        Assert.Contains("event: message_stop", output);
        Assert.Empty(translator.Finish());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PumpKeepsFinalUsageAfterFinishReason(bool hasDone)
    {
        var input = """data: {"choices":[{"delta":{"content":"hello"},"finish_reason":"stop"}]}""" + "\n\n"
            + """data: {"choices":[],"usage":{"prompt_tokens":19,"completion_tokens":7}}""" + "\n\n"
            + (hasDone ? "data: [DONE]\n\n" : "");
        using var source = new MemoryStream(Encoding.UTF8.GetBytes(input));
        using var destination = new MemoryStream();
        await RelaySseTranslator.PumpAsync(source, destination, WireProtocol.OpenAi, WireProtocol.Anthropic, CancellationToken.None);

        var events = Encoding.UTF8.GetString(destination.ToArray()).Split('\n')
            .Where(line => line.StartsWith("data: ")).Select(line => JsonNode.Parse(line[6..])!).ToList();
        var final = Assert.Single(events, e => e["type"]!.GetValue<string>() == "message_delta");
        Assert.Equal(19, final["usage"]!["input_tokens"]!.GetValue<int>());
        Assert.Equal(7, final["usage"]!["output_tokens"]!.GetValue<int>());
        Assert.Equal("message_stop", events[^1]["type"]!.GetValue<string>());
    }

    [Theory]
    [InlineData(WireProtocol.OpenAi, WireProtocol.Anthropic, "data: {\"choices\":[{\"delta\":{\"content\":\"partial\"}}]}\n\n")]
    [InlineData(WireProtocol.Anthropic, WireProtocol.OpenAi, "data: {\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"end_turn\"}}\n\n")]
    [InlineData(WireProtocol.OpenAi, WireProtocol.Anthropic, "")]
    public async Task PumpRejectsPrematureEof(WireProtocol from, WireProtocol to, string input)
    {
        using var source = new MemoryStream(Encoding.UTF8.GetBytes(input));
        using var destination = new MemoryStream();
        await Assert.ThrowsAsync<InvalidDataException>(() => RelaySseTranslator.PumpAsync(source, destination, from, to, CancellationToken.None));
        Assert.DoesNotContain("event: message_stop", Encoding.UTF8.GetString(destination.ToArray()));
    }

    [Fact]
    public void HeartbeatsSurviveTranslation()
    {
        var translator = ProtocolStreamTranslator.For(WireProtocol.Anthropic, WireProtocol.OpenAi)!;
        Assert.Contains(": keepalive", translator.Transform(": keepalive"));
        translator.Transform("data: {\"type\":\"ping\"}");
        Assert.Contains(": ping", translator.Transform(""));
    }

    private sealed class TerminalThenStallStream(string input) : MemoryStream(Encoding.UTF8.GetBytes(input))
    {
        public TaskCompletionSource ReadPastTerminal { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (Position < Length) return await base.ReadAsync(buffer, cancellationToken);
            ReadPastTerminal.TrySetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }
    }
}
