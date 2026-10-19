using FluentAssertions;
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

/// <summary>
/// The session's transfers and workspace, and what it makes of a stage's result, through the real transport. The transport's
/// resends are in <see cref="RemoteShellTests"/>, the stage protocol in <see cref="RemoteStageRunnerTests"/>.
/// </summary>
public sealed class RemoteSshProcessingSessionTests : IDisposable
{
    /// <summary>A 100-byte original whose bytes count up from 0, so every prefix has its own checksum.</summary>
    private static readonly byte[] Original = [.. Enumerable.Range(0, 100).Select(value => (byte)value)];

    /// <summary>The encoded output on the worker.</summary>
    private static readonly byte[] Good = "verified-output"u8.ToArray();

    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"VideoOptimiser.Remote.{Guid.NewGuid():N}");

    public RemoteSshProcessingSessionTests() => Directory.CreateDirectory(_directory);

    private string Output => Path.Combine(_directory, "output.mkv");

    [Fact]
    public void UsesDeterministicGuidWorkspaceAndAttemptPaths()
    {
        var id = Guid.Parse("11111111-2222-3333-4444-555555555555");
        var session = CreateSession(id, new ScriptedProcessRunner([]));

        session.RemoteWorkspace.Should().Be("/var/tmp/video-optimiser/11111111222233334444555555555555");
        session.RemoteSourcePath.Should().Be("/var/tmp/video-optimiser/11111111222233334444555555555555/source.mkv");
        session.CrfDirectory.Should().EndWith("/crf");
        session.EncodeDirectory(3).Should().EndWith("/encode-3");
    }

    [Fact]
    public void RequiredDiskRoundsUp() => RemoteSshProcessingSession.CalculateRequiredDisk(101, 2.5).Should().Be(253);

    [Theory]
    [InlineData("2 1024 100000", "Remote host has 2 CPUs*")]
    [InlineData("8 1024 100000", "Remote host has 1024 available bytes*")]
    [InlineData("8 20000000000 200", "Remote host has 200 free bytes; 250 are required*")]
    public async Task RejectsInsufficientRemoteResourcesBeforeTransfer(string probeOutput, string expectedMessage)
    {
        var source = await SourceAsync();
        var runner = new ScriptedProcessRunner([Ok(probeOutput), Ok()]);

        var action = async () => await CreateSession(Guid.NewGuid(), runner, source).StageAsync(source, "fingerprint");

        await action.Should().ThrowAsync<InvalidOperationException>().WithMessage(expectedMessage);
        runner.Requests.Should().NotContain(request => request.FileName == "sftp");
    }

    [Fact]
    public async Task ReusesAValidatedPartialUploadPrefixWithReput()
    {
        var source = await SourceAsync();
        var runner = new ScriptedProcessRunner([Ok("8 20000000000 100000"), Ok(), Ok($"40 {Sha256(Original.AsSpan(0, 40))}"), Ok(), Ok()]);
        var progress = new ListProgress();

        await CreateSession(Guid.NewGuid(), runner, source).StageAsync(source, "fingerprint", progress);

        progress.Lines.Should().Equal(
            JobProgress.Step("Resuming the upload at 40 B of 100 B."),
            JobProgress.Step("Uploaded 100 B in 0s, checksum verified."));
        runner.Requests.Should().ContainSingle(request => request.FileName == "sftp" && request.StandardInput!.StartsWith("reput ", StringComparison.Ordinal));
        runner.Requests.Where(request => request.FileName == "ssh").Select(request => request.Arguments[^1]).Should().NotContain(command => RemovesThePartial(command));
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
            JobProgress.Step("Uploading 100 B to the worker."),
            JobProgress.Step("Lost the connection to the worker; resuming the upload at 40 B of 100 B."),
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
            JobProgress.Step("Uploading 100 B to the worker."),
            JobProgress.Step("Lost the connection to the worker; uploading 100 B again from the start."),
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
            JobProgress.Step("Uploading 100 B to the worker."),
            JobProgress.Step("Uploaded 100 B in 0s, checksum verified."));
    }

    /// <summary>
    /// A large original's checksum can outlast the first try of the verify-and-rename. The cut-off try keeps running on the
    /// worker and renames the partial, so the resend finds no partial; it failed the job as StagingFailed (VOPT-150).
    /// </summary>
    [Fact]
    public async Task AVerifyAndRenameCutOffAfterItFinishedOnTheWorkerSucceedsOnItsResend()
    {
        var source = await SourceAsync();
        var worker = new VerifyWorker(uploaded: Original, cutOffFirstVerify: true);
        var progress = new ListProgress();

        await CreateSession(Guid.NewGuid(), worker, source, new ManualClock(), ShortTimeouts).StageAsync(source, "fingerprint", progress);

        worker.Verifies.Should().Be(2);
        worker.Source.Should().Equal(Original);
        worker.Partial.Should().BeNull();
        progress.Lines.Should().Equal(
            JobProgress.Step("Uploading 100 B to the worker."),
            JobProgress.Detail("The worker didn't answer within 0s; asking again (try 2 of 3).", JobTone.Warning),
            JobProgress.Detail("The worker answered again."),
            JobProgress.Step("Uploaded 100 B in 0s, checksum verified."));
    }

    [Fact]
    public async Task AnUploadWhoseChecksumIsWrongNeverBecomesTheSource()
    {
        var source = await SourceAsync();
        var worker = new VerifyWorker(uploaded: [.. Original.Select(value => (byte)~value)], cutOffFirstVerify: false);

        var action = async () => await CreateSession(Guid.NewGuid(), worker, source, new ManualClock(), ShortTimeouts).StageAsync(source, "fingerprint");

        await action.Should().ThrowAsync<InvalidOperationException>().WithMessage("Remote original verification failed*");
        worker.Source.Should().BeNull();
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

    /// <summary>
    /// sftp ends a transfer whose connection dropped with status 1 as often as 255 (VOPT-83). Judged by stderr, such a drop resumes
    /// like any other, instead of failing the job for good; the status alone said it was a refused file.
    /// </summary>
    [Theory]
    [InlineData("Connection closed")]
    [InlineData("Connection reset by peer")]
    [InlineData("Write failed: Broken pipe")]
    [InlineData("Read from remote host 203.0.113.10: Connection timed out")]
    [InlineData("client_loop: send disconnect: Error")]
    [InlineData("packet_write_wait: Connection to 203.0.113.10 port 22: Input/output error")]
    public async Task AnUploadWhoseConnectionDroppedWithExitStatusOneResumesLikeAnyDrop(string stderr)
    {
        var source = await SourceAsync();
        var runner = new DroppedUploadRunner($"40 {Sha256(Original.AsSpan(0, 40))}", firstExit: 1, firstError: stderr);
        var progress = new ListProgress();

        await CreateSession(Guid.NewGuid(), runner, source).StageAsync(source, "fingerprint", progress);

        runner.Transfers.Should().Equal("put", "reput");
        progress.Lines.Should().Equal(
            JobProgress.Step("Uploading 100 B to the worker."),
            JobProgress.Step("Lost the connection to the worker; resuming the upload at 40 B of 100 B."),
            JobProgress.Step("Uploaded 100 B in 0s, checksum verified."));
    }

    /// <summary>A refused file is no drop: reconnecting cannot help, so the job fails with sftp's own line saying what to fix.</summary>
    [Fact]
    public async Task AnUploadSftpRefusedFailsWithSftpsOwnLine()
    {
        var source = await SourceAsync();
        var runner = new DroppedUploadRunner($"0 {Sha256(Original.AsSpan(0, 0))}", firstExit: 1, firstError: "remote open(\"/var/tmp/video-optimiser/a/source.mkv.partial\"): No space left on device");

        var action = async () => await CreateSession(Guid.NewGuid(), runner, source).StageAsync(source, "fingerprint", new ListProgress());

        await action.Should().ThrowAsync<InvalidOperationException>().WithMessage("SFTP transfer failed: remote open(\"/var/tmp/video-optimiser/a/source.mkv.partial\"): No space left on device");
        runner.Transfers.Should().Equal("put");
    }

    [Theory]
    [InlineData(1, "Connection closed")]
    [InlineData(1, "Read from remote host 203.0.113.10: Connection reset by peer")]
    [InlineData(255, "ssh: connect to host 203.0.113.10 port 22: Network is unreachable")]
    public async Task ADownloadWhoseConnectionDroppedIsTriedAgainAndFinishes(int exitCode, string stderr)
    {
        var runner = new FlakyDownloadRunner(Output, Good, [new ExternalProcessResult(exitCode, string.Empty, stderr)]);

        await CreateSession(Guid.NewGuid(), runner).RetrieveOutputAsync(Output, 1);

        runner.Transfers.Should().Be(2);
        (await File.ReadAllBytesAsync(Output)).Should().Equal(Good);
    }

    [Fact]
    public async Task ADownloadThatKeepsLosingItsConnectionLeavesTheJobResumable()
    {
        var drop = new ExternalProcessResult(1, string.Empty, "Connection closed");
        var runner = new FlakyDownloadRunner(Output, Good, [drop, drop, drop]);

        var action = async () => await CreateSession(Guid.NewGuid(), runner).RetrieveOutputAsync(Output, 1);

        await action.Should().ThrowAsync<RemoteConnectionException>().WithMessage("SFTP connection failed: Connection closed");
        runner.Transfers.Should().Be(3);
    }

    [Fact]
    public async Task ADownloadSftpRefusedFailsAtOnceWithSftpsOwnLine()
    {
        var runner = new FlakyDownloadRunner(Output, Good, [new ExternalProcessResult(1, string.Empty, "remote open(\"/var/tmp/video-optimiser/a/encode-1/output.mkv\"): Permission denied")]);

        var action = async () => await CreateSession(Guid.NewGuid(), runner).RetrieveOutputAsync(Output, 1);

        await action.Should().ThrowAsync<InvalidOperationException>().WithMessage("SFTP transfer failed: remote open(\"/var/tmp/video-optimiser/a/encode-1/output.mkv\"): Permission denied");
        runner.Transfers.Should().Be(1);
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
        var runner = new ScriptedProcessRunner([Ok("8 20000000000 100000"), Ok(), Ok($"{partialLength} {partialHash}"), Ok(), Ok(), Ok()]);
        var progress = new ListProgress();
        var session = CreateSession(Guid.NewGuid(), runner, source, new ManualClock());

        await session.StageAsync(source, "fingerprint", progress);

        var partial = session.RemoteSourcePath + ".partial";
        var removal = runner.Requests.FindIndex(request => request.FileName == "ssh" && RemovesThePartial(request.Arguments[^1]) && request.Arguments[^1].Contains(partial, StringComparison.Ordinal));
        var transfer = runner.Requests.FindIndex(request => request.FileName == "sftp");
        removal.Should().BeGreaterThan(-1, "bytes that do not belong to this original must not be built upon");
        transfer.Should().BeGreaterThan(removal);
        runner.Requests[transfer].StandardInput.Should().StartWith("put ");
        runner.Requests.Should().NotContain(request => request.StandardInput != null && request.StandardInput.StartsWith("reput ", StringComparison.Ordinal));
        progress.Lines.Should().Equal(
            JobProgress.Step("Uploading 100 B to the worker."),
            JobProgress.Step("Uploaded 100 B in 0s, checksum verified."));
    }

    [Fact]
    public async Task StagesWithResumableSftpAndAtomicRemoteRename()
    {
        var source = await SourceAsync();
        var runner = new ScriptedProcessRunner([Ok("8 20000000000 100000000000"), Ok(), Ok(), Ok(), Ok()]);
        var progress = new ListProgress();

        // The checks (CPU, hash, existing upload, verification) and the quick transfer all run inside the first second.
        await CreateSession(Guid.NewGuid(), runner, clock: new ManualClock()).StageAsync(source, "fingerprint", progress);

        progress.Lines.Should().Equal(
            JobProgress.Step("Uploading 100 B to the worker."),
            JobProgress.Step("Uploaded 100 B in 0s, checksum verified."));
        runner.Requests[0].Arguments[^1].Should().Contain("df -B1 --output=avail").And.NotContain("df -PB1");
        runner.Requests.Should().Contain(request => request.FileName == "sftp" && request.StandardInput!.StartsWith("put ", StringComparison.Ordinal));
        runner.Requests[^1].Arguments[^1].Should().Contain("mv -f --");
    }

    [Fact]
    public async Task UploadProgressAppearsOnlyAfterTenSecondsThenAtMostOnceEveryTenSeconds()
    {
        var source = await SourceAsync(new byte[1_048_576]);
        var clock = new ManualClock();
        var runner = new UploadRunner(clock, remoteSizes: [262_144, 524_288], finishAtTick: 6);
        var progress = new ListProgress();

        await CreateSession(Guid.NewGuid(), runner, clock: clock).StageAsync(source, "fingerprint", progress);

        // Ticks are 5 s apart: nothing at 5 s, the first line at 10 s, nothing at 15 s, the second at 20 s, nothing at 25 s.
        progress.Lines.Should().Equal(
            JobProgress.Step("Uploading 1.0 MiB to the worker."),
            JobProgress.Reading("25% of 1.0 MiB, 25.6 KiB/s, about 30s left", 25),
            JobProgress.Reading("50% of 1.0 MiB, 25.6 KiB/s, about 20s left", 50),
            JobProgress.Step("Uploaded 1.0 MiB in 30s, checksum verified."));
        runner.SizePolls.Should().Be(2, "a poll is an SSH connection, so none is made while no line is due");
    }

    /// <summary>Its single line also shows that a hash quicker than the delay prints no progress.</summary>
    [Fact]
    public async Task AnOriginalAlreadyOnTheWorkerIsOneLineAndNoTransfer()
    {
        var source = await SourceAsync();
        var runner = new ScriptedProcessRunner([Ok("8 20000000000 100000000000"), Ok($"100 {Sha256(Original)}")]);
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
        var runner = new ScriptedProcessRunner([Ok("8 20000000000 100000000000"), Ok($"{bytes.Length} {Sha256(bytes)}")]);
        var progress = new ListProgress();
        // A clock that moves on every reading makes each read of the file look ten seconds apart.
        var clock = new ManualClock { StepPerReading = TimeSpan.FromSeconds(10) };

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
        var session = CreateSession(Guid.NewGuid(), runner, clock: new ManualClock { TickLength = TimeSpan.FromSeconds(10) });

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
        var runner = new ScriptedProcessRunner([Ok(AtEnd("completed 0", log)), Ok(log)]);
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

    [Fact]
    public async Task ReattachesToCompletedEncodingAndVerifiesItsOutput()
    {
        var source = await SourceAsync();
        var runner = new ScriptedProcessRunner([Ok(AtEnd("completed 0", "encode complete\n")), Ok("encode complete\n"), Ok()]);

        var result = await CreateSession(Guid.NewGuid(), runner, source).EncodeAsync(source, Output, 42, new QualitySettings(), 3, new AudioPlan([], [], 1));

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
        var runner = new ScriptedProcessRunner([Ok(remoteOutput)]);

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
            JobProgress.Step("Resuming the download at 5 B of 15 B."),
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

        progress.Lines[0].Should().Be(JobProgress.Step("Downloading 15 B from the worker."));
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
            JobProgress.Step("Downloading 15 B from the worker."),
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
        var runner = new ScriptedProcessRunner([Ok($"{Good.Length}\n{Sha256(Good)}")]);
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
        var runner = new ScriptedProcessRunner([Ok(AtEnd("completed 9", "fatal encode error\n")), Ok("fatal encode error\n"), Ok()]);
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
        var runner = new ScriptedProcessRunner([Ok(AtEnd("completed 1", NoWorthwhileCrfLog.Text)), Ok(NoWorthwhileCrfLog.Text), Ok()]);
        var session = CreateSession(Guid.NewGuid(), runner, source);

        var action = async () => await session.SearchCrfAsync(source, new QualitySettings { MinimumVmaf = 97, MaxEncodedPercent = 80 });

        var thrown = (await action.Should().ThrowAsync<NoWorthwhileCrfException>()).Which;
        thrown.Best.Should().Be(new CrfAttempt(31, 97.31, 95));
        File.Exists(Path.Combine(_directory, ".video-optimiser", $"{session.RemoteWorkspace!.Split('/').Last()}.crf.remote.log")).Should().BeTrue();
        runner.Requests[^1].Arguments[^1].Should().Contain("rm -rf --").And.Contain(session.RemoteWorkspace);
    }

    private static readonly IReadOnlyList<TimeSpan> ShortTimeouts = [TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(50), TimeSpan.FromSeconds(1)];

    /// <summary>
    /// A dropped video's stage keeps running on the worker with its workspace (VOPT-90). Only the workspaces of jobs this database
    /// knows ended are stopped and removed. A stopped job's is still wanted by the run that picks it up, an unknown job's may be
    /// another database's stage on a shared manual worker, and a name that is not a workspace of this tool's making is none of
    /// its business.
    /// </summary>
    [Fact]
    public async Task OnlyTheWorkspacesOfJobsTheDatabaseKnowsEndedAreStoppedAndRemoved()
    {
        var dropped = Guid.NewGuid();
        var stopped = Guid.NewGuid();
        var unknown = Guid.NewGuid();
        var upper = new string('A', 32);
        var runner = new ScriptedProcessRunner([Ok($"{dropped:N}\n{stopped:N}\n{unknown:N}\nlost+found\nnotes.txt\n{upper}\n{Guid.NewGuid():N}.tmp\n"), Ok()]);

        await CreateSession(Guid.NewGuid(), runner).StopAndRemoveWorkspacesAsync(new HashSet<Guid> { dropped });

        runner.Requests.Should().HaveCount(2);
        runner.Requests[0].Arguments[^1].Should().Contain("ls -1 --").And.Contain("/var/tmp/video-optimiser");
        runner.Requests[1].StandardInput.Should().Be($"{dropped:N}\n");
        var removal = runner.Requests[1].Arguments[^1];
        // The group of the stage script is ended, then the workspace goes; the root is quoted, never built from the names.
        removal.Should().Contain("kill -s TERM -- ").And.Contain("kill -s KILL -- ").And.Contain("rm -rf -- ").And.Contain("root=");
        runner.Requests.SelectMany(request => request.Arguments.Append(request.StandardInput ?? string.Empty)).Should()
            .NotContain(text => text.Contains(stopped.ToString("N"), StringComparison.Ordinal) || text.Contains(unknown.ToString("N"), StringComparison.Ordinal));
    }

    [Fact]
    public async Task NothingEndedCostsOnlyTheListing()
    {
        var unknown = Guid.NewGuid();
        var runner = new ScriptedProcessRunner([Ok($"{unknown:N}\n")]);

        await CreateSession(Guid.NewGuid(), runner).StopAndRemoveWorkspacesAsync(new HashSet<Guid> { Guid.NewGuid() });

        runner.Requests.Should().ContainSingle();
    }

    /// <summary>The session times a CRF search only when this command launched it.</summary>
    [Fact]
    public async Task AFreshlyLaunchedStageHasNoPickUpLineAndAKnownDuration()
    {
        var source = await SourceAsync();
        var runner = new ScriptedProcessRunner([Ok(AtEnd("missing", "")), Ok(), Ok(), Ok(Sent("completed 0", "crf 42 successful\n")), Ok("crf 42 successful\n")]);
        var progress = new ListProgress();

        var result = await CreateSession(Guid.NewGuid(), runner, source).SearchCrfAsync(source, new QualitySettings(), progress);

        Steps(progress).Should().BeEmpty();
        result.Duration.Should().NotBeNull();
        runner.Requests.Should().ContainSingle(request => request.StandardInput != null, "one stage script is uploaded");
    }

    [Fact]
    public async Task AReattachedCrfSearchHasNoDuration()
    {
        var source = await SourceAsync();
        var runner = new ScriptedProcessRunner([Ok(AtEnd("running", "")), Ok(Sent("completed 0", "crf 42 successful\n")), Ok("crf 42 successful\n")]);

        var result = await CreateSession(Guid.NewGuid(), runner, source).SearchCrfAsync(source, new QualitySettings());

        result.Duration.Should().BeNull();
    }

    /// <summary>
    /// A later command that picks up a running stage knows nothing of when it started; the worker's own times say, so a stage
    /// that hung while no command watched it is stopped at the first look, with no script launched. Through the session, so the
    /// default <c>stageSilenceTimeout</c> and the workspace removal reach the stage runner (its own cases are in
    /// <see cref="RemoteStageRunnerTests"/>).
    /// </summary>
    [Fact]
    public async Task AReattachedStageIsJudgedByTheWorkersTimes()
    {
        var source = await SourceAsync();
        const long started = 1_000_000;
        var runner = new ScriptedProcessRunner([Ok($"running\n\ntimes {started + 300 + 2820} {started + 300} {started}\n\u001e0\u001e0\u001e"), Ok("stopped"), Ok(), Ok()]);
        var session = CreateSession(Guid.NewGuid(), runner, source);

        var action = async () => await session.EncodeAsync(source, Output, 42, new QualitySettings(), 1, new AudioPlan([], [], 1));

        await action.Should().ThrowAsync<RemoteStageStalledException>().WithMessage("The encode on the worker wrote nothing to its log for 47m 00s, so it was stopped.");
        runner.Requests.Should().NotContain(request => request.StandardInput != null, "no stage script is uploaded");
        runner.Requests[1].Arguments[^1].Should().Contain("stop_stage").And.Contain(session.EncodeDirectory(1));
        runner.Requests[^1].Arguments[^1].Should().Contain("rm -rf --").And.Contain(session.RemoteWorkspace);
    }

    /// <summary>A mode given for one run (<c>--remote</c>) can bring settings the validator skipped; the session refuses a bad limit.</summary>
    [Theory]
    [InlineData("0s")]
    [InlineData("never")]
    public void ASessionRefusesAStageSilenceTimeoutThatIsNotAPositiveDuration(string timeout)
    {
        var settings = CreateSettings();
        settings.Processing.RemoteSsh.StageSilenceTimeout = timeout;

        var create = () => new RemoteSshProcessingSession(new JobRecord { Id = Guid.NewGuid(), SourcePath = "C:\\Videos\\movie.mkv" }, settings, new ScriptedProcessRunner([]));

        create.Should().Throw<InvalidOperationException>().WithMessage(SettingsValidator.StageSilenceTimeoutMessage);
    }

    /// <summary>Exit 1 is the worker's answer no; any other failure is not an answer.</summary>
    [Theory]
    [InlineData(0, true)]
    [InlineData(1, false)]
    public async Task TheWorkerSaysWhetherAnAttemptsEncodeFinished(int exitCode, bool finished)
    {
        var runner = new ScriptedProcessRunner([new ExternalProcessResult(exitCode, string.Empty, string.Empty)]);
        var session = CreateSession(Guid.NewGuid(), runner);

        (await session.HasFinishedEncodeAsync(Output, 3)).Should().Be(finished);

        var command = runner.Requests.Single().Arguments[^1];
        command.Should().Contain($"{session.EncodeDirectory(3)}/exit-code").And.Contain($"{session.EncodeDirectory(3)}/output.mkv");
    }

    [Fact]
    public async Task AFailedEncodeCheckIsNotTakenForNo()
    {
        var runner = new ScriptedProcessRunner([new ExternalProcessResult(2, string.Empty, "bash: broken")]);

        var action = async () => await CreateSession(Guid.NewGuid(), runner).HasFinishedEncodeAsync(Output, 3);

        await action.Should().ThrowAsync<InvalidOperationException>().WithMessage("Remote encode check failed: bash: broken");
    }

    private static RemoteSshProcessingSession CreateSession(Guid id, IExternalProcessRunner runner, string sourcePath = "C:\\Videos\\movie.mkv", ManualClock? clock = null, IReadOnlyList<TimeSpan>? commandTimeouts = null)
    {
        clock ??= new ManualClock();
        return new RemoteSshProcessingSession(new JobRecord { Id = id, SourcePath = sourcePath }, CreateSettings(), runner, _ => Task.CompletedTask, clock.Read, clock.TickAsync, commandTimeouts, _ => Task.CompletedTask);
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

    /// <summary>Whether <paramref name="command"/> removes the partial upload: the verify-and-rename runs <c>rm -f --</c> too, on the checksum file.</summary>
    private static bool RemovesThePartial(string command) =>
        command.Contains("rm -f --", StringComparison.Ordinal) && !command.Contains("mv -f --", StringComparison.Ordinal);

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

    /// <summary>A download whose first sftp calls end as <paramref name="failures"/> say, each writing nothing; the next one writes the output.</summary>
    private sealed class FlakyDownloadRunner(string output, byte[] good, IEnumerable<ExternalProcessResult> failures) : IExternalProcessRunner
    {
        private readonly Queue<ExternalProcessResult> _failures = new(failures);

        public int Transfers { get; private set; }

        public Task<ExternalProcessResult> RunAsync(ExternalProcessRequest request, CancellationToken cancellationToken = default)
        {
            if (request.FileName == "ssh") return OkAsync($"{good.Length}\n{Sha256(good)}");
            Transfers++;
            if (_failures.TryDequeue(out var failure)) return Task.FromResult(failure);
            File.WriteAllBytes(output + ".partial", good);
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

    /// <summary>
    /// A worker with nothing uploaded yet whose first sftp call loses its connection (exit 255). After the drop, the partial
    /// check answers <paramref name="afterDrop"/>, or every SSH call fails to connect when it is null.
    /// </summary>
    /// <param name="afterRefusedRetry">When set, the second sftp call exits 1, as reput does onto a partial the dropped session
    /// finished after the re-check, and the partial check then answers this.</param>
    private sealed class DroppedUploadRunner(string? afterDrop, string? afterRefusedRetry = null, int firstExit = 255, string firstError = "Connection closed") : IExternalProcessRunner
    {
        public List<string> Transfers { get; } = [];
        public bool PartialRemovedAfterDrop { get; private set; }
        public bool Verified { get; private set; }

        public Task<ExternalProcessResult> RunAsync(ExternalProcessRequest request, CancellationToken cancellationToken = default)
        {
            if (request.FileName == "sftp")
            {
                Transfers.Add(request.StandardInput!.Split(' ')[0]);
                if (Transfers.Count == 1) return Result(firstExit, firstError);
                return Transfers.Count == 2 && afterRefusedRetry is not null ? Result(1, "destination file bigger or same size as source file") : Result(0);
            }
            var command = request.Arguments[^1];
            if (command.Contains("nproc", StringComparison.Ordinal)) return Result(0, "8 20000000000 100000000000");
            if (Transfers.Count == 0) return Result(0);
            if (afterDrop is null) return Result(255, "Connection timed out");
            if (command.Contains("head -c", StringComparison.Ordinal)) return Result(0, Transfers.Count == 1 ? afterDrop : afterRefusedRetry ?? afterDrop);
            if (RemovesThePartial(command)) PartialRemovedAfterDrop = true;
            if (command.Contains("mv -f --", StringComparison.Ordinal)) Verified = true;
            return Result(0);
        }

        private static Task<ExternalProcessResult> Result(int exitCode, string output = "") =>
            Task.FromResult(exitCode == 0 ? Ok(output) : new ExternalProcessResult(exitCode, string.Empty, output));
    }

    /// <summary>
    /// The worker's side of one upload with nothing on it yet: sftp writes <paramref name="uploaded"/> as the partial, and the
    /// verify-and-rename runs as the remote command does. It passes when the source's checksum file already holds the hash it
    /// was sent; otherwise it checks the partial against that hash, renames it to the source and writes the checksum file.
    /// A command that names no checksum file behaves as the plain check-then-rename did, and fails once the partial is gone.
    /// </summary>
    /// <param name="cutOffFirstVerify">The first verify does its work, as a try the session cut off keeps running on the worker,
    /// and never answers.</param>
    private sealed class VerifyWorker(byte[] uploaded, bool cutOffFirstVerify) : IExternalProcessRunner
    {
        private static readonly Regex HashInCommand = new("[0-9a-f]{64}", RegexOptions.CultureInvariant);
        private string? _checksum;

        public byte[]? Partial { get; private set; }
        public byte[]? Source { get; private set; }
        public int Verifies { get; private set; }

        public async Task<ExternalProcessResult> RunAsync(ExternalProcessRequest request, CancellationToken cancellationToken = default)
        {
            if (request.FileName == "sftp")
            {
                Partial = uploaded;
                return Ok();
            }
            var command = request.Arguments[^1];
            if (command.Contains("nproc", StringComparison.Ordinal)) return Ok("8 20000000000 100000");
            if (command.Contains("mv -f --", StringComparison.Ordinal))
            {
                var result = Verify(command);
                if (++Verifies > 1 || !cutOffFirstVerify) return result;
                await Task.Delay(Timeout.Infinite, cancellationToken);
                throw new System.Diagnostics.UnreachableException();
            }
            if (command.Contains("head -c", StringComparison.Ordinal)) return Ok(Partial is null ? string.Empty : $"{Partial.Length} {Sha256(Partial)}");
            if (RemovesThePartial(command)) Partial = null;
            // The check for a source an earlier run left.
            if (command.Contains("if test -f", StringComparison.Ordinal)) return Ok(Source is null ? string.Empty : $"{Source.Length} {Sha256(Source)}");
            return Ok();
        }

        private ExternalProcessResult Verify(string command)
        {
            var hash = HashInCommand.Match(command).Value;
            var readsChecksum = command.Contains(".sha256", StringComparison.Ordinal);
            bool Verified() => readsChecksum && Source is not null && _checksum == hash;
            if (Verified()) return Ok();
            if (Partial is not null && Sha256(Partial) == hash)
            {
                Source = Partial;
                Partial = null;
                if (readsChecksum) _checksum = hash;
                return Ok();
            }
            return Verified() ? Ok() : new ExternalProcessResult(1, string.Empty, Partial is null ? "stat: cannot statx 'source.mkv.partial': No such file or directory" : string.Empty);
        }
    }

    /// <summary>An SFTP transfer that stays pending until the clock reaches the tick it is given (<c>finishAtTick</c>), with the remote partial's size growing per poll.</summary>
    private sealed class UploadRunner : IExternalProcessRunner
    {
        // Continuations run inline, so completing it inside a tick finishes the transfer before the session looks again.
        private readonly TaskCompletionSource<ExternalProcessResult> _sftp = new();
        private readonly Queue<long> _remoteSizes;

        public UploadRunner(ManualClock clock, IEnumerable<long> remoteSizes, int finishAtTick)
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
