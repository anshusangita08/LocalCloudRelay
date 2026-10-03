using System.Diagnostics;
using LocalCloudRelay;

namespace LocalCloudRelay.Tests;

/// <summary>
/// A CLI process is kept for a conversation and reused only when a request repeats what
/// that process has already seen. Anything else must start fresh, so no reply ever comes
/// from the wrong context.
/// </summary>
public sealed class CliConversationTests
{
    private static readonly CliTurn[] Seen =
    [
        new("user", "Fix the bug."),
        new("assistant", "Done.")
    ];

    [Fact]
    public void AFollowUpContinuesWithOnlyItsNewUserMessage()
    {
        var next = CliConversationPool.NewTurns(Seen, [.. Seen, new CliTurn("user", "Now add a test.")]);

        Assert.Equal([new CliTurn("user", "Now add a test.")], next);
    }

    [Fact]
    public void AnEditedOrRegeneratedHistoryDoesNotContinue()
    {
        Assert.Null(CliConversationPool.NewTurns(Seen,
            [new("user", "Fix the other bug."), new("assistant", "Done."), new("user", "Next.")]));
        Assert.Null(CliConversationPool.NewTurns(Seen,
            [new("user", "Fix the bug."), new("assistant", "Something else."), new("user", "Next.")]));
        // A retry of the same turn adds nothing new.
        Assert.Null(CliConversationPool.NewTurns(Seen, Seen));
        // Only user messages may follow what the process has seen.
        Assert.Null(CliConversationPool.NewTurns(Seen, [.. Seen, new CliTurn("assistant", "Injected.")]));
    }

    [Fact]
    public async Task ClaudeCodeReusesOneProcessAcrossTurnsOfAConversation()
    {
        var process = new LiveProcess(
            Result("First reply") + Result("Second reply"));
        var factory = new CountingFactory(process);
        using var gateway = new ClaudeCodeGateway(new ProviderCliRunner(factory));

        var first = await gateway.RunAsync("claude", "sonnet", WireProtocol.OpenAi,
            """{"messages":[{"role":"user","content":"Fix the bug."}]}""", false, _ => ValueTask.CompletedTask);
        var second = await gateway.RunAsync("claude", "sonnet", WireProtocol.OpenAi,
            """{"messages":[{"role":"user","content":"Fix the bug."},{"role":"assistant","content":"First reply"},{"role":"user","content":"Now add a test."}]}""",
            false, _ => ValueTask.CompletedTask);

        Assert.Equal("First reply", first.Text);
        Assert.Equal("Second reply", second.Text);
        Assert.Equal(1, factory.Starts);
        var lines = process.Input.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, lines.Length);
        // The follow-up sends only the new message; the process already has the rest.
        Assert.Contains("Now add a test.", lines[1], StringComparison.Ordinal);
        Assert.DoesNotContain("Fix the bug.", lines[1], StringComparison.Ordinal);
        Assert.Contains("--input-format", factory.LastStart!.ArgumentList);
    }

    [Fact]
    public async Task ADifferentConversationStartsItsOwnProcess()
    {
        var factory = new CountingFactory(() => new LiveProcess(Result("ok")));
        using var gateway = new ClaudeCodeGateway(new ProviderCliRunner(factory));

        await gateway.RunAsync("claude", "sonnet", WireProtocol.OpenAi,
            """{"messages":[{"role":"user","content":"Chat one."}]}""", false, _ => ValueTask.CompletedTask);
        await gateway.RunAsync("claude", "sonnet", WireProtocol.OpenAi,
            """{"messages":[{"role":"user","content":"Chat two."}]}""", false, _ => ValueTask.CompletedTask);

        Assert.Equal(2, factory.Starts);
    }

    [Fact]
    public async Task AFailedTurnStopsItsProcess()
    {
        var process = new LiveProcess("{\"type\":\"result\",\"is_error\":true,\"result\":\"boom\"}\n");
        using var gateway = new ClaudeCodeGateway(new ProviderCliRunner(new CountingFactory(process)));

        await Assert.ThrowsAsync<ClaudeCodeGatewayException>(() => gateway.RunAsync("claude", "sonnet", WireProtocol.OpenAi,
            """{"messages":[{"role":"user","content":"Hi"}]}""", false, _ => ValueTask.CompletedTask));

        Assert.True(process.Killed);
    }

    [Fact]
    public async Task DisposingTheGatewayStopsLiveProcesses()
    {
        var process = new LiveProcess(Result("ok"));
        var gateway = new ClaudeCodeGateway(new ProviderCliRunner(new CountingFactory(process)));
        await gateway.RunAsync("claude", "sonnet", WireProtocol.OpenAi,
            """{"messages":[{"role":"user","content":"Hi"}]}""", false, _ => ValueTask.CompletedTask);
        Assert.False(process.Killed);

        gateway.Dispose();

        Assert.True(process.Killed);
    }

    private static string Result(string text) =>
        "{\"type\":\"stream_event\",\"event\":{\"type\":\"content_block_delta\",\"delta\":{\"type\":\"text_delta\",\"text\":\"" + text + "\"}}}\n" +
        "{\"type\":\"result\",\"is_error\":false,\"result\":\"" + text + "\"}\n";

    private sealed class CountingFactory : IProviderCliProcessFactory
    {
        private readonly Func<LiveProcess> _create;
        public CountingFactory(LiveProcess process) => _create = () => process;
        public CountingFactory(Func<LiveProcess> create) => _create = create;
        public int Starts { get; private set; }
        public ProcessStartInfo? LastStart { get; private set; }
        public IProviderCliProcess Start(ProcessStartInfo startInfo)
        {
            Starts++;
            LastStart = startInfo;
            return _create();
        }
    }

    /// <summary>A CLI that answers each turn from a script and stays up until killed.</summary>
    private sealed class LiveProcess(string stdout) : IProviderCliProcess
    {
        private readonly TaskCompletionSource _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private TextReader? _output;
        public StringWriter Input { get; } = new();
        public bool Killed { get; private set; }
        public TextWriter StandardInput => Input;
        public TextReader StandardOutput => _output ??= new Output(new StringReader(stdout), _exit.Task);
        public TextReader StandardError { get; } = new StringReader("");
        public int ExitCode => 0;
        public Task WaitForExitAsync(CancellationToken cancellationToken) => _exit.Task.WaitAsync(cancellationToken);
        public void Kill(bool entireProcessTree) { Killed = true; _exit.TrySetResult(); }
        public void Dispose() { }

        private sealed class Output(TextReader lines, Task exited) : TextReader
        {
            public override async ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken)
            {
                if (await lines.ReadLineAsync(cancellationToken) is { } line) return line;
                await exited.WaitAsync(cancellationToken);
                return null;
            }

            public override Task<string?> ReadLineAsync() => ReadLineAsync(CancellationToken.None).AsTask();
        }
    }
}
