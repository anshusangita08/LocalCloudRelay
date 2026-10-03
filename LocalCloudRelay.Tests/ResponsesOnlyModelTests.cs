using System.Net;
using System.Net.Http.Headers;
using System.Text;
using LocalCloudRelay;

namespace LocalCloudRelay.Tests;

/// <summary>
/// Some models answer only the Responses API and refuse Chat Completions and Messages
/// with ModelProtocolUnsupported. Every client still reaches them through the relay.
/// </summary>
public sealed class ResponsesOnlyModelTests
{
    private const string ResponsesStream =
        "event: response.created\ndata: {\"type\":\"response.created\",\"response\":{\"id\":\"resp_1\",\"model\":\"gpt-5.6\",\"status\":\"in_progress\"}}\n\n" +
        "event: response.output_text.delta\ndata: {\"type\":\"response.output_text.delta\",\"output_index\":0,\"delta\":\"pong\"}\n\n" +
        "event: response.completed\ndata: {\"type\":\"response.completed\",\"response\":{\"id\":\"resp_1\",\"model\":\"gpt-5.6\",\"status\":\"completed\"," +
        "\"output\":[{\"type\":\"message\",\"role\":\"assistant\",\"content\":[{\"type\":\"output_text\",\"text\":\"pong\"}]}],\"usage\":{\"input_tokens\":5,\"output_tokens\":1}}}\n\n";

    [Theory]
    [InlineData("/v1/chat/completions", """{"model":"gpt-5.6","messages":[{"role":"user","content":"ping"}]}""", "\"content\":\"pong\"")]
    [InlineData("/v1/messages", """{"model":"gpt-5.6","max_tokens":20,"messages":[{"role":"user","content":"ping"}]}""", "\"text\":\"pong\"")]
    [InlineData("/v1/responses", """{"model":"gpt-5.6","input":"ping"}""", "\"text\":\"pong\"")]
    public async Task EveryClientDialectReachesAResponsesOnlyModel(string path, string body, string expected)
    {
        var paths = new List<string>();
        var upstream = new FakeUpstreamHandler(request =>
        {
            paths.Add(request.RequestUri!.AbsolutePath);
            if (request.RequestUri.AbsolutePath.EndsWith("/responses", StringComparison.Ordinal))
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(ResponsesStream, Encoding.UTF8, "text/event-stream") };
            return new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new StringContent("""{"type":"error","error":{"type":"ModelProtocolUnsupported","message":"Model gpt-5.6 does not support this protocol"}}""",
                    Encoding.UTF8, "application/json")
            };
        });
        using var server = new RelayServer(new RelayTelemetryStore(), upstream, port: 0);
        server.Apply("key", new ProviderRouter(
            [new ProviderSettings("zen", "Zen", ProviderKinds.OpenAi, "https://opencode.ai/zen/v1", "k", true, 0)],
            new CatalogSnapshot(DateTimeOffset.UtcNow, new Dictionary<string, IReadOnlyList<string>> { ["zen"] = ["gpt-5.6"] })));
        await server.StartAsync();
        try
        {
            using var client = new HttpClient();
            async Task<(HttpStatusCode, string)> Ask()
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{server.Port}{path}")
                {
                    Content = new StringContent(body, Encoding.UTF8, "application/json")
                };
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "key");
                using var response = await client.SendAsync(request);
                return (response.StatusCode, await response.Content.ReadAsStringAsync());
            }

            var (status, reply) = await Ask();
            Assert.Equal(HttpStatusCode.OK, status);
            Assert.Contains(expected, reply);
            Assert.EndsWith("/responses", paths[^1]);

            // Remembered: the next turn goes straight to the Responses API.
            paths.Clear();
            var (again, _) = await Ask();
            Assert.Equal(HttpStatusCode.OK, again);
            Assert.Single(paths);
            Assert.EndsWith("/responses", paths[0]);
        }
        finally
        {
            await server.StopAsync();
        }
    }
}
