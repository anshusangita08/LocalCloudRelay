using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using LocalCloudRelay;

namespace LocalCloudRelay.Tests;

/// <summary>
/// Codex speaks the Responses API. The relay serves it through Chat Completions so it
/// reaches every provider and router, and the reply comes back as Responses.
/// </summary>
public sealed class ResponsesClientBridgeTests
{
    // Shaped like a Codex turn: instructions, a user message, a previous tool call and its
    // output, a function tool, a custom (freeform) tool and a hosted tool.
    private const string CodexRequest = """
        {"model":"local-model","instructions":"You are Codex.","store":false,"stream":true,
         "include":["reasoning.encrypted_content"],"prompt_cache_key":"abc","reasoning":{"effort":"medium"},
         "input":[
           {"type":"message","role":"developer","content":[{"type":"input_text","text":"Be careful."}]},
           {"type":"message","role":"user","content":[{"type":"input_text","text":"List the files."}]},
           {"type":"reasoning","id":"rs_1","summary":[],"encrypted_content":"xyz"},
           {"type":"function_call","call_id":"call_1","name":"shell","arguments":"{\"command\":[\"ls\"]}"},
           {"type":"function_call_output","call_id":"call_1","output":"a.txt\nb.txt"},
           {"type":"custom_tool_call","call_id":"call_2","name":"apply_patch","input":"*** Begin Patch"},
           {"type":"custom_tool_call_output","call_id":"call_2","output":"Done"}
         ],
         "tools":[
           {"type":"function","name":"shell","description":"Run a command","parameters":{"type":"object","properties":{"command":{"type":"array"}}},"strict":false},
           {"type":"custom","name":"apply_patch","description":"Edit files","format":{"type":"grammar","syntax":"lark","definition":"start: x"}},
           {"type":"web_search"}
         ],
         "tool_choice":"auto","parallel_tool_calls":false,"max_output_tokens":2000}
        """;

    [Fact]
    public void ACodexRequestBecomesAChatRequest()
    {
        var chat = JsonNode.Parse(ResponsesClientBridge.ToChat(CodexRequest, out var state))!;
        var messages = chat["messages"]!.AsArray();

        Assert.Equal("local-model", chat["model"]!.GetValue<string>());
        Assert.Equal("system", messages[0]!["role"]!.GetValue<string>());
        Assert.Equal("You are Codex.", messages[0]!["content"]!.GetValue<string>());
        Assert.Equal("system", messages[1]!["role"]!.GetValue<string>());            // developer -> system
        Assert.Equal("List the files.", messages[2]!["content"]![0]!["text"]!.GetValue<string>());
        Assert.Equal("call_1", messages[3]!["tool_calls"]![0]!["id"]!.GetValue<string>());
        Assert.Equal("tool", messages[4]!["role"]!.GetValue<string>());
        Assert.Equal("a.txt\nb.txt", messages[4]!["content"]!.GetValue<string>());
        // A custom call goes back as a function call with its input wrapped.
        Assert.Contains("*** Begin Patch", messages[5]!["tool_calls"]![0]!["function"]!["arguments"]!.GetValue<string>());

        var tools = chat["tools"]!.AsArray();
        Assert.Equal(2, tools.Count);                                                  // hosted web_search left out
        Assert.Equal("apply_patch", tools[1]!["function"]!["name"]!.GetValue<string>());
        Assert.Contains("start: x", tools[1]!["function"]!["description"]!.GetValue<string>());
        Assert.Contains("apply_patch", state.CustomTools);

        Assert.True(chat["stream"]!.GetValue<bool>());
        Assert.True(chat["stream_options"]!["include_usage"]!.GetValue<bool>());
        Assert.Equal(2000, chat["max_tokens"]!.GetValue<int>());
        Assert.Null(chat["include"]);
    }

    [Fact]
    public void ABufferedChatReplyBecomesAResponseObject()
    {
        ResponsesClientBridge.ToChat(CodexRequest, out var state);
        const string chat = """
            {"id":"c1","object":"chat.completion","model":"kimi-k3","choices":[{"index":0,"finish_reason":"tool_calls",
              "message":{"role":"assistant","content":"Running it.","tool_calls":[
                {"id":"call_9","type":"function","function":{"name":"shell","arguments":"{\"command\":[\"pwd\"]}"}},
                {"id":"call_10","type":"function","function":{"name":"apply_patch","arguments":"{\"input\":\"*** Begin Patch\\n*** End Patch\"}"}}]}}],
             "usage":{"prompt_tokens":120,"completion_tokens":30,"total_tokens":150,"prompt_tokens_details":{"cached_tokens":100}}}
            """;

        var response = JsonNode.Parse(ResponsesClientBridge.FromChat(chat, state))!;
        var output = response["output"]!.AsArray();

        Assert.Equal("response", response["object"]!.GetValue<string>());
        Assert.Equal("completed", response["status"]!.GetValue<string>());
        Assert.Equal("Running it.", output[0]!["content"]![0]!["text"]!.GetValue<string>());
        Assert.Equal("function_call", output[1]!["type"]!.GetValue<string>());
        Assert.Equal("call_9", output[1]!["call_id"]!.GetValue<string>());
        Assert.Equal("custom_tool_call", output[2]!["type"]!.GetValue<string>());
        Assert.Equal("*** Begin Patch\n*** End Patch", output[2]!["input"]!.GetValue<string>());
        Assert.Equal(120, response["usage"]!["input_tokens"]!.GetValue<long>());
        Assert.Equal(100, response["usage"]!["input_tokens_details"]!["cached_tokens"]!.GetValue<long>());
    }

    private static List<(string Type, JsonObject Data)> Events(string sse) =>
        sse.Split("\n\n", StringSplitOptions.RemoveEmptyEntries)
            .Select(block => block.Split('\n'))
            .Select(lines => (lines[0][7..], JsonNode.Parse(lines[1][6..])!.AsObject()))
            .ToList();

    [Fact]
    public void AChatStreamBecomesAResponsesEventStream()
    {
        ResponsesClientBridge.ToChat(CodexRequest, out var state);
        var translator = new ResponsesClientBridge.StreamTranslator(state);
        var sse = new StringBuilder();
        foreach (var line in new[]
        {
            """data: {"model":"kimi-k3","choices":[{"index":0,"delta":{"role":"assistant"}}]}""",
            """data: {"choices":[{"index":0,"delta":{"content":"Hel"}}]}""",
            """data: {"choices":[{"index":0,"delta":{"content":"lo"}}]}""",
            """data: {"choices":[{"index":0,"delta":{"tool_calls":[{"index":0,"id":"call_1","type":"function","function":{"name":"shell","arguments":""}}]}}]}""",
            """data: {"choices":[{"index":0,"delta":{"tool_calls":[{"index":0,"function":{"arguments":"{\"command\":"}}]}}]}""",
            """data: {"choices":[{"index":0,"delta":{"tool_calls":[{"index":0,"function":{"arguments":"[\"ls\"]}"}}]}}]}""",
            """data: {"choices":[{"index":0,"delta":{},"finish_reason":"tool_calls"}]}""",
            """data: {"choices":[],"usage":{"prompt_tokens":50,"completion_tokens":7,"total_tokens":57}}""",
            "data: [DONE]"
        })
            sse.Append(translator.Feed(line));

        var events = Events(sse.ToString());
        var types = events.Select(e => e.Type).ToList();
        Assert.Equal("response.created", types[0]);
        Assert.Contains("response.output_text.delta", types);
        Assert.Contains("response.function_call_arguments.delta", types);
        Assert.Equal("response.completed", types[^1]);
        Assert.Equal(Enumerable.Range(0, events.Count), events.Select(e => e.Data["sequence_number"]!.GetValue<int>()));

        var completed = events[^1].Data["response"]!;
        Assert.Equal("Hello", completed["output"]![0]!["content"]![0]!["text"]!.GetValue<string>());
        Assert.Equal("""{"command":["ls"]}""", completed["output"]![1]!["arguments"]!.GetValue<string>());
        Assert.Equal(50, completed["usage"]!["input_tokens"]!.GetValue<long>());
        Assert.True(translator.Finished);
    }

    [Fact]
    public void AStreamThatEndsWithoutDoneIsStillClosed()
    {
        ResponsesClientBridge.ToChat("""{"model":"m","input":"hi","stream":true}""", out var state);
        var translator = new ResponsesClientBridge.StreamTranslator(state);
        translator.Feed("""data: {"choices":[{"index":0,"delta":{"content":"ok"}}]}""");
        var tail = Events(translator.Complete());
        Assert.Equal("response.completed", tail[^1].Type);
    }

    [Fact]
    public async Task CodexReachesAChatOnlyProviderThroughTheRelay()
    {
        string? upstreamPath = null, upstreamBody = null;
        var streaming = false;
        var upstream = new FakeUpstreamHandler(request =>
        {
            upstreamPath = request.RequestUri!.AbsolutePath;
            upstreamBody = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            if (streaming)
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        "data: {\"model\":\"kimi-k3\",\"choices\":[{\"index\":0,\"delta\":{\"content\":\"pong\"}}]}\n\n" +
                        "data: {\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"stop\"}]}\n\n" +
                        "data: {\"choices\":[],\"usage\":{\"prompt_tokens\":9,\"completion_tokens\":1}}\n\n" +
                        "data: [DONE]\n\n", Encoding.UTF8, "text/event-stream")
                };
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"id":"c","model":"kimi-k3","choices":[{"index":0,"message":{"role":"assistant","content":"pong"},"finish_reason":"stop"}],"usage":{"prompt_tokens":9,"completion_tokens":1}}""",
                    Encoding.UTF8, "application/json")
            };
        });
        using var server = new RelayServer(new RelayTelemetryStore(), upstream, port: 0);
        server.Apply("key", new ProviderRouter(
            [new ProviderSettings("zen", "Zen", ProviderKinds.OpenAi, "https://zen.example/v1", "k", true, 0)],
            new CatalogSnapshot(DateTimeOffset.UtcNow, new Dictionary<string, IReadOnlyList<string>> { ["zen"] = ["kimi-k3"] }),
            [RouterRule.Create("code", RouterStrategies.Sticky, ["kimi-k3"])]));
        await server.StartAsync();
        try
        {
            using var client = new HttpClient();
            async Task<(HttpStatusCode Status, string Body)> Ask(bool stream)
            {
                streaming = stream;
                using var request = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{server.Port}/v1/responses")
                {
                    Content = new StringContent($$"""{"model":"code","input":"ping","stream":{{(stream ? "true" : "false")}}}""", Encoding.UTF8, "application/json")
                };
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "key");
                using var response = await client.SendAsync(request);
                return (response.StatusCode, await response.Content.ReadAsStringAsync());
            }

            var (status, body) = await Ask(stream: false);
            Assert.Equal(HttpStatusCode.OK, status);
            Assert.Equal("/v1/chat/completions", upstreamPath);
            Assert.Contains("\"model\":\"kimi-k3\"", upstreamBody);
            var response = JsonNode.Parse(body)!;
            Assert.Equal("response", response["object"]!.GetValue<string>());
            Assert.Equal("pong", response["output"]![0]!["content"]![0]!["text"]!.GetValue<string>());

            var (streamStatus, streamed) = await Ask(stream: true);
            Assert.Equal(HttpStatusCode.OK, streamStatus);
            var events = Events(streamed);
            Assert.Equal("response.created", events[0].Type);
            Assert.Equal("response.completed", events[^1].Type);
            Assert.Equal(9, events[^1].Data["response"]!["usage"]!["input_tokens"]!.GetValue<long>());
        }
        finally
        {
            await server.StopAsync();
        }
    }
}
