using System.Diagnostics;
using System.Text;
using System.Threading.Channels;

namespace LocalCloudRelay;

/// <summary>One message of a text-only conversation, as a CLI provider sees it.</summary>
public sealed record CliTurn(string Role, string Text);

/// <summary>
/// A provider CLI kept running between requests for one conversation.
///
/// Starting `claude` or `agy` costs seconds and, for agy, about 12k input tokens of
/// agent setup on every request. Both CLIs accept one user message per stdin line in
/// stream-json mode and keep the conversation, and its prompt cache, inside the process.
/// Measured: a follow-up turn on a live process took 2 s against 4 s (Claude) and
/// 10+ s (agy) for a fresh one, and read the earlier context from cache.
/// </summary>
internal sealed class CliConversation : IDisposable
{
    private readonly IProviderCliProcess _process;
    private readonly Channel<string> _lines = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });
    private readonly string _workingDirectory;
    private int _disposed;

    public CliConversation(string key, IProviderCliProcess process, string workingDirectory, DateTimeOffset now)
    {
        Key = key;
        _process = process;
        _workingDirectory = workingDirectory;
        LastUsed = now;
        _ = PumpAsync(process.StandardOutput, _lines.Writer);
        _ = DrainAsync(process.StandardError);
    }

    /// <summary>Executable, model and system prompt: what must match for reuse.</summary>
    public string Key { get; }

    /// <summary>Everything this process has seen, its own replies included.</summary>
    public IReadOnlyList<CliTurn> History { get; set; } = [];

    public DateTimeOffset LastUsed { get; set; }

    /// <summary>True when the process exited or this conversation was thrown away.</summary>
    public bool IsDead => Volatile.Read(ref _disposed) == 1 || _lines.Reader.Completion.IsCompleted;

    /// <summary>
    /// Sends one stdin line and hands stdout lines to <paramref name="onLine"/> until it
    /// reports the turn finished. False when the process ended first.
    /// </summary>
    public async Task<bool> RunTurnAsync(string inputLine, Func<string, ValueTask<bool>> onLine, CancellationToken cancellationToken)
    {
        await _process.StandardInput.WriteLineAsync(inputLine.AsMemory(), cancellationToken);
        await _process.StandardInput.FlushAsync(cancellationToken);
        while (await _lines.Reader.WaitToReadAsync(cancellationToken))
        {
            while (_lines.Reader.TryRead(out var line))
                if (await onLine(line)) return true;
        }
        return false;
    }

    private static async Task PumpAsync(TextReader output, ChannelWriter<string> writer)
    {
        try
        {
            while (await output.ReadLineAsync() is { } line)
                await writer.WriteAsync(line);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            // The process went away; Completion below tells the waiting turn.
        }
        finally
        {
            writer.TryComplete();
        }
    }

    private static async Task DrainAsync(TextReader error)
    {
        try
        {
            while (await error.ReadLineAsync() is not null)
            {
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
        try { _process.Kill(entireProcessTree: true); } catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) { }
        _process.Dispose();
        try { Directory.Delete(_workingDirectory, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}

/// <summary>
/// Live CLI conversations, matched to requests by their message history. A request
/// continues a conversation when it repeats exactly what the process has already seen and
/// adds only new user messages; anything else (an edited or regenerated history, another
/// system prompt) starts a fresh process, so a reply never comes from the wrong context.
/// </summary>
public sealed class CliConversationPool : IDisposable
{
    /// <summary>A conversation idle this long is stopped; its prompt cache is cold by then.</summary>
    public static readonly TimeSpan IdleLimit = TimeSpan.FromMinutes(10);

    /// <summary>Live processes kept per CLI; each one holds a full agent in memory.</summary>
    public const int MaxLive = 4;

    private readonly ProviderCliRunner _runner;
    private readonly TimeProvider _time;
    private readonly List<CliConversation> _idle = [];
    private readonly Lock _gate = new();

    public CliConversationPool(ProviderCliRunner runner, TimeProvider? time = null)
    {
        _runner = runner;
        _time = time ?? TimeProvider.System;
    }

    internal int IdleCount
    {
        get { lock (_gate) return _idle.Count; }
    }

    /// <summary>
    /// Takes an idle conversation this request continues, with the new user messages to
    /// send, or null when none matches. A taken conversation belongs to the caller until
    /// it is returned or discarded.
    /// </summary>
    internal (CliConversation Conversation, IReadOnlyList<CliTurn> NewTurns)? Take(string key, IReadOnlyList<CliTurn> request)
    {
        lock (_gate)
        {
            Sweep();
            foreach (var conversation in _idle)
            {
                if (conversation.Key != key || NewTurns(conversation.History, request) is not { } newTurns) continue;
                _idle.Remove(conversation);
                return (conversation, newTurns);
            }
            return null;
        }
    }

    internal CliConversation Start(string key, string executable, IEnumerable<string> arguments, string directoryPrefix)
    {
        var workingDirectory = Path.Combine(Path.GetTempPath(), directoryPrefix + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workingDirectory);
        try
        {
            return new CliConversation(key, _runner.StartInteractive(executable, arguments, workingDirectory), workingDirectory, _time.GetUtcNow());
        }
        catch
        {
            try { Directory.Delete(workingDirectory, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            throw;
        }
    }

    /// <summary>Keeps a conversation that finished its turn cleanly for the next request.</summary>
    internal void Return(CliConversation conversation)
    {
        if (conversation.IsDead)
        {
            conversation.Dispose();
            return;
        }
        conversation.LastUsed = _time.GetUtcNow();
        lock (_gate)
        {
            _idle.Add(conversation);
            Sweep();
            while (_idle.Count > MaxLive)
            {
                var oldest = _idle.MinBy(c => c.LastUsed)!;
                _idle.Remove(oldest);
                oldest.Dispose();
            }
        }
    }

    /// <summary>
    /// History the process has seen must be repeated exactly, and only user messages may
    /// follow it. Null when the request does not continue this history.
    /// </summary>
    internal static IReadOnlyList<CliTurn>? NewTurns(IReadOnlyList<CliTurn> history, IReadOnlyList<CliTurn> request)
    {
        if (history.Count == 0 || request.Count <= history.Count) return null;
        for (var i = 0; i < history.Count; i++)
            if (history[i].Role != request[i].Role || !SameText(history[i].Text, request[i].Text)) return null;
        var newTurns = request.Skip(history.Count).ToArray();
        return newTurns.All(turn => turn.Role == "user") ? newTurns : null;
    }

    private static bool SameText(string a, string b) =>
        string.Equals(a.TrimEnd(), b.TrimEnd(), StringComparison.Ordinal);

    private void Sweep()
    {
        var now = _time.GetUtcNow();
        for (var i = _idle.Count - 1; i >= 0; i--)
        {
            var conversation = _idle[i];
            if (!conversation.IsDead && now - conversation.LastUsed < IdleLimit) continue;
            _idle.RemoveAt(i);
            conversation.Dispose();
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            foreach (var conversation in _idle) conversation.Dispose();
            _idle.Clear();
        }
    }
}

/// <summary>Stdin and stdout of the CLIs are UTF-8; the console code page is not.</summary>
internal static class CliEncoding
{
    public static readonly Encoding Utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    public static void Apply(ProcessStartInfo startInfo)
    {
        startInfo.StandardInputEncoding = Utf8;
        startInfo.StandardOutputEncoding = Utf8;
        startInfo.StandardErrorEncoding = Utf8;
    }
}
