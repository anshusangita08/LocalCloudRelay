using System.Diagnostics;

namespace LocalCloudRelay;

/// <summary>Runs a local provider CLI while streaming its stdout one line at a time.</summary>
public sealed class ProviderCliRunner
{
    private const string StartError = "The provider CLI could not be started.";
    private const string RunError = "The provider CLI request failed.";
    private static readonly TimeSpan StreamShutdownGracePeriod = TimeSpan.FromMilliseconds(250);
    private readonly IProviderCliProcessFactory _processFactory;

    public ProviderCliRunner() : this(new ProviderCliProcessFactory())
    {
    }

    internal ProviderCliRunner(IProviderCliProcessFactory processFactory)
    {
        _processFactory = processFactory;
    }

    /// <summary>
    /// Starts a provider CLI without a shell, writes optional stdin, and forwards stdout
    /// lines as they arrive. Stderr is drained to avoid blocking but is never retained.
    /// The stdout callback should honor its cancellation token. During cancellation the
    /// runner waits briefly for stream work to stop, then returns without waiting for a
    /// callback that ignores cancellation; any eventual callback fault is observed.
    /// </summary>
    public async Task<ProviderCliResult> RunAsync(
        string executable,
        IEnumerable<string> arguments,
        string? standardInput,
        Func<string, CancellationToken, ValueTask>? onStdoutLine,
        CancellationToken cancellationToken = default) =>
        await RunAsync(executable, arguments, standardInput, onStdoutLine, Environment.CurrentDirectory, cancellationToken).ConfigureAwait(false);

    public async Task<ProviderCliResult> RunAsync(
        string executable,
        IEnumerable<string> arguments,
        string? standardInput,
        Func<string, CancellationToken, ValueTask>? onStdoutLine,
        string workingDirectory,
        CancellationToken cancellationToken = default) =>
        await RunAsync(executable, arguments, standardInput, onStdoutLine, workingDirectory, null, cancellationToken).ConfigureAwait(false);

    /// <param name="onStderrLine">Sees stderr lines, for CLIs that print a sign-in link there.</param>
    public async Task<ProviderCliResult> RunAsync(
        string executable,
        IEnumerable<string> arguments,
        string? standardInput,
        Func<string, CancellationToken, ValueTask>? onStdoutLine,
        string workingDirectory,
        Action<string>? onStderrLine,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executable);
        ArgumentNullException.ThrowIfNull(arguments);
        cancellationToken.ThrowIfCancellationRequested();

        var startInfo = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = workingDirectory,
            CreateNoWindow = true
        };
        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);
        CliEncoding.Apply(startInfo);

        IProviderCliProcess process;
        try
        {
            process = _processFactory.Start(startInfo);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return new ProviderCliResult(null, StartError);
        }

        using (process)
        using (cancellationToken.Register(static state => TryKillTree((IProviderCliProcess)state!), process))
        {
            var stdoutTask = ForwardStdoutAsync(process.StandardOutput, onStdoutLine, cancellationToken);
            var stderrTask = DrainStderrAsync(process.StandardError, onStderrLine);
            var stdinTask = WriteStdinAndCloseAsync(process.StandardInput, standardInput, cancellationToken);
            var exitTask = process.WaitForExitAsync(cancellationToken);
            var work = Task.WhenAll(stdoutTask, stderrTask, stdinTask, exitTask);

            try
            {
                // Awaiting WhenAll directly delays cancellation until every stream task
                // finishes, including callbacks that may ignore their token.
                await work.WaitAsync(cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                var exitCode = process.ExitCode;
                return exitCode == 0
                    ? new ProviderCliResult(exitCode, null)
                    : new ProviderCliResult(exitCode, $"The provider CLI exited with code {exitCode}.");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                await StopAndDrainAsync(process, stdoutTask, stderrTask, stdinTask).ConfigureAwait(false);
                throw new OperationCanceledException(cancellationToken);
            }
            catch (Exception)
            {
                await StopAndDrainAsync(process, stdoutTask, stderrTask, stdinTask).ConfigureAwait(false);
                return new ProviderCliResult(null, RunError);
            }
        }
    }

    private static async Task ForwardStdoutAsync(
        TextReader output,
        Func<string, CancellationToken, ValueTask>? onLine,
        CancellationToken cancellationToken)
    {
        while (await output.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (onLine is not null)
                await onLine(line, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Starts a CLI that stays running, with stdin left open for more input. The caller
    /// owns the process and must kill and dispose it.
    /// </summary>
    internal IProviderCliProcess StartInteractive(string executable, IEnumerable<string> arguments, string workingDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executable);
        var startInfo = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = workingDirectory,
            CreateNoWindow = true
        };
        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);
        CliEncoding.Apply(startInfo);
        return _processFactory.Start(startInfo);
    }

    private static async Task DrainStderrAsync(TextReader error, Action<string>? onLine)
    {
        while (await error.ReadLineAsync().ConfigureAwait(false) is { } line)
            onLine?.Invoke(line);
    }

    private static async Task WriteStdinAndCloseAsync(
        TextWriter input,
        string? contents,
        CancellationToken cancellationToken)
    {
        try
        {
            if (contents is not null)
                await input.WriteAsync(contents.AsMemory(), cancellationToken).ConfigureAwait(false);
            await input.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await input.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static async Task StopAndDrainAsync(
        IProviderCliProcess process,
        params Task[] streamTasks)
    {
        TryKillTree(process);
        try
        {
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception)
        {
        }

        var drain = Task.WhenAll(streamTasks);
        if (await Task.WhenAny(drain, Task.Delay(StreamShutdownGracePeriod)).ConfigureAwait(false) == drain)
        {
            try
            {
                await drain.ConfigureAwait(false);
            }
            catch (Exception)
            {
            }
            return;
        }

        // A user callback may ignore cancellation forever. Do not hold the caller or
        // disposed process open for it; retrieving a late aggregate observes any fault
        // raised by detached stdout, stderr, or stdin work.
        _ = drain.ContinueWith(
            static completed => _ = completed.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private static void TryKillTree(IProviderCliProcess process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception)
        {
            // The process may have exited between cancellation and Kill.
        }
    }
}

public sealed record ProviderCliResult(int? ExitCode, string? Error)
{
    public bool Succeeded => ExitCode == 0 && Error is null;
}

internal interface IProviderCliProcessFactory
{
    IProviderCliProcess Start(ProcessStartInfo startInfo);
}

internal interface IProviderCliProcess : IDisposable
{
    TextWriter StandardInput { get; }
    TextReader StandardOutput { get; }
    TextReader StandardError { get; }
    int ExitCode { get; }
    Task WaitForExitAsync(CancellationToken cancellationToken);
    void Kill(bool entireProcessTree);
}

internal sealed class ProviderCliProcessFactory : IProviderCliProcessFactory
{
    public IProviderCliProcess Start(ProcessStartInfo startInfo)
    {
        var process = Process.Start(startInfo);
        if (process is null)
            throw new InvalidOperationException("Process start returned false.");
        return new ProviderCliProcess(process);
    }

    private sealed class ProviderCliProcess(Process process) : IProviderCliProcess
    {
        public TextWriter StandardInput => process.StandardInput;
        public TextReader StandardOutput => process.StandardOutput;
        public TextReader StandardError => process.StandardError;
        public int ExitCode => process.ExitCode;

        public Task WaitForExitAsync(CancellationToken cancellationToken) =>
            process.WaitForExitAsync(cancellationToken);

        public void Kill(bool entireProcessTree) => process.Kill(entireProcessTree);

        public void Dispose() => process.Dispose();
    }
}
