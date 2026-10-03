using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using LocalCloudRelay;

namespace LocalCloudRelay.Tests;

/// <summary>
/// Streaming is the default for Claude Code and OpenCode, so a bridge that only handled
/// complete bodies would not work for the clients this exists for. These cover both
/// directions, at the translator and then through the relay.
/// </summary>
public sealed class ProtocolStreamTests
{
    private static List<string> Feed(WireProtocol from, WireProtocol to, params string[] lines)
    {
        var translator = ProtocolStreamTranslator.For(from, to)!;
        var output = new List<string>();
        foreach (var line in lines) output.AddRange(translator.Transform(line));
        output.AddRange(translator.Finish());
        return output;
    }

    /// <summary>The event names in an Anthropic-shaped stream, in order.</summary>
    private static List<string> EventNames(IEnumerable<string> lines) =>
        lines.Where(l => l.StartsWith("event:", StringComparison.Ordinal))
             .Select(l => l["event:".Length..].Trim())
             .ToList();

    private static string Data(IEnumerable<string> lines, string eventName) =>
        lines.SkipWhile(l => l != $"event: {eventName}").Skip(1).FirstOrDefault() ?? string.Empty;

    [Fact]
    public void AnOpenAiStreamBecomesAnAnthropicStream()
    {
        var output = Feed(WireProtocol.OpenAi, WireProtocol.Anthropic,
            """data: {"id":"c1","model":"gpt-5.6-luna","choices":[{"index":0,"delta":{"role":"assistant"},"finish_reason":null}]}""",
            "",
            """data: {"id":"c1","choices":[{"index":0,"delta":{"content":"Hel"},"finish_reason":null}]}""",
            "",
            """data: {"id":"c1","choices":[{"index":0,"delta":{"content":"lo"},"finish_reason":null}]}""",
            "",
            """data: {"id":"c1","choices":[{"index":0,"delta":{},"finish_reason":"stop"}],"usage":{"completion_tokens":2}}""",
            "",
            "data: [DONE]",
            "");

        var names = EventNames(output);

        // The Anthropic protocol requires this order, and a client that gets it wrong
        // will not render the message at all.
        Assert.Equal(["message_start", "content_block_start", "content_block_delta", "content_block_delta",
            "content_block_stop", "message_delta", "message_stop"], names);

        Assert.Contains("\"text\":\"Hel\"", Data(output, "content_block_delta"));
        Assert.Contains("\"type\":\"text\"", Data(output, "content_block_start"));
        // stop_reason has to be Anthropic's vocabulary, not OpenAI's "stop".
        Assert.Contains("\"stop_reason\":\"end_turn\"", Data(output, "message_delta"));
    }

    [Fact]
    public void AnOpenAiToolCallBecomesAnAnthropicToolUseBlock()
    {
        var output = Feed(WireProtocol.OpenAi, WireProtocol.Anthropic,
            """data: {"id":"c1","choices":[{"index":0,"delta":{"tool_calls":[{"index":0,"id":"call_1","function":{"name":"get_weather","arguments":""}}]},"finish_reason":null}]}""",
            "",
            """data: {"id":"c1","choices":[{"index":0,"delta":{"tool_calls":[{"index":0,"function":{"arguments":"{\"city\":"}}]},"finish_reason":null}]}""",
            "",
            """data: {"id":"c1","choices":[{"index":0,"delta":{},"finish_reason":"tool_calls"}]}""",
            "",
            "data: [DONE]",
            "");

        var start = Data(output, "content_block_start");
        Assert.Contains("\"type\":\"tool_use\"", start);
        Assert.Contains("\"name\":\"get_weather\"", start);
        Assert.Contains("\"id\":\"call_1\"", start);

        // Arguments arrive as fragments and must be relayed as partial_json.
        Assert.Contains("input_json_delta", string.Join("\n", output));
        Assert.Contains("\"stop_reason\":\"tool_use\"", Data(output, "message_delta"));
    }

    [Fact]
    public void AnAnthropicStreamBecomesAnOpenAiStream()
    {
        var output = Feed(WireProtocol.Anthropic, WireProtocol.OpenAi,
            """event: message_start""",
            """data: {"type":"message_start","message":{"id":"msg_1","model":"claude-sonnet-4-5"}}""",
            "",
            """event: content_block_start""",
            """data: {"type":"content_block_start","index":0,"content_block":{"type":"text","text":""}}""",
            "",
            """event: content_block_delta""",
            """data: {"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"Hi"}}""",
            "",
            """event: content_block_stop""",
            """data: {"type":"content_block_stop","index":0}""",
            "",
            """event: message_delta""",
            """data: {"type":"message_delta","delta":{"stop_reason":"end_turn"}}""",
            "",
            """event: message_stop""",
            """data: {"type":"message_stop"}""",
            "");

        var payloads = output.Where(l => l.StartsWith("data: ", StringComparison.Ordinal)).ToList();
        var chunks = payloads.Where(p => p.StartsWith("data: {", StringComparison.Ordinal)).ToList();

        // Every chunk is a chat.completion.chunk; the [DONE] sentinel is the one
        // non-object data line, and Anthropic's event names must not leak into it.
        Assert.NotEmpty(chunks);
        Assert.All(chunks, p => Assert.Contains("\"object\":\"chat.completion.chunk\"", p));
        Assert.Contains(chunks, p => p.Contains("\"content\":\"Hi\""));
        Assert.Contains(chunks, p => p.Contains("\"finish_reason\":\"stop\""));
        Assert.Equal("data: [DONE]", output[^2]);
        Assert.Equal(string.Empty, output[^1]);
        // OpenAI's dialect has no content_block_* lines at all.
        Assert.DoesNotContain(output, l => l.StartsWith("event:", StringComparison.Ordinal));
    }

    [Fact]
    public void AMatchingProtocolYieldsNoTranslatorAtAll()
    {
        Assert.Null(ProtocolStreamTranslator.For(WireProtocol.Anthropic, WireProtocol.Anthropic));
        Assert.Null(ProtocolStreamTranslator.For(WireProtocol.OpenAi, WireProtocol.OpenAi));
        // And nothing is claimed for a shape the relay does not bridge, such as Gemini.
        Assert.Null(ProtocolStreamTranslator.For(WireProtocol.Other, WireProtocol.OpenAi));
    }

    [Fact]
    public void AnEmptyStreamStillClosesProperly()
    {
        // An upstream that closes without sending anything must not leave the client
        // waiting for a message_stop that never comes.
        var output = Feed(WireProtocol.OpenAi, WireProtocol.Anthropic, "data: [DONE]", "");

        var names = EventNames(output);
        Assert.Contains("message_start", names);
        Assert.Equal("message_stop", names[^1]);
    }

    [Fact]
    public void OpenAiChunksHaveBlankLineSeparators()
    {
        // SSE format requires blank lines between events. When Anthropic->OpenAI chunks
        // are emitted, each must be followed by a blank line. Test by joining with \n,
        // splitting on \n\n, and verifying each non-empty chunk is valid JSON or [DONE].
        var output = Feed(WireProtocol.Anthropic, WireProtocol.OpenAi,
            """event: message_start""",
            """data: {"type":"message_start","message":{"id":"msg_1","model":"claude-sonnet-4-5"}}""",
            "",
            """event: content_block_delta""",
            """data: {"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"Hi"}}""",
            "",
            """event: message_stop""",
            """data: {"type":"message_stop"}""",
            "");

        var joined = string.Join("\n", output);
        var doubleLf = new[] { "\n\n" };
        var events = joined.Split(doubleLf, StringSplitOptions.None);

        // Should have at least role chunk, content chunk, finish chunk, and [DONE]
        var nonEmpty = events.Where(e => !string.IsNullOrWhiteSpace(e)).ToList();
        Assert.NotEmpty(nonEmpty);

        foreach (var evt in nonEmpty)
        {
            var lines = evt.Trim().Split('\n');
            // Each event should have "data: ..." as the only line (OpenAI has no event names)
            Assert.True(lines.All(l => l.StartsWith("data:", StringComparison.Ordinal)),
                $"Event contains non-data line: {evt}");

            var dataLine = lines[0];
            var json = dataLine["data: ".Length..].Trim();
            if (!json.Equals("[DONE]", StringComparison.OrdinalIgnoreCase))
            {
                // Should be valid JSON
                var parsed = JsonNode.Parse(json);
                Assert.NotNull(parsed);
            }
        }
    }

    [Fact]
    public void OpenAiErrorsTranslateToAnthropicErrorEvents()
    {
        // OpenAI error chunk in streaming response; translate to Anthropic error event
        var output = Feed(WireProtocol.OpenAi, WireProtocol.Anthropic,
            """data: {"error":{"message":"Request failed","type":"api_error"}}""",
            "");

        Assert.Contains(output, l => l.StartsWith("event: error", StringComparison.Ordinal));
        var errorData = output.FirstOrDefault(l => l.StartsWith("data: {", StringComparison.Ordinal) && l.Contains("api_error"));
        Assert.NotNull(errorData);
        Assert.Contains("\"type\":\"error\"", errorData);
        Assert.Contains("Request failed", errorData);
    }

    [Fact]
    public void AnthropicErrorsTranslateToOpenAiErrorFormat()
    {
        // Anthropic error event; translate to OpenAI error format
        var output = Feed(WireProtocol.Anthropic, WireProtocol.OpenAi,
            """event: error""",
            """data: {"type":"error","error":{"type":"api_error","message":"Service unavailable"}}""",
            "");

        var errorLine = output.FirstOrDefault(l => l.StartsWith("data: {", StringComparison.Ordinal));
        Assert.NotNull(errorLine);
        Assert.Contains("\"error\":", errorLine);
        Assert.Contains("\"type\":\"api_error\"", errorLine);
        Assert.Contains("Service unavailable", errorLine);
        // Should have blank line separator after error
        var errorIndex = output.IndexOf(errorLine);
        Assert.True(errorIndex >= 0 && errorIndex + 1 < output.Count && output[errorIndex + 1] == string.Empty,
            "Error event should be followed by blank line");
    }
}

/// <summary>
/// Streaming end to end: a streaming Claude-shaped client against an OpenAI-only gateway.
/// </summary>
public sealed class StreamingBridgeEndToEndTests : IAsyncLifetime
{
    private const string LocalKey = "local-stream-key";
    private readonly RelayTelemetryStore _telemetry = new();
    private HttpClient _client = null!;
    private RelayServer _server = null!;
    private string _base = string.Empty;

    public async Task InitializeAsync()
    {
        var upstream = new FakeUpstreamHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/messages", StringComparison.Ordinal))
                return new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("no route") };

            // OpenAI-shaped SSE, which is what an OpenAI-only gateway streams.
            var sse = string.Join("\n",
                """data: {"id":"c1","model":"gpt-5.6-luna","choices":[{"index":0,"delta":{"role":"assistant"},"finish_reason":null}]}""",
                "",
                """data: {"id":"c1","choices":[{"index":0,"delta":{"content":"streamed"},"finish_reason":null}]}""",
                "",
                """data: {"id":"c1","choices":[{"index":0,"delta":{},"finish_reason":"stop"}],"usage":{"prompt_tokens":5,"completion_tokens":2}}""",
                "",
                "data: [DONE]",
                "");
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(sse, Encoding.UTF8, "text/event-stream")
            };
        });

        _server = new RelayServer(_telemetry, upstream, port: 0);
        _server.Apply(LocalKey, new ProviderRouter(
            [new ProviderSettings("oa", "OpenAI Gateway", ProviderKinds.OpenAi, "https://openai.example/v1", "k", true, 0)],
            new CatalogSnapshot(DateTimeOffset.UtcNow, new Dictionary<string, IReadOnlyList<string>> { ["oa"] = ["gpt-5.6-luna"] })));
        await _server.StartAsync();
        _base = $"http://127.0.0.1:{_server.Port}";
        _client = new HttpClient();
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _server.StopAsync();
        _server.Dispose();
    }

    [Fact]
    public async Task AStreamingClaudeClientGetsAnthropicEventsFromAnOpenAiGateway()
    {
        var request = new HttpRequestMessage(HttpMethod.Post, _base + "/v1/messages")
        {
            Content = new StringContent(
                """{"model":"gpt-5.6-luna","max_tokens":32,"stream":true,"messages":[{"role":"user","content":"hi"}]}""",
                Encoding.UTF8, "application/json")
        };
        request.Headers.TryAddWithoutValidation("x-api-key", LocalKey);

        var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();

        // Anthropic event names, not OpenAI chunks - this is what the client parses.
        Assert.Contains("event: message_start", body);
        Assert.Contains("event: content_block_delta", body);
        Assert.Contains("event: message_stop", body);
        Assert.Contains("streamed", body);
        Assert.DoesNotContain("chat.completion.chunk", body);
    }

    [Fact]
    public async Task AStreamedRequestIsStillRecordedAgainstItsModel()
    {
        // The bug this pins: response headers were written after a streamed body had
        // already started, which throws "Headers are read-only, response has already
        // started". Every streamed request - and Claude Code streams all of them - fell
        // into the error path and was recorded with no model, so streamed traffic never
        // reached the per-model view. The client still got its stream, which is what made
        // it invisible.
        var request = new HttpRequestMessage(HttpMethod.Post, _base + "/v1/messages")
        {
            Content = new StringContent(
                """{"model":"gpt-5.6-luna","max_tokens":32,"stream":true,"messages":[{"role":"user","content":"hi"}]}""",
                Encoding.UTF8, "application/json")
        };
        request.Headers.TryAddWithoutValidation("x-api-key", LocalKey);
        var response = await _client.SendAsync(request);
        await response.Content.ReadAsStringAsync();

        var report = _telemetry.GetReport();

        Assert.Contains(report.Models, m => m.Key.Equals("gpt-5.6-luna", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(report.Models, m => m.Key.Equals("unknown", StringComparison.OrdinalIgnoreCase));
        // And the usage that arrived in the stream is kept, not just the model name.
        Assert.Contains(report.Models, m => m.InputTokens == 5 && m.OutputTokens == 2);
    }
}
