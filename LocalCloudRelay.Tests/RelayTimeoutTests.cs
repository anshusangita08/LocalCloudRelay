using System.Net;
using System.Net.Http.Headers;
using System.Text;
using LocalCloudRelay;

namespace LocalCloudRelay.Tests;

public sealed class RelayTimeoutTests : IAsyncLifetime
{
    private static readonly TimeSpan TestLimit = TimeSpan.FromSeconds(5);
    private readonly RelayTelemetryStore _telemetry = new();
    private readonly HttpClient _client = new() { Timeout = TestLimit };
    private FakeUpstreamHandler _upstream = null!;
    private RelayServer _server = null!;
    private Func<HttpRequestMessage, HttpResponseMessage> _respond = _ => HealthyResponse();

    public async Task InitializeAsync()
    {
        _upstream = new FakeUpstreamHandler(request => _respond(request));
        _server = new RelayServer(_telemetry, _upstream, port: 0,
            upstreamIdleTimeout: TimeSpan.FromMilliseconds(150));
        _server.Apply("timeout-test", new ProviderRouter(
            [new ProviderSettings("test", "Test Provider", ProviderKinds.OpenAi, "https://test.example", "secret", true, 0)],
            new CatalogSnapshot(DateTimeOffset.UtcNow, new Dictionary<string, IReadOnlyList<string>>
            {
                ["test"] = ["test-model"]
            })));
        await _server.StartAsync();
        _client.BaseAddress = new Uri($"http://127.0.0.1:{_server.Port}");
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "timeout-test");
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _server.StopAsync();
        _server.Dispose();
        _upstream.Dispose();
    }

    [Theory]
    [InlineData("/v1/chat/completions", HttpStatusCode.OK)]
    [InlineData("/v1/messages", HttpStatusCode.BadRequest)]
    public async Task Returns504AndRecoversWhenUpstreamBodyStalls(string path, HttpStatusCode status)
    {
        using var stalled = new StalledStream();
        HttpRequestMessage? upstreamRequest = null;
        _respond = request =>
        {
            upstreamRequest = request;
            return StreamingResponse(status, stalled, "application/json");
        };
        using var request = Request(path);

        var pending = _client.SendAsync(request);
        await stalled.Waiting.Task.WaitAsync(TestLimit);
        Assert.Equal(1, _server.ActiveRequestCount);
        using var response = await pending.WaitAsync(TestLimit);

        Assert.Equal(HttpStatusCode.GatewayTimeout, response.StatusCode);
        await stalled.Disposed.Task.WaitAsync(TestLimit);
        Assert.NotNull(upstreamRequest);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => upstreamRequest.Content!.ReadAsStringAsync());
        await AssertFailureAsync(504);
        Assert.Single(_upstream.Requests); // A generation must never be retried after a timeout.
        await AssertHealthyAsync();
    }

    [Fact]
    public async Task CancelsAndDisposesProbeWhenClientDisconnects()
    {
        using var stalled = new StalledStream();
        HttpRequestMessage? upstreamRequest = null;
        _respond = request =>
        {
            upstreamRequest = request;
            return StreamingResponse(HttpStatusCode.BadRequest, stalled, "application/json");
        };
        using var request = Request("/v1/messages");
        using var cancel = new CancellationTokenSource();
        var pending = _client.SendAsync(request, cancel.Token);
        await stalled.Waiting.Task.WaitAsync(TestLimit);
        Assert.Equal(1, _server.ActiveRequestCount);

        cancel.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TestLimit));
        await stalled.Disposed.Task.WaitAsync(TestLimit);
        Assert.NotNull(upstreamRequest);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => upstreamRequest.Content!.ReadAsStringAsync());
        await AssertFailureAsync(499);
        Assert.Single(_upstream.Requests);
        await AssertHealthyAsync();
    }

    [Fact]
    public async Task AbortsAndRecords504WhenStreamStallsAfterOutput()
    {
        const string firstEvent = "data: {\"choices\":[]}\n\n";
        using var stalled = new StalledStream(firstEvent);
        _respond = _ => StreamingResponse(HttpStatusCode.OK, stalled, "text/event-stream");
        using var request = Request("/v1/chat/completions");
        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead).WaitAsync(TestLimit);
        using var body = await response.Content.ReadAsStreamAsync();
        var bytes = new byte[Encoding.UTF8.GetByteCount(firstEvent)];
        await body.ReadExactlyAsync(bytes).AsTask().WaitAsync(TestLimit);
        Assert.Equal(firstEvent, Encoding.UTF8.GetString(bytes));

        await Assert.ThrowsAnyAsync<IOException>(() => body.ReadAsync(new byte[1]).AsTask().WaitAsync(TestLimit));

        await stalled.Disposed.Task.WaitAsync(TestLimit);
        await AssertFailureAsync(504);
        Assert.Single(_upstream.Requests);
        await AssertHealthyAsync();
    }

    [Fact]
    public async Task PreservesBodyAndContentHeadersWhenProbeDeclinesTranslation()
    {
        const string error = "{\"error\":\"bad input\"}";
        _respond = _ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new StreamContent(new MemoryStream(Encoding.UTF8.GetBytes(error)))
            };
            response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/problem+json");
            response.Content.Headers.TryAddWithoutValidation("x-body-info", "retained");
            return response;
        };
        using var request = Request("/v1/messages");

        using var response = await _client.SendAsync(request).WaitAsync(TestLimit);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(error, await response.Content.ReadAsStringAsync());
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType!.MediaType);
        Assert.Equal("retained", response.Headers.GetValues("x-body-info").Single());
        Assert.Single(_upstream.Requests);
    }

    private async Task AssertFailureAsync(int status)
    {
        using var deadline = new CancellationTokenSource(TestLimit);
        while (_telemetry.GetReport().RequestCount == 0 || _server.ActiveRequestCount != 0)
            await Task.Delay(10, deadline.Token);
        Assert.Equal(0, _server.ActiveRequestCount);
        var record = Assert.Single(_telemetry.GetReport().RecentRequests);
        Assert.Equal(status, record.StatusCode);
        Assert.Equal("test-model", record.Model);
        Assert.Equal("Test Provider", record.Provider);
    }

    private async Task AssertHealthyAsync()
    {
        _respond = _ => HealthyResponse();
        using var request = Request("/v1/chat/completions");
        using var response = await _client.SendAsync(request).WaitAsync(TestLimit);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("healthy", await response.Content.ReadAsStringAsync());
        Assert.Equal(2, _upstream.Requests.Count);
    }

    private static HttpRequestMessage Request(string path) => new(HttpMethod.Post, path)
    {
        Content = new StringContent("""{"model":"test-model","messages":[{"role":"user","content":"hello"}]}""",
            Encoding.UTF8, "application/json")
    };

    private static HttpResponseMessage HealthyResponse() => new(HttpStatusCode.OK)
    {
        Content = new StringContent("""{"id":"healthy","model":"test-model","choices":[]}""", Encoding.UTF8, "application/json")
    };

    private static HttpResponseMessage StreamingResponse(HttpStatusCode status, Stream stream, string mediaType)
    {
        var response = new HttpResponseMessage(status) { Content = new StreamContent(stream) };
        response.Content.Headers.ContentType = new MediaTypeHeaderValue(mediaType);
        return response;
    }

    private sealed class StalledStream(string prefix = "") : Stream
    {
        private readonly MemoryStream _prefix = new(Encoding.UTF8.GetBytes(prefix));
        public TaskCompletionSource Waiting { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Disposed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_prefix.Position < _prefix.Length) return await _prefix.ReadAsync(buffer, cancellationToken);
            Waiting.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _prefix.Dispose();
                Disposed.TrySetResult();
            }
            base.Dispose(disposing);
        }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
