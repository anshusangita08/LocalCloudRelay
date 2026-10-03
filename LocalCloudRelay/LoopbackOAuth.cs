using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace LocalCloudRelay;

/// <summary>PKCE values for a single authorization attempt.</summary>
public sealed class OAuthPkceParameters
{
    internal OAuthPkceParameters(string verifier, string challenge, string state)
    {
        Verifier = verifier;
        Challenge = challenge;
        State = state;
    }

    public string Verifier { get; }
    public string Challenge { get; }
    public string State { get; }

    public override string ToString() => "OAuth PKCE parameters (redacted)";
}

public static class OAuthPkce
{
    private const int RandomLength = 32;

    /// <summary>
    /// Creates an RFC 7636 S256 verifier/challenge and an independent OAuth state.
    /// The optional byte provider exists to make the cryptographic transformation testable.
    /// </summary>
    public static OAuthPkceParameters Create(Func<int, byte[]>? randomBytes = null)
    {
        randomBytes ??= RandomNumberGenerator.GetBytes;
        var verifierBytes = ReadRandomBytes(randomBytes);
        var stateBytes = ReadRandomBytes(randomBytes);
        try
        {
            var verifier = Base64Url(verifierBytes);
            var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
            return new OAuthPkceParameters(verifier, challenge, Base64Url(stateBytes));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(verifierBytes);
            CryptographicOperations.ZeroMemory(stateBytes);
        }
    }

    private static byte[] ReadRandomBytes(Func<int, byte[]> source)
    {
        var bytes = source(RandomLength);
        if (bytes is null || bytes.Length != RandomLength)
            throw new InvalidOperationException("The OAuth random source returned an invalid byte count.");
        return bytes;
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

/// <summary>Builds an authorization URL with standard PKCE fields and provider-specific extensions.</summary>
public static class OAuthAuthorizationUri
{
    private static readonly HashSet<string> ReservedFields = new(StringComparer.Ordinal)
    {
        "response_type", "client_id", "redirect_uri", "code_challenge", "code_challenge_method", "state", "scope"
    };

    public static Uri Create(
        Uri authorizationEndpoint,
        string clientId,
        LoopbackOAuthCallback callback,
        OAuthPkceParameters pkce,
        IEnumerable<string>? scopes = null,
        IReadOnlyDictionary<string, string>? additionalParameters = null)
    {
        ArgumentNullException.ThrowIfNull(authorizationEndpoint);
        ArgumentException.ThrowIfNullOrWhiteSpace(clientId);
        ArgumentNullException.ThrowIfNull(callback);
        ArgumentNullException.ThrowIfNull(pkce);
        if (!authorizationEndpoint.IsAbsoluteUri || authorizationEndpoint.Scheme != Uri.UriSchemeHttps ||
            !string.IsNullOrEmpty(authorizationEndpoint.UserInfo) || !string.IsNullOrEmpty(authorizationEndpoint.Query) ||
            !string.IsNullOrEmpty(authorizationEndpoint.Fragment))
            throw new ArgumentException("The OAuth authorization endpoint must be a clean absolute HTTPS URI.", nameof(authorizationEndpoint));

        var parameters = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["response_type"] = "code",
            ["client_id"] = clientId,
            ["redirect_uri"] = callback.RedirectUri.ToString(),
            ["code_challenge"] = pkce.Challenge,
            ["code_challenge_method"] = "S256",
            ["state"] = pkce.State
        };
        var scopeList = scopes?.Where(scope => !string.IsNullOrWhiteSpace(scope)).ToArray() ?? [];
        if (scopeList.Length > 0) parameters["scope"] = string.Join(' ', scopeList);
        if (additionalParameters is not null)
        {
            foreach (var pair in additionalParameters)
            {
                if (string.IsNullOrWhiteSpace(pair.Key) || pair.Value is null || pair.Key.Any(char.IsControl) || pair.Value.Any(char.IsControl) ||
                    ReservedFields.Contains(pair.Key) || parameters.ContainsKey(pair.Key))
                    throw new ArgumentException("An OAuth extension parameter is invalid or duplicates a standard field.", nameof(additionalParameters));
                parameters.Add(pair.Key, pair.Value);
            }
        }

        var query = string.Join('&', parameters.Select(pair => $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}"));
        return new Uri($"{authorizationEndpoint.AbsoluteUri}?{query}", UriKind.Absolute);
    }
}

/// <summary>A validated loopback OAuth callback. Query values can contain credentials and are redacted from ToString.</summary>
public sealed class OAuthCallbackResult
{
    internal OAuthCallbackResult(string code, IReadOnlyDictionary<string, string> parameters)
    {
        Code = code;
        Parameters = parameters;
    }

    public string Code { get; }
    public IReadOnlyDictionary<string, string> Parameters { get; }
    public string? ClientId => Parameters.TryGetValue("client_id", out var value) ? value : null;

    public override string ToString() => "OAuth callback result (redacted)";
}

/// <summary>A sanitized OAuth authorization error returned by the provider.</summary>
public sealed class OAuthAuthorizationException : Exception
{
    public OAuthAuthorizationException() : base("The OAuth provider returned an authorization error.") { }
}

/// <summary>
/// A one-shot OAuth callback listener bound to 127.0.0.1 on an operating-system-selected port.
/// It accepts only the configured path and the exact authorization state.
/// </summary>
public sealed class LoopbackOAuthCallback : IAsyncDisposable
{
    private const int MaximumRequestLineLength = 8192;
    private const int MaximumHeaderLength = 16384;
    private const int MaximumQueryValueLength = 4096;
    private readonly TcpListener _listener;
    private readonly string _path;
    private readonly byte[] _expectedState;
    private readonly CancellationTokenSource _timeout;
    private readonly CancellationTokenSource _disposed = new();
    private readonly Uri _redirectUri;
    private int _waitStarted;
    private int _disposeStarted;

    private LoopbackOAuthCallback(TcpListener listener, string path, string state, TimeSpan timeout, TimeProvider timeProvider)
    {
        _listener = listener;
        _path = path;
        _expectedState = Encoding.UTF8.GetBytes(state);
        _timeout = new CancellationTokenSource(timeout, timeProvider);
        var endpoint = (IPEndPoint)listener.LocalEndpoint;
        _redirectUri = new UriBuilder(Uri.UriSchemeHttp, IPAddress.Loopback.ToString(), endpoint.Port, path).Uri;
    }

    public Uri RedirectUri => _redirectUri;

    public static LoopbackOAuthCallback Start(string callbackPath, string expectedState, TimeSpan timeout, TimeProvider? timeProvider = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(callbackPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedState);
        if (callbackPath[0] != '/' || callbackPath.StartsWith("//", StringComparison.Ordinal) ||
            callbackPath.Contains('?') || callbackPath.Contains('#') || callbackPath.Contains('\\') || callbackPath.Any(char.IsControl))
            throw new ArgumentException("The callback path must be a local absolute path without a query or fragment.", nameof(callbackPath));
        if (expectedState.Length > 512)
            throw new ArgumentException("The OAuth state is too long.", nameof(expectedState));
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);
        if (timeout == Timeout.InfiniteTimeSpan)
            throw new ArgumentOutOfRangeException(nameof(timeout), "A finite positive callback timeout is required.");

        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start(16);
        try
        {
            return new LoopbackOAuthCallback(listener, callbackPath, expectedState, timeout, timeProvider ?? TimeProvider.System);
        }
        catch
        {
            listener.Stop();
            throw;
        }
    }

    public async Task<OAuthCallbackResult> WaitForCallbackAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposeStarted) != 0, this);
        if (Interlocked.Exchange(ref _waitStarted, 1) != 0)
            throw new InvalidOperationException("This OAuth callback listener can only be awaited once.");

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _timeout.Token, _disposed.Token);
        try
        {
            while (true)
            {
                using var client = await _listener.AcceptTcpClientAsync(linked.Token).ConfigureAwait(false);
                client.NoDelay = true;
                var parsed = await ReadRequestAsync(client, linked.Token).ConfigureAwait(false);
                if (!string.Equals(parsed.Path, _path, StringComparison.Ordinal))
                {
                    await WriteResponseAsync(client, HttpStatusCode.NotFound, "This callback path is not available.", linked.Token).ConfigureAwait(false);
                    continue;
                }

                if (!TryValidateCallbackQuery(parsed.Query, out var result, out var authorizationError))
                {
                    await WriteResponseAsync(client, HttpStatusCode.BadRequest,
                        "The authorization response could not be validated. Return to the app and try again.", linked.Token).ConfigureAwait(false);
                    continue;
                }

                await WriteResponseAsync(client, HttpStatusCode.OK,
                    authorizationError
                        ? "Authentication could not be completed. You can return to the app."
                        : "Authentication received. You can return to the app.", linked.Token).ConfigureAwait(false);
                _listener.Stop();
                if (authorizationError) throw new OAuthAuthorizationException();
                return result!;
            }
        }
        catch (OperationCanceledException) when (_timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested && !_disposed.IsCancellationRequested)
        {
            throw new TimeoutException("The OAuth authorization callback timed out.");
        }
        catch (ObjectDisposedException) when (_disposed.IsCancellationRequested)
        {
            throw new OperationCanceledException("The OAuth authorization callback was canceled.");
        }
    }

    private bool TryValidateCallbackQuery(string query, out OAuthCallbackResult? result, out bool authorizationError)
    {
        result = null;
        authorizationError = false;
        var parameters = new Dictionary<string, string>(StringComparer.Ordinal);
        if (query.Length == 0 || query.Length > MaximumRequestLineLength)
            return false;

        foreach (var field in query.Split('&', StringSplitOptions.None))
        {
            if (field.Length == 0) return false;
            var separator = field.IndexOf('=');
            if (separator <= 0) return false;
            string name;
            string value;
            try
            {
                name = Uri.UnescapeDataString(field[..separator].Replace('+', ' '));
                value = Uri.UnescapeDataString(field[(separator + 1)..].Replace('+', ' '));
            }
            catch (UriFormatException) { return false; }

            if (name.Length is 0 or > 128 || value.Length > MaximumQueryValueLength ||
                name.Any(char.IsControl) || value.Any(char.IsControl) || parameters.ContainsKey(name))
                return false;
            parameters.Add(name, value);
        }

        if (!parameters.TryGetValue("state", out var state) ||
            !CryptographicOperations.FixedTimeEquals(_expectedState, Encoding.UTF8.GetBytes(state)))
            return false;

        if (parameters.TryGetValue("error", out var error) && !string.IsNullOrWhiteSpace(error))
        {
            authorizationError = true;
            return true;
        }

        if (!parameters.TryGetValue("code", out var code) || string.IsNullOrWhiteSpace(code)) return false;

        result = new OAuthCallbackResult(code, new System.Collections.ObjectModel.ReadOnlyDictionary<string, string>(parameters));
        return true;
    }

    private static async Task<(string Path, string Query)> ReadRequestAsync(TcpClient client, CancellationToken cancellationToken)
    {
        var stream = client.GetStream();
        var requestLine = await ReadLineAsync(stream, MaximumRequestLineLength, cancellationToken).ConfigureAwait(false);
        if (requestLine is null) return (string.Empty, string.Empty);
        var parts = requestLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 3 || !string.Equals(parts[0], "GET", StringComparison.Ordinal) ||
            (parts[2] != "HTTP/1.0" && parts[2] != "HTTP/1.1") || parts[1][0] != '/')
        {
            await DrainHeadersAsync(stream, cancellationToken).ConfigureAwait(false);
            return (string.Empty, string.Empty);
        }

        var headerLength = await DrainHeadersAsync(stream, cancellationToken).ConfigureAwait(false);
        if (headerLength < 0) return (string.Empty, string.Empty);
        if (!Uri.TryCreate("http://127.0.0.1" + parts[1], UriKind.Absolute, out var uri) ||
            uri.Host != IPAddress.Loopback.ToString() || uri.Fragment.Length != 0)
            return (string.Empty, string.Empty);
        var querySeparator = parts[1].IndexOf('?');
        var rawPath = querySeparator < 0 ? parts[1] : parts[1][..querySeparator];
        var rawQuery = querySeparator < 0 ? string.Empty : parts[1][(querySeparator + 1)..];
        return (rawPath, rawQuery);
    }

    private static async Task<int> DrainHeadersAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        var total = 0;
        while (total <= MaximumHeaderLength)
        {
            var line = await ReadLineAsync(stream, MaximumHeaderLength - total, cancellationToken).ConfigureAwait(false);
            if (line is null) return -1;
            total += line.Length + 2;
            if (line.Length == 0) return total;
        }
        return -1;
    }

    private static async Task<string?> ReadLineAsync(NetworkStream stream, int maximumLength, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream(Math.Min(maximumLength, 256));
        var one = new byte[1];
        while (buffer.Length <= maximumLength)
        {
            var read = await stream.ReadAsync(one.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (read == 0) return null;
            if (one[0] == '\n')
            {
                var line = buffer.ToArray();
                var length = line.Length > 0 && line[^1] == '\r' ? line.Length - 1 : line.Length;
                return Encoding.ASCII.GetString(line, 0, length);
            }
            buffer.WriteByte(one[0]);
        }
        return null;
    }

    private static async Task WriteResponseAsync(TcpClient client, HttpStatusCode status, string message, CancellationToken cancellationToken)
    {
        var reason = status == HttpStatusCode.OK ? "OK" : status == HttpStatusCode.NotFound ? "Not Found" : "Bad Request";
        var body = Encoding.UTF8.GetBytes(message);
        var header = Encoding.ASCII.GetBytes(string.Create(CultureInfo.InvariantCulture,
            $"HTTP/1.1 {(int)status} {reason}\r\nContent-Type: text/plain; charset=utf-8\r\nContent-Length: {body.Length}\r\nConnection: close\r\nCache-Control: no-store\r\n\r\n"));
        var stream = client.GetStream();
        await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(body, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposeStarted, 1) == 0)
        {
            _disposed.Cancel();
            _listener.Stop();
            _timeout.Dispose();
            _disposed.Dispose();
            CryptographicOperations.ZeroMemory(_expectedState);
        }
        return ValueTask.CompletedTask;
    }
}
