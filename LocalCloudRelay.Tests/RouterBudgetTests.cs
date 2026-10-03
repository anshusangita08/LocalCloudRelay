using System.Net;
using System.Net.Http.Headers;
using System.Text;
using LocalCloudRelay;

namespace LocalCloudRelay.Tests;

/// <summary>
/// A router with a daily budget stops answering once today's spend through it reaches
/// the budget, survives a restart within the day, and starts fresh the next day.
/// </summary>
public sealed class RouterBudgetTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), "spend-" + Guid.NewGuid().ToString("N") + ".json");

    public void Dispose()
    {
        try { File.Delete(_path); } catch (IOException) { }
    }

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 10, 3, 9, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }

    private static readonly RouterRule Capped = new("paid", RouterStrategies.Premium, ["m"], true, DailyBudgetUsd: 1.00m);

    [Fact]
    public void SpendAccumulatesAndTheBudgetRefusesOnceReached()
    {
        var spend = new RouterSpend(time: new Clock());
        spend.Add("paid", 0.60m);
        Assert.Null(spend.OverBudget(Capped));
        spend.Add("paid", 0.40m);
        Assert.Contains("daily budget of $1.00", spend.OverBudget(Capped));
        Assert.Null(spend.OverBudget(Capped with { DailyBudgetUsd = null }));   // no budget, no limit
    }

    [Fact]
    public void TheBudgetResetsAtMidnight()
    {
        var clock = new Clock();
        var spend = new RouterSpend(time: clock);
        spend.Add("paid", 5m);
        clock.Now = clock.Now.AddDays(1).Date;
        Assert.Equal(0m, spend.SpentToday("paid"));
    }

    [Fact]
    public void ARestartTheSameDayKeepsTheCountButNotTheNextDay()
    {
        var clock = new Clock();
        var first = new RouterSpend(_path, clock);
        first.Add("paid", 0.75m);
        first.Save();

        Assert.Equal(0.75m, new RouterSpend(_path, clock).SpentToday("paid"));
        clock.Now = clock.Now.AddDays(1);
        Assert.Equal(0m, new RouterSpend(_path, clock).SpentToday("paid"));
    }

    [Theory]
    [InlineData("", true, null)]
    [InlineData("5", true, 5.0)]
    [InlineData("$2.50", true, 2.5)]
    [InlineData("2,5", true, 2.5)]
    [InlineData("0", false, null)]
    [InlineData("abc", false, null)]
    public void BudgetTextIsParsed(string text, bool valid, double? expected)
    {
        Assert.Equal(valid, RouterEditorForm.TryParseBudget(text, out var budget));
        Assert.Equal(expected is null ? null : (decimal)expected.Value, budget);
    }

    [Fact]
    public void TheBudgetIsSavedWithTheRule()
    {
        var config = new RelayConfig(RelayConfig.CurrentSchemaVersion, "key", [], [Capped]);
        var json = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(config);
        Assert.Equal(1.00m, RelayConfigReader.FromJson(json)!.RouterRules![0].DailyBudgetUsd);
    }

    [Fact]
    public async Task ARoutedRequestsCostCountsTowardItsRouter()
    {
        var upstream = new FakeUpstreamHandler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"id":"x","model":"m","choices":[],"usage":{"prompt_tokens":1,"completion_tokens":1}}""",
                    Encoding.UTF8, "application/json")
            };
            response.Headers.TryAddWithoutValidation("x-litellm-response-cost", "0.40");
            return response;
        });
        var spend = new RouterSpend();
        using var server = new RelayServer(new RelayTelemetryStore(), upstream, port: 0, routerSpend: spend);
        server.Apply("key", new ProviderRouter(
            [new ProviderSettings("p", "P", ProviderKinds.OpenAi, "https://p.example/v1", "k", true, 0)],
            new CatalogSnapshot(DateTimeOffset.UtcNow, new Dictionary<string, IReadOnlyList<string>> { ["p"] = ["m"] }),
            [Capped]));
        await server.StartAsync();
        try
        {
            using var client = new HttpClient();
            for (var i = 0; i < 3; i++)
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{server.Port}/v1/chat/completions")
                {
                    Content = new StringContent("""{"model":"paid","messages":[{"role":"user","content":"hi"}]}""", Encoding.UTF8, "application/json")
                };
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "key");
                using var response = await client.SendAsync(request);
                Assert.Equal(i < 3 ? HttpStatusCode.OK : HttpStatusCode.TooManyRequests, response.StatusCode);
            }
            // 3 x $0.40 = $1.20, past the $1.00 budget: the next request is refused.
            Assert.Equal(1.20m, spend.SpentToday("paid"));
            Assert.NotNull(spend.OverBudget(Capped));
        }
        finally
        {
            await server.StopAsync();
        }
    }

    [Fact]
    public async Task AnOverBudgetRouterAnswers429WithAReason()
    {
        var upstream = new FakeUpstreamHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"id":"x","model":"m","choices":[],"usage":{"prompt_tokens":1,"completion_tokens":1}}""",
                Encoding.UTF8, "application/json")
        });
        var spend = new RouterSpend();
        spend.Add("paid", 2m);
        using var server = new RelayServer(new RelayTelemetryStore(), upstream, port: 0, routerSpend: spend);
        server.Apply("key", new ProviderRouter(
            [new ProviderSettings("p", "P", ProviderKinds.OpenAi, "https://p.example/v1", "k", true, 0)],
            new CatalogSnapshot(DateTimeOffset.UtcNow, new Dictionary<string, IReadOnlyList<string>> { ["p"] = ["m"] }),
            [Capped]));
        await server.StartAsync();
        try
        {
            using var client = new HttpClient();
            using var request = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{server.Port}/v1/chat/completions")
            {
                Content = new StringContent("""{"model":"paid","messages":[{"role":"user","content":"hi"}]}""", Encoding.UTF8, "application/json")
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "key");
            using var response = await client.SendAsync(request);

            Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
            Assert.Contains("budget_exceeded", await response.Content.ReadAsStringAsync());
            Assert.NotNull(response.Headers.RetryAfter);
            Assert.Empty(upstream.Requests);                 // nothing was sent, nothing billed
        }
        finally
        {
            await server.StopAsync();
        }
    }
}
