using System.Net;
using System.Net.Http.Headers;
using System.Text;
using LocalCloudRelay;

namespace LocalCloudRelay.Tests;

/// <summary>
/// An account-wide refusal (429, 401) on one provider moves the request to the same
/// model on another provider, within the same request.
/// </summary>
public sealed class FailoverAcrossProvidersTests
{
    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.PaymentRequired)]
    public async Task AnAccountFailureMovesToTheSameModelOnAnotherProvider(HttpStatusCode failure)
    {
        var upstream = new FakeUpstreamHandler(request => request.RequestUri!.Host == "a.example"
            ? new HttpResponseMessage(failure) { Content = new StringContent("{\"error\":\"no\"}") }
            : new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"id":"x","model":"shared","choices":[],"usage":{"prompt_tokens":1,"completion_tokens":1}}""",
                    Encoding.UTF8, "application/json")
            });
        using var server = new RelayServer(new RelayTelemetryStore(), upstream, port: 0);
        server.Apply("key", new ProviderRouter(
            [
                new ProviderSettings("a", "A", ProviderKinds.OpenAi, "https://a.example/v1", "k", true, 0),
                new ProviderSettings("b", "B", ProviderKinds.OpenAi, "https://b.example/v1", "k", true, 1)
            ],
            new CatalogSnapshot(DateTimeOffset.UtcNow, new Dictionary<string, IReadOnlyList<string>> { ["a"] = ["shared"], ["b"] = ["shared"] }),
            [RouterRule.Create("r", RouterStrategies.Sticky, ["shared"])]));
        await server.StartAsync();
        try
        {
            using var client = new HttpClient();
            // Sticky's first pick rotates, so ask until one conversation starts on A.
            for (var i = 0; i < 4; i++)
            {
                upstream.Requests.Clear();
                using var request = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{server.Port}/v1/chat/completions")
                {
                    Content = new StringContent("""{"model":"r","messages":[{"role":"user","content":"hi"}]}""", Encoding.UTF8, "application/json")
                };
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "key");
                request.Headers.TryAddWithoutValidation("x-relay-session-id", $"chat-{i}");
                using var response = await client.SendAsync(request);

                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                Assert.Equal("b.example", upstream.Requests[^1].Host);
                if (upstream.Requests.Count == 2)
                {
                    Assert.Equal("a.example", upstream.Requests[0].Host);
                    Assert.Contains("attempt=2", response.Headers.GetValues("x-relay-decision").Single());
                    return;
                }
            }
            Assert.Fail("No conversation started on provider A.");
        }
        finally
        {
            await server.StopAsync();
        }
    }
}
