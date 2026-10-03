using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using LocalCloudRelay;

namespace LocalCloudRelay.Tests;

/// <summary>
/// OpenAI routes requests that share a prompt_cache_key to the same cache. The relay
/// supplies one per conversation, only where the upstream is known to accept it.
/// </summary>
public sealed class PromptCacheKeyTests
{
    [Fact]
    public void AddsTheKeyWhenAbsentAndKeepsTheClientsOwn()
    {
        var added = JsonDocument.Parse(RelayProtocol.WithPromptCacheKey("""{"model":"m"}""", "abc")).RootElement;
        Assert.Equal("abc", added.GetProperty("prompt_cache_key").GetString());

        const string own = """{"model":"m","prompt_cache_key":"client"}""";
        Assert.Same(own, RelayProtocol.WithPromptCacheKey(own, "abc"));
        const string notJson = "not json";
        Assert.Same(notJson, RelayProtocol.WithPromptCacheKey(notJson, "abc"));
        const string array = "[1]";
        Assert.Same(array, RelayProtocol.WithPromptCacheKey(array, "abc"));
    }

    [Fact]
    public void OnlyOpenAiItselfIsAssumedToAcceptTheField()
    {
        Assert.True(RelayProtocol.AcceptsPromptCacheKey("https://api.openai.com/v1"));
        Assert.False(RelayProtocol.AcceptsPromptCacheKey("https://opencode.ai/zen/v1"));
        Assert.False(RelayProtocol.AcceptsPromptCacheKey("https://openai.example/v1"));
        Assert.False(RelayProtocol.AcceptsPromptCacheKey(null));
    }

    [Fact]
    public void SessionHashIsStableShortAndHidesTheKey()
    {
        var hash = RelayProtocol.SessionHash("10.0.0.5:abcd");
        Assert.Equal(hash, RelayProtocol.SessionHash("10.0.0.5:abcd"));
        Assert.Equal(16, hash.Length);
        Assert.DoesNotContain("10.0.0.5", hash);
        Assert.NotEqual(hash, RelayProtocol.SessionHash("10.0.0.5:other"));
    }

    [Theory]
    [InlineData("https://api.openai.com/v1", true)]
    [InlineData("https://corp.example/v1", false)]
    public async Task TheRelaySendsOneKeyPerConversationToOpenAi(string baseUrl, bool expectKey)
    {
        var upstream = new FakeUpstreamHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"id":"x","model":"gpt-5","choices":[],"usage":{"prompt_tokens":1,"completion_tokens":1}}""",
                Encoding.UTF8, "application/json")
        });
        using var server = new RelayServer(new RelayTelemetryStore(), upstream, port: 0);
        server.Apply("local-key", new ProviderRouter(
            [new ProviderSettings("oa", "OpenAI", ProviderKinds.OpenAi, baseUrl, "secret", true, 0)],
            new CatalogSnapshot(DateTimeOffset.UtcNow, new Dictionary<string, IReadOnlyList<string>> { ["oa"] = ["gpt-5"] })));
        await server.StartAsync();
        try
        {
            using var client = new HttpClient();
            async Task Send(string opener, string? followUp = null)
            {
                var turns = followUp is null
                    ? $$"""[{"role":"user","content":"{{opener}}"}]"""
                    : $$"""[{"role":"user","content":"{{opener}}"},{"role":"assistant","content":"ok"},{"role":"user","content":"{{followUp}}"}]""";
                using var request = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{server.Port}/v1/chat/completions")
                {
                    Content = new StringContent($$"""{"model":"gpt-5","messages":{{turns}}}""", Encoding.UTF8, "application/json")
                };
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "local-key");
                using var response = await client.SendAsync(request);
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            }

            await Send("chat one");
            await Send("chat one", "and more");
            await Send("chat two");

            string? Key(int i) => JsonDocument.Parse(upstream.Bodies[i]).RootElement
                .TryGetProperty("prompt_cache_key", out var k) ? k.GetString() : null;

            if (!expectKey)
            {
                Assert.All(Enumerable.Range(0, 3), i => Assert.Null(Key(i)));
                return;
            }
            Assert.NotNull(Key(0));
            Assert.Equal(Key(0), Key(1));      // same conversation, same cache
            Assert.NotEqual(Key(0), Key(2));   // a new chat gets its own
        }
        finally
        {
            await server.StopAsync();
        }
    }
}
