using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using LocalCloudRelay;

namespace LocalCloudRelay.Tests;

/// <summary>
/// Events that need the operator reach the tray once a day, not once per request.
/// </summary>
public sealed class NoticeTests
{
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 10, 3, 9, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }

    [Fact]
    public void EachNoticeShowsOncePerDay()
    {
        var clock = new Clock();
        var notices = new DailyNotices(clock);
        Assert.True(notices.ShouldShow("budget|coding"));
        Assert.False(notices.ShouldShow("budget|coding"));
        Assert.True(notices.ShouldShow("budget|free"));      // a different event still shows
        clock.Now = clock.Now.AddDays(1);
        Assert.True(notices.ShouldShow("budget|coding"));    // and the next day again
    }

    private static async Task<List<RelayNotice>> RunAsync(Func<HttpRequestMessage, HttpResponseMessage> upstreamReply,
        RouterRule rule, RouterSpend? spend = null, int requests = 1)
    {
        var upstream = new FakeUpstreamHandler(upstreamReply);
        using var server = new RelayServer(new RelayTelemetryStore(), upstream, port: 0, routerSpend: spend);
        var raised = new ConcurrentQueue<RelayNotice>();
        server.Notice += raised.Enqueue;
        server.Apply("key", new ProviderRouter(
            [new ProviderSettings("p", "Prov", ProviderKinds.OpenAi, "https://p.example/v1", "k", true, 0)],
            new CatalogSnapshot(DateTimeOffset.UtcNow, new Dictionary<string, IReadOnlyList<string>> { ["p"] = ["m"] }),
            [rule]));
        await server.StartAsync();
        try
        {
            using var client = new HttpClient();
            for (var i = 0; i < requests; i++)
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{server.Port}/v1/chat/completions")
                {
                    Content = new StringContent("""{"model":"r","messages":[{"role":"user","content":"hi"}]}""", Encoding.UTF8, "application/json")
                };
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "key");
                using var _ = await client.SendAsync(request);
            }
        }
        finally
        {
            await server.StopAsync();
        }
        return [.. raised];
    }

    [Fact]
    public async Task ABudgetRefusalRaisesANotice()
    {
        var spend = new RouterSpend();
        spend.Add("r", 5m);
        var raised = await RunAsync(_ => new HttpResponseMessage(HttpStatusCode.OK),
            new RouterRule("r", RouterStrategies.Sticky, ["m"], true, DailyBudgetUsd: 1m), spend);
        var notice = Assert.Single(raised);
        Assert.Equal("budget|r", notice.Key);
    }

    [Fact]
    public async Task ARefusedKeyRaisesANoticeAndAnEmptyPoolSaysSo()
    {
        var raised = await RunAsync(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StringContent("{}") },
            RouterRule.Create("r", RouterStrategies.Sticky, ["m"]), requests: 2);

        Assert.Contains(raised, n => n.Key == "unusable|p" && n.Message.Contains("Refresh all"));
        // The second request finds the only model out of rotation and says so.
        Assert.Contains(raised, n => n.Key == "resting|r");
    }
}
