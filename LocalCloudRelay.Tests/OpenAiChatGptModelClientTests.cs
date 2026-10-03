using System.Net;
using LocalCloudRelay;

namespace LocalCloudRelay.Tests;

public sealed class OpenAiChatGptModelClientTests
{
    [Fact]
    public async Task FetchModelsUsesOAuthAndOnlyImportsVisibleAccountModels()
    {
        var handler = new RecordingHandler((request, _) =>
        {
            Assert.Equal(OpenAiChatGptModelClient.ModelsEndpoint, request.RequestUri);
            // Without client_version the account catalog omits its newest models.
            Assert.Contains("client_version=", request.RequestUri!.Query, StringComparison.Ordinal);
            Assert.Equal("fresh-access", request.Headers.Authorization?.Parameter);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"models":[{"slug":"gpt-z","visibility":"list"},{"slug":"preview","visibility":"hidden"},{"slug":"gpt-a","visibility":"list"}]}""")
            });
        });
        using var http = new HttpClient(handler);
        var store = new MemoryOAuthTokenStore(new OAuthTokenSet("fresh-access", "refresh", DateTimeOffset.UtcNow.AddHours(1),
            scope: "resource.invoke chatgpt.tokens.use.direct"));
        var client = new OpenAiChatGptModelClient(http, new OAuthTokenClient(http, store));

        var models = await client.FetchModelsAsync(OpenAiProfile());

        Assert.Equal(["gpt-z", "gpt-a"], models);
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task FetchModelsRejectsMissingPlanUsageConsentWithoutSendingCredentials()
    {
        var handler = new RecordingHandler((_, _) => throw new InvalidOperationException("The request must be denied before HTTP."));
        using var http = new HttpClient(handler);
        var client = new OpenAiChatGptModelClient(http,
            new OAuthTokenClient(http, new MemoryOAuthTokenStore(new OAuthTokenSet("secret-token", "refresh", null, scope: "openid"))));

        var profile = OpenAiProfile() with
        {
            OAuthTokens = new ProviderOAuthTokens("saved-access", "saved-refresh", null, "openid")
        };
        var error = await Assert.ThrowsAsync<OpenAiChatGptException>(() => client.FetchModelsAsync(profile));

        Assert.Equal(HttpStatusCode.Forbidden, error.StatusCode);
        Assert.DoesNotContain("secret-token", error.ToString(), StringComparison.Ordinal);
        Assert.Equal(0, handler.RequestCount);
    }

    [Fact]
    public async Task FetchModelsPropagatesCancellationWhileWaitingForTheProvider()
    {
        var requestStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new RecordingHandler(async (_, cancellationToken) =>
        {
            requestStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        using var http = new HttpClient(handler);
        var store = new MemoryOAuthTokenStore(new OAuthTokenSet("fresh-access", "refresh", DateTimeOffset.UtcNow.AddHours(1),
            scope: "resource.invoke chatgpt.tokens.use.direct"));
        var client = new OpenAiChatGptModelClient(http, new OAuthTokenClient(http, store));
        using var cancellation = new CancellationTokenSource();
        var fetching = client.FetchModelsAsync(OpenAiProfile(), cancellation.Token);

        await requestStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fetching);
    }

    private static ProviderSettings OpenAiProfile() => new(
        "openai-account", "OpenAI account", ProviderKinds.OpenAi, "https://api.openai.com/v1", null, true, 0,
        AuthMode: ProviderAuthMode.OAuth, OAuthClientId: "issued-client",
        OAuthTokens: new ProviderOAuthTokens("saved-access", "saved-refresh", DateTimeOffset.UtcNow.AddHours(1),
            "resource.invoke chatgpt.tokens.use.direct"));

    private sealed class RecordingHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            return respond(request, cancellationToken);
        }
    }

    private sealed class MemoryOAuthTokenStore(OAuthTokenSet tokens) : IOAuthTokenStore
    {
        public ValueTask<OAuthTokenSet?> GetAsync(string profileId, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<OAuthTokenSet?>(tokens);

        public ValueTask SetAsync(string profileId, OAuthTokenSet value, CancellationToken cancellationToken = default)
        {
            tokens = value;
            return ValueTask.CompletedTask;
        }
    }
}
