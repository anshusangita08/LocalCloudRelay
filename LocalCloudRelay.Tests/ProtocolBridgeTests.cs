using System.Net;
using System.Text;
using LocalCloudRelay;

namespace LocalCloudRelay.Tests;

/// <summary>
/// One base URL and one key have to reach every model, whichever wire protocol the
/// upstream happens to speak. An OpenAI-only gateway has no /v1/messages, and an
/// Anthropic-only gateway has no /v1/chat/completions, so the relay cannot simply
/// forward the client's path when the two disagree.
/// </summary>
public sealed class ProtocolBridgeTests : IAsyncLifetime
{
    private const string LocalKey = "local-bridge-key";
    private readonly RelayTelemetryStore _telemetry = new();
    private HttpClient _client = null!;
    private RelayServer _server = null!;
    private string _base = string.Empty;
    private readonly List<string> _upstreamPaths = [];
    private readonly List<string> _upstreamBodies = [];

    /// <summary>An OpenAI-compatible gateway and nothing else: /v1/messages is a 404.</summary>
    private HttpResponseMessage OpenAiOnly(HttpRequestMessage request)
    {
        var path = request.RequestUri!.AbsolutePath;
        _upstreamPaths.Add(path);
        _upstreamBodies.Add(request.Content is null ? string.Empty : request.Content.ReadAsStringAsync().GetAwaiter().GetResult());

        if (path.EndsWith("/messages", StringComparison.Ordinal))
            return new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("no such route") };

        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                """
                {"id":"chatcmpl-1","object":"chat.completion","model":"gpt-5.6-luna",
                 "choices":[{"index":0,"message":{"role":"assistant","content":"Hello from an OpenAI gateway"},"finish_reason":"stop"}],
                 "usage":{"prompt_tokens":11,"completion_tokens":7,"total_tokens":18}}
                """, Encoding.UTF8, "application/json")
        };
    }

    /// <summary>An Anthropic-compatible gateway and nothing else.</summary>
    private HttpResponseMessage AnthropicOnly(HttpRequestMessage request)
    {
        var path = request.RequestUri!.AbsolutePath;
        _upstreamPaths.Add(path);
        _upstreamBodies.Add(request.Content is null ? string.Empty : request.Content.ReadAsStringAsync().GetAwaiter().GetResult());

        if (path.EndsWith("/chat/completions", StringComparison.Ordinal))
            return new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("no such route") };

        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                """
                {"id":"msg_1","type":"message","role":"assistant","model":"claude-sonnet-4-5",
                 "content":[{"type":"text","text":"Hello from an Anthropic gateway"}],
                 "stop_reason":"end_turn","usage":{"input_tokens":11,"output_tokens":7}}
                """, Encoding.UTF8, "application/json")
        };
    }

    public async Task InitializeAsync()
    {
        var upstream = new FakeUpstreamHandler(request =>
            request.RequestUri!.Host == "anthropic.example" ? AnthropicOnly(request) : OpenAiOnly(request));

        _server = new RelayServer(_telemetry, upstream, port: 0);
        _server.Apply(LocalKey, new ProviderRouter(
        [
            new ProviderSettings("oa", "OpenAI Gateway", ProviderKinds.OpenAi, "https://openai.example/v1", "oa-key", true, 0),
            new ProviderSettings("an", "Anthropic Gateway", ProviderKinds.Anthropic, "https://anthropic.example/v1", "an-key", true, 1)
        ], new CatalogSnapshot(DateTimeOffset.UtcNow, new Dictionary<string, IReadOnlyList<string>>
        {
            ["oa"] = ["gpt-5.6-luna"],
            ["an"] = ["claude-sonnet-4-5"]
        })));
        await _server.StartAsync();
        _base = $"http://127.0.0.1:{_server.Port}";
        _client = new HttpClient();
        _upstreamPaths.Clear();
        _upstreamBodies.Clear();
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _server.StopAsync();
        _server.Dispose();
    }

    private async Task<HttpResponseMessage> PostAsync(string path, string json)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, _base + path)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        request.Headers.TryAddWithoutValidation("x-api-key", LocalKey);
        return await _client.SendAsync(request);
    }

    private const string AnthropicRequest = """
    {"model":"gpt-5.6-luna","max_tokens":64,
     "system":"Be brief.",
     "messages":[{"role":"user","content":"Say hello"}]}
    """;

    private const string OpenAiRequest = """
    {"model":"claude-sonnet-4-5","max_tokens":64,
     "messages":[{"role":"system","content":"Be brief."},{"role":"user","content":"Say hello"}]}
    """;

    [Fact]
    public async Task AClaudeShapedClientReachesAnOpenAiOnlyGateway()
    {
        var response = await PostAsync("/v1/messages", AnthropicRequest);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();

        // The client asked in Anthropic shape and has to get Anthropic shape back,
        // whatever the upstream spoke.
        Assert.Contains("\"type\":\"message\"", body);
        Assert.Contains("Hello from an OpenAI gateway", body);
        Assert.Contains("\"stop_reason\"", body);

        // Verbatim is tried first, so the upstream sees the client's path once and the
        // translated one after. What matters is that the request that succeeded used the
        // shape the gateway actually implements.
        Assert.Equal("/v1/chat/completions", _upstreamPaths[^1]);
        // And the translated body is an OpenAI request, not the Anthropic one forwarded.
        Assert.Contains("\"role\":\"system\"", _upstreamBodies[^1]);
        Assert.DoesNotContain("\"system\":\"Be brief.\"", _upstreamBodies[^1]);
    }

    [Fact]
    public async Task TheTranslationIsRememberedSoLaterRequestsGoStraightThrough()
    {
        await PostAsync("/v1/messages", AnthropicRequest);
        var afterFirst = _upstreamPaths.Count;
        _upstreamPaths.Clear();

        await PostAsync("/v1/messages", AnthropicRequest);

        // Second time round there is no wasted 404: the provider is known to need the
        // other dialect, so translation happens up front and costs exactly one call.
        Assert.Single(_upstreamPaths);
        Assert.Equal("/v1/chat/completions", _upstreamPaths[0]);
        Assert.True(afterFirst >= 2, $"first request should have probed then translated, saw {afterFirst}");
    }

    [Fact]
    public async Task AnOpenAiShapedClientReachesAnAnthropicOnlyGateway()
    {
        var response = await PostAsync("/v1/chat/completions", OpenAiRequest);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();

        Assert.Contains("\"object\":\"chat.completion\"", body);
        Assert.Contains("Hello from an Anthropic gateway", body);
        // The upstream's own id is passed through rather than invented.
        Assert.Contains("\"id\":\"msg_1\"", body);
        Assert.Contains("\"finish_reason\":\"stop\"", body);
        Assert.Contains("\"prompt_tokens\":11", body);

        Assert.Equal("/v1/messages", _upstreamPaths[^1]);
        // The system turn is lifted out of the message list, which is where Anthropic
        // wants it and where OpenAI clients keep it.
        Assert.Contains("\"system\":\"Be brief.\"", _upstreamBodies[^1]);
    }

    [Fact]
    public async Task AMatchingProtocolIsForwardedUntouched()
    {
        // A gateway that already speaks the client's protocol must not be translated -
        // and must not even be probed, since both sides agree on the dialect.
        await PostAsync("/v1/chat/completions",
            """{"model":"gpt-5.6-luna","max_tokens":64,"messages":[{"role":"user","content":"Say hello"}]}""");

        Assert.Single(_upstreamPaths);
        Assert.Equal("/v1/chat/completions", _upstreamPaths[0]);
        // The body arrives byte-for-byte, which is the promise the relay makes.
        Assert.Contains("\"model\":\"gpt-5.6-luna\"", _upstreamBodies[0]);
        Assert.Contains("\"Say hello\"", _upstreamBodies[0]);
    }

    [Fact]
    public async Task ToolCallsSurviveTheCrossingInBothShapes()
    {
        // Tools are where the two dialects differ most: Anthropic splits the call into a
        // name plus a structured input object, OpenAI packs the arguments into a JSON
        // string. A bridge that drops them makes an agent loop impossible.
        var anthropic = """
        {"model":"gpt-5.6-luna","max_tokens":64,
         "messages":[{"role":"user","content":"weather?"}],
         "tools":[{"name":"get_weather","description":"Look it up",
                   "input_schema":{"type":"object","properties":{"city":{"type":"string"}},"required":["city"]}}],
         "tool_choice":{"type":"auto"}}
        """;

        await PostAsync("/v1/messages", anthropic);
        var sent = _upstreamBodies[^1];

        Assert.Contains("\"type\":\"function\"", sent);
        Assert.Contains("\"name\":\"get_weather\"", sent);
        // Anthropic's input_schema becomes OpenAI's function.parameters.
        Assert.Contains("\"parameters\"", sent);
        Assert.Contains("\"city\"", sent);
    }

    [Fact]
    public void StopAsStringBecomesStopSequencesArray()
    {
        const string openAiRequest = """{"model":"test","stop":"END","messages":[]}""";
        var translated = ProtocolBridge.TranslateRequest(WireProtocol.OpenAi, WireProtocol.Anthropic, openAiRequest);

        // OpenAI's stop string becomes Anthropic's stop_sequences array
        Assert.Contains("\"stop_sequences\":[\"END\"]", translated);
    }

    [Fact]
    public void MaxCompletionTokensFallsBackWhenMaxTokensAbsent()
    {
        const string openAiRequest = """{"model":"test","max_completion_tokens":128,"messages":[]}""";
        var translated = ProtocolBridge.TranslateRequest(WireProtocol.OpenAi, WireProtocol.Anthropic, openAiRequest);

        // max_completion_tokens becomes max_tokens in Anthropic
        Assert.Contains("\"max_tokens\":128", translated);
    }

    [Fact]
    public void AssistantMessageWithTextAndToolCallsStaysInOneMessage()
    {
        const string openAiRequest = """
        {"model":"test","messages":[
            {"role":"assistant","content":"Hello world",
             "tool_calls":[{"id":"call_1","type":"function",
                            "function":{"name":"test_func","arguments":"{\"a\":\"b\"}"}}]}
        ]}
        """;

        var translated = ProtocolBridge.TranslateRequest(WireProtocol.OpenAi, WireProtocol.Anthropic, openAiRequest);

        // The text and tool_use should both be in the content array of a single message
        Assert.Contains("\"type\":\"text\"", translated);
        Assert.Contains("Hello world", translated);
        Assert.Contains("\"type\":\"tool_use\"", translated);
        Assert.Contains("test_func", translated);
    }

    [Fact]
    public void EmptyChoicesArrayReturnsUnchanged()
    {
        const string response = """{"id":"test","choices":[],"usage":{"prompt_tokens":1,"completion_tokens":1}}""";
        var translated = ProtocolBridge.TranslateResponse(WireProtocol.OpenAi, WireProtocol.Anthropic, response);

        // Should return unchanged since there's no choice to translate
        Assert.Equal(response, translated);
    }

    [Fact]
    public void ToolCallArgumentsNullBecomesEmptyObject()
    {
        const string openAiRequest = """
        {"model":"test","messages":[
            {"role":"assistant","content":"","tool_calls":[
                {"id":"call_1","type":"function","function":{"name":"get_data","arguments":null}}
            ]}
        ]}
        """;

        var translated = ProtocolBridge.TranslateRequest(WireProtocol.OpenAi, WireProtocol.Anthropic, openAiRequest);

        // null arguments should become {} in Anthropic
        Assert.Contains("\"input\":{}", translated);
    }
}
