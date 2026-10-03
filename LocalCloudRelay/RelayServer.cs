using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace LocalCloudRelay;

public sealed class RelayServer : IDisposable
{
    private const int MaxRequestBodyBytes = 16 * 1024 * 1024;

    // ResponseHeadersRead limits this timeout to sending the request and receiving
    // headers. Body reads have a separate idle timeout so active generations can continue.
    private static readonly TimeSpan UpstreamTimeout = TimeSpan.FromMinutes(10);

    private readonly HttpClient _client;
    private readonly RelayTelemetryStore _telemetry;
    private readonly OAuthTokenClient _oauthTokenClient;
    private readonly ClaudeCodeGateway _claudeCodeGateway;
    private readonly GeminiCliGateway _geminiCliGateway;
    private readonly AntigravityGateway _antigravityGateway;
    private readonly Func<Stopwatch, long> _durationMilliseconds;
    private readonly DateTimeOffset _startedAt = DateTimeOffset.UtcNow;
    private readonly int _port;
    private readonly TimeSpan _upstreamIdleTimeout;
    private int _activeRequestCount;

    /// <param name="upstreamHandler">Test seam. Without it the relay is unobservable end
    /// to end, which is how a response-capture regression shipped once already.</param>
    public RelayServer(RelayTelemetryStore telemetry, HttpMessageHandler? upstreamHandler = null, int port = RelayProtocol.Port,
        TimeSpan? upstreamIdleTimeout = null, IOAuthTokenStore? oauthTokenStore = null,
        ClaudeCodeGateway? claudeCodeGateway = null,
        Func<Stopwatch, long>? durationMilliseconds = null,
        AntigravityGateway? antigravityGateway = null,
        GeminiCliGateway? geminiCliGateway = null,
        RouterMemoryStore? routerMemory = null,
        RouterSpend? routerSpend = null)
    {
        _spend = routerSpend ?? new RouterSpend();
        _telemetry = telemetry;
        _routerMemory = routerMemory;
        if (routerMemory?.Load() is { } remembered) _routerEngine.Import(remembered);
        _port = port;
        _upstreamIdleTimeout = upstreamIdleTimeout ?? TimeSpan.FromMinutes(2);
        // HttpClient takes ownership of the handler and disposes it with itself, so
        // disposing the handler here as well would be wrong, not merely redundant.
#pragma warning disable CA2000 // false positive: HttpClient owns its handler
        _client = upstreamHandler is null
            ? new HttpClient(new HttpClientHandler
            {
                AllowAutoRedirect = false,
                AutomaticDecompression = System.Net.DecompressionMethods.All
            }) { Timeout = UpstreamTimeout }
            : new HttpClient(upstreamHandler, disposeHandler: false) { Timeout = UpstreamTimeout };
#pragma warning restore CA2000
        _oauthTokenClient = new OAuthTokenClient(_client, oauthTokenStore ?? new RelaySettingsStore());
        _claudeCodeGateway = claudeCodeGateway ?? new ClaudeCodeGateway(new ProviderCliRunner());
        _antigravityGateway = antigravityGateway ?? new AntigravityGateway(new ProviderCliRunner());
        _geminiCliGateway = geminiCliGateway ?? new GeminiCliGateway(new ProviderCliRunner());
        _durationMilliseconds = durationMilliseconds ?? (watch => watch.ElapsedMilliseconds);
    }
    private WebApplication? _app;
    public bool IsRunning => _app is not null;
    public int ActiveRequestCount => Volatile.Read(ref _activeRequestCount);
    public TimeSpan Runtime => DateTimeOffset.UtcNow - _startedAt;

    private sealed record State(string LocalKey, ProviderRouter Router,
        Func<string, ProviderSettings?>? ProviderResolver);
    private State _state = new(string.Empty, new ProviderRouter([], CatalogSnapshot.Empty), null);

    /// <summary>
    /// Remembers, per provider and client dialect, whether the upstream needed
    /// translation. Learned from a 404 the first time, so a gateway that speaks both is
    /// never translated twice and one that speaks only its own is not probed every call.
    /// </summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> _bridgeMemo = new();

    /// <summary>Provider and model pairs that answer only the Responses API.</summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> _responsesMemo = new();

    /// <summary>Rotation, stickiness and cooldowns for named routers, for this process.</summary>
    private readonly RouterEngine _routerEngine = new();

    /// <summary>
    /// Raised, on a request thread, for events the operator should hear about without
    /// opening the window: a router over budget, a provider that refused its key or
    /// sign-in, a router whose whole pool is resting. The UI de-duplicates per day.
    /// </summary>
    public event Action<RelayNotice>? Notice;

    private void Notify(string key, string title, string message)
    {
        try { Notice?.Invoke(new RelayNotice(key, title, message)); }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException) { }
    }

    /// <summary>Today's spend per router, for daily budgets.</summary>
    private readonly RouterSpend _spend;

    /// <summary>Read by the UI to show each router's spend against its budget.</summary>
    public RouterSpend Spend => _spend;

    /// <summary>Where routing memory is kept across restarts; null keeps it in memory only.</summary>
    private readonly RouterMemoryStore? _routerMemory;
    private long _routerMemorySavedAt;
    private static readonly TimeSpan RouterMemorySaveInterval = TimeSpan.FromMinutes(1);

    /// <summary>Saves routing memory now. Called on stop, and at most once a minute after requests.</summary>
    public void SaveRouterMemory()
    {
        _spend.Save();
        if (_routerMemory is null) return;
        Volatile.Write(ref _routerMemorySavedAt, Environment.TickCount64);
        _routerEngine.Prune();
        _routerMemory.Save(_routerEngine.Export());
    }

    private void MaybeSaveRouterMemory()
    {
        var last = Volatile.Read(ref _routerMemorySavedAt);
        var now = Environment.TickCount64;
        if (now - last < (long)RouterMemorySaveInterval.TotalMilliseconds) return;
        // One writer per interval; the others see the new stamp and skip.
        if (Interlocked.CompareExchange(ref _routerMemorySavedAt, now, last) != last) return;
        _ = Task.Run(() =>
        {
            _spend.Save();
            if (_routerMemory is null) return;
            _routerEngine.Prune();
            _routerMemory.Save(_routerEngine.Export());
        });
    }

    /// <summary>Live availability, read by the UI to show why a model is out of rotation.</summary>
    public RouterEngine RouterEngine => _routerEngine;

    /// <summary>Hot-swaps providers without rebinding the port, so config edits apply live.</summary>
    public void Apply(string localKey, ProviderRouter router,
        Func<string, ProviderSettings?>? providerResolver = null)
    {
        _bridgeMemo.Clear();
        _responsesMemo.Clear();
        Volatile.Write(ref _state, new State(localKey ?? string.Empty, router, providerResolver));
    }

    public ProviderRouter Router => Volatile.Read(ref _state).Router;

    /// <summary>Actual bound port. Differs from the requested one when port 0 was passed.</summary>
    public int Port
    {
        get
        {
            var app = _app;
            if (app is null) return _port;
            foreach (var url in app.Urls)
                if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Port > 0) return uri.Port;
            return _port;
        }
    }

    public async Task StartAsync()
    {
        await StopAsync();

        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = Array.Empty<string>() });
        builder.Logging.ClearProviders();
        builder.WebHost.UseKestrel().UseUrls($"http://{RelayProtocol.ListenAddress}:{_port}");
        var app = builder.Build();
        app.Use(async (HttpContext context, RequestDelegate next) =>
        {
            // The relay's own routes are matched on the raw path. They are not upstream
            // API paths, so they must not be run through the version normalization below
            // or /health would become /v1/health.
            var raw = context.Request.Path.Value ?? "/";

            if (raw == "/health")
            {
                context.Response.ContentType = "text/plain; charset=utf-8";
                await context.Response.WriteAsync("ok\n");
                return;
            }
            if (!Authorized(context))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }
            if (raw == "/relay/capabilities" || raw == "/relay/telemetry")
            {
                context.Response.ContentType = "application/json; charset=utf-8";
                if (raw == "/relay/capabilities")
                    await context.Response.WriteAsJsonAsync(RelayCapabilityCatalog.Manifest());
                else
                {
                    var report = _telemetry.GetReport(context.Request.Query["session"].FirstOrDefault());
                    await context.Response.WriteAsJsonAsync(new
                    {
                        runtime_seconds = (long)(DateTimeOffset.UtcNow - _startedAt).TotalSeconds,
                        active_requests = ActiveRequestCount,
                        report
                    });
                }
                return;
            }
            // The union catalog is the whole multi-provider contract: downstream clients
            // list models here and send a bare id, and the relay picks the upstream.
            // One normalization up front, so this route and the upstream hop both see a
            // canonical path. A client on the root base URL asks for /models; one on a
            // /v1 base asks for /v1/models. Both land here.
            var path = RelayProtocol.NormalizeRequestPath(raw);
            if (HttpMethods.IsGet(context.Request.Method) && path == "/v1/models")
            {
                context.Response.ContentType = "application/json; charset=utf-8";
                await context.Response.WriteAsJsonAsync(Router.UnionModelsDocument());
                return;
            }
            if (HttpMethods.IsGet(context.Request.Method) && path == "/v1beta/models")
            {
                context.Response.ContentType = "application/json; charset=utf-8";
                await context.Response.WriteAsJsonAsync(Router.GeminiModelsDocument());
                return;
            }
            await HandleAsync(context);
        });
        try
        {
            await app.StartAsync();
            _app = app;
        }
        catch
        {
            await app.DisposeAsync();
            throw;
        }
    }

    public async Task StopAsync()
    {
        var app = Interlocked.Exchange(ref _app, null);
        if (app is null) return;
        SaveRouterMemory();
        // Was .Wait() + .GetAwaiter().GetResult() on the UI thread, which can deadlock
        // against the WindowsFormsSynchronizationContext.
        using var shutdown = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try { await app.StopAsync(shutdown.Token); } catch { }
        await app.DisposeAsync();
    }

    private bool Authorized(HttpContext context)
    {
        var key = Volatile.Read(ref _state).LocalKey;
        if (string.IsNullOrEmpty(key)) return false;
        if (RelayProtocol.IsAuthorized(key, RelayProtocol.StripBearer(context.Request.Headers.Authorization.FirstOrDefault()))) return true;
        foreach (var header in RelayProtocol.CredentialHeaders)
            if (RelayProtocol.IsAuthorized(key, context.Request.Headers[header].FirstOrDefault())) return true;
        return false;
    }

    private static async Task<byte[]?> ReadBodyAsync(Stream body, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await body.ReadAsync(chunk, cancellationToken)) > 0)
        {
            if (buffer.Length + read > MaxRequestBodyBytes) return null;
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }

    private static string? ExtractModel(byte[]? body)
    {
        if (body is null || body.Length == 0) return null;
        try
        {
            using var document = JsonDocument.Parse(body);
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("model", out var model)
                && model.ValueKind == JsonValueKind.String
                ? model.GetString()
                : null;
        }
        catch (JsonException) { return null; }
    }

    private static bool IsStreamRequest(byte[]? body)
    {
        if (body is null || body.Length == 0) return false;
        try
        {
            using var document = JsonDocument.Parse(body);
            return document.RootElement.ValueKind == JsonValueKind.Object &&
                document.RootElement.TryGetProperty("stream", out var stream) &&
                stream.ValueKind == JsonValueKind.True;
        }
        catch (JsonException) { return false; }
    }

    /// <summary>
    /// Sends the request upstream, translating between the Anthropic and OpenAI dialects
    /// when the upstream does not speak the client's.
    ///
    /// Verbatim goes first, always. A gateway that already serves the client's protocol
    /// must never see a rewritten body, and the provider's declared type is only a hint -
    /// a gateway labelled OpenAI may still answer /v1/messages natively. The answer is
    /// learned per provider: a 404 or 405 means that route does not exist, which also
    /// means nothing was billed, so the retry in the provider's own dialect is safe.
    /// </summary>
    private async Task<(HttpResponseMessage? Response, HttpRequestMessage? Request, bool Translated, bool OpenAiResponses)> SendUpstreamAsync(
        ProviderRoute route, string upstreamPath, byte[]? requestBody, HttpContext context, string sessionKey)
    {
        var clientProtocol = ProtocolBridge.FromRequestPath(upstreamPath);
        var openAiResponses = route.Provider.AuthMode == ProviderAuthMode.OAuth &&
            route.Provider.Kind.Equals(ProviderKinds.OpenAi, StringComparison.OrdinalIgnoreCase);
        var providerProtocol = ProtocolBridge.ForProviderKind(route.Provider.Kind);
        var bridgeable = ProtocolBridge.CanBridge(clientProtocol, providerProtocol);
        var memoKey = route.Provider.Id + ":" + route.Model + ":" + clientProtocol;

        var bodyText = requestBody is null ? null : Encoding.UTF8.GetString(requestBody);

        if (openAiResponses)
        {
            if (clientProtocol is not (WireProtocol.OpenAi or WireProtocol.Anthropic))
                throw new OpenAiResponsesUnsupportedException("OpenAI account profiles accept Chat Completions or Anthropic Messages requests only.");
            if (string.IsNullOrWhiteSpace(route.Provider.OAuthClientId) ||
                !OpenAiChatGptOAuth.HasDirectUsagePermission(route.Provider.OAuthTokens?.Scope))
                throw new OpenAiChatGptException("This OpenAI account profile is not authorized for ChatGPT plan usage.", System.Net.HttpStatusCode.Forbidden);

            // The imported account alias is resolved to the exact upstream model in route.Model.
            // Responses requests use that ID even when the client used account:<profile>/<model>.
            var payload = RelayProtocol.WithPromptCacheKey(
                OpenAiResponsesProtocol.BuildRequest(bodyText ?? "{}", clientProtocol, route.Model),
                RelayProtocol.SessionHash(sessionKey));
            var accessToken = await _oauthTokenClient.GetValidAccessTokenAsync(route.Provider.Id,
                new Uri(OpenAiChatGptOAuth.TokenEndpoint), route.Provider.OAuthClientId, null,
                TimeSpan.FromMinutes(2),
                new Dictionary<string, string>(StringComparer.Ordinal) { ["resource"] = OpenAiChatGptOAuth.Resource },
                context.RequestAborted);
            HttpRequestMessage? accountRequest = new(HttpMethod.Post, OpenAiChatGptModelClient.ResponsesEndpoint)
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json")
            };
            accountRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            accountRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
            try
            {
                var accountResponse = await _client.SendAsync(accountRequest, HttpCompletionOption.ResponseHeadersRead, context.RequestAborted);
                var ownedRequest = accountRequest;
                accountRequest = null;
                return (accountResponse, ownedRequest, true, true);
            }
            finally { accountRequest?.Dispose(); }
        }

        async Task<(HttpResponseMessage Response, HttpRequestMessage Request)> Send(string path, byte[]? bodyBytes, string? bodyText = null)
        {
            HttpRequestMessage? request = null;
            try
            {
                var isGeminiOAuth = route.Provider.AuthMode == ProviderAuthMode.OAuth &&
                    route.Provider.Kind.Equals(ProviderKinds.Gemini, StringComparison.OrdinalIgnoreCase);
                // OAuth credentials belong only on Google's Gemini API origin. Provider
                // BaseUrl is editable configuration, so never use it as the destination
                // for a request that carries a Google bearer token and project header.
                var providerBaseUri = isGeminiOAuth ? GeminiOAuth.ApiBaseUri : new Uri(route.Provider.BaseUrl);
                Uri requestUri = RelayProtocol.Join(providerBaseUri, path + context.Request.QueryString);
                request = new HttpRequestMessage(new HttpMethod(context.Request.Method), requestUri);
                if (providerProtocol == WireProtocol.OpenAi && RelayProtocol.AcceptsPromptCacheKey(route.Provider.BaseUrl))
                {
                    var json = bodyText ?? (bodyBytes is not null &&
                        (context.Request.ContentType?.Contains("json", StringComparison.OrdinalIgnoreCase) ?? false)
                            ? Encoding.UTF8.GetString(bodyBytes) : null);
                    if (json is not null)
                    {
                        var keyed = RelayProtocol.WithPromptCacheKey(json, RelayProtocol.SessionHash(sessionKey));
                        if (!ReferenceEquals(keyed, json)) { bodyText = keyed; bodyBytes = null; }
                    }
                }
                if (bodyBytes is not null || bodyText is not null)
                {
                    var contentBytes = bodyBytes ?? (bodyText is not null ? Encoding.UTF8.GetBytes(bodyText) : null);
                    if (contentBytes is not null)
                    {
                        request.Content = new ByteArrayContent(contentBytes);
                        if (!string.IsNullOrWhiteSpace(context.Request.ContentType))
                            request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(context.Request.ContentType);
                    }
                }
                foreach (var header in context.Request.Headers)
                {
                    // Strip the client's own credentials and any X-Forwarded-* it supplied,
                    // so a LAN client cannot leak its key upstream or spoof its address to
                    // the gateway. Content-Length goes too: a translated body is a different
                    // size, and forwarding the original length makes the write fail outright.
                    // Accept-Encoding is also skipped since HttpClientHandler handles
                    // decompression transparently via AutomaticDecompression.
                    // Content-Type is skipped as it is set explicitly when content is present.
                    if (RelayProtocol.IsHopByHop(header.Key) || RelayProtocol.IsForwarded(header.Key) ||
                        header.Key.Equals("Authorization", StringComparison.OrdinalIgnoreCase) ||
                        header.Key.Equals("Host", StringComparison.OrdinalIgnoreCase) ||
                        header.Key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase) ||
                        header.Key.Equals("Accept-Encoding", StringComparison.OrdinalIgnoreCase) ||
                        header.Key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase) ||
                        RelayProtocol.CredentialHeaders.Contains(header.Key, StringComparer.OrdinalIgnoreCase)) continue;
                    if (!request.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray()) && request.Content is not null) request.Content.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray());
                }
                if (isGeminiOAuth)
                {
                    if (string.IsNullOrWhiteSpace(route.Provider.OAuthClientId) ||
                        string.IsNullOrWhiteSpace(route.Provider.OAuthClientSecret) ||
                        string.IsNullOrWhiteSpace(route.Provider.ProjectId))
                        throw new GeminiOAuthException(
                            "Gemini OAuth requires a Google desktop client ID, client secret, and Cloud project ID.",
                            System.Net.HttpStatusCode.BadRequest);
                    var accessToken = await _oauthTokenClient.GetValidAccessTokenAsync(route.Provider.Id,
                        new Uri(GeminiOAuth.TokenEndpoint), route.Provider.OAuthClientId,
                        route.Provider.OAuthClientSecret, TimeSpan.FromMinutes(2),
                        cancellationToken: context.RequestAborted);
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
                    request.Headers.TryAddWithoutValidation("x-goog-user-project", route.Provider.ProjectId);
                }
                else if (!string.IsNullOrWhiteSpace(route.Provider.ApiKey))
                {
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", route.Provider.ApiKey);
                    request.Headers.TryAddWithoutValidation("x-api-key", route.Provider.ApiKey);
                }

                // OpenCode rejects a request with no session id: "Request is missing
                // x-opencode-session and cannot be routed efficiently" - a 400, not a
                // warning, so every Claude Code request to Zen or Go failed. The client has
                // no idea the header exists, so the relay supplies it from the session key
                // it already computes, which is stable for the length of a conversation.
                // A client that sends its own is left alone.
                if (RelayProtocol.SpeaksOpenCodeSession(route.Provider.BaseUrl) &&
                    !context.Request.Headers.ContainsKey("x-opencode-session"))
                {
                    request.Headers.TryAddWithoutValidation("x-opencode-session", RelayProtocol.SessionHash(sessionKey));
                }

                var sent = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, context.RequestAborted);
                // Ownership moves to the caller with the response: with ResponseHeadersRead
                // the body is still being streamed, so the request has to outlive that read.
                var owned = request;
                request = null;
                return (sent, owned);
            }
            finally
            {
                request?.Dispose();
            }
        }

        // Some models answer only the Responses API (OpenCode Zen's GPT models), and say so
        // with ModelProtocolUnsupported to both Chat Completions and Messages. Such a
        // request is sent again as a Responses call, built and read back by the same bridge
        // the ChatGPT account uses, and remembered so later turns go there directly.
        var responsesCapable = providerProtocol == WireProtocol.OpenAi &&
            clientProtocol is WireProtocol.OpenAi or WireProtocol.Anthropic;
        var responsesKey = route.Provider.Id + ":" + route.Model;

        async Task<(HttpResponseMessage, HttpRequestMessage, bool, bool)> SendAsResponses()
        {
            _responsesMemo[responsesKey] = true;
            var payload = OpenAiResponsesProtocol.BuildRequest(bodyText ?? "{}", clientProtocol, route.Model);
            var (sent, request) = await Send("/v1/responses", null, payload);
            return (sent, request, true, true);
        }

        async Task<(HttpResponseMessage, HttpRequestMessage, bool, bool)?> FallBackToResponses(HttpResponseMessage response, HttpRequestMessage request)
        {
            if (!responsesCapable || !await IsProtocolUnsupported(response, context.RequestAborted)) return null;
            response.Dispose();
            request.Dispose();
            return await SendAsResponses();
        }

        try
        {
            if (responsesCapable && _responsesMemo.ContainsKey(responsesKey))
                return await SendAsResponses();

            // Already learned that this provider cannot serve the client's dialect.
            if (bridgeable && _bridgeMemo.TryGetValue(memoKey, out var known) && known)
            {
                var translatedBody = ProtocolBridge.TranslateRequest(clientProtocol, providerProtocol, bodyText ?? string.Empty);
                var (sent, request) = await Send(ProtocolBridge.PathFor(providerProtocol), null, translatedBody);
                return await FallBackToResponses(sent, request) ?? (sent, request, true, false);
            }

            var (first, firstRequest) = await Send(upstreamPath, requestBody);

            bool retryTranslated;
            try
            {
                retryTranslated = bridgeable && await ShouldRetryTranslated(first, context.RequestAborted);
            }
            catch
            {
                first.Dispose();
                firstRequest.Dispose();
                throw;
            }

            if (retryTranslated)
            {
                first.Dispose();
                firstRequest.Dispose();
                _bridgeMemo[memoKey] = true;
                var translatedBody = ProtocolBridge.TranslateRequest(clientProtocol, providerProtocol, bodyText ?? string.Empty);
                var (retry, retryRequest) = await Send(ProtocolBridge.PathFor(providerProtocol), null, translatedBody);
                return await FallBackToResponses(retry, retryRequest) ?? (retry, retryRequest, true, false);
            }

            if (bridgeable) _bridgeMemo[memoKey] = false;
            return await FallBackToResponses(first, firstRequest) ?? (first, firstRequest, false, false);
        }
        catch (ArgumentException ex) when (ex.Message.Contains("SSRF"))
        {
            // SSRF attempt detected - return 400 error
            // The response will be disposed by the caller in HandleAsync via 'using var _ = response;'
#pragma warning disable CA2000 // The HttpResponseMessage is transferred to caller for disposal
            return (new HttpResponseMessage(System.Net.HttpStatusCode.BadRequest)
            {
                Content = new StringContent("""{"error":"Invalid request URI"}""", Encoding.UTF8, "application/json")
                }, null!, false, false);
#pragma warning restore CA2000
        }
    }

    /// <summary>
    /// Whether a rejection means "wrong dialect" rather than "bad request".
    ///
    /// A missing route is the obvious case. The other is OpenCode's 400
    /// "ModelProtocolUnsupported", which it returns when a model does not implement the
    /// dialect the client used - several of its free models are OpenAI-shaped only, so a
    /// Claude-shaped request must be translated rather than passed through. It is a plain
    /// 400, which is why the status code alone was not enough to spot it.
    ///
    /// The body is buffered before reading so it is still there to relay when the answer
    /// is no.
    /// </summary>
    private async Task<bool> ShouldRetryTranslated(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.StatusCode is System.Net.HttpStatusCode.NotFound or System.Net.HttpStatusCode.MethodNotAllowed)
            return true;
        return await IsProtocolUnsupported(response, cancellationToken);
    }

    /// <summary>
    /// A 400 saying the model does not implement the dialect it was sent. The body is
    /// buffered so it can still be relayed, and read again, when the answer is no.
    /// </summary>
    private async Task<bool> IsProtocolUnsupported(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.StatusCode != System.Net.HttpStatusCode.BadRequest) return false;

        var original = response.Content;
        var bytes = await ReadUpstreamBodyAsync(original, cancellationToken);
        var buffered = new ByteArrayContent(bytes);
        foreach (var header in original.Headers)
            buffered.Headers.TryAddWithoutValidation(header.Key, header.Value);
        response.Content = buffered;
        original.Dispose();
        var body = await buffered.ReadAsStringAsync(cancellationToken);
        return body.Contains("ModelProtocolUnsupported", StringComparison.OrdinalIgnoreCase)
            || body.Contains("does not support this protocol", StringComparison.OrdinalIgnoreCase);
    }

    private async Task<byte[]> ReadUpstreamBodyAsync(HttpContent content, CancellationToken cancellationToken)
    {
        await using var stream = new IdleTimeoutStream(
            await content.ReadAsStreamAsync(cancellationToken), _upstreamIdleTimeout);
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer, cancellationToken);
        return buffer.ToArray();
    }

    /// <summary>
    /// Points a request body at a different model, preserving everything else byte for
    /// byte apart from the one field. Returns the original on anything unexpected, so a
    /// body this does not understand is forwarded rather than mangled.
    /// </summary>
    internal static byte[]? RewriteModel(byte[]? body, string model)
    {
        if (body is null || body.Length == 0) return body;
        try
        {
            var node = JsonNode.Parse(body);
            if (node is not JsonObject root || !root.ContainsKey("model")) return body;
            root["model"] = model;
            return Encoding.UTF8.GetBytes(root.ToJsonString());
        }
        catch (JsonException)
        {
            return body;
        }
    }

    internal static string RewriteGeminiModelPath(string path, string model)
    {
        const string marker = "/models/";
        var start = path.LastIndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (start < 0) return path;
        var modelStart = start + marker.Length;
        var actionSeparator = path.LastIndexOf(':');
        if (actionSeparator <= modelStart) return path;
        var action = path[(actionSeparator + 1)..];
        if (action is not ("generateContent" or "streamGenerateContent")) return path;
        return path[..modelStart] + Uri.EscapeDataString(model) + path[actionSeparator..];
    }

    private static string? ExtractGeminiModelFromPath(string path)
    {
        const string marker = "/models/";
        var start = path.LastIndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (start < 0) return null;
        var modelStart = start + marker.Length;
        var actionSeparator = path.LastIndexOf(':');
        if (actionSeparator <= modelStart) return null;
        var action = path[(actionSeparator + 1)..];
        if (action is not ("generateContent" or "streamGenerateContent")) return null;
        try
        {
            var model = Uri.UnescapeDataString(path[modelStart..actionSeparator]);
            return string.IsNullOrWhiteSpace(model) ? null : model;
        }
        catch (UriFormatException) { return null; }
    }

    /// <summary>
    /// Records a CLI-backed request. Claude Code and Gemini report token counts in their
    /// result events; those are priced at the vendor's API rates like other account
    /// providers, so their cache hits show up in the Session view. agy reports none.
    /// </summary>
    private void RecordCli(DateTimeOffset startedAt, string requestId, string sessionId, HttpContext context,
        long durationMs, ProviderRoute route, string model, RelayUsageSnapshot? usage, string? clientAddress, string? userAgent)
    {
        var hasTokens = usage?.InputTokens.HasValue == true || usage?.OutputTokens.HasValue == true;
        var apiEquivalent = hasTokens
            ? ModelPricingTable.Estimate(route.Provider, model, usage!.InputTokens, usage.OutputTokens,
                  usage.CacheReadInputTokens, usage.CacheCreationInputTokens)
              ?? ModelCatalog.Estimate(model, usage.InputTokens, usage.OutputTokens)
            : null;
        var total = usage?.TotalTokens ?? (hasTokens
            ? (usage!.InputTokens ?? 0) + (usage.OutputTokens ?? 0) + (usage.CacheReadInputTokens ?? 0) + (usage.CacheCreationInputTokens ?? 0)
            : null);
        _telemetry.Record(new RelayRequestRecord(
            startedAt, requestId, sessionId, context.Request.Method, context.Request.Path,
            StatusCodes.Status200OK, durationMs, model, route.Provider.Name, null, null,
            usage?.InputTokens, usage?.OutputTokens, total, usage?.CacheReadInputTokens, usage?.CacheCreationInputTokens,
            // A CLI account is a subscription: nothing is spent per request. The API
            // figure is kept beside it, labelled as such, never added to spend.
            usage?.ReasoningTokens, 0m, "plan", new Dictionary<string, string>(),
            clientAddress, userAgent, apiEquivalent));
    }

    /// <summary>
    /// Writes x-relay-decision for a routed request: why this provider and model answered,
    /// whether the conversation stayed where its cache is, and what was passed over. Makes
    /// a bad routing decision diagnosable after the fact. Plain model requests get none.
    /// </summary>
    private void SetDecisionHeader(HttpContext context, ProviderRouter router, string? requestedModel,
        ProviderRoute route, string sessionKey, TimeSpan? cacheLifetime, long? promptTokens, int attempt, bool needsTools = false)
    {
        if (route.ViaRouter is null || context.Response.HasStarted) return;
        // Warm means the conversation was here within its cache lifetime and is still on
        // the same provider and model it used last time.
        var key = "decision|" + sessionKey;
        var identity = route.Provider.Id + "\0" + route.Model;
        var warm = _routerEngine.TouchIsWarm(key, cacheLifetime) &&
                   string.Equals(_routerEngine.StickyFor(key), identity, StringComparison.OrdinalIgnoreCase);
        _routerEngine.SetSticky(key, identity);
        if (router.ExplainRule(requestedModel, route, _routerEngine, promptTokens, warm, attempt, needsTools) is { } decision)
            context.Response.Headers["x-relay-decision"] = decision;
    }

    private static string? DecisionOf(HttpContext context) =>
        context.Response.Headers.TryGetValue("x-relay-decision", out var decision) && decision.Count > 0 ? decision.ToString() : null;

    /// <summary>The exception's type and first line, for the diagnostic log.</summary>
    internal static string ErrorOf(Exception ex)
    {
        var message = ex.Message.ReplaceLineEndings(" ");
        if (message.Length > 300) message = message[..300] + "...";
        return $"{ex.GetType().Name}: {message}";
    }

    /// <summary>Most upstream attempts one routed request makes, the first included.</summary>
    internal const int MaxAttempts = 3;

    /// <summary>
    /// Statuses that mean the upstream did no billable work: refused, rate-limited, not
    /// found, or a gateway in front of the model refusing it. Not here: 500 (the model may
    /// have run before failing) and 504 (a gateway gave up waiting while the model may
    /// still have run). Retrying either could charge twice.
    /// </summary>
    internal static bool IsUnbilledFailure(System.Net.HttpStatusCode status) => (int)status is
        401 or 402 or 403 or 404 or 429 or 502 or 503 or 529;

    /// <summary>Statuses that take a model or provider out of rotation: the unbilled ones, plus 504.</summary>
    internal static bool RestsRoute(System.Net.HttpStatusCode status) =>
        IsUnbilledFailure(status) || status == System.Net.HttpStatusCode.GatewayTimeout;

    /// <summary>Whether this request may move to another pool model: routed, attempts left,
    /// nothing sent to the client yet, and not a native Gemini path whose URL names the model.</summary>
    private static bool CanFailOver(ProviderRoute route, int attempt, string? geminiPathModel, HttpContext context) =>
        route.ViaRouter is not null && attempt < MaxAttempts && geminiPathModel is null && !context.Response.HasStarted;

    /// <summary>
    /// Takes a failed model or provider out of rotation for the right length of time. Shared
    /// by the failover loop and the final response, so both classify a failure the same way.
    /// </summary>
    private void MarkRouteFailure(ProviderRoute route, HttpResponseMessage response)
    {
        var headers = RelayUsageParser.ReadHeaders(response.Headers.Concat(response.Content.Headers));
        if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
            _routerEngine.MarkProviderBusy(route.Provider, RateLimitSignal.BackoffFor429(headers.RateLimits, DateTimeOffset.UtcNow));
        else if (UnusableReason(response.StatusCode) is { } unusable)
        {
            // 403 can be about one model (a gated tier); 401 and 402 are the account.
            var forbidden = response.StatusCode == System.Net.HttpStatusCode.Forbidden;
            _routerEngine.MarkUnusable(route.Provider, unusable, forbidden ? route.Model : null);
            Notify("unusable|" + route.Provider.Id + (forbidden ? "|" + route.Model : string.Empty),
                $"{route.Provider.Name} refused",
                $"{(forbidden ? route.Model + ": " : string.Empty)}{unusable}. It is out of rotation until you press Refresh all, or Edit on that provider.");
        }
        else if (response.StatusCode is System.Net.HttpStatusCode.NotFound or System.Net.HttpStatusCode.BadGateway
            or System.Net.HttpStatusCode.ServiceUnavailable or System.Net.HttpStatusCode.GatewayTimeout ||
            (int)response.StatusCode == 529)
            _routerEngine.MarkFailed(route.Provider, route.Model, RetryAfter(response));
    }

    /// <summary>
    /// Failures a timer cannot fix: the key or sign-in was refused, or there is no credit.
    /// Null for everything else.
    /// </summary>
    internal static string? UnusableReason(System.Net.HttpStatusCode status) => (int)status switch
    {
        401 => "key or sign-in was refused (401)",
        402 => "out of credit (402)",
        403 => "access was refused (403)",
        _ => null
    };

    /// <summary>The upstream's Retry-After, as a delay, or null when it gave none.</summary>
    internal static TimeSpan? RetryAfter(HttpResponseMessage response)
    {
        var header = response.Headers.RetryAfter;
        if (header?.Delta is { } delta) return delta;
        if (header?.Date is { } date) return date - DateTimeOffset.UtcNow is { } wait && wait > TimeSpan.Zero ? wait : null;
        return null;
    }

    private async Task HandleAsync(HttpContext context)
    {
        Interlocked.Increment(ref _activeRequestCount);
        var startedAt = DateTimeOffset.UtcNow;
        var stopwatch = Stopwatch.StartNew();
        var requestId = RelayIds.Create("relay");
        var sessionId = context.Request.Headers["x-relay-session-id"].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(sessionId)) sessionId = "unassigned";
        var clientAddress = context.Connection.RemoteIpAddress?.ToString();
        var userAgent = context.Request.Headers.UserAgent.ToString();
        context.Response.Headers["x-relay-request-id"] = requestId;

        ProviderRoute? route = null;
        // Set once the failover loop has already taken the failed route out of rotation,
        // so the final response or exception does not count the same failure twice.
        var failoverMarked = false;
        ResponsesClientStream? responsesClient = null;
        try
        {
            var state = Volatile.Read(ref _state);
            var router = state.Router;
            if (router.Providers.Count == 0)
            {
                context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                return;
            }

            // Buffered so the model can be read for routing. LLM request bodies are JSON;
            // 16 MB covers roughly a 1M-token context.
            byte[]? requestBody = null;
            if (context.Request.ContentLength is > 0 || context.Request.Headers.ContainsKey("Transfer-Encoding"))
            {
                requestBody = await ReadBodyAsync(context.Request.Body, context.RequestAborted);
                if (requestBody is null)
                {
                    context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
                    return;
                }
            }

            var requestPath = context.Request.PathBase.Add(context.Request.Path).Value ?? "/";
            // A Responses client (Codex) is served through Chat Completions: translated here,
            // routed and bridged like any other request, and translated back on the way out.
            if (requestBody is not null &&
                ResponsesClientBridge.IsResponsesRequest(context.Request.Method, RelayProtocol.NormalizeRequestPath(requestPath)))
            {
                requestBody = Encoding.UTF8.GetBytes(ResponsesClientBridge.ToChat(Encoding.UTF8.GetString(requestBody), out var responsesState));
                requestPath = "/v1/chat/completions";
                responsesClient = new ResponsesClientStream(context.Response.Body, responsesState,
                    () => context.Response.StatusCode, () => context.Response.ContentType);
                context.Response.Body = responsesClient;
            }
            var bodyModel = ExtractModel(requestBody);
            // Native Gemini requests name the model in the URL, while OpenAI-shaped
            // clients may deliberately put a model in the body even when they use a
            // Gemini-looking endpoint. Preserve the established body-model precedence.
            var geminiPathModel = bodyModel is null ? ExtractGeminiModelFromPath(requestPath) : null;
            var requestedModel = bodyModel ?? geminiPathModel;
            // Stickiness follows the conversation: a session header when the client sends
            // one, otherwise a digest of the opening user message, so a new chat picks a
            // new model and a continuing one stays put.
            var sessionKey = RelaySession.Key(name => context.Request.Headers[name].FirstOrDefault(), requestBody, clientAddress);
            var turnKey = RelaySession.TurnKey(requestBody);
            var cacheLifetime = RelaySession.CacheLifetime(requestBody);
            // A router that has used its daily budget refuses before anything is sent, with
            // a message that says why and when it resets.
            if (router.RuleFor(requestedModel) is { } budgeted && _spend.OverBudget(budgeted) is { } overBudget)
            {
                Notify("budget|" + budgeted.Key, "Router budget reached", overBudget);
                context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
                context.Response.ContentType = "application/json; charset=utf-8";
                var midnight = DateTimeOffset.Now.Date.AddDays(1);
                context.Response.Headers.RetryAfter = ((int)Math.Ceiling((midnight - DateTimeOffset.Now.DateTime).TotalSeconds)).ToString(CultureInfo.InvariantCulture);
                await context.Response.WriteAsJsonAsync(new { error = new { type = "budget_exceeded", message = overBudget } });
                return;
            }
            var promptTokens = RelaySession.EstimatePromptTokens(requestBody);
            var needsTools = RelaySession.HasTools(requestBody);
            // Kept as the client sent it, so a retry on another pool model rewrites the
            // model name from the original rather than from the failed attempt's body.
            var clientBody = requestBody;
            if (!router.TryResolve(requestedModel, out var selectedRoute, _routerEngine, sessionKey, turnKey, cacheLifetime, promptTokens, needsTools))
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                context.Response.ContentType = "application/json; charset=utf-8";
                var message = router.IsDisabledRouter(requestedModel)
                    ? $"Router '{requestedModel}' is switched off."
                    : router.HasRule(requestedModel)
                        ? $"Router '{requestedModel}' is configured but nothing matches it right now."
                        : $"No enabled provider offers model '{requestedModel}'.";
                await context.Response.WriteAsJsonAsync(new
                {
                    error = new
                    {
                        message,
                        available_models = router.AdvertisedModelNames.Take(200).ToArray(),
                        routers = router.RouterNames.ToArray(),
                        providers = router.Providers.Select(p => p.Name).ToArray()
                    }
                });
                return;
            }

            route = selectedRoute;
            if (geminiPathModel is not null &&
                !selectedRoute.Provider.Kind.Equals(ProviderKinds.Gemini, StringComparison.OrdinalIgnoreCase))
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                context.Response.ContentType = "application/json; charset=utf-8";
                await context.Response.WriteAsJsonAsync(new
                {
                    error = new { message = "Native Gemini requests require a Gemini provider for the selected model." }
                });
                return;
            }
            if (state.ProviderResolver is not null)
            {
                var currentProvider = state.ProviderResolver(route.Provider.Id);
                if (currentProvider is null || !currentProvider.Enabled ||
                    !currentProvider.ServesModel(route.Model) ||
                    (currentProvider.RequiresExactModelId &&
                     !(currentProvider.ImportedModels?.Contains(route.Model, StringComparer.OrdinalIgnoreCase) ?? false)))
                {
                    context.Response.StatusCode = StatusCodes.Status400BadRequest;
                    context.Response.ContentType = "application/json; charset=utf-8";
                    await context.Response.WriteAsJsonAsync(new
                    {
                        error = new { message = "The selected provider profile or model is no longer available." }
                    });
                    return;
                }
                // Credentials can rotate without rebuilding the catalog router. Resolve the
                // current profile after routing so this request never uses stale tokens.
                route = route with { Provider = currentProvider };
            }

            // The selector only returns a resting model when every one in the pool rests.
            if (route.ViaRouter is not null && _routerEngine.Unavailable(route.Provider, route.Model) is { } allResting)
                Notify("resting|" + route.ViaRouter, $"Router '{route.ViaRouter}' has nothing available",
                    $"Every model in its list is resting; trying {route.Provider.Name}/{route.Model} anyway. {allResting}");
            SetDecisionHeader(context, router, requestedModel, route, sessionKey, cacheLifetime, promptTokens, attempt: 1, needsTools);

            // Normalized so one advertised base URL works for OpenAI, Anthropic and
            // Gemini shaped clients alike; see RelayProtocol.NormalizeRequestPath.
            var upstreamPath = RelayProtocol.NormalizeRequestPath(requestPath);

            // A router rule resolves to a concrete upstream model, so the body has to be
            // rewritten to name it: the client asked for "free", which no gateway has ever
            // heard of. Ordinary routing leaves the body alone, because a gateway that
            // accepts an alias already resolves it and rewriting would be a surprise.
            if ((route.ViaRouter is not null || route.ViaAccountAlias is not null) &&
                !string.Equals(requestedModel, route.Model, StringComparison.Ordinal))
            {
                requestBody = RewriteModel(requestBody, route.Model);
                if (geminiPathModel is not null)
                    upstreamPath = RewriteGeminiModelPath(upstreamPath, route.Model);
            }

            if (route.Provider.AuthMode == ProviderAuthMode.CliAccount &&
                route.Provider.Kind.Equals(ProviderKinds.ClaudeCode, StringComparison.OrdinalIgnoreCase))
            {
                var protocol = ProtocolBridge.FromRequestPath(upstreamPath);
                var streaming = IsStreamRequest(requestBody);
                var executable = string.IsNullOrWhiteSpace(route.Provider.CliExecutable) ? "claude" : route.Provider.CliExecutable;
                context.Response.StatusCode = StatusCodes.Status200OK;
                context.Response.Headers["x-relay-provider"] = route.Provider.Name;
                context.Response.Headers["x-relay-cost-source"] = "plan";
                if (streaming)
                {
                    context.Response.ContentType = protocol == WireProtocol.Anthropic
                        ? "text/event-stream"
                        : "text/event-stream; charset=utf-8";
                    context.Response.Headers.CacheControl = "no-cache";
                    var generation = await _claudeCodeGateway.RunAsync(executable, route.Model, protocol,
                        requestBody is null ? "{}" : Encoding.UTF8.GetString(requestBody), true,
                        async chunk =>
                        {
                            if (!context.Response.HasStarted)
                                context.Response.Headers["x-relay-duration-ms"] = _durationMilliseconds(stopwatch).ToString(CultureInfo.InvariantCulture);
                            await context.Response.WriteAsync(chunk, context.RequestAborted);
                            await context.Response.Body.FlushAsync(context.RequestAborted);
                        }, context.RequestAborted);
                    if (!context.Response.HasStarted)
                        context.Response.Headers["x-relay-duration-ms"] = _durationMilliseconds(stopwatch).ToString(CultureInfo.InvariantCulture);
                    RecordCli(startedAt, requestId, sessionId, context, stopwatch.ElapsedMilliseconds, route, generation.Model, generation.Usage, clientAddress, userAgent);
                    return;
                }

                var buffered = await _claudeCodeGateway.RunAsync(executable, route.Model, protocol,
                    requestBody is null ? "{}" : Encoding.UTF8.GetString(requestBody), false,
                    _ => ValueTask.CompletedTask, context.RequestAborted);
                context.Response.Headers["x-relay-duration-ms"] = _durationMilliseconds(stopwatch).ToString(CultureInfo.InvariantCulture);
                context.Response.ContentType = "application/json; charset=utf-8";
                await context.Response.WriteAsync(ClaudeCodeGateway.BufferedResponse(buffered, protocol), context.RequestAborted);
                RecordCli(startedAt, requestId, sessionId, context, stopwatch.ElapsedMilliseconds, route, buffered.Model, buffered.Usage, clientAddress, userAgent);
                return;
            }

            var isGeminiCli = route.Provider.Kind.Equals(ProviderKinds.GeminiCli, StringComparison.OrdinalIgnoreCase);
            if (route.Provider.AuthMode == ProviderAuthMode.CliAccount &&
                (isGeminiCli || route.Provider.Kind.Equals(ProviderKinds.Antigravity, StringComparison.OrdinalIgnoreCase)))
            {
                var protocol = ProtocolBridge.FromRequestPath(upstreamPath);
                var streaming = IsStreamRequest(requestBody);
                var executable = string.IsNullOrWhiteSpace(route.Provider.CliExecutable)
                    ? (isGeminiCli ? "gemini" : "agy")
                    : route.Provider.CliExecutable;
                // Both CLIs speak the same text-only contract, so one path serves them.
                async Task<AntigravityGeneration> RunCliAsync(bool stream, Func<string, ValueTask> onEvent)
                {
                    var json = requestBody is null ? "{}" : Encoding.UTF8.GetString(requestBody);
                    if (!isGeminiCli)
                        return await _antigravityGateway.RunAsync(executable, route.Model, protocol, json, stream, onEvent, context.RequestAborted);
                    var gemini = await _geminiCliGateway.RunAsync(executable, route.Model, protocol, json, stream, onEvent, context.RequestAborted);
                    return new AntigravityGeneration(gemini.Text, gemini.Model, gemini.Usage);
                }
                context.Response.StatusCode = StatusCodes.Status200OK;
                context.Response.Headers["x-relay-provider"] = route.Provider.Name;
                context.Response.Headers["x-relay-cost-source"] = "plan";
                if (!isGeminiCli)
                    context.Response.Headers["x-relay-provider-capability"] = AntigravityGateway.ToolPermissionNotice;
                if (streaming)
                {
                    context.Response.ContentType = protocol == WireProtocol.Anthropic
                        ? "text/event-stream"
                        : "text/event-stream; charset=utf-8";
                    context.Response.Headers.CacheControl = "no-cache";
                    var generation = await RunCliAsync(true,
                        async chunk =>
                        {
                            if (!context.Response.HasStarted)
                                context.Response.Headers["x-relay-duration-ms"] = _durationMilliseconds(stopwatch).ToString(CultureInfo.InvariantCulture);
                            await context.Response.WriteAsync(chunk, context.RequestAborted);
                            await context.Response.Body.FlushAsync(context.RequestAborted);
                        });
                    if (!context.Response.HasStarted)
                        context.Response.Headers["x-relay-duration-ms"] = _durationMilliseconds(stopwatch).ToString(CultureInfo.InvariantCulture);
                    RecordCli(startedAt, requestId, sessionId, context, stopwatch.ElapsedMilliseconds, route, generation.Model, generation.Usage, clientAddress, userAgent);
                    return;
                }

                var buffered = await RunCliAsync(false, _ => ValueTask.CompletedTask);
                context.Response.Headers["x-relay-duration-ms"] = _durationMilliseconds(stopwatch).ToString(CultureInfo.InvariantCulture);
                context.Response.ContentType = "application/json; charset=utf-8";
                await context.Response.WriteAsync(AntigravityGateway.BufferedResponse(buffered, protocol), context.RequestAborted);
                RecordCli(startedAt, requestId, sessionId, context, stopwatch.ElapsedMilliseconds, route, buffered.Model, buffered.Usage, clientAddress, userAgent);
                return;
            }

            // Same-request failover. A routed request that fails before anything can have
            // been billed (no connection, timeout before headers, 429, 401/402/403, 404,
            // 502/503/504/529) moves to the next pool model inside this request, so the client
            // never sees a failure another model could have answered. A 500 or a reply that
            // started streaming may have been billed, so those are never retried.
            HttpResponseMessage? response;
            HttpRequestMessage? upstreamRequest;
            bool translated, openAiResponses;
            var attempt = 1;
            var tried = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { route.Provider.Id + "\0" + route.Model };
            while (true)
            {
                Exception? sendFailure = null;
                response = null;
                upstreamRequest = null;
                translated = openAiResponses = false;
                try
                {
                    (response, upstreamRequest, translated, openAiResponses) =
                        await SendUpstreamAsync(route, upstreamPath, requestBody, context, sessionKey);
                }
                catch (Exception ex) when (CanFailOver(route, attempt, geminiPathModel, context) &&
                    // Only a request that never reached the upstream: connection refused, DNS,
                    // TLS. A timeout is not retried; the model may still be running and billing.
                    ex is HttpRequestException && !context.RequestAborted.IsCancellationRequested)
                {
                    sendFailure = ex;
                }

                var retryable = sendFailure is not null ||
                    (response is not null && CanFailOver(route, attempt, geminiPathModel, context) && IsUnbilledFailure(response.StatusCode));
                if (!retryable) break;

                // Record what went wrong before choosing again, so the selector skips it.
                if (response is not null) MarkRouteFailure(route, response);
                else _routerEngine.MarkFailed(route.Provider, route.Model);

                if (!router.TryResolve(requestedModel, out var next, _routerEngine, sessionKey, turnKey, cacheLifetime, promptTokens, needsTools) ||
                    !tried.Add(next.Provider.Id + "\0" + next.Model) ||
                    next.Provider.AuthMode == ProviderAuthMode.CliAccount ||
                    state.ProviderResolver?.Invoke(next.Provider.Id) is { Enabled: false } ||
                    (state.ProviderResolver is not null && state.ProviderResolver(next.Provider.Id) is null))
                {
                    // Nothing else to try: the client gets the last answer, or the error.
                    failoverMarked = true;
                    if (sendFailure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(sendFailure).Throw();
                    break;
                }

                response?.Dispose();
                upstreamRequest?.Dispose();
                if (state.ProviderResolver?.Invoke(next.Provider.Id) is { } fresh) next = next with { Provider = fresh };
                route = next;
                attempt++;
                requestBody = RewriteModel(clientBody, route.Model);
                SetDecisionHeader(context, router, requestedModel, route, sessionKey, cacheLifetime, promptTokens, attempt, needsTools);
            }
            if (response is null) return;
            using var _ = response;
            using var __ = upstreamRequest;
            var upstreamHeaders = RelayUsageParser.ReadHeaders(response.Headers.Concat(response.Content.Headers));
            var upstreamIsEventStream = response.Content.Headers.ContentType?.MediaType?.Equals("text/event-stream", StringComparison.OrdinalIgnoreCase) == true;
            var clientWantsStream = openAiResponses && IsStreamRequest(requestBody);
            var isEventStream = upstreamIsEventStream && (!openAiResponses || clientWantsStream);
            byte[]? bufferedBody = null;
            RelayUsageSnapshot? usage = null;
            if (!upstreamIsEventStream)
            {
                var raw = await ReadUpstreamBodyAsync(response.Content, context.RequestAborted);
                // The client asked in its own dialect and gets that dialect back, whatever
                // the upstream spoke.
                bufferedBody = openAiResponses
                    ? Encoding.UTF8.GetBytes(response.IsSuccessStatusCode
                        ? OpenAiResponsesStreamTranslator.TranslateCompletedResponse(Encoding.UTF8.GetString(raw),
                            ProtocolBridge.FromRequestPath(upstreamPath))
                        : OpenAiResponsesStreamTranslator.TranslateError(Encoding.UTF8.GetString(raw),
                            ProtocolBridge.FromRequestPath(upstreamPath)))
                    : translated
                    ? Encoding.UTF8.GetBytes(response.IsSuccessStatusCode
                        ? ProtocolBridge.TranslateResponse(
                            ProtocolBridge.ForProviderKind(route.Provider.Kind),
                            ProtocolBridge.FromRequestPath(upstreamPath),
                            Encoding.UTF8.GetString(raw))
                        : ProtocolBridge.TranslateError(
                            ProtocolBridge.ForProviderKind(route.Provider.Kind),
                            ProtocolBridge.FromRequestPath(upstreamPath),
                            Encoding.UTF8.GetString(raw)))
                    : raw;
                usage = RelayUsageParser.Parse(Encoding.UTF8.GetString(bufferedBody));
            }
            else if (openAiResponses && !clientWantsStream)
            {
                await using var upstreamStream = new IdleTimeoutStream(
                    await response.Content.ReadAsStreamAsync(context.RequestAborted), _upstreamIdleTimeout);
                var json = await OpenAiResponsesStreamTranslator.ReadCompletedResponseAsync(
                    upstreamStream, ProtocolBridge.FromRequestPath(upstreamPath), context.RequestAborted);
                bufferedBody = Encoding.UTF8.GetBytes(json);
                usage = RelayUsageParser.Parse(json);
            }
            context.Response.StatusCode = (int)response.StatusCode;
            foreach (var header in response.Headers.Concat(response.Content.Headers))
            {
                if (!RelayProtocol.IsHopByHop(header.Key) && !header.Key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase) &&
                    !header.Key.Equals("Set-Cookie", StringComparison.OrdinalIgnoreCase) &&
                    !header.Key.Equals("Server", StringComparison.OrdinalIgnoreCase) &&
                    !(openAiResponses && header.Key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase)))
                    context.Response.Headers[header.Key] = header.Value.ToArray();
            }
            if (openAiResponses) context.Response.ContentType = isEventStream ? "text/event-stream" : "application/json; charset=utf-8";
            context.Response.Headers["x-relay-duration-ms"] = stopwatch.ElapsedMilliseconds.ToString(CultureInfo.InvariantCulture);
            if (!string.IsNullOrWhiteSpace(upstreamHeaders.UpstreamRequestId)) context.Response.Headers["x-relay-upstream-request-id"] = upstreamHeaders.UpstreamRequestId;
            context.Response.Headers["x-relay-provider"] = route.Provider.Name;
            // Names the router a request came through, so it is visible that "free" answered
            // rather than leaving the operator to infer it from the model that ran.
            if (route.ViaRouter is not null) context.Response.Headers["x-relay-router"] = route.ViaRouter;

            // What "available" means, learned from what actually happened: a model that
            // is not answering steps out of rotation for a while so the next request moves
            // on. Only failures that cannot have been billed count - a 404 or a connection
            // error never reached a model, whereas a 500 may have.
            // Every request that reached a model counts against its allowance, routed or
            // not: OpenCode Go counts what it served, not what came through a router.
            if ((int)response.StatusCode < 500 && response.StatusCode != System.Net.HttpStatusCode.TooManyRequests &&
                response.StatusCode != System.Net.HttpStatusCode.NotFound)
                _routerEngine.RecordRequest(route.Provider, route.Model);

            if (route.ViaRouter is not null)
            {
                // A 429 is the provider saying "not now" for the whole account, and it
                // cannot have been billed, so every model on it steps aside for as long as
                // Retry-After asks. 529 is Anthropic's "overloaded".
                if (!failoverMarked && RestsRoute(response.StatusCode))
                    MarkRouteFailure(route, response);
                else if (response.IsSuccessStatusCode)
                {
                    _routerEngine.MarkSucceeded(route.Provider, route.Model);
                    // Move new turns elsewhere before the limit is hit, not after.
                    if (RateLimitSignal.LowHeadroomFor(upstreamHeaders.RateLimits, DateTimeOffset.UtcNow) is { } wait)
                        _routerEngine.MarkProviderBusy(route.Provider, wait);
                }
            }
            if (isEventStream)
            {
                // The capture tees to the live response body, so it is disposable now
                // that DisposeAsync no longer touches the stream it wraps.
                var capture = new RelayBodyCaptureStream(context.Response.Body);
                try
                {
                    await using var upstreamStream = new IdleTimeoutStream(
                        await response.Content.ReadAsStreamAsync(context.RequestAborted), _upstreamIdleTimeout);
                    if (openAiResponses)
                    {
                        await new OpenAiResponsesStreamTranslator(ProtocolBridge.FromRequestPath(upstreamPath))
                            .PumpAsync(upstreamStream, capture, context.RequestAborted);
                    }
                    else if (translated)
                    {
                        // Streaming is the default for Claude Code and OpenCode, so an
                        // event stream that is not translated would be unreadable to the
                        // client that asked for it. The capture sees bytes written to the
                        // client (translated), so usage parsing works on translated events.
                        await RelaySseTranslator.PumpAsync(
                            upstreamStream, capture,
                            ProtocolBridge.ForProviderKind(route.Provider.Kind),
                            ProtocolBridge.FromRequestPath(upstreamPath),
                            context.RequestAborted);
                    }
                    else
                    {
                        await upstreamStream.CopyToAsync(capture, context.RequestAborted);
                    }
                    usage = RelayUsageParser.ParseServerSentEvents(capture.CapturedText);
                    await capture.FlushAsync(context.RequestAborted);
                }
                finally { await capture.DisposeAsync(); }
            }
            var model = usage?.Model ?? upstreamHeaders.Model ?? route.Model;
            // A local model is free to run, so a token-price estimate would be a lie.
            // The published plan rates go first, because the same model can cost
            // differently on Zen and Go; the generic catalog is the fallback.
            var hasTokens = usage?.InputTokens.HasValue == true || usage?.OutputTokens.HasValue == true;
            var estimatedCost = ProviderKinds.IsFree(route.Provider.Kind)
                ? null
                : hasTokens
                  ? ModelPricingTable.Estimate(route.Provider, model, usage?.InputTokens, usage?.OutputTokens,
                        usage?.CacheReadInputTokens, usage?.CacheCreationInputTokens)
                    ?? ModelCatalog.Estimate(model, usage?.InputTokens, usage?.OutputTokens)
                  : null;
            var finalCost = upstreamHeaders.CostUsd ?? usage?.CostUsd ?? estimatedCost;
            var costSource = upstreamHeaders.CostUsd.HasValue || usage?.CostUsd is not null ? "provider" : estimatedCost.HasValue ? "estimated" : "unknown";
            // A signed-in plan account (ChatGPT, Gemini sign-in) is a flat subscription, so
            // nothing is spent per request. The API-rate figure is kept apart as what the
            // same usage would have cost, never counted as spend.
            decimal? apiEquivalent = null;
            if (RouterRuleSelector.IsPlanAccount(route.Provider))
            {
                apiEquivalent = finalCost;
                finalCost = 0m;
                costSource = "plan";
            }

            // Headers can only be set before the body starts, and a streamed body starts as
            // soon as its first event is flushed. Writing to them afterwards throws
            // "Headers are read-only, response has already started", which for every
            // streamed request landed in the catch below: the client got its stream, but
            // the request was recorded with no model and no usage, so streamed traffic -
            // which is all of Claude Code's - vanished from the per-model view. Usage is
            // only known once the stream has ended, so on that path the numbers go to
            // telemetry and not to headers.
            if (!context.Response.HasStarted)
            {
                // Was hardcoded to "pending" for everything that was not a provider-reported
                // cost, so clients could never distinguish estimated from unknown.
                context.Response.Headers["x-relay-cost-source"] = costSource;
                if (usage is not null)
                {
                    if (usage.InputTokens.HasValue) context.Response.Headers["x-relay-input-tokens"] = usage.InputTokens.Value.ToString(CultureInfo.InvariantCulture);
                    if (usage.OutputTokens.HasValue) context.Response.Headers["x-relay-output-tokens"] = usage.OutputTokens.Value.ToString(CultureInfo.InvariantCulture);
                    if (usage.TotalTokens.HasValue) context.Response.Headers["x-relay-total-tokens"] = usage.TotalTokens.Value.ToString(CultureInfo.InvariantCulture);
                    if (usage.CacheReadInputTokens.HasValue) context.Response.Headers["x-relay-cache-read-tokens"] = usage.CacheReadInputTokens.Value.ToString(CultureInfo.InvariantCulture);
                    if (usage.CacheCreationInputTokens.HasValue) context.Response.Headers["x-relay-cache-creation-tokens"] = usage.CacheCreationInputTokens.Value.ToString(CultureInfo.InvariantCulture);
                    if (usage.ReasoningTokens.HasValue) context.Response.Headers["x-relay-reasoning-tokens"] = usage.ReasoningTokens.Value.ToString(CultureInfo.InvariantCulture);
                }
            }
            _telemetry.Record(new RelayRequestRecord(
                startedAt, requestId, sessionId, context.Request.Method, context.Request.Path,
                (int)response.StatusCode, stopwatch.ElapsedMilliseconds, model,
                upstreamHeaders.Provider ?? route.Provider.Name,
                upstreamHeaders.UpstreamRequestId, upstreamHeaders.LiteLlmCallId, usage?.InputTokens, usage?.OutputTokens,
                usage?.TotalTokens, usage?.CacheReadInputTokens, usage?.CacheCreationInputTokens, usage?.ReasoningTokens,
                finalCost, costSource, upstreamHeaders.RateLimits, clientAddress, userAgent, apiEquivalent,
                DecisionOf(context)));
            if (route.ViaRouter is not null && finalCost is > 0m) _spend.Add(route.ViaRouter, finalCost.Value);
            if (!isEventStream && bufferedBody is not null)
                await context.Response.Body.WriteAsync(bufferedBody, context.RequestAborted);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            _telemetry.Record(new RelayRequestRecord(
                startedAt, requestId, sessionId, context.Request.Method, context.Request.Path,
                StatusCodes.Status499ClientClosedRequest, stopwatch.ElapsedMilliseconds, route?.Model, route?.Provider.Name, null, null,
                null, null, null, null, null, null, null, "unknown", new Dictionary<string, string>(),
                clientAddress, userAgent));
        }
        catch (Exception ex)
        {
            var failureStatus = ex switch
            {
                OpenAiResponsesUnsupportedException => StatusCodes.Status400BadRequest,
                ClaudeCodeGatewayException claudeError => (int)claudeError.StatusCode,
                AntigravityGatewayException antigravityError => (int)antigravityError.StatusCode,
                GeminiCliGatewayException geminiCliError => (int)geminiCliError.StatusCode,
                OAuthTokenException { StatusCode: System.Net.HttpStatusCode.TooManyRequests } => StatusCodes.Status429TooManyRequests,
                OpenAiChatGptException { StatusCode: not null } openAiError => (int)openAiError.StatusCode.Value,
                OAuthTokenException { StatusCode: System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden } => StatusCodes.Status401Unauthorized,
                GeminiOAuthException { StatusCode: not null } geminiError => (int)geminiError.StatusCode.Value,
                GeminiOAuthException => StatusCodes.Status502BadGateway,
                OpenAiResponsesIncompleteException => StatusCodes.Status502BadGateway,
                TimeoutException or OperationCanceledException => StatusCodes.Status504GatewayTimeout,
                _ => StatusCodes.Status502BadGateway
            };
            // A plan account that hit its usage limit steps aside as a whole, for longer
            // than a one-off failure; anything else rests just the model.
            // A refused or expired sign-in is the same problem whichever way the request came
            // in: the provider steps out of rotation and the operator hears about it once.
            var signInRefused = failureStatus is StatusCodes.Status401Unauthorized or StatusCodes.Status403Forbidden;
            if (route is not null && signInRefused && !failoverMarked)
            {
                _routerEngine.MarkUnusable(route.Provider, "sign-in was refused or expired");
                Notify("unusable|" + route.Provider.Id, $"{route.Provider.Name} needs signing in",
                    "Its sign-in was refused or has expired. Select it and press Edit to sign in again.");
            }
            else if (route?.ViaRouter is not null && !failoverMarked)
            {
                if (failureStatus == StatusCodes.Status429TooManyRequests)
                    _routerEngine.MarkProviderBusy(route.Provider, RateLimitSignal.PlanLimitBackoff);
                else
                    _routerEngine.MarkFailed(route.Provider, route.Model);
            }

            _telemetry.Record(new RelayRequestRecord(
                startedAt, requestId, sessionId, context.Request.Method, context.Request.Path,
                failureStatus, stopwatch.ElapsedMilliseconds, route?.Model, route?.Provider.Name, null, null,
                null, null, null, null, null, null, null, "unknown", new Dictionary<string, string>(),
                clientAddress, userAgent, null, DecisionOf(context), ErrorOf(ex)));
            if (!context.Response.HasStarted)
            {
                // The decision is most useful exactly when the request failed, so it survives.
                var decision = context.Response.Headers["x-relay-decision"];
                context.Response.Headers.Clear();
                context.Response.Headers["x-relay-request-id"] = requestId;
                if (!string.IsNullOrEmpty(decision)) context.Response.Headers["x-relay-decision"] = decision;
                context.Response.StatusCode = failureStatus;
                context.Response.ContentType = "application/json; charset=utf-8";
                if (ex is OpenAiResponsesUnsupportedException or OpenAiResponsesIncompleteException or OpenAiChatGptException or OAuthTokenException or GeminiOAuthException or ClaudeCodeGatewayException or AntigravityGatewayException or GeminiCliGatewayException)
                    await context.Response.WriteAsJsonAsync(new { error = new { message = ex.Message } });
                else
                    await context.Response.WriteAsJsonAsync(new { relay_error = ex.GetType().Name });
            }
            else
            {
                // Mid-stream failure: close the connection so client sees a broken stream
                context.Abort();
            }
        }
        finally
        {
            if (responsesClient is not null)
            {
                try { await responsesClient.CompleteAsync(context.RequestAborted); }
                catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException) { }
            }
            Interlocked.Decrement(ref _activeRequestCount);
            MaybeSaveRouterMemory();
        }
    }

    public void Dispose()
    {
        var app = Interlocked.Exchange(ref _app, null);
        // IDisposable is synchronous by contract and WebApplication is IAsyncDisposable-only.
        // This blocks, but only during application shutdown after the form is closing -
        // never on a request path, which is where the deadlock risk actually lived.
        if (app is not null)
        {
            try
            {
                var stopAndDispose = async () =>
                {
                    using var shutdown = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                    try { await app.StopAsync(shutdown.Token); } catch { }
                    await app.DisposeAsync();
                };
                stopAndDispose().Wait(TimeSpan.FromSeconds(3));
            }
            catch { }
        }
        // Live CLI conversations are child processes; they must not outlive the relay.
        _claudeCodeGateway.Dispose();
        _antigravityGateway.Dispose();
        _client.Dispose();
    }
}
