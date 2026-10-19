using FluentAssertions;
using VideoOptimiser.Application.Configuration;
using VideoOptimiser.Application.Features.Run;
using VideoOptimiser.Application.Jobs;
using VideoOptimiser.Infrastructure.Features.Run;
using VideoOptimiser.Infrastructure.Tools;

namespace VideoOptimiser.UnitTests.Features.Run;

public sealed class RemoteShellTests
{
    /// <summary>Every ssh and sftp call goes through Build, so its keepalive is what stops a dead worker hanging the run.</summary>
    [Fact]
    public void ManagedConnectionOptionsAreValidForBothSshAndSftp()
    {
        var arguments = OpenSshConnectionArguments.Build(new RemoteSshSettings
        {
            User = "videoopt",
            IdentityFile = "C:\\keys\\worker",
            KnownHostsFile = "C:\\state\\known_hosts"
        });

        arguments.Should().ContainInOrder("-o", "User=videoopt", "-i", "C:\\keys\\worker", "-o", "IdentitiesOnly=yes");
        arguments.Should().NotContain("-l");
        arguments.Should().ContainInOrder("-o", "UserKnownHostsFile=C:\\state\\known_hosts", "-o", "StrictHostKeyChecking=yes");
        arguments.Should().ContainInOrder("-o", "ServerAliveInterval=15", "-o", "ServerAliveCountMax=4");
    }

    [Fact]
    public void ConnectionArgumentsPinTheConfiguredKnownHostsFile()
    {
        var arguments = OpenSshConnectionArguments.Build(new RemoteSshSettings { KnownHostsFile = "C:\\state\\known_hosts" }, true);

        arguments.Should().ContainInOrder("-o", "UserKnownHostsFile=C:\\state\\known_hosts", "-o", "StrictHostKeyChecking=accept-new");
    }

    [Fact]
    public void PosixQuotePreservesArguments() => PosixShell.Quote("a'b").Should().Be("'a'\"'\"'b'");

    [Fact]
    public void SftpQuoteNormalizesWindowsSeparators() => RemoteShell.SftpQuote("C:\\Video Files\\a.mkv").Should().Be("\"C:/Video Files/a.mkv\"");

    [Fact]
    public async Task RepeatedSsh255BecomesRemoteConnectionFailure()
    {
        var runner = new ScriptedProcessRunner(Enumerable.Repeat(new ExternalProcessResult(255, string.Empty, "connection refused"), 3));
        var progress = new ListProgress();

        var action = async () => await CreateShell(runner).SshAsync("true", progress: progress);

        await action.Should().ThrowAsync<RemoteConnectionException>();
        runner.Requests.Should().HaveCount(3);
        // Each resend says so, so a run that is recovering does not look stuck.
        progress.Lines.Should().Equal(
            JobProgress.Detail("Lost the connection to the worker (connection refused); trying again (try 2 of 3).", JobTone.Warning),
            JobProgress.Detail("Lost the connection to the worker (connection refused); trying again (try 3 of 3).", JobTone.Warning));
    }

    /// <summary>
    /// An ssh whose connection stays up but whose command never returns held a run for over half an hour; past the command
    /// timeout it counts as a lost connection, is resent, and the caller goes on with the resend's answer.
    /// </summary>
    [Fact]
    public async Task AnSshCommandWithNoReplyIsResentAfterTheTimeout()
    {
        var runner = new HangingRunner([null, Ok("answer")]);
        var progress = new ListProgress();

        var result = await CreateShell(runner, ShortTimeouts).SshAsync("true", progress: progress);

        result.StandardOutput.Should().Be("answer");
        runner.Requests.Should().HaveCount(2).And.OnlyContain(request => request.Arguments.Contains("-n"));
        // The resend and the answer that ends it are both said; the timeout here is 50 ms.
        progress.Lines.Should().Equal(
            JobProgress.Detail("The worker didn't answer within 0s; asking again (try 2 of 3).", JobTone.Warning),
            JobProgress.Detail("The worker answered again."));
    }

    [Fact]
    public async Task ThreeSshCommandsWithNoReplyBecomeARemoteConnectionFailure()
    {
        var runner = new HangingRunner([null, null, null]);

        var action = async () => await CreateShell(runner, ShortTimeouts).SshAsync("true");

        await action.Should().ThrowAsync<RemoteConnectionException>();
        runner.Requests.Should().HaveCount(3);
    }

    /// <summary>
    /// A command too slow for the first tries, such as a checksum of a large video, would be too slow for an equal resend; the
    /// last, longer try lets it finish instead of failing the video as an unreachable worker.
    /// </summary>
    [Fact]
    public async Task ACommandTooSlowForTheFirstTriesFinishesOnTheLongerLastTry()
    {
        var runner = new SlowRunner(TimeSpan.FromMilliseconds(300), [Ok("answer")]);

        var result = await CreateShell(runner, ShortTimeouts).SshAsync("true");

        result.StandardOutput.Should().Be("answer");
        runner.Requests.Should().HaveCount(3, "the command is cut off twice and answers on its third try");
    }

    private static readonly IReadOnlyList<TimeSpan> ShortTimeouts = [TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(50), TimeSpan.FromSeconds(1)];

    private static RemoteShell CreateShell(IExternalProcessRunner runner, IReadOnlyList<TimeSpan>? commandTimeouts = null) =>
        new(new AppSettings { Processing = new ProcessingSettings { Mode = "remoteSsh", RemoteSsh = new RemoteSshSettings { Host = "video-worker" } } },
            runner, commandTimeouts ?? RemoteShell.DefaultCommandTimeouts, _ => Task.CompletedTask);

    private static ExternalProcessResult Ok(string output = "") => new(0, output, string.Empty);

    /// <summary>Answers in order; a null answer is an ssh that never returns until its token is cancelled.</summary>
    private sealed class HangingRunner(IEnumerable<ExternalProcessResult?> results) : IExternalProcessRunner
    {
        private readonly Queue<ExternalProcessResult?> _results = new(results);
        public List<ExternalProcessRequest> Requests { get; } = [];

        public async Task<ExternalProcessResult> RunAsync(ExternalProcessRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            if (_results.Dequeue() is { } result) return result;
            await Task.Delay(Timeout.Infinite, cancellationToken);
            throw new System.Diagnostics.UnreachableException();
        }
    }

    /// <summary>Answers in order, each after <paramref name="delay"/>; a request cut off first leaves its answer for the resend.</summary>
    private sealed class SlowRunner(TimeSpan delay, IEnumerable<ExternalProcessResult> results) : IExternalProcessRunner
    {
        private readonly Queue<ExternalProcessResult> _results = new(results);
        public List<ExternalProcessRequest> Requests { get; } = [];

        public async Task<ExternalProcessResult> RunAsync(ExternalProcessRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            await Task.Delay(delay, cancellationToken);
            return _results.Dequeue();
        }
    }
}
