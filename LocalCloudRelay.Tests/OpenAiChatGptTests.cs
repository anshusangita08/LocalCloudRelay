using System.Text.Json.Nodes;
using System.Text.Json;
using LocalCloudRelay;

namespace LocalCloudRelay.Tests;

public sealed class OpenAiChatGptTests
{
    [Fact]
    public async Task CreateAuthorizationAttemptUsesDynamicRegistrationAndStableHostIdentity()
    {
        await using var attempt = OpenAiChatGptOAuth.CreateAuthorizationAttempt(
            registeredClientId: null,
            hostId: "urn:uuid:install-host");
        var query = ParseQuery(attempt.AuthorizationUri.Query);

        Assert.Equal("dynamic_agent_client", query["client_id"]);
        Assert.Equal("Local Cloud Relay", query["agent_name_hint"]);
        Assert.Equal("urn:uuid:install-host", query["ext_agent_host_id"]);
        Assert.Equal("https://api.openai.com/v1", query["resource"]);
        Assert.Contains("chatgpt.tokens.use.direct", query["scope"]);
        Assert.Contains("resource.invoke", query["scope"]);
        Assert.Equal("S256", query["code_challenge_method"]);
        Assert.NotEmpty(query["nonce"]);
    }

    [Fact]
    public async Task CreateAuthorizationAttemptReusesExistingRegistrationAndIdentityHints()
    {
        await using var attempt = OpenAiChatGptOAuth.CreateAuthorizationAttempt(
            "issued-client", "urn:uuid:install-host", "prior-id-token", "user@example.com");
        var query = ParseQuery(attempt.AuthorizationUri.Query);

        Assert.Equal("issued-client", query["client_id"]);
        Assert.Equal("prior-id-token", query["id_token_hint"]);
        Assert.Equal("user@example.com", query["login_hint"]);
        Assert.False(query.ContainsKey("agent_name_hint"));
    }

    [Fact]
    public void ParseEligibleModelIdsReturnsOnlyVisibleAccountSlugs()
    {
        const string json = """
        {"models":[
          {"slug":"gpt-5.6","display_name":"GPT 5.6","visibility":"list"},
          {"slug":"internal-preview","visibility":"hidden"},
          {"slug":"no-visibility"},
          {"display_name":"Missing slug","visibility":"list"}
        ]}
        """;

        var models = OpenAiChatGptModelClient.ParseEligibleModelIds(json);

        Assert.Equal(["gpt-5.6"], models);
    }

    [Fact]
    public void BuildRequestMapsChatAndToolsToStatelessStreamingResponsesShape()
    {
        const string body = """
        {"model":"client-alias","stream":false,"messages":[
          {"role":"system","content":"Be concise"},
          {"role":"user","content":"hello"}
        ],"tools":[{"type":"function","function":{"name":"lookup","description":"find it","parameters":{"type":"object","properties":{"id":{"type":"string"}}}}}]}
        """;

        using var request = JsonDocument.Parse(OpenAiResponsesProtocol.BuildRequest(body, WireProtocol.OpenAi, "gpt-5.6"));
        var root = request.RootElement;

        Assert.Equal("gpt-5.6", root.GetProperty("model").GetString());
        Assert.False(root.GetProperty("store").GetBoolean());
        Assert.True(root.GetProperty("stream").GetBoolean());
        Assert.Equal("Be concise", root.GetProperty("instructions").GetString());
        Assert.Equal("hello", root.GetProperty("input")[0].GetProperty("content")[0].GetProperty("text").GetString());
        Assert.Equal("function", root.GetProperty("tools")[0].GetProperty("type").GetString());
        Assert.Equal("lookup", root.GetProperty("tools")[0].GetProperty("name").GetString());
    }

    [Fact]
    public void BuildRequestMapsAnthropicToolTurnsToResponsesFunctionCalls()
    {
        const string body = """
        {"model":"alias","max_tokens":1000,"system":"Do work","messages":[
          {"role":"assistant","content":[{"type":"tool_use","id":"call-1","name":"lookup","input":{"id":"7"}}]},
          {"role":"user","content":[{"type":"tool_result","tool_use_id":"call-1","content":"found"}]}
        ],"tools":[{"name":"lookup","input_schema":{"type":"object"}}]}
        """;

        using var request = JsonDocument.Parse(OpenAiResponsesProtocol.BuildRequest(body, WireProtocol.Anthropic, "gpt-5.6"));
        var root = request.RootElement;

        Assert.Equal("Do work", root.GetProperty("instructions").GetString());
        Assert.Equal("function_call", root.GetProperty("input")[0].GetProperty("type").GetString());
        Assert.Equal("function_call_output", root.GetProperty("input")[1].GetProperty("type").GetString());
        Assert.Equal("call-1", root.GetProperty("input")[1].GetProperty("call_id").GetString());
        Assert.Equal(1000, root.GetProperty("max_output_tokens").GetInt32());
        Assert.False(root.TryGetProperty("max_tokens", out _));
    }

    [Theory]
    [InlineData("max_tokens")]
    [InlineData("max_completion_tokens")]
    public void BuildRequestMapsChatTokenLimitToResponsesOutputLimit(string limitName)
    {
        var body = $$"""
        {"{{limitName}}":123,"temperature":0.4,"top_p":0.8,"parallel_tool_calls":false,"messages":[{"role":"user","content":"hello"}]}
        """;

        using var request = JsonDocument.Parse(OpenAiResponsesProtocol.BuildRequest(body, WireProtocol.OpenAi, "gpt-5.6"));
        var root = request.RootElement;

        Assert.Equal(123, root.GetProperty("max_output_tokens").GetInt32());
        Assert.Equal(0.4, root.GetProperty("temperature").GetDouble());
        Assert.Equal(0.8, root.GetProperty("top_p").GetDouble());
        Assert.False(root.GetProperty("parallel_tool_calls").GetBoolean());
        Assert.False(root.TryGetProperty("max_tokens", out _));
        Assert.False(root.TryGetProperty("max_completion_tokens", out _));
    }

    [Fact]
    public void BuildRequestIgnoresControlsTheResponsesApiHasNoUseFor()
    {
        // Claude Code and OpenCode send fields such as metadata, stream_options and
        // presence_penalty; refusing them made every turn fail on a ChatGPT model.
        const string chat = """{"messages":[{"role":"user","content":"hello"}],"presence_penalty":0.4,"stream_options":{"include_usage":true},"user":"u"}""";
        const string anthropic = """{"messages":[{"role":"user","content":"hello"}],"stop_sequences":["END"],"metadata":{"user_id":"x"},"thinking":{"type":"enabled","budget_tokens":1024}}""";

        var fromChat = JsonNode.Parse(OpenAiResponsesProtocol.BuildRequest(chat, WireProtocol.OpenAi, "gpt-5.6"))!;
        var fromAnthropic = JsonNode.Parse(OpenAiResponsesProtocol.BuildRequest(anthropic, WireProtocol.Anthropic, "gpt-5.6"))!;

        Assert.Equal("hello", fromChat["input"]![0]!["content"]![0]!["text"]!.GetValue<string>());
        Assert.Null(fromChat["presence_penalty"]);
        Assert.Equal("hello", fromAnthropic["input"]![0]!["content"]![0]!["text"]!.GetValue<string>());
        Assert.Null(fromAnthropic["metadata"]);
    }

    [Fact]
    public void BuildRequestMapsImagesAndSkipsThinkingBlocks()
    {
        const string body = """
        {"model":"alias","messages":[
          {"role":"user","content":[{"type":"image","source":{"type":"base64","media_type":"image/png","data":"AA=="}},{"type":"text","text":"what is this","cache_control":{"type":"ephemeral"}}]},
          {"role":"assistant","content":[{"type":"thinking","thinking":"hmm","signature":"s"},{"type":"text","text":"a square"}]}]}
        """;

        var request = JsonNode.Parse(OpenAiResponsesProtocol.BuildRequest(body, WireProtocol.Anthropic, "gpt-5.6"))!;
        var user = request["input"]![0]!["content"]!.AsArray();
        Assert.Equal("input_image", user[0]!["type"]!.GetValue<string>());
        Assert.Equal("data:image/png;base64,AA==", user[0]!["image_url"]!.GetValue<string>());
        Assert.Equal("what is this", user[1]!["text"]!.GetValue<string>());
        var assistant = request["input"]![1]!["content"]!.AsArray();
        Assert.Single(assistant);
        Assert.Equal("a square", assistant[0]!["text"]!.GetValue<string>());
    }
    [Fact]
    public void StreamTranslatorMapsResponsesTextDeltaToOpenAiChatChunk()
    {
        var translator = new OpenAiResponsesStreamTranslator(WireProtocol.OpenAi);

        var output = translator.Transform("response.output_text.delta", """{"delta":"hello"}""");

        Assert.Contains(output, line => line.StartsWith("data: ", StringComparison.Ordinal) && line.Contains("\"content\":\"hello\"", StringComparison.Ordinal));
    }

    [Fact]
    public void StreamTranslatorMapsFunctionCallToAnthropicToolUseEvents()
    {
        var translator = new OpenAiResponsesStreamTranslator(WireProtocol.Anthropic);
        var start = translator.Transform("response.output_item.added", """{"output_index":0,"item":{"type":"function_call","call_id":"call-1","name":"lookup"}}""");

        var delta = translator.Transform("response.function_call_arguments.delta", """{"output_index":0,"delta":"{\"id\":\"7\"}"}""");
        var output = start.Concat(delta).ToArray();

        Assert.Contains("event: content_block_start", output);
        Assert.Contains(output, line => line.Contains("\"type\":\"tool_use\"", StringComparison.Ordinal));
        Assert.Contains(output, line => line.Contains("\"type\":\"input_json_delta\"", StringComparison.Ordinal));
    }

    [Fact]
    public void StreamTranslatorHandlesInterleavedParallelAnthropicToolCallsWithoutReopeningBlocks()
    {
        var translator = new OpenAiResponsesStreamTranslator(WireProtocol.Anthropic);
        var output = new List<string>();
        output.AddRange(translator.Transform("response.output_item.added", """{"output_index":0,"item":{"type":"function_call","call_id":"call-1","name":"lookup"}}"""));
        output.AddRange(translator.Transform("response.output_item.added", """{"output_index":1,"item":{"type":"function_call","call_id":"call-2","name":"search"}}"""));
        output.AddRange(translator.Transform("response.function_call_arguments.delta", """{"output_index":0,"delta":"{\"id\":\"1\"}"}"""));
        output.AddRange(translator.Transform("response.function_call_arguments.delta", """{"output_index":1,"delta":"{\"q\":\"two\"}"}"""));
        output.AddRange(translator.Transform("response.completed", """{"response":{"id":"resp-1","model":"gpt-5.6","status":"completed","output":[]}}"""));

        Assert.Equal(2, output.Count(line => line == "event: content_block_start"));
        Assert.Equal(2, output.Count(line => line == "event: content_block_stop"));
        Assert.Contains(output, line => line.Contains("\"id\":\"call-1\"", StringComparison.Ordinal));
        Assert.Contains(output, line => line.Contains("\"id\":\"call-2\"", StringComparison.Ordinal));
        Assert.Equal(2, output.Count(line => line.Contains("\"type\":\"input_json_delta\"", StringComparison.Ordinal)));
    }

    [Fact]
    public void StreamTranslatorDoesNotReportSuccessUntilResponseCompleted()
    {
        var translator = new OpenAiResponsesStreamTranslator(WireProtocol.OpenAi);
        translator.Transform("response.output_text.delta", """{"delta":"partial"}""");

        Assert.False(translator.IsCompleted);

        translator.Transform("response.completed", """{"response":{"id":"resp-1","model":"gpt-5.6","status":"completed","output":[]}}""");

        Assert.True(translator.IsCompleted);
    }

    [Fact]
    public void StreamTranslatorWrapsAnthropicTextInMessageLifecycleEvents()
    {
        var translator = new OpenAiResponsesStreamTranslator(WireProtocol.Anthropic);

        var output = translator.Transform("response.output_text.delta", """{"delta":"hello"}""");
        var completed = translator.Transform("response.completed", """{"response":{"id":"resp-1","model":"gpt-5.6","status":"completed","output":[],"usage":{"input_tokens":2,"output_tokens":1}}}""");

        Assert.Equal("event: message_start", output[0]);
        Assert.Contains("event: content_block_start", output);
        Assert.Contains("event: message_delta", completed);
        Assert.Contains("event: message_stop", completed);
    }

    [Fact]
    public void TranslateCompletedResponsePreservesTextAndFunctionCallsForAnthropic()
    {
        const string response = """
        {"id":"resp-1","model":"gpt-5.6","status":"completed","output":[
          {"type":"message","content":[{"type":"output_text","text":"done"}]},
          {"type":"function_call","call_id":"call-1","name":"lookup","arguments":"{\"id\":\"7\"}"}
        ],"usage":{"input_tokens":3,"output_tokens":2}}
        """;

        using var translated = JsonDocument.Parse(OpenAiResponsesStreamTranslator.TranslateCompletedResponse(response, WireProtocol.Anthropic));
        var root = translated.RootElement;

        Assert.Equal("done", root.GetProperty("content")[0].GetProperty("text").GetString());
        Assert.Equal("tool_use", root.GetProperty("content")[1].GetProperty("type").GetString());
        Assert.Equal("call-1", root.GetProperty("content")[1].GetProperty("id").GetString());
        Assert.Equal("tool_use", root.GetProperty("stop_reason").GetString());
    }

    [Theory]
    [InlineData(WireProtocol.OpenAi)]
    [InlineData(WireProtocol.Anthropic)]
    public void StreamTranslatorMakesRefusalVisible(WireProtocol protocol)
    {
        var translator = new OpenAiResponsesStreamTranslator(protocol);

        var output = translator.Transform("response.refusal.delta", """{"delta":"I cannot help with that."}""");

        Assert.Contains(output, line => line.Contains("I cannot help with that.", StringComparison.Ordinal));
        Assert.Contains(output, line => protocol == WireProtocol.OpenAi
            ? line.Contains("\"refusal\":", StringComparison.Ordinal)
            : line.Contains("\"type\":\"text_delta\"", StringComparison.Ordinal));
        if (protocol == WireProtocol.OpenAi)
            Assert.Contains(output, line => line.Contains("\"content\":\"I cannot help with that.\"", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(WireProtocol.OpenAi)]
    [InlineData(WireProtocol.Anthropic)]
    public void TranslateCompletedResponseMakesRefusalVisible(WireProtocol protocol)
    {
        const string response = """
        {"id":"resp-1","model":"gpt-5.6","status":"completed","output":[{"type":"message","content":[{"type":"refusal","refusal":"I cannot help with that."}]}]}
        """;

        using var translated = JsonDocument.Parse(OpenAiResponsesStreamTranslator.TranslateCompletedResponse(response, protocol));
        var root = translated.RootElement;
        var visible = protocol == WireProtocol.OpenAi
            ? root.GetProperty("choices")[0].GetProperty("message").GetProperty("refusal").GetString()
            : root.GetProperty("content")[0].GetProperty("text").GetString();

        Assert.Equal("I cannot help with that.", visible);
        if (protocol == WireProtocol.OpenAi)
            Assert.Equal("I cannot help with that.", root.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString());
    }

    [Fact]
    public async Task StreamPumpRejectsEndOfStreamBeforeResponseCompleted()
    {
        const string incomplete = "event: response.output_text.delta\ndata: {\"delta\":\"partial\"}\n\n";
        var translator = new OpenAiResponsesStreamTranslator(WireProtocol.OpenAi);
        await using var source = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(incomplete));
        await using var destination = new MemoryStream();

        await Assert.ThrowsAsync<OpenAiResponsesIncompleteException>(() => translator.PumpAsync(source, destination, CancellationToken.None));
        Assert.False(translator.IsCompleted);
    }

    [Fact]
    public async Task StreamPumpHonorsCallerCancellation()
    {
        var translator = new OpenAiResponsesStreamTranslator(WireProtocol.OpenAi);
        await using var source = new MemoryStream(System.Text.Encoding.UTF8.GetBytes("event: response.created\n"));
        await using var destination = new MemoryStream();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => translator.PumpAsync(source, destination, cancellation.Token));
    }

    private static Dictionary<string, string> ParseQuery(string query) => query.TrimStart('?')
        .Split('&', StringSplitOptions.RemoveEmptyEntries)
        .Select(field => field.Split('=', 2))
        .ToDictionary(pair => Uri.UnescapeDataString(pair[0]), pair => Uri.UnescapeDataString(pair[1]), StringComparer.Ordinal);
}
