using System.Net;
using System.Text;
using LocalCloudRelay;

namespace LocalCloudRelay.Tests;

/// <summary>
/// End to end: asking for a router name has to reach the upstream as a real model id.
/// A gateway has never heard of "free", so a request forwarded with that name in the body
/// would fail even though the relay had resolved it correctly.
/// </summary>
public sealed class RouterEndToEndTests : IAsyncLifetime
{
    private const string LocalKey = "local-router-key";
    private readonly RelayTelemetryStore _telemetry = new();
    private readonly List<string> _upstreamBodies = [];
    private HttpClient _client = null!;
    private RelayServer _server = null!;
    private string _base = string.Empty;
    private Func<string, HttpStatusCode>? _faultModel;
    private Func<string, bool>? _throwModel;

    public async Task InitializeAsync()
    {
        var upstream = new FakeUpstreamHandler(request =>
        {
            var body = request.Content is null
                ? string.Empty
                : request.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            _upstreamBodies.Add(body);

            // A real gateway reports back the model that served the request, so the fake
            // echoes it. Returning an invented name would make the telemetry assertion
            // below vacuous.
            var served = System.Text.Json.JsonDocument.Parse(body).RootElement.TryGetProperty("model", out var m)
                ? m.GetString() ?? "unknown"
                : "unknown";

            // A test can make a named model throw an exception, simulating connection failure.
            if (_throwModel?.Invoke(served) == true)
                throw new HttpRequestException("Connection refused");

            // A test can make a named model fail, which is how "not available" is exercised.
            if (_faultModel?.Invoke(served) is { } fault && fault != HttpStatusCode.OK)
                return new HttpResponseMessage(fault) { Content = new StringContent("{\"error\":\"down\"}") };

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "{\"id\":\"c1\",\"object\":\"chat.completion\",\"model\":\"" + served +
                    "\",\"choices\":[{\"index\":0,\"message\":{\"role\":\"assistant\",\"content\":\"ok\"},\"finish_reason\":\"stop\"}]," +
                    "\"usage\":{\"prompt_tokens\":1,\"completion_tokens\":1}}",
                    Encoding.UTF8, "application/json")
            };
        });

        _server = new RelayServer(_telemetry, upstream, port: 0);
        _server.Apply(LocalKey, new ProviderRouter(
            [new ProviderSettings("zen", "OpenCode Zen", ProviderKinds.OpenAi, "https://opencode.ai/zen/v1", "k", true, 0)],
            new CatalogSnapshot(DateTimeOffset.UtcNow, new Dictionary<string, IReadOnlyList<string>>
            {
                // kimi-k3 (3/15) is dearest, gpt-5.6-luna (0.20/1.20) cheapest.
                ["zen"] = ["kimi-k3", "gpt-5.6-luna", "space-bunny-free"]
            }),
            [
                RouterRule.Create("pair", RouterStrategies.Premium, ["kimi-k3", "gpt-5.6-luna"]),
                RouterRule.Create("rr", RouterStrategies.RoundRobin, ["kimi-k3", "gpt-5.6-luna"]),
                RouterRule.Create("stay", RouterStrategies.Sticky, ["kimi-k3", "gpt-5.6-luna"]),
                RouterRule.Create("unpicked", RouterStrategies.Sticky, ["space-bunny-free"]),
                RouterRule.Create("off", RouterStrategies.Sticky, ["kimi-k3"], enabled: false)
            ]));
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

    private async Task<(HttpStatusCode Status, string Body, string? Router)> AskAsync(
        string model, string? session = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, _base + "/v1/chat/completions")
        {
            Content = new StringContent(
                $$"""{"model":"{{model}}","max_tokens":32,"messages":[{"role":"user","content":"hi"}]}""",
                Encoding.UTF8, "application/json")
        };
        request.Headers.TryAddWithoutValidation("x-api-key", LocalKey);
        if (session is not null) request.Headers.TryAddWithoutValidation("x-relay-session-id", session);
        var response = await _client.SendAsync(request);
        return (response.StatusCode, await response.Content.ReadAsStringAsync(),
            response.Headers.TryGetValues("x-relay-router", out var values) ? string.Join(",", values) : null);
    }

    private async Task AskWithBodyAsync(string body, string session)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, _base + "/v1/chat/completions")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        request.Headers.TryAddWithoutValidation("x-api-key", LocalKey);
        request.Headers.TryAddWithoutValidation("x-relay-session-id", session);
        using var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private async Task<(HttpStatusCode Status, string? Decision)> DecideAsync(string model, string session)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, _base + "/v1/chat/completions")
        {
            Content = new StringContent($$"""{"model":"{{model}}","messages":[{"role":"user","content":"hi"}]}""", Encoding.UTF8, "application/json")
        };
        request.Headers.TryAddWithoutValidation("x-api-key", LocalKey);
        request.Headers.TryAddWithoutValidation("x-relay-session-id", session);
        using var response = await _client.SendAsync(request);
        return (response.StatusCode, response.Headers.TryGetValues("x-relay-decision", out var values) ? values.Single() : null);
    }

    [Fact]
    public async Task TheDecisionHeaderSaysWhatWasChosenAndWhetherTheCacheWasWarm()
    {
        var (_, first) = await DecideAsync("pair", "chat-d");
        var (_, second) = await DecideAsync("pair", "chat-d");

        Assert.NotNull(first);
        Assert.Contains("router=pair", first);
        Assert.Contains("strategy=premium", first);
        Assert.Contains("chose=OpenCode Zen/kimi-k3", first);
        Assert.Contains("cache=cold", first);
        Assert.Contains("cache=warm", second);
    }

    [Fact]
    public async Task TheDecisionHeaderNamesWhatWasSkippedAndWhy()
    {
        _faultModel = served => served == "kimi-k3" ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK;
        await DecideAsync("pair", "chat-s1");                // kimi-k3 fails and rests
        _faultModel = null;
        var (_, decision) = await DecideAsync("pair", "chat-s2");

        Assert.Contains("chose=OpenCode Zen/gpt-5.6-luna", decision);
        Assert.Contains("skipped=OpenCode Zen/kimi-k3 (", decision);
        Assert.Contains("rests until", decision);
    }

    [Fact]
    public async Task APlainModelRequestHasNoDecisionHeader()
    {
        var (_, decision) = await DecideAsync("kimi-k3", "chat-p");
        Assert.Null(decision);
    }

    [Fact]
    public async Task AskingForARouterSendsTheResolvedModelUpstream()
    {
        var (status, _, router) = await AskAsync("pair");

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("pair", router);
        Assert.Single(_upstreamBodies);
        // The upstream gets a model it knows, not the name the client invented.
        Assert.Contains("\"model\":\"kimi-k3\"", _upstreamBodies[0]);
        Assert.DoesNotContain("\"model\":\"pair\"", _upstreamBodies[0]);
        // And nothing else in the body was disturbed.
        Assert.Contains("\"max_tokens\":32", _upstreamBodies[0]);
        Assert.Contains("\"content\":\"hi\"", _upstreamBodies[0]);
    }

    [Fact]
    public async Task ARouterStaysInsideItsPoolEvenWhenTheCatalogOffersBetter()
    {
        // space-bunny-free is free and in the catalog but not in this pool, so the router
        // must not fall back to it however good an idea it looks.
        for (var i = 0; i < 4; i++)
        {
            await AskAsync("rr");
            Assert.DoesNotContain("space-bunny-free", _upstreamBodies[^1]);
        }
    }

    [Fact]
    public async Task RoundRobinUsesBothModelsAcrossConversations()
    {
        await AskWithBodyAsync("""{"model":"rr","messages":[{"role":"user","content":"first chat"}]}""", "session-a");
        await AskWithBodyAsync("""{"model":"rr","messages":[{"role":"user","content":"second chat"}]}""", "session-b");

        Assert.Contains("\"model\":\"kimi-k3\"", _upstreamBodies[0]);
        Assert.Contains("\"model\":\"gpt-5.6-luna\"", _upstreamBodies[1]);
    }

    [Fact]
    public async Task RoundRobinKeepsAWarmConversationOnOneModel()
    {
        const string firstTurn = """{"model":"rr","max_tokens":32,"messages":[{"role":"user","content":"implement the change"}]}""";
        const string toolContinuation = """{"model":"rr","max_tokens":32,"messages":[{"role":"user","content":"implement the change"},{"role":"assistant","tool_calls":[{"id":"call-1","type":"function","function":{"name":"edit","arguments":"{}"}}]},{"role":"tool","tool_call_id":"call-1","content":"done"}]}""";
        const string nextTurn = """{"model":"rr","max_tokens":32,"messages":[{"role":"user","content":"implement the change"},{"role":"assistant","content":"Done."},{"role":"user","content":"now review it"}]}""";

        await AskWithBodyAsync(firstTurn, "claude-session");
        await AskWithBodyAsync(toolContinuation, "claude-session");
        await AskWithBodyAsync(nextTurn, "claude-session");

        static string Model(string body) => System.Text.Json.JsonDocument.Parse(body).RootElement
            .GetProperty("model").GetString()!;

        // Tool continuation and the next turn arrive within the cache lifetime, so all
        // three reuse one model and its prompt cache.
        Assert.Equal(Model(_upstreamBodies[0]), Model(_upstreamBodies[1]));
        Assert.Equal(Model(_upstreamBodies[1]), Model(_upstreamBodies[2]));
    }

    [Fact]
    public async Task StickyKeepsOneSessionOnOneModel()
    {
        await AskAsync("stay", "session-a");
        await AskAsync("stay", "session-a");
        await AskAsync("stay", "session-a");

        var models = _upstreamBodies.Select(b => b.Contains("\"model\":\"kimi-k3\"") ? "kimi-k3" : "other").Distinct().ToArray();
        Assert.Single(models);
    }

    [Fact]
    public async Task AModelThatFailsStepsAsideForTheNextInThePool()
    {
        _faultModel = model => model == "kimi-k3" ? HttpStatusCode.BadGateway : HttpStatusCode.OK;

        await AskAsync("pair");   // dearest, and now cooling
        await AskAsync("pair");   // must move to the other one in the pool

        Assert.Contains("\"model\":\"kimi-k3\"", _upstreamBodies[0]);
        Assert.Contains("\"model\":\"gpt-5.6-luna\"", _upstreamBodies[1]);
    }

    [Fact]
    public async Task TheResolvedModelIsWhatGetsRecordedAndReported()
    {
        await AskAsync("pair");

        // The per-model view and the cost path work off the real model, so a router does
        // not hide what actually ran.
        var report = _telemetry.GetReport();
        Assert.Contains(report.Models, m => m.Key.Equals("kimi-k3", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(report.Models, m => m.Key.Equals("pair", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task AnOrdinaryModelIsStillForwardedByteForByte()
    {
        await AskAsync("gpt-5.6-luna");

        // Rewriting is for routers only. A real model id goes up exactly as it arrived.
        Assert.Single(_upstreamBodies);
        Assert.Contains("\"model\":\"gpt-5.6-luna\"", _upstreamBodies[0]);
    }

    [Fact]
    public async Task ASwitchedOffRouterIsRefusedWithAnExplanation()
    {
        var (status, body, _) = await AskAsync("off");

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Contains("switched off", body);
        // And it must not have been forwarded upstream as a model id.
        Assert.Empty(_upstreamBodies);
    }

    [Fact]
    public async Task ExceptionOnUpstreamTrigggersCooldown()
    {
        _throwModel = model => model == "kimi-k3";

        // The connection failure is retried on the other pool model inside the same
        // request, so the client never sees it; kimi-k3 then rests.
        var (status1, _, _) = await AskAsync("pair");
        Assert.Equal(HttpStatusCode.OK, status1);
        Assert.Contains("\"model\":\"kimi-k3\"", _upstreamBodies[0]);
        Assert.Contains("\"model\":\"gpt-5.6-luna\"", _upstreamBodies[1]);

        var (status2, _, _) = await AskAsync("pair");   // goes straight to the other one
        Assert.Equal(HttpStatusCode.OK, status2);
        Assert.Equal(3, _upstreamBodies.Count);
        Assert.Contains("\"model\":\"gpt-5.6-luna\"", _upstreamBodies[2]);
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.Unauthorized)]
    public async Task AnAccountWideFailureDoesNotRetryOnTheSameProvider(HttpStatusCode failure)
    {
        // 429 and 401 are about the account, not the model, and this pool has one
        // provider: another of its models would fail the same way, so nothing is retried.
        _faultModel = served => served == "kimi-k3" ? failure : HttpStatusCode.OK;

        var (status, _) = await DecideAsync("pair", "chat-acct");

        Assert.Equal(failure, status);
        Assert.Single(_upstreamBodies);
    }

    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.NotFound)]
    public async Task AnUnbilledFailureFailsOverWithinTheSameRequest(HttpStatusCode failure)
    {
        _faultModel = served => served == "kimi-k3" ? failure : HttpStatusCode.OK;

        var (status, decision) = await DecideAsync("pair", "chat-f");

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Contains("chose=OpenCode Zen/gpt-5.6-luna", decision);
        Assert.Contains("attempt=2", decision);
        Assert.Contains("\"model\":\"kimi-k3\"", _upstreamBodies[0]);
        Assert.Contains("\"model\":\"gpt-5.6-luna\"", _upstreamBodies[^1]);
    }

    [Fact]
    public async Task AServerErrorIsNotRetriedBecauseItMayHaveBeenBilled()
    {
        _faultModel = served => served == "kimi-k3" ? HttpStatusCode.InternalServerError : HttpStatusCode.OK;

        var (status, _) = await DecideAsync("pair", "chat-500");

        Assert.Equal(HttpStatusCode.InternalServerError, status);
        Assert.Single(_upstreamBodies);
    }

    [Fact]
    public async Task WhenEveryPoolModelFailsTheClientGetsTheLastAnswer()
    {
        _faultModel = _ => HttpStatusCode.ServiceUnavailable;

        var (status, decision) = await DecideAsync("pair", "chat-all");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, status);
        Assert.Equal(2, _upstreamBodies.Count);              // each pool model once, no loop
        Assert.Contains("attempt=2", decision);
    }

    [Fact]
    public async Task APlainModelRequestIsNeverRetriedElsewhere()
    {
        _faultModel = _ => HttpStatusCode.ServiceUnavailable;

        var (status, _, _) = await AskAsync("kimi-k3");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, status);
        Assert.Single(_upstreamBodies);
    }

    [Fact]
    public async Task RoutersAreListedSoAnAgentCanSelectThem()
    {
        var response = await _client.SendAsync(new HttpRequestMessage(HttpMethod.Get, _base + "/v1/models")
        {
            Headers = { { "x-api-key", LocalKey } }
        });
        var body = await response.Content.ReadAsStringAsync();

        Assert.Contains("\"id\":\"pair\"", body);
        Assert.Contains("\"id\":\"rr\"", body);
        Assert.Contains("\"owned_by\":\"Router\"", body);
        // A switched-off router is not advertised.
        Assert.DoesNotContain("\"id\":\"off\"", body);
    }
}
