using System.Diagnostics;
using FluentAssertions;
using VideoOptimiser.Infrastructure.Tools;

namespace VideoOptimiser.UnitTests;

/// <summary>
/// Real children, Windows-only like the tools: <c>ping</c> runs for 30 seconds and never reads its input, so it stands in
/// for a tool that is still busy; <c>cmd /c for</c> prints more lines than a pipe holds. The timeouts only stop a regression
/// from hanging the suite; a passing test finishes long before them.
/// </summary>
public sealed class ChildProcessTests
{
    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(10);
    private static readonly ExternalProcessRequest LongPing = new("ping.exe", ["-n", "30", "127.0.0.1"]);

    [Fact]
    public async Task ACancelDuringTheStandardInputWriteKillsTheChildAndThrowsTheCancellation()
    {
        // More input than the pipe holds, to a child that never reads it, so the write is still pending when the cancel lands.
        // Were the cancel to land before the write began, the guarantee would be the same: no wait here decides the outcome.
        var request = LongPing with { StandardInput = new string('x', 1024 * 1024) };
        using var cancellation = new CancellationTokenSource();
        Process? child = null;

        var run = () => ChildProcess.RunAsync(request, id =>
        {
            child = Process.GetProcessById(id);
            cancellation.CancelAfter(TimeSpan.FromMilliseconds(50));
        }, cancellation.Token).WaitAsync(Guard);

        await run.Should().ThrowAsync<OperationCanceledException>();
        using (child) child!.HasExited.Should().BeTrue();
    }

    [Fact]
    public async Task ALineCallbackThatThrowsDoesNotHangTheChildAndItsExceptionSurfacesAfterExit()
    {
        // Far more output than a pipe holds: were the reader to stop at the first line, the child would block writing.
        var request = new ExternalProcessRequest("cmd.exe", ["/c", "for /l %i in (1,1,3000) do @echo line %i"]);

        var run = () => ChildProcess.StreamAsync(request, _ => throw new CallbackFailed(), null).WaitAsync(Guard);

        await run.Should().ThrowAsync<CallbackFailed>();
    }

    [Fact]
    public async Task ACancelWhileStreamingKillsTheChildAndThrowsTheCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        Process? child = null;

        var run = () => ChildProcess.StreamAsync(LongPing, _ => cancellation.Cancel(), null, id => child = Process.GetProcessById(id), cancellation.Token).WaitAsync(Guard);

        await run.Should().ThrowAsync<OperationCanceledException>();
        using (child) child!.HasExited.Should().BeTrue();
    }

    private sealed class CallbackFailed : Exception;
}
