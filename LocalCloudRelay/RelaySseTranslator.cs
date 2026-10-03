using System.Text;

namespace LocalCloudRelay;

/// <summary>
/// Pumps an event stream through <see cref="ProtocolStreamTranslator"/>, line by line,
/// flushing each translated event as it completes so a streamed answer still arrives
/// progressively rather than in one lump at the end.
/// </summary>
public static class RelaySseTranslator
{
    public static async Task PumpAsync(Stream source, Stream destination, WireProtocol from, WireProtocol to,
        CancellationToken cancellationToken)
    {
        var translator = ProtocolStreamTranslator.For(from, to);
        if (translator is null)
        {
            await source.CopyToAsync(destination, cancellationToken);
            return;
        }

        using var reader = new StreamReader(source, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
        while (true)
        {
            var line = await reader.ReadLineAsync(cancellationToken);
            if (line is null) break;

            foreach (var output in translator.Transform(line))
            {
                await destination.WriteAsync(Encoding.UTF8.GetBytes(output + "\n"), cancellationToken);
            }
            await destination.FlushAsync(cancellationToken);
            if (translator.IsTerminal) return;
        }

        foreach (var output in translator.Finish())
        {
            await destination.WriteAsync(Encoding.UTF8.GetBytes(output + "\n"), cancellationToken);
        }
        await destination.FlushAsync(cancellationToken);
    }
}

/// <summary>Applies a timeout to each upstream read so active long streams can continue.</summary>
internal sealed class IdleTimeoutStream(Stream inner, TimeSpan idleTimeout) : Stream
{
    public override bool CanRead => inner.CanRead;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
    public override int Read(Span<byte> buffer)
    {
        var bytes = new byte[buffer.Length];
        var count = Read(bytes, 0, bytes.Length);
        bytes.AsSpan(0, count).CopyTo(buffer);
        return count;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(idleTimeout);
        try
        {
            return await inner.ReadAsync(buffer, timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && timeout.IsCancellationRequested)
        {
            throw new TimeoutException($"The upstream stream was idle for {idleTimeout.TotalMinutes:0} minutes.");
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) inner.Dispose();
        base.Dispose(disposing);
    }

}
