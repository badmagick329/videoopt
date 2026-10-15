using FluentAssertions;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using VideoOptimiser.Application.Configuration;
using VideoOptimiser.Application.Features.Run;
using VideoOptimiser.Application.Jobs;
using VideoOptimiser.Application.Tools;
using VideoOptimiser.Domain.Jobs;
using VideoOptimiser.Infrastructure.Features.Run;
using VideoOptimiser.Infrastructure.Tools;

namespace VideoOptimiser.UnitTests.Features.Run;

public sealed class RemoteSshProcessingSessionTests : IDisposable
{
    /// <summary>A 100-byte original whose bytes count up from 0, so every prefix has its own checksum.</summary>
    private static readonly byte[] Original = [.. Enumerable.Range(0, 100).Select(value => (byte)value)];

    /// <summary>The encoded output on the worker.</summary>
    private static readonly byte[] Good = "verified-output"u8.ToArray();

    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"VideoOptimiser.Remote.{Guid.NewGuid():N}");

    public RemoteSshProcessingSessionTests() => Directory.CreateDirectory(_directory);

    private string Output => Path.Combine(_directory, "output.mkv");

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
    public void UsesDeterministicGuidWorkspaceAndAttemptPaths()
    {
        var id = Guid.Parse("11111111-2222-3333-4444-555555555555");
        var session = CreateSession(id, new QueueRunner([]));

        session.RemoteWorkspace.Should().Be("/var/tmp/video-optimiser/11111111222233334444555555555555");
        session.RemoteSourcePath.Should().Be("/var/tmp/video-optimiser/11111111222233334444555555555555/source.mkv");
        session.CrfDirectory.Should().EndWith("/crf");
        session.EncodeDirectory(3).Should().EndWith("/encode-3");
    }

    [Fact]
    public void PosixQuotePreservesArguments() => RemoteSshProcessingSession.Quote("a'b").Should().Be("'a'\"'\"'b'");

    [Fact]
    public void SftpQuoteNormalizesWindowsSeparators() => RemoteSshProcessingSession.SftpQuote("C:\\Video Files\\a.mkv").Should().Be("\"C:/Video Files/a.mkv\"");

    [Fact]
    public void RequiredDiskRoundsUp() => RemoteSshProcessingSession.CalculateRequiredDisk(101, 2.5).Should().Be(253);

    [Theory]
    [InlineData("2 1024 100000", "Remote host has 2 CPUs*")]
    [InlineData("8 1024 100000", "Remote host has 1024 available bytes*")]
    [InlineData("8 20000000000 200", "Remote host has 200 free bytes; 250 are required*")]
    public async Task RejectsInsufficientRemoteResourcesBeforeTransfer(string probeOutput, string expectedMessage)
    {
        var source = await SourceAsync();
        var runner = new QueueRunner([Ok(probeOutput), Ok()]);

        var action = async () => await CreateSession(Guid.NewGuid(), runner, source).StageAsync(source, "fingerprint");

        await action.Should().ThrowAsync<InvalidOperationException>().WithMessage(expectedMessage);
        runner.Requests.Should().NotContain(request => request.FileName == "sftp");
    }

    [Fact]
    public async Task ReusesAValidatedPartialUploadPrefixWithReput()
    {
        var source = await SourceAsync();
        var runner = new QueueRunner([Ok("8 20000000000 100000"), Ok(), Ok($"40 {Sha256(Original.AsSpan(0, 40))}"), Ok(), Ok()]);
        var progress = new ListProgress();

        await CreateSession(Guid.NewGuid(), runner, source).StageAsync(source, "fingerprint", progress);

        progress.Lines.Should().Equal(
            JobProgress.Step("Resuming the upload at 40 B of 100 B..."),
            JobProgress.Step("Uploaded 100 B in 0s, checksum verified."));
        runner.Requests.Should().ContainSingle(request => request.FileName == "sftp" && request.StandardInput!.StartsWith("reput ", StringComparison.Ordinal));
        runner.Requests.SelectMany(request => request.Arguments).Should().NotContain(argument => argument.Contains("rm -f --", StringComparison.Ordinal));
    }

    /// <summary>An upload whose connection drops resumes from what reached the worker, not from 0.</summary>
    [Fact]
    public async Task AnUploadThatLosesItsConnectionResumesFromTheVerifiedPartial()
    {
        var source = await SourceAsync();
        var runner = new DroppedUploadRunner($"40 {Sha256(Original.AsSpan(0, 40))}");
        var progress = new ListProgress();

        await CreateSession(Guid.NewGuid(), runner, source).StageAsync(source, "fingerprint", progress);

        runner.Transfers.Should().Equal("put", "reput");
        progress.Lines.Should().Equal(
            JobProgress.Step("Uploading 100 B to the worker..."),
            JobProgress.Step("Lost the connection to the worker; resuming the upload at 40 B of 100 B..."),
            JobProgress.Step("Uploaded 100 B in 0s, checksum verified."));
    }

    /// <summary>Nothing usable on the worker after the drop: an empty partial, or one that is not a prefix of the original
    /// (which is deleted), uploads from the start again.</summary>
    [Theory]
    [InlineData(0, false)]
    [InlineData(40, true)]
    public async Task AnUploadThatLosesItsConnectionWithNothingUsableOnTheWorkerStartsAgain(int partialLength, bool mismatched)
    {
        var source = await SourceAsync();
        var runner = new DroppedUploadRunner($"{partialLength} {(mismatched ? new string('0', 64) : Sha256(Original.AsSpan(0, partialLength)))}");
        var progress = new ListProgress();

        await CreateSession(Guid.NewGuid(), runner, source).StageAsync(source, "fingerprint", progress);

        runner.Transfers.Should().Equal("put", "put");
        runner.PartialRemovedAfterDrop.Should().BeTrue();
        progress.Lines.Should().Equal(
            JobProgress.Step("Uploading 100 B to the worker..."),
            JobProgress.Step("Lost the connection to the worker; uploading 100 B again from the start..."),
            JobProgress.Step("Uploaded 100 B in 0s, checksum verified."));
    }

    /// <summary>A drop after the last byte leaves a complete partial, which reput would refuse with a permanent failure.</summary>
    [Fact]
    public async Task AnUploadThatLosesItsConnectionAfterTheLastByteGoesStraightToVerification()
    {
        var source = await SourceAsync();
        var runner = new DroppedUploadRunner($"100 {Sha256(Original)}");
        var progress = new ListProgress();

        await CreateSession(Guid.NewGuid(), runner, source).StageAsync(source, "fingerprint", progress);

        runner.Transfers.Should().Equal("put");
        runner.Verified.Should().BeTrue();
        progress.Lines.Should().Equal(
            JobProgress.Step("Uploading 100 B to the worker..."),
            JobProgress.Step("Uploaded 100 B in 0s, checksum verified."));
    }

    /// <summary>The dropped session can still finish the partial after the re-check; the reput it then refuses is not a failure,
    /// but a refusal with the partial still short is.</summary>
    [Theory]
    [InlineData(100, true)]
    [InlineData(60, false)]
    public async Task AResumedUploadRefusedBecauseTheDroppedSessionFinishedThePartialGoesToVerification(int lengthAfterRefusal, bool succeeds)
    {
        var source = await SourceAsync();
        var runner = new DroppedUploadRunner($"40 {Sha256(Original.AsSpan(0, 40))}", $"{lengthAfterRefusal} {Sha256(Original.AsSpan(0, lengthAfterRefusal))}");
        var progress = new ListProgress();

        var action = async () => await CreateSession(Guid.NewGuid(), runner, source).StageAsync(source, "fingerprint", progress);

        if (succeeds)
        {
            await action.Should().NotThrowAsync();
            progress.Lines[^1].Should().Be(JobProgress.Step("Uploaded 100 B in 0s, checksum verified."));
        }
        else await action.Should().ThrowAsync<InvalidOperationException>().WithMessage("SFTP transfer failed: destination file*");
        runner.Transfers.Should().Equal("put", "reput");
    }

    [Fact]
    public async Task AnUploadWhoseRecheckCannotReachTheWorkerFailsAsUnreachable()
    {
        var source = await SourceAsync();
        var runner = new DroppedUploadRunner(afterDrop: null);

        var action = async () => await CreateSession(Guid.NewGuid(), runner, source).StageAsync(source, "fingerprint", new ListProgress());

        await action.Should().ThrowAsync<RemoteConnectionException>();
        runner.Transfers.Should().Equal("put");
    }

    [Theory]
    [InlineData(40, false)]
    [InlineData(150, true)]
    public async Task ADivergentPartialUploadIsRemovedAndTheOriginalIsUploadedAgainFromTheStart(int partialLength, bool hashMatchesTheWholeOriginal)
    {
        var source = await SourceAsync();
        // A partial longer than the original cannot be a prefix of it, even when its checksum equals the whole file's.
        var partialHash = hashMatchesTheWholeOriginal ? Sha256(Original) : new string('0', 64);
        var runner = new QueueRunner([Ok("8 20000000000 100000"), Ok(), Ok($"{partialLength} {partialHash}"), Ok(), Ok(), Ok()]);
        var progress = new ListProgress();
        var session = CreateSession(Guid.NewGuid(), runner, source, new FakeClock());

        await session.StageAsync(source, "fingerprint", progress);

        var partial = session.RemoteSourcePath + ".partial";
        var removal = runner.Requests.FindIndex(request => request.FileName == "ssh" && request.Arguments[^1].Contains("rm -f --", StringComparison.Ordinal) && request.Arguments[^1].Contains(partial, StringComparison.Ordinal));
        var transfer = runner.Requests.FindIndex(request => request.FileName == "sftp");
        removal.Should().BeGreaterThan(-1, "bytes that do not belong to this original must not be built upon");
        transfer.Should().BeGreaterThan(removal);
        runner.Requests[transfer].StandardInput.Should().StartWith("put ");
        runner.Requests.Should().NotContain(request => request.StandardInput != null && request.StandardInput.StartsWith("reput ", StringComparison.Ordinal));
        progress.Lines.Should().Equal(
            JobProgress.Step("Uploading 100 B to the worker..."),
            JobProgress.Step("Uploaded 100 B in 0s, checksum verified."));
    }

    [Fact]
    public async Task StagesWithResumableSftpAndAtomicRemoteRename()
    {
        var source = await SourceAsync();
        var runner = new QueueRunner([Ok("8 20000000000 100000000000"), Ok(), Ok(), Ok(), Ok()]);
        var progress = new ListProgress();

        // The checks (CPU, hash, existing upload, verification) and the quick transfer all run inside the first second.
        await CreateSession(Guid.NewGuid(), runner, clock: new FakeClock()).StageAsync(source, "fingerprint", progress);

        progress.Lines.Should().Equal(
            JobProgress.Step("Uploading 100 B to the worker..."),
            JobProgress.Step("Uploaded 100 B in 0s, checksum verified."));
        runner.Requests[0].Arguments[^1].Should().Contain("df -B1 --output=avail").And.NotContain("df -PB1");
        runner.Requests.Should().Contain(request => request.FileName == "sftp" && request.StandardInput!.StartsWith("put ", StringComparison.Ordinal));
        runner.Requests[^1].Arguments[^1].Should().Contain("mv -f --");
    }

    [Fact]
    public async Task UploadProgressAppearsOnlyAfterTenSecondsThenAtMostOnceEveryTenSeconds()
    {
        var source = await SourceAsync(new byte[1_048_576]);
        var clock = new FakeClock();
        var runner = new UploadRunner(clock, remoteSizes: [262_144, 524_288], finishAtTick: 6);
        var progress = new ListProgress();

        await CreateSession(Guid.NewGuid(), runner, clock: clock).StageAsync(source, "fingerprint", progress);

        // Ticks are 5 s apart: nothing at 5 s, the first line at 10 s, nothing at 15 s, the second at 20 s, nothing at 25 s.
        progress.Lines.Should().Equal(
            JobProgress.Step("Uploading 1.0 MiB to the worker..."),
            JobProgress.Detail("25% of 1.0 MiB, 25.6 KiB/s, about 30s left"),
            JobProgress.Detail("50% of 1.0 MiB, 25.6 KiB/s, about 20s left"),
            JobProgress.Step("Uploaded 1.0 MiB in 30s, checksum verified."));
        runner.SizePolls.Should().Be(2, "a poll is an SSH connection, so none is made while no line is due");
    }

    /// <summary>Its single line also shows that a hash quicker than the delay prints no progress.</summary>
    [Fact]
    public async Task AnOriginalAlreadyOnTheWorkerIsOneLineAndNoTransfer()
    {
        var source = await SourceAsync();
        var runner = new QueueRunner([Ok("8 20000000000 100000000000"), Ok($"100 {Sha256(Original)}")]);
        var progress = new ListProgress();

        await CreateSession(Guid.NewGuid(), runner).StageAsync(source, "fingerprint", progress);

        progress.Lines.Should().Equal(JobProgress.Step("Already on the worker, checksum verified."));
        runner.Requests.Should().NotContain(request => request.FileName == "sftp");
    }

    [Fact]
    public async Task HashingPrintsProgressWhenItOutlastsTheDelay()
    {
        var bytes = new byte[3 * 1_048_576];
        var source = await SourceAsync(bytes);
        var runner = new QueueRunner([Ok("8 20000000000 100000000000"), Ok($"{bytes.Length} {Sha256(bytes)}")]);
        var progress = new ListProgress();
        // A clock that moves on every reading makes each read of the file look ten seconds apart.
        var clock = new FakeClock { StepPerReading = TimeSpan.FromSeconds(10) };

        await CreateSession(Guid.NewGuid(), runner, clock: clock).StageAsync(source, "fingerprint", progress);

        // The percentages are details, so without a step before them they hang under the job's header (VOPT-72, B19).
        progress.Lines[0].Should().Be(JobProgress.Step("Checking the original's checksum before upload."));
        progress.Lines.Count(line => line.Kind == JobProgressKind.Step && line.Text.StartsWith("Checking", StringComparison.Ordinal)).Should().Be(1);
        var checks = progress.Lines.Where(line => line.Kind == JobProgressKind.Detail).ToArray();
        checks.Length.Should().BeGreaterThanOrEqualTo(3);
        checks.Should().OnlyContain(line => line.Text.StartsWith("Checking the checksum: ", StringComparison.Ordinal) && line.Text.EndsWith("% of 3.0 MiB", StringComparison.Ordinal));
    }

    [Fact]
    public async Task FailedUploadSizePollDoesNotAbortSuccessfulSftpTransfer()
    {
        var source = await SourceAsync();
        var runner = new FailingSizePollRunner();
        var session = CreateSession(Guid.NewGuid(), runner, clock: new FakeClock { TickLength = TimeSpan.FromSeconds(10) });

        // Progress is what makes the session poll at all, and only a poll that is due: the tick is 10 s, so the first one is.
        await session.StageAsync(source, "fingerprint", new ListProgress());

        runner.SftpCompleted.Should().BeTrue();
        runner.VerificationObserved.Should().BeTrue();
    }

    /// <summary>No command watched the stage end and none kept its log, so the rerun says it finished while this one was stopped.</summary>
    [Fact]
    public async Task ReattachesToACompletedCrfStageWithoutLaunchingAnotherScript()
    {
        var source = await SourceAsync();
        var log = AbAv1OutputTests.Fixture("remote-crf-search.log");
        var runner = new QueueRunner([Ok(AtEnd("completed 0", log)), Ok(log)]);
        var session = CreateSession(Guid.NewGuid(), runner, source);
        var progress = new ListProgress();

        var result = await session.SearchCrfAsync(source, new QualitySettings(), progress);

        result.Crf.Should().Be(29);
        result.Prediction.Should().Be(new CrfPrediction("284.87 MiB", 29, "5 minutes"));
        runner.Requests.Should().HaveCount(2);
        runner.Requests.Should().OnlyContain(request => request.StandardInput == null);
        File.Exists(Path.Combine(_directory, ".video-optimiser", $"{session.RemoteWorkspace!.Split('/').Last()}.crf.remote.log")).Should().BeTrue();
        Steps(progress).Should().Equal("The CRF search on the worker had already finished.");
    }

    /// <summary>A launch that ran on the worker but lost its reply is resent by the SSH retry; the resend must not start a
    /// second script, and the stage reads as missing until the first one writes its pid.</summary>
    [Fact]
    public async Task AResentLaunchAfterALostReplyStartsNoSecondScript()
    {
        var source = await SourceAsync();
        var worker = new StageWorker(["missing", "missing", "running", "completed 0"]) { DropFirstLaunchReply = true };

        var result = await CreateSession(Guid.NewGuid(), worker, source).SearchCrfAsync(source, new QualitySettings());

        result.Crf.Should().Be(42);
        worker.Launches.Should().HaveCount(2);
        worker.Launches[1].Should().Be(worker.Launches[0]);
        worker.ScriptsStarted.Should().Be(1);
    }

    /// <summary>A script that died without an exit code (a reboot, an OOM kill) leaves its marker behind; the next command
    /// must still launch the stage again.</summary>
    [Fact]
    public async Task AStageWhoseScriptDiedLeavingItsMarkerIsLaunchedAgainByTheNextCommand()
    {
        var source = await SourceAsync();
        var worker = new StageWorker(["missing", "missing", "missing", "missing", "missing", "missing", "running", "completed 0"]);
        var id = Guid.NewGuid();

        var died = async () => await CreateSession(id, worker, source).SearchCrfAsync(source, new QualitySettings());
        await died.Should().ThrowAsync<InvalidOperationException>().WithMessage("Remote stage has neither*");
        var result = await CreateSession(id, worker, source).SearchCrfAsync(source, new QualitySettings());

        result.Crf.Should().Be(42);
        worker.Launches.Should().HaveCount(2);
        worker.ScriptsStarted.Should().Be(2);
    }

    [Fact]
    public async Task FreshStageUsesWritableCacheInsideItsGuidWorkspace()
    {
        var source = await SourceAsync();
        var runner = new QueueRunner([Ok(AtEnd("missing", "")), Ok(), Ok(), Ok(Sent("completed 0", "crf 42 successful\n")), Ok("crf 42 successful\n")]);
        var session = CreateSession(Guid.NewGuid(), runner, source);

        _ = await session.SearchCrfAsync(source, new QualitySettings());

        var script = runner.Requests.Single(request => request.StandardInput?.StartsWith("#!/usr/bin/env bash", StringComparison.Ordinal) == true).StandardInput!;
        var expectedCache = $"{session.CrfDirectory}/cache";
        script.Should().Contain($"mkdir -p -- '{expectedCache}'");
        script.Should().Contain($"export XDG_CACHE_HOME='{expectedCache}'");
        expectedCache.StartsWith(session.RemoteWorkspace + "/", StringComparison.Ordinal).Should().BeTrue();
        script.IndexOf("export XDG_CACHE_HOME", StringComparison.Ordinal).Should().BeLessThan(script.IndexOf("'/usr/bin/time'", StringComparison.Ordinal));

        // Backgrounding the whole `cd && nohup …` list keeps a subshell on the SSH channel until ab-av1 exits, which
        // turns live progress into one dump at the end of each stage (VOPT-41). Only the braced script may be backgrounded.
        var launch = runner.Requests[2].Arguments[^1];
        launch.Should().Contain("{ nohup setsid bash ");
        launch.Should().Contain("</dev/null >/dev/null 2>&1 & }");
        launch.Should().Contain($"cd -- '\"'\"'{session.CrfDirectory}'\"'\"' && ");
        RemoteSshProcessingSession.BuildLaunchCommand("/w/a b", "/w/a b/run.sh", "launch-1").Should().Be("cd -- '/w/a b' && if mkdir -- 'launch-1'; then { nohup setsid bash '/w/a b/run.sh' </dev/null >/dev/null 2>&1 & }; elif ! test -d 'launch-1'; then exit 1; fi");
    }

    [Fact]
    public async Task ReattachesToCompletedEncodingAndVerifiesItsOutput()
    {
        var source = await SourceAsync();
        var runner = new QueueRunner([Ok(AtEnd("completed 0", "encode complete\n")), Ok("encode complete\n"), Ok()]);

        var result = await CreateSession(Guid.NewGuid(), runner, source).EncodeAsync(source, Output, 42, new QualitySettings(), 3, []);

        result.OutputPath.Should().Be(Output);
        runner.Requests.Should().HaveCount(3);
        runner.Requests.Should().OnlyContain(request => request.StandardInput == null);
        runner.Requests[^1].Arguments[^1].Should().Contain("test -s");
    }

    [Theory]
    [InlineData("", "Remote output checksum was missing.")]
    [InlineData("15\n0123456789abcdef", "Remote output checksum was invalid.")]
    public async Task ARemoteChecksumThatIsMissingOrNotSha256HexStopsBeforeAnythingIsDownloadedOrInstalled(string remoteOutput, string expectedMessage)
    {
        var source = await SourceAsync();
        var runner = new QueueRunner([Ok(remoteOutput)]);

        var action = async () => await CreateSession(Guid.NewGuid(), runner, source).RetrieveOutputAsync(Output, 1);

        await action.Should().ThrowAsync<InvalidDataException>().WithMessage(expectedMessage);
        runner.Requests.Should().ContainSingle().Which.FileName.Should().Be("ssh");
        File.Exists(Output).Should().BeFalse();
        File.Exists(Output + ".partial").Should().BeFalse();
    }

    [Fact]
    public async Task DownloadReportsRemoteSizeAndResumesExistingPartial()
    {
        var source = await SourceAsync();
        await File.WriteAllBytesAsync(Output + ".partial", Good[..5]);
        var progress = new ListProgress();

        await CreateSession(Guid.NewGuid(), new DownloadRunner(Output, Good), source).RetrieveOutputAsync(Output, 1, progress);

        progress.Lines.Should().Equal(
            JobProgress.Step("Resuming the download at 5 B of 15 B..."),
            JobProgress.Step("Downloaded 15 B in 0s, checksum verified."));
        File.ReadAllBytes(Output).Should().Equal(Good);
    }

    /// <summary>
    /// A partial longer than the output is left from an earlier encode (a worker deleted mid-download); sftp would refuse to
    /// resume onto it and fail the job for good, so the download starts over.
    /// </summary>
    [Fact]
    public async Task APartialLongerThanTheOutputIsDiscardedAndTheDownloadStartsOver()
    {
        var source = await SourceAsync();
        await File.WriteAllTextAsync(Output + ".partial", "an older, longer encode's partial download");
        var progress = new ListProgress();

        await CreateSession(Guid.NewGuid(), new DownloadRunner(Output, Good), source).RetrieveOutputAsync(Output, 1, progress);

        progress.Lines[0].Should().Be(JobProgress.Step("Downloading 15 B from the worker..."));
        File.ReadAllBytes(Output).Should().Equal(Good);
    }

    /// <summary>Its exact lines also show that a download prints only its start and summary lines, besides the mismatch.</summary>
    [Fact]
    public async Task ADownloadThatFailsItsChecksumSaysSoAndDownloadsAgain()
    {
        var source = await SourceAsync();
        var runner = new DownloadRunner(Output, Good, corruptTransfers: 1);
        var progress = new ListProgress();

        await CreateSession(Guid.NewGuid(), runner, source).RetrieveOutputAsync(Output, 1, progress);

        progress.Lines.Should().Equal(
            JobProgress.Step("Downloading 15 B from the worker..."),
            JobProgress.Detail("Checksum mismatch; downloading again."),
            JobProgress.Step("Downloaded 15 B in 0s, checksum verified."));
        runner.Transfers.Should().Be(2);
        File.ReadAllBytes(Output).Should().Equal(Good);
        File.Exists(Output + ".partial").Should().BeFalse();
    }

    [Fact]
    public async Task ALocalOutputThatAlreadyMatchesIsNotDownloadedAgain()
    {
        var source = await SourceAsync();
        await File.WriteAllBytesAsync(Output, Good);
        var runner = new QueueRunner([Ok($"{Good.Length}\n{Sha256(Good)}")]);
        var progress = new ListProgress();

        await CreateSession(Guid.NewGuid(), runner, source).RetrieveOutputAsync(Output, 1, progress);

        progress.Lines.Should().Equal(JobProgress.Step("Already downloaded, checksum verified."));
        runner.Requests.Should().ContainSingle();
    }

    [Fact]
    public async Task RepeatedChecksumMismatchNeverInstallsOutputAndRemovesPartial()
    {
        var source = await SourceAsync();
        var runner = new DownloadRunner(Output, Good, corruptTransfers: int.MaxValue);

        var action = async () => await CreateSession(Guid.NewGuid(), runner, source).RetrieveOutputAsync(Output, 1);

        await action.Should().ThrowAsync<InvalidDataException>().WithMessage("*after a clean retry*");
        File.Exists(Output).Should().BeFalse();
        File.Exists(Output + ".partial").Should().BeFalse();
    }

    [Fact]
    public async Task FailedRemoteStageRetainsLogThenCleansItsGuidWorkspace()
    {
        var source = await SourceAsync();
        var runner = new QueueRunner([Ok(AtEnd("completed 9", "fatal encode error\n")), Ok("fatal encode error\n"), Ok()]);
        var session = CreateSession(Guid.NewGuid(), runner, source);

        var action = async () => await session.SearchCrfAsync(source, new QualitySettings());

        var thrown = (await action.Should().ThrowAsync<AbAv1StageException>().WithMessage("*exit code 9*")).Which;
        var logPath = Path.Combine(_directory, ".video-optimiser", $"{session.RemoteWorkspace!.Split('/').Last()}.crf.remote.log");
        thrown.LogPath.Should().Be(logPath);
        (await File.ReadAllTextAsync(logPath)).Should().Contain("fatal encode error");
        runner.Requests[^1].Arguments[^1].Should().Contain("rm -rf --").And.Contain(session.RemoteWorkspace);
    }

    [Fact]
    public async Task ACrfStageThatFoundNoWorthwhileCrfThrowsTheSkipExceptionAndStillCleansTheWorkspace()
    {
        var source = await SourceAsync();
        var runner = new QueueRunner([Ok(AtEnd("completed 1", NoWorthwhileCrfLog.Text)), Ok(NoWorthwhileCrfLog.Text), Ok()]);
        var session = CreateSession(Guid.NewGuid(), runner, source);

        var action = async () => await session.SearchCrfAsync(source, new QualitySettings { MinimumVmaf = 97, MaxEncodedPercent = 80 });

        var thrown = (await action.Should().ThrowAsync<NoWorthwhileCrfException>()).Which;
        thrown.Best.Should().Be(new CrfAttempt(31, 97.31, 95));
        File.Exists(Path.Combine(_directory, ".video-optimiser", $"{session.RemoteWorkspace!.Split('/').Last()}.crf.remote.log")).Should().BeTrue();
        runner.Requests[^1].Arguments[^1].Should().Contain("rm -rf --").And.Contain(session.RemoteWorkspace);
    }

    [Fact]
    public async Task AnotherFailedRemoteStageReportsTheErrorLineOfItsLog()
    {
        var source = await SourceAsync();
        const string log = "[2026-10-13T10:55:18Z INFO  ab_av1::command::crf_search] starting\nError: ffmpeg exited with status 1\nCommand exited with non-zero status 1\n\tExit status: 1\n";
        var runner = new QueueRunner([Ok(AtEnd("completed 1", log)), Ok(log), Ok()]);

        var action = async () => await CreateSession(Guid.NewGuid(), runner, source).SearchCrfAsync(source, new QualitySettings());

        await action.Should().ThrowAsync<InvalidOperationException>().WithMessage("Remote processing stage failed with exit code 1: ffmpeg exited with status 1");
    }

    [Fact]
    public async Task RepeatedSsh255BecomesRemoteConnectionFailure()
    {
        var source = await SourceAsync();
        var runner = new QueueRunner(Enumerable.Repeat(new ExternalProcessResult(255, string.Empty, "connection refused"), 3));

        var action = async () => await CreateSession(Guid.NewGuid(), runner, source).StageAsync(source, "fingerprint");

        await action.Should().ThrowAsync<RemoteConnectionException>();
        runner.Requests.Should().HaveCount(3);
    }

    [Fact]
    public async Task CtrlCDuringAStageLeavesItRunningOnTheWorker()
    {
        var source = await SourceAsync();
        var runner = new QueueRunner([Ok(AtEnd("running", ""))]);
        using var cancellation = new CancellationTokenSource();
        var session = new RemoteSshProcessingSession(new JobRecord { Id = Guid.NewGuid(), SourcePath = source }, CreateSettings(), runner, _ =>
        {
            cancellation.Cancel();
            return Task.FromCanceled(cancellation.Token);
        });

        var action = async () => await session.EncodeAsync(source, Output, 30, new QualitySettings(), 1, [], cancellationToken: cancellation.Token);

        await action.Should().ThrowAsync<OperationCanceledException>();
        runner.Requests.Should().ContainSingle();
    }

    /// <summary>
    /// A rerun says what it found: a stage still running, which it follows until it completes, or one that ended while no command
    /// watched it. A stage whose log is already kept ended under the command that kept it, such as a rerun after a stopped
    /// download. Either way it reads the result without launching another script.
    /// </summary>
    [Theory]
    [InlineData("running", false, "Picked up the CRF search running on the worker.")]
    [InlineData("completed 0", false, "The CRF search on the worker had already finished.")]
    [InlineData("completed 0", true, null)]
    public async Task AReattachedStageSaysWhetherItIsStillRunningOrFinishedUnwatched(string state, bool logAlreadyKept, string? expected)
    {
        var source = await SourceAsync();
        var worker = new StageWorker(state == "running" ? [state, "completed 0"] : [state]);
        var id = Guid.NewGuid();
        if (logAlreadyKept)
        {
            Directory.CreateDirectory(Path.Combine(_directory, ".video-optimiser"));
            await File.WriteAllTextAsync(Path.Combine(_directory, ".video-optimiser", $"{id:N}.crf.remote.log"), "crf 42 successful\n");
        }
        var progress = new ListProgress();

        var result = await CreateSession(id, worker, source).SearchCrfAsync(source, new QualitySettings(), progress);

        Steps(progress).Should().Equal(expected is null ? [] : [expected]);
        result.Crf.Should().Be(42);
        worker.ScriptsStarted.Should().Be(0);
    }

    /// <summary>
    /// The worker's log already holds what an earlier command showed. Replaying it printed a finished encode's first progress
    /// line again, measured the time left from it, and reported a duration of the reattach alone (VOPT-72). Each poll also read
    /// the whole log, so a long encode cost more with every line.
    /// </summary>
    [Fact]
    public async Task AReattachedEncodeShowsOnlyWhatIsNewAndNamesNoDurationItCannotKnow()
    {
        var source = await SourceAsync();
        const string before = "[2026-10-13T10:06:43Z INFO  ab_av1::command::encode] encoding movie.mkv\n[2026-10-13T10:06:59Z INFO  ab_av1::command::encode] 20%, 54 fps, eta 3 minutes\n[2026-10-13T10:07:15Z INFO  ab_av1::command::encode] 42%, 57 fps, eta 2 minutes\n";
        const string progressLine = "[2026-10-13T10:07:31Z INFO  ab_av1::command::encode] 60%, 58 fps, eta 1 minute\n";
        const string encoded = "Encoded 99.8 MiB (29%)\n";
        var worker = new StageWorker([new("running"), new("running", progressLine), new("completed 0", encoded)], before);
        var progress = new ListProgress();
        var translator = new AbAv1OutputTranslator(AbAv1Stage.Encode, 95, 80, () => TimeSpan.Zero).Wrap(progress);

        await CreateSession(Guid.NewGuid(), worker, source).EncodeAsync(source, Output, 42, new QualitySettings(), 1, [], translator);

        progress.Lines.Should().Equal(
            JobProgress.Step("Picked up the encode running on the worker."),
            JobProgress.Detail("60%, 58 fps"),
            JobProgress.Step("Encoded 99.8 MiB (29% of the original)."));
        var beforeBytes = Encoding.UTF8.GetByteCount(before);
        worker.Polls.Should().Equal([(-1, 0), (beforeBytes, Encoding.UTF8.GetByteCount(progressLine)), (beforeBytes + Encoding.UTF8.GetByteCount(progressLine), Encoding.UTF8.GetByteCount(encoded))],
            "after joining at the end of the log each poll asks only for the bytes past the last one it read");
        worker.WholeLogReads.Should().Be(1, "the whole log is read once, when the stage has ended");
    }

    [Fact]
    public async Task AFreshlyLaunchedStageHasNoPickUpLineAndAKnownDuration()
    {
        var source = await SourceAsync();
        var worker = new StageWorker(["missing", "missing", "running", new("completed 0", "crf 42 successful\n")], log: string.Empty);
        var progress = new ListProgress();

        var result = await CreateSession(Guid.NewGuid(), worker, source).SearchCrfAsync(source, new QualitySettings(), progress);

        Steps(progress).Should().BeEmpty();
        result.Duration.Should().NotBeNull();
        worker.ScriptsStarted.Should().Be(1);
    }

    [Fact]
    public async Task AReattachedCrfSearchHasNoDuration()
    {
        var source = await SourceAsync();
        var worker = new StageWorker(["running", "completed 0"]);

        var result = await CreateSession(Guid.NewGuid(), worker, source).SearchCrfAsync(source, new QualitySettings());

        result.Duration.Should().BeNull();
    }

    /// <summary>A poll can land in the middle of a line ab-av1 is writing; the half is kept for the next poll, not printed as a line.</summary>
    [Fact]
    public async Task ALineStillBeingWrittenIsReportedWholeOnTheNextPoll()
    {
        var source = await SourceAsync();
        const string start = "[2026-10-13T10:06:59Z INFO  ab_av1::command::encode] 20%, 54 fps, e";
        const string rest = "ta 2 minutes\n";
        var worker = new StageWorker([new("running"), new("running", start), new("running", rest), new("completed 0")], log: string.Empty);
        var progress = new ListProgress();

        await CreateSession(Guid.NewGuid(), worker, source).EncodeAsync(source, Output, 42, new QualitySettings(), 1, [], progress);

        progress.Lines.Where(line => line.Kind == JobProgressKind.ToolOutput).Select(line => line.Text).Should().Equal(start + rest.TrimEnd('\n'));
    }

    /// <summary>Exit 1 is the worker's answer no; any other failure is not an answer.</summary>
    [Theory]
    [InlineData(0, true)]
    [InlineData(1, false)]
    public async Task TheWorkerSaysWhetherAnAttemptsEncodeFinished(int exitCode, bool finished)
    {
        var runner = new QueueRunner([new ExternalProcessResult(exitCode, string.Empty, string.Empty)]);
        var session = CreateSession(Guid.NewGuid(), runner);

        (await session.HasFinishedEncodeAsync(Output, 3)).Should().Be(finished);

        var command = runner.Requests.Single().Arguments[^1];
        command.Should().Contain($"{session.EncodeDirectory(3)}/exit-code").And.Contain($"{session.EncodeDirectory(3)}/output.mkv");
    }

    [Fact]
    public async Task AFailedEncodeCheckIsNotTakenForNo()
    {
        var runner = new QueueRunner([new ExternalProcessResult(2, string.Empty, "bash: broken")]);

        var action = async () => await CreateSession(Guid.NewGuid(), runner).HasFinishedEncodeAsync(Output, 3);

        await action.Should().ThrowAsync<InvalidOperationException>().WithMessage("Remote encode check failed: bash: broken");
    }

    private static RemoteSshProcessingSession CreateSession(Guid id, IExternalProcessRunner runner, string sourcePath = "C:\\Videos\\movie.mkv", FakeClock? clock = null)
    {
        clock ??= new FakeClock();
        return new RemoteSshProcessingSession(new JobRecord { Id = id, SourcePath = sourcePath }, CreateSettings(), runner, _ => Task.CompletedTask, clock.Read, clock.TickAsync);
    }

    private static AppSettings CreateSettings() => new()
    {
        Processing = new ProcessingSettings
        {
            Mode = "remoteSsh",
            RemoteSsh = new RemoteSshSettings { Host = "video-worker", WorkingDirectory = "/var/tmp/video-optimiser", MinimumCpuCount = 8, MinimumAvailableMemory = "14GiB", MinimumFreeDiskMultiplier = 2.5 }
        }
    };

    /// <summary>Writes the original, <see cref="Original"/> unless given, as <c>source.mkv</c>; the session keeps its logs beside it.</summary>
    private async Task<string> SourceAsync(byte[]? bytes = null)
    {
        var source = Path.Combine(_directory, "source.mkv");
        await File.WriteAllBytesAsync(source, bytes ?? Original);
        return source;
    }

    private static string Sha256(ReadOnlySpan<byte> bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static ExternalProcessResult Ok(string output = "") => new(0, output, string.Empty);

    /// <summary>The state check's answer to a poll that asked for the end of the log: the state, no log, and where the log ends.</summary>
    private static string AtEnd(string header, string log)
    {
        var size = Encoding.UTF8.GetByteCount(log);
        return $"{header}\n\u001e{size}\u001e{size}\u001e";
    }

    /// <summary>The state check's answer carrying <paramref name="chunk"/> of the log, which starts at byte <paramref name="start"/>.</summary>
    private static string Sent(string header, string chunk, long start = 0) =>
        $"{header}\n\u001e{start}\u001e{start + Encoding.UTF8.GetByteCount(chunk)}\u001e{chunk}";

    private static Task<ExternalProcessResult> OkAsync(string output = "") => Task.FromResult(Ok(output));

    private static string[] Steps(ListProgress progress) => [.. progress.Lines.Where(line => line.Kind == JobProgressKind.Step).Select(line => line.Text)];

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private static class NoWorthwhileCrfLog
    {
        public const string Text = """
            [2026-10-13T10:55:18Z INFO  ab_av1::command::crf_search] crf 28 VMAF 98.47 (122%)
            [2026-10-13T10:56:02Z INFO  ab_av1::command::sample_encode] crf 31 VMAF 97.31 predicted video stream size 418.66 MiB (95%) taking 2 minutes
            [2026-10-13T10:56:02Z INFO  ab_av1::command::crf_search] crf 31 VMAF 97.31 (95%)
            [2026-10-13T10:56:45Z INFO  ab_av1::command::crf_search] crf 32 VMAF 96.64 (86%)
            Error: Failed to find a suitable crf
            Command exited with non-zero status 1
            	Command being timed: "ab-av1 crf-search --input /var/tmp/video-optimiser/8f62/source.mp4 --encoder libsvtav1"
            	Exit status: 1

            """;
    }

    private sealed class QueueRunner(IEnumerable<ExternalProcessResult> results) : IExternalProcessRunner
    {
        private readonly Queue<ExternalProcessResult> _results = new(results);
        public List<ExternalProcessRequest> Requests { get; } = [];

        public Task<ExternalProcessResult> RunAsync(ExternalProcessRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return Task.FromResult(_results.Dequeue());
        }
    }

    /// <summary>
    /// The worker's side of a download: ssh answers the output's size and checksum, and each transfer writes the partial, with
    /// every byte flipped for the first <paramref name="corruptTransfers"/>.
    /// </summary>
    private sealed class DownloadRunner(string output, byte[] good, int corruptTransfers = 0) : IExternalProcessRunner
    {
        public int Transfers { get; private set; }

        public Task<ExternalProcessResult> RunAsync(ExternalProcessRequest request, CancellationToken cancellationToken = default)
        {
            if (request.FileName == "ssh") return OkAsync($"{good.Length}\n{Sha256(good)}");
            File.WriteAllBytes(output + ".partial", ++Transfers <= corruptTransfers ? [.. good.Select(value => (byte)~value)] : good);
            return OkAsync();
        }
    }

    private sealed class FailingSizePollRunner : IExternalProcessRunner
    {
        private readonly TaskCompletionSource<ExternalProcessResult> _sftp = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool SftpCompleted => _sftp.Task.IsCompletedSuccessfully;
        public bool VerificationObserved { get; private set; }

        public Task<ExternalProcessResult> RunAsync(ExternalProcessRequest request, CancellationToken cancellationToken = default)
        {
            if (request.FileName == "sftp") return _sftp.Task;

            var command = request.Arguments[^1];
            if (command.Contains("nproc", StringComparison.Ordinal)) return OkAsync("8 20000000000 100000000000");
            if (command.Contains("mv -f", StringComparison.Ordinal))
            {
                VerificationObserved = true;
                return OkAsync();
            }
            if (command.Contains("source.mkv.partial", StringComparison.Ordinal) && command.Contains("stat -c", StringComparison.Ordinal) && !command.Contains("sha256sum", StringComparison.Ordinal))
            {
                _sftp.TrySetResult(Ok());
                return Task.FromResult(new ExternalProcessResult(1, string.Empty, "size poll failed"));
            }
            return OkAsync();
        }
    }

    /// <summary>What the stage's state check finds at one poll: its state, and what ab-av1 had written to the log by then.</summary>
    private sealed record WorkerStep(string Header, string Appended = "")
    {
        public static implicit operator WorkerStep(string header) => new(header);
    }

    /// <summary>
    /// A worker for one stage that keeps the launch markers it was asked to create, as <c>mkdir</c> would, and answers the state
    /// check as the remote command does: the log from the byte offset it was asked for, or from the end for -1, or from the
    /// start when the offset is past the end. A launch command without a marker starts a script every time it arrives.
    /// </summary>
    /// <param name="log">The log already on the worker before the first poll.</param>
    private sealed class StageWorker(IEnumerable<WorkerStep> steps, string log = "crf 42 successful\n") : IExternalProcessRunner
    {
        // The marker as it appears once the command is quoted for `bash -lc`.
        private static readonly Regex MarkerPattern = new(@"mkdir -- '""'""'([^']+)'""'""'", RegexOptions.CultureInvariant);
        private static readonly Regex OffsetPattern = new(@"off=(-?\d+);", RegexOptions.CultureInvariant);
        private readonly Queue<WorkerStep> _steps = new(steps);
        private readonly HashSet<string> _markers = [];
        private string _log = log;

        public bool DropFirstLaunchReply { get; set; }
        public List<string> Launches { get; } = [];
        public int ScriptsStarted { get; private set; }

        /// <summary>For each state check, the offset it asked for and how many bytes of log it was sent.</summary>
        public List<(long Asked, int Sent)> Polls { get; } = [];

        public int WholeLogReads { get; private set; }

        public Task<ExternalProcessResult> RunAsync(ExternalProcessRequest request, CancellationToken cancellationToken = default)
        {
            var command = request.Arguments[^1];
            if (command.Contains("nohup setsid bash", StringComparison.Ordinal))
            {
                Launches.Add(command);
                var marker = MarkerPattern.Match(command);
                if (!marker.Success || _markers.Add(marker.Groups[1].Value)) ScriptsStarted++;
                if (!DropFirstLaunchReply) return OkAsync();
                DropFirstLaunchReply = false;
                return Task.FromResult(new ExternalProcessResult(255, string.Empty, "Connection reset by peer"));
            }
            if (command.Contains("kill -0", StringComparison.Ordinal)) return OkAsync(Poll(command));
            if (command.Contains("cat '", StringComparison.Ordinal) && command.Contains("/log", StringComparison.Ordinal))
            {
                WholeLogReads++;
                return OkAsync(_log);
            }
            return OkAsync();
        }

        private string Poll(string command)
        {
            var step = _steps.Dequeue();
            _log += step.Appended;
            var bytes = Encoding.UTF8.GetBytes(_log);
            var asked = long.Parse(OffsetPattern.Match(command).Groups[1].Value, CultureInfo.InvariantCulture);
            var start = asked < 0 ? bytes.Length : asked > bytes.Length ? 0 : (int)asked;
            Polls.Add((asked, bytes.Length - start));
            return $"{step.Header}\n\u001e{start}\u001e{bytes.Length}\u001e{Encoding.UTF8.GetString(bytes[start..])}";
        }
    }

    /// <summary>
    /// A worker with nothing uploaded yet whose first sftp call loses its connection (exit 255). After the drop, the partial
    /// check answers <paramref name="afterDrop"/>, or every SSH call fails to connect when it is null.
    /// </summary>
    /// <param name="afterRefusedRetry">When set, the second sftp call exits 1, as reput does onto a partial the dropped session
    /// finished after the re-check, and the partial check then answers this.</param>
    private sealed class DroppedUploadRunner(string? afterDrop, string? afterRefusedRetry = null) : IExternalProcessRunner
    {
        public List<string> Transfers { get; } = [];
        public bool PartialRemovedAfterDrop { get; private set; }
        public bool Verified { get; private set; }

        public Task<ExternalProcessResult> RunAsync(ExternalProcessRequest request, CancellationToken cancellationToken = default)
        {
            if (request.FileName == "sftp")
            {
                Transfers.Add(request.StandardInput!.Split(' ')[0]);
                if (Transfers.Count == 1) return Result(255, "Connection closed");
                return Transfers.Count == 2 && afterRefusedRetry is not null ? Result(1, "destination file bigger or same size as source file") : Result(0);
            }
            var command = request.Arguments[^1];
            if (command.Contains("nproc", StringComparison.Ordinal)) return Result(0, "8 20000000000 100000000000");
            if (Transfers.Count == 0) return Result(0);
            if (afterDrop is null) return Result(255, "Connection timed out");
            if (command.Contains("head -c", StringComparison.Ordinal)) return Result(0, Transfers.Count == 1 ? afterDrop : afterRefusedRetry ?? afterDrop);
            if (command.Contains("rm -f --", StringComparison.Ordinal)) PartialRemovedAfterDrop = true;
            if (command.Contains("mv -f --", StringComparison.Ordinal)) Verified = true;
            return Result(0);
        }

        private static Task<ExternalProcessResult> Result(int exitCode, string output = "") =>
            Task.FromResult(exitCode == 0 ? Ok(output) : new ExternalProcessResult(exitCode, string.Empty, output));
    }

    /// <summary>
    /// The session's clock and its transfer-poll wait. A tick moves time on by <see cref="TickLength"/> without waiting, so a
    /// 30 s transfer takes no time to test; <see cref="StepPerReading"/> moves it on every reading, to make hashing look slow.
    /// </summary>
    private sealed class FakeClock
    {
        private TimeSpan _now;

        public TimeSpan TickLength { get; init; } = TimeSpan.FromSeconds(5);
        public TimeSpan StepPerReading { get; init; }
        public int Ticks { get; private set; }
        public Action<int>? OnTick { get; set; }

        public TimeSpan Read()
        {
            _now += StepPerReading;
            return _now;
        }

        public async Task TickAsync(CancellationToken cancellationToken)
        {
            _now += TickLength;
            Ticks++;
            OnTick?.Invoke(Ticks);
            await Task.Yield();
        }
    }

    /// <summary>An SFTP transfer that stays pending until the clock reaches the tick it is given (<c>finishAtTick</c>), with the remote partial's size growing per poll.</summary>
    private sealed class UploadRunner : IExternalProcessRunner
    {
        // Continuations run inline, so completing it inside a tick finishes the transfer before the session looks again.
        private readonly TaskCompletionSource<ExternalProcessResult> _sftp = new();
        private readonly Queue<long> _remoteSizes;

        public UploadRunner(FakeClock clock, IEnumerable<long> remoteSizes, int finishAtTick)
        {
            _remoteSizes = new Queue<long>(remoteSizes);
            clock.OnTick = tick => { if (tick == finishAtTick) _sftp.TrySetResult(Ok()); };
        }

        public int SizePolls { get; private set; }

        public Task<ExternalProcessResult> RunAsync(ExternalProcessRequest request, CancellationToken cancellationToken = default)
        {
            if (request.FileName == "sftp") return _sftp.Task;
            var command = request.Arguments[^1];
            if (command.Contains("nproc", StringComparison.Ordinal)) return OkAsync("8 20000000000 100000000000");
            if (command.Contains("else printf", StringComparison.Ordinal))
            {
                SizePolls++;
                return OkAsync(_remoteSizes.Dequeue().ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
            return OkAsync();
        }
    }
}
