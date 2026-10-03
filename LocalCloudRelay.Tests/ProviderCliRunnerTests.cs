using System.Diagnostics;
using LocalCloudRelay;

namespace LocalCloudRelay.Tests;

public sealed class ProviderCliRunnerTests
{
    private static readonly string[] CliArguments = ["--print", "value with spaces", "$(not-a-shell-command)"];
    private static readonly string[] ExpectedOutputLines = ["first line", "second line"];

    [Fact]
    public async Task RunAsyncPreservesArgumentsAndStreamsOutputLines()
    {
        var fake = new FakeProcess("first line\nsecond line\n", "", 0, waitForExit: true);
        var factory = new FakeProcessFactory(fake);
        var runner = new ProviderCliRunner(factory);
        var lines = new List<string>();
        var args = CliArguments;

        var result = await runner.RunAsync("provider-cli", args, "prompt body", (line, _) =>
        {
            Assert.False(fake.ExitObserved);
            lines.Add(line);
            if (lines.Count == 2)
                fake.CompleteExit();
            return ValueTask.CompletedTask;
        });

        Assert.True(result.Succeeded);
        Assert.Equal(args, factory.StartInfo!.ArgumentList);
        Assert.False(factory.StartInfo.UseShellExecute);
        Assert.True(factory.StartInfo.RedirectStandardInput);
        Assert.Equal(ExpectedOutputLines, lines);
        Assert.Equal("prompt body", fake.Input.ToString());
        Assert.True(fake.ExitObserved);
    }

    [Fact]
    public async Task RunAsyncNonzeroExitDoesNotExposeStderr()
    {
        var runner = new ProviderCliRunner(new FakeProcessFactory(new FakeProcess("", "private access token: abc123", 17)));

        var result = await runner.RunAsync("provider-cli", [], null, null);

        Assert.False(result.Succeeded);
        Assert.Equal(17, result.ExitCode);
        Assert.NotNull(result.Error);
        Assert.DoesNotContain("private access token", result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("abc123", result.Error, StringComparison.Ordinal);
        Assert.True(result.Error.Length <= 128);
    }

    [Fact]
    public async Task RunAsyncCancellationKillsEntireProcessTreeAndWaitsForExit()
    {
        var fake = new FakeProcess("", "", 0, waitForExit: true);
        var runner = new ProviderCliRunner(new FakeProcessFactory(fake));
        using var cancellation = new CancellationTokenSource();
        var run = runner.RunAsync("provider-cli", [], null, null, cancellation.Token);
        await fake.WaitStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        Assert.True(fake.KilledEntireTree);
        Assert.True(fake.ExitObserved);
    }

    [Fact]
    public async Task RunAsyncCancellationDoesNotWaitForStdoutCallbackThatIgnoresCancellation()
    {
        var fake = new FakeProcess("response line\n", "", 0, waitForExit: true);
        var runner = new ProviderCliRunner(new FakeProcessFactory(fake));
        using var cancellation = new CancellationTokenSource();
        var callbackStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCallback = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var callbackFinished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var run = runner.RunAsync("provider-cli", [], null, async (_, _) =>
        {
            callbackStarted.TrySetResult();
            try
            {
                await releaseCallback.Task;
                throw new InvalidOperationException("late callback failure");
            }
            finally
            {
                callbackFinished.TrySetResult();
            }
        }, cancellation.Token);

        await callbackStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cancellation.Cancel();

        var returnedPromptly = await Task.WhenAny(run, Task.Delay(TimeSpan.FromSeconds(2))) == run;
        releaseCallback.TrySetResult();
        if (!returnedPromptly)
        {
            try
            {
                await run.WaitAsync(TimeSpan.FromSeconds(2));
            }
            catch (OperationCanceledException)
            {
            }
        }

        Assert.True(returnedPromptly, "Cancellation must not wait indefinitely for a stdout callback that ignores its token.");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        await callbackFinished.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(fake.KilledEntireTree);
        Assert.True(fake.ExitObserved);
    }

    [Fact]
    public async Task RunAsyncAlreadyCancelledDoesNotStartAProcess()
    {
        var factory = new FakeProcessFactory(new FakeProcess("", "", 0));
        var runner = new ProviderCliRunner(factory);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            runner.RunAsync("provider-cli", [], null, null, cancellation.Token));

        Assert.Null(factory.StartInfo);
    }

    [Fact]
    public async Task RunAsyncStartFailureReturnsBoundedGenericError()
    {
        var runner = new ProviderCliRunner(new ThrowingProcessFactory());

        var result = await runner.RunAsync("provider-cli", [], null, null);

        Assert.False(result.Succeeded);
        Assert.Null(result.ExitCode);
        Assert.Equal("The provider CLI could not be started.", result.Error);
        Assert.DoesNotContain("account-secret", result.Error, StringComparison.Ordinal);
    }

    private sealed class FakeProcessFactory(FakeProcess process) : IProviderCliProcessFactory
    {
        public ProcessStartInfo? StartInfo { get; private set; }

        public IProviderCliProcess Start(ProcessStartInfo startInfo)
        {
            StartInfo = startInfo;
            return process;
        }
    }

    private sealed class ThrowingProcessFactory : IProviderCliProcessFactory
    {
        public IProviderCliProcess Start(ProcessStartInfo startInfo) =>
            throw new InvalidOperationException("account-secret must not leak");
    }

    private sealed class FakeProcess : IProviderCliProcess
    {
        private readonly bool _waitForExit;
        private readonly TaskCompletionSource _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly StringReader _output;
        private readonly StringReader _error;

        public FakeProcess(string output, string error, int exitCode, bool waitForExit = false)
        {
            _output = new StringReader(output);
            _error = new StringReader(error);
            ExitCode = exitCode;
            _waitForExit = waitForExit;
        }

        public StringWriter Input { get; } = new();
        public TextWriter StandardInput => Input;
        public TextReader StandardOutput => _output;
        public TextReader StandardError => _error;
        public int ExitCode { get; }
        public bool KilledEntireTree { get; private set; }
        public bool ExitObserved { get; private set; }
        public TaskCompletionSource WaitStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task WaitForExitAsync(CancellationToken cancellationToken)
        {
            WaitStarted.TrySetResult();
            if (_waitForExit)
                await _exit.Task.WaitAsync(cancellationToken);
            ExitObserved = true;
        }

        public void Kill(bool entireProcessTree)
        {
            KilledEntireTree = entireProcessTree;
            _exit.TrySetResult();
        }

        public void CompleteExit() => _exit.TrySetResult();

        public void Dispose()
        {
            _output.Dispose();
            _error.Dispose();
            Input.Dispose();
        }
    }
}
