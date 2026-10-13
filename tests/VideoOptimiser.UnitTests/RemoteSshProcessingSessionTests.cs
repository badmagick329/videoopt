using FluentAssertions;
using System.Security.Cryptography;
using System.Text;
using VideoOptimiser.Application.Configuration;
using VideoOptimiser.Application.Jobs;
using VideoOptimiser.Application.Processing;
using VideoOptimiser.Infrastructure.Processing;

namespace VideoOptimiser.UnitTests;

public sealed class RemoteSshProcessingSessionTests
{
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
        arguments.Should().Contain("StrictHostKeyChecking=yes");
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
        var directory = CreateTemporaryDirectory();
        var source = Path.Combine(directory, "source.mkv");
        await File.WriteAllBytesAsync(source, new byte[100]);
        var runner = new QueueRunner([
            new ExternalProcessResult(0, probeOutput, string.Empty),
            new ExternalProcessResult(0, string.Empty, string.Empty)
        ]);
        var session = CreateSession(Guid.NewGuid(), runner, source);

        var action = async () => await session.StageAsync(source, "fingerprint");

        await action.Should().ThrowAsync<InvalidOperationException>().WithMessage(expectedMessage);
        runner.Requests.Should().NotContain(request => request.FileName == "sftp");
        Directory.Delete(directory, true);
    }

    [Fact]
    public async Task ReusesAValidatedPartialUploadPrefixWithReput()
    {
        var directory = CreateTemporaryDirectory();
        var source = Path.Combine(directory, "source.mkv");
        var bytes = Enumerable.Range(0, 100).Select(value => (byte)value).ToArray();
        await File.WriteAllBytesAsync(source, bytes);
        var prefixHash = Convert.ToHexString(SHA256.HashData(bytes.AsSpan(0, 40))).ToLowerInvariant();
        var runner = new QueueRunner([
            new ExternalProcessResult(0, "8 20000000000 100000", string.Empty),
            new ExternalProcessResult(0, string.Empty, string.Empty),
            new ExternalProcessResult(0, $"40 {prefixHash}", string.Empty),
            new ExternalProcessResult(0, string.Empty, string.Empty),
            new ExternalProcessResult(0, string.Empty, string.Empty)
        ]);
        var session = CreateSession(Guid.NewGuid(), runner, source);

        var progress = new CaptureProgress();
        await session.StageAsync(source, "fingerprint", progress);

        progress.Lines.Should().Equal(
            JobProgress.Step("Resuming the upload at 40 B of 100 B..."),
            JobProgress.Step("Uploaded 100 B in 0s, checksum verified."));
        runner.Requests.Should().ContainSingle(request => request.FileName == "sftp" && request.StandardInput!.StartsWith("reput ", StringComparison.Ordinal));
        runner.Requests.SelectMany(request => request.Arguments).Should().NotContain(argument => argument.Contains("rm -f --", StringComparison.Ordinal));
        Directory.Delete(directory, true);
    }

    [Fact]
    public async Task StagesWithResumableSftpAndAtomicRemoteRename()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"VideoOptimiser.Remote.{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var source = Path.Combine(directory, "source.mkv");
        await File.WriteAllBytesAsync(source, new byte[100]);
        var runner = new QueueRunner([
            new ExternalProcessResult(0, "8 20000000000 100000000000", string.Empty),
            new ExternalProcessResult(0, string.Empty, string.Empty),
            new ExternalProcessResult(0, string.Empty, string.Empty),
            new ExternalProcessResult(0, string.Empty, string.Empty),
            new ExternalProcessResult(0, string.Empty, string.Empty)
        ]);
        var progress = new CaptureProgress();
        var session = CreateSession(Guid.NewGuid(), runner, clock: new FakeClock());

        // The checks (CPU, hash, existing upload, verification) and the quick transfer all run inside the first second.
        await session.StageAsync(source, "fingerprint", progress);

        progress.Lines.Should().Equal(
            JobProgress.Step("Uploading 100 B to the worker..."),
            JobProgress.Step("Uploaded 100 B in 0s, checksum verified."));
        runner.Requests[0].Arguments[^1].Should().Contain("df -B1 --output=avail").And.NotContain("df -PB1");
        runner.Requests.Should().Contain(request => request.FileName == "sftp" && request.StandardInput!.StartsWith("put ", StringComparison.Ordinal));
        runner.Requests[^1].Arguments[^1].Should().Contain("mv -f --");
        Directory.Delete(directory, true);
    }

    [Fact]
    public async Task UploadProgressAppearsOnlyAfterTenSecondsThenAtMostOnceEveryTenSeconds()
    {
        var directory = CreateTemporaryDirectory();
        var source = Path.Combine(directory, "source.mkv");
        await File.WriteAllBytesAsync(source, new byte[1_048_576]);
        var clock = new FakeClock();
        var runner = new UploadRunner(clock, remoteSizes: [262_144, 524_288], finishAtTick: 6);
        var progress = new CaptureProgress();
        var session = CreateSession(Guid.NewGuid(), runner, clock: clock);

        await session.StageAsync(source, "fingerprint", progress);

        // Ticks are 5 s apart: nothing at 5 s, the first line at 10 s, nothing at 15 s, the second at 20 s, nothing at 25 s.
        progress.Lines.Should().Equal(
            JobProgress.Step("Uploading 1.0 MiB to the worker..."),
            JobProgress.Detail("25% of 1.0 MiB, 25.6 KiB/s, about 30s left"),
            JobProgress.Detail("50% of 1.0 MiB, 25.6 KiB/s, about 20s left"),
            JobProgress.Step("Uploaded 1.0 MiB in 30s, checksum verified."));
        runner.SizePolls.Should().Be(2, "a poll is an SSH connection, so none is made while no line is due");
        Directory.Delete(directory, true);
    }

    [Fact]
    public async Task AnOriginalAlreadyOnTheWorkerIsOneLineAndNoTransfer()
    {
        var directory = CreateTemporaryDirectory();
        var source = Path.Combine(directory, "source.mkv");
        var bytes = Enumerable.Range(0, 100).Select(value => (byte)value).ToArray();
        await File.WriteAllBytesAsync(source, bytes);
        var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var runner = new QueueRunner([
            new ExternalProcessResult(0, "8 20000000000 100000000000", string.Empty),
            new ExternalProcessResult(0, $"100 {hash}", string.Empty)
        ]);
        var progress = new CaptureProgress();
        var session = CreateSession(Guid.NewGuid(), runner);

        await session.StageAsync(source, "fingerprint", progress);

        progress.Lines.Should().Equal(JobProgress.Step("Already on the worker, checksum verified."));
        runner.Requests.Should().NotContain(request => request.FileName == "sftp");
        Directory.Delete(directory, true);
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(true, 3)]
    public async Task HashingPrintsProgressOnlyWhenItOutlastsTheDelay(bool slow, int minimumLines)
    {
        var directory = CreateTemporaryDirectory();
        var source = Path.Combine(directory, "source.mkv");
        await File.WriteAllBytesAsync(source, new byte[3 * 1_048_576]);
        var bytes = await File.ReadAllBytesAsync(source);
        var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var runner = new QueueRunner([
            new ExternalProcessResult(0, "8 20000000000 100000000000", string.Empty),
            new ExternalProcessResult(0, $"{bytes.Length} {hash}", string.Empty)
        ]);
        var progress = new CaptureProgress();
        // A clock that moves on every reading makes each read of the file look ten seconds apart.
        var clock = new FakeClock { StepPerReading = slow ? TimeSpan.FromSeconds(10) : TimeSpan.Zero };
        var session = CreateSession(Guid.NewGuid(), runner, clock: clock);

        await session.StageAsync(source, "fingerprint", progress);

        var checks = progress.Lines.Where(line => line.Kind == JobProgressKind.Detail).ToArray();
        checks.Length.Should().BeGreaterThanOrEqualTo(minimumLines);
        if (!slow) checks.Should().BeEmpty();
        else checks.Should().OnlyContain(line => line.Text.StartsWith("Checking the checksum: ", StringComparison.Ordinal) && line.Text.EndsWith("% of 3.0 MiB", StringComparison.Ordinal));
        Directory.Delete(directory, true);
    }

    [Fact]
    public async Task FailedUploadSizePollDoesNotAbortSuccessfulSftpTransfer()
    {
        var directory = CreateTemporaryDirectory();
        var source = Path.Combine(directory, "source.mkv");
        await File.WriteAllBytesAsync(source, new byte[100]);
        var runner = new FailingSizePollRunner();
        var session = CreateSession(Guid.NewGuid(), runner, clock: new FakeClock { TickLength = TimeSpan.FromSeconds(10) });

        // Progress is what makes the session poll at all, and only a poll that is due: the tick is 10 s, so the first one is.
        await session.StageAsync(source, "fingerprint", new CaptureProgress());

        runner.SftpCompleted.Should().BeTrue();
        runner.VerificationObserved.Should().BeTrue();
        Directory.Delete(directory, true);
    }

    [Fact]
    public async Task ReattachesToACompletedCrfStageWithoutLaunchingAnotherScript()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"VideoOptimiser.Remote.{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var source = Path.Combine(directory, "source.mkv");
        await File.WriteAllTextAsync(source, "source");
        var log = AbAv1OutputTests.Fixture("remote-crf-search.log");
        var runner = new QueueRunner([
            new ExternalProcessResult(0, $"completed 0\n\u001e{log}", string.Empty),
            new ExternalProcessResult(0, log, string.Empty)
        ]);
        var session = CreateSession(Guid.NewGuid(), runner, source);

        var result = await session.SearchCrfAsync(source, new QualitySettings());

        result.Crf.Should().Be(29);
        result.Prediction.Should().Be(new CrfPrediction("284.87 MiB", 29, "5 minutes"));
        runner.Requests.Should().HaveCount(2);
        runner.Requests.Should().OnlyContain(request => request.StandardInput == null);
        File.Exists(Path.Combine(directory, ".video-optimiser", $"{session.RemoteWorkspace!.Split('/').Last()}.crf.remote.log")).Should().BeTrue();
        Directory.Delete(directory, true);
    }

    [Fact]
    public async Task AlreadyRunningCrfStageIsPolledUntilCompletion()
    {
        var directory = CreateTemporaryDirectory();
        var source = Path.Combine(directory, "source.mkv");
        await File.WriteAllTextAsync(source, "source");
        var runner = new QueueRunner([
            new ExternalProcessResult(0, "running\n\u001eworking\n", string.Empty),
            new ExternalProcessResult(0, "completed 0\n\u001eworking\ncrf 42 successful\n", string.Empty),
            new ExternalProcessResult(0, "working\ncrf 42 successful\n", string.Empty)
        ]);
        var session = CreateSession(Guid.NewGuid(), runner, source);

        var result = await session.SearchCrfAsync(source, new QualitySettings());

        result.Crf.Should().Be(42);
        runner.Requests.Should().HaveCount(3);
        runner.Requests.Should().OnlyContain(request => request.StandardInput == null);
        Directory.Delete(directory, true);
    }

    [Fact]
    public async Task FreshStageUsesWritableCacheInsideItsGuidWorkspace()
    {
        var directory = CreateTemporaryDirectory();
        var source = Path.Combine(directory, "source.mkv");
        await File.WriteAllTextAsync(source, "source");
        var runner = new QueueRunner([
            new ExternalProcessResult(0, "missing\n\u001e", string.Empty),
            new ExternalProcessResult(0, string.Empty, string.Empty),
            new ExternalProcessResult(0, string.Empty, string.Empty),
            new ExternalProcessResult(0, "completed 0\n\u001ecrf 42 successful\n", string.Empty),
            new ExternalProcessResult(0, "crf 42 successful\n", string.Empty)
        ]);
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
        RemoteSshProcessingSession.BuildLaunchCommand("/w/a b", "/w/a b/run.sh").Should().Be("cd -- '/w/a b' && { nohup setsid bash '/w/a b/run.sh' </dev/null >/dev/null 2>&1 & }");
        Directory.Delete(directory, true);
    }

    [Fact]
    public async Task ReattachesToCompletedEncodingAndVerifiesItsOutput()
    {
        var directory = CreateTemporaryDirectory();
        var source = Path.Combine(directory, "source.mkv");
        var output = Path.Combine(directory, "output.mkv");
        await File.WriteAllTextAsync(source, "source");
        var runner = new QueueRunner([
            new ExternalProcessResult(0, "completed 0\n\u001eencode complete\n", string.Empty),
            new ExternalProcessResult(0, "encode complete\n", string.Empty),
            new ExternalProcessResult(0, string.Empty, string.Empty)
        ]);
        var session = CreateSession(Guid.NewGuid(), runner, source);

        var result = await session.EncodeAsync(source, output, 42, new QualitySettings(), 3, []);

        result.OutputPath.Should().Be(output);
        runner.Requests.Should().HaveCount(3);
        runner.Requests.Should().OnlyContain(request => request.StandardInput == null);
        runner.Requests[^1].Arguments[^1].Should().Contain("test -s");
        Directory.Delete(directory, true);
    }

    [Fact]
    public async Task DownloadReportsRemoteSizeAndResumesExistingPartial()
    {
        var directory = CreateTemporaryDirectory();
        var source = Path.Combine(directory, "source.mkv");
        var output = Path.Combine(directory, "output.mkv");
        await File.WriteAllTextAsync(source, "source");
        var good = Encoding.UTF8.GetBytes("verified-output");
        await File.WriteAllBytesAsync(output + ".partial", good[..5]);
        var expectedHash = Convert.ToHexString(SHA256.HashData(good)).ToLowerInvariant();
        var runner = new CallbackRunner(request =>
        {
            if (request.FileName == "ssh") return new ExternalProcessResult(0, $"{good.Length}\n{expectedHash}", string.Empty);
            File.WriteAllBytes(output + ".partial", good);
            return new ExternalProcessResult(0, string.Empty, string.Empty);
        });
        var progress = new CaptureProgress();
        var session = CreateSession(Guid.NewGuid(), runner, source);

        await session.RetrieveOutputAsync(output, 1, progress);

        progress.Lines.Should().Equal(
            JobProgress.Step("Resuming the download at 5 B of 15 B..."),
            JobProgress.Step("Downloaded 15 B in 0s, checksum verified."));
        File.ReadAllBytes(output).Should().Equal(good);
        Directory.Delete(directory, true);
    }

    [Fact]
    public async Task ADownloadPrintsItsStartAndSummaryLinesOnly()
    {
        var directory = CreateTemporaryDirectory();
        var source = Path.Combine(directory, "source.mkv");
        var output = Path.Combine(directory, "output.mkv");
        await File.WriteAllTextAsync(source, "source");
        var good = new byte[2 * 1_048_576];
        var expectedHash = Convert.ToHexString(SHA256.HashData(good)).ToLowerInvariant();
        var runner = new CallbackRunner(request =>
        {
            if (request.FileName == "ssh") return new ExternalProcessResult(0, $"{good.Length}\n{expectedHash}", string.Empty);
            File.WriteAllBytes(output + ".partial", good);
            return new ExternalProcessResult(0, string.Empty, string.Empty);
        });
        var progress = new CaptureProgress();
        var clock = new FakeClock { StepPerReading = TimeSpan.Zero };
        var session = CreateSession(Guid.NewGuid(), runner, source, clock);

        await session.RetrieveOutputAsync(output, 1, progress);

        progress.Lines.Should().Equal(
            JobProgress.Step("Downloading 2.0 MiB from the worker..."),
            JobProgress.Step("Downloaded 2.0 MiB in 0s, checksum verified."));
        Directory.Delete(directory, true);
    }

    [Fact]
    public async Task ADownloadThatFailsItsChecksumSaysSoAndDownloadsAgain()
    {
        var directory = CreateTemporaryDirectory();
        var source = Path.Combine(directory, "source.mkv");
        var output = Path.Combine(directory, "output.mkv");
        await File.WriteAllTextAsync(source, "source");
        var good = Encoding.UTF8.GetBytes("verified-output");
        var expectedHash = Convert.ToHexString(SHA256.HashData(good)).ToLowerInvariant();
        var transfers = 0;
        var runner = new CallbackRunner(request =>
        {
            if (request.FileName == "ssh") return new ExternalProcessResult(0, $"{good.Length}\n{expectedHash}", string.Empty);
            File.WriteAllBytes(output + ".partial", ++transfers == 1 ? Encoding.UTF8.GetBytes("corrupt-output!") : good);
            return new ExternalProcessResult(0, string.Empty, string.Empty);
        });
        var progress = new CaptureProgress();
        var session = CreateSession(Guid.NewGuid(), runner, source);

        await session.RetrieveOutputAsync(output, 1, progress);

        progress.Lines.Should().Equal(
            JobProgress.Step("Downloading 15 B from the worker..."),
            JobProgress.Detail("Checksum mismatch; downloading again."),
            JobProgress.Step("Downloaded 15 B in 0s, checksum verified."));
        transfers.Should().Be(2);
        File.ReadAllBytes(output).Should().Equal(good);
        File.Exists(output + ".partial").Should().BeFalse();
        Directory.Delete(directory, true);
    }

    [Fact]
    public async Task ALocalOutputThatAlreadyMatchesIsNotDownloadedAgain()
    {
        var directory = CreateTemporaryDirectory();
        var source = Path.Combine(directory, "source.mkv");
        var output = Path.Combine(directory, "output.mkv");
        await File.WriteAllTextAsync(source, "source");
        var good = Encoding.UTF8.GetBytes("verified-output");
        await File.WriteAllBytesAsync(output, good);
        var expectedHash = Convert.ToHexString(SHA256.HashData(good)).ToLowerInvariant();
        var runner = new QueueRunner([new ExternalProcessResult(0, $"{good.Length}\n{expectedHash}", string.Empty)]);
        var progress = new CaptureProgress();
        var session = CreateSession(Guid.NewGuid(), runner, source);

        await session.RetrieveOutputAsync(output, 1, progress);

        progress.Lines.Should().Equal(JobProgress.Step("Already downloaded, checksum verified."));
        runner.Requests.Should().ContainSingle();
        Directory.Delete(directory, true);
    }

    [Fact]
    public async Task RepeatedChecksumMismatchNeverInstallsOutputAndRemovesPartial()
    {
        var directory = CreateTemporaryDirectory();
        var source = Path.Combine(directory, "source.mkv");
        var output = Path.Combine(directory, "output.mkv");
        await File.WriteAllTextAsync(source, "source");
        var expectedHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("expected"))).ToLowerInvariant();
        var runner = new CallbackRunner(request =>
        {
            if (request.FileName == "ssh") return new ExternalProcessResult(0, expectedHash, string.Empty);
            File.WriteAllText(output + ".partial", "always-corrupt");
            return new ExternalProcessResult(0, string.Empty, string.Empty);
        });
        var session = CreateSession(Guid.NewGuid(), runner, source);

        var action = async () => await session.RetrieveOutputAsync(output, 1);

        await action.Should().ThrowAsync<InvalidDataException>().WithMessage("*after a clean retry*");
        File.Exists(output).Should().BeFalse();
        File.Exists(output + ".partial").Should().BeFalse();
        Directory.Delete(directory, true);
    }

    [Fact]
    public async Task FailedRemoteStageRetainsLogThenCleansItsGuidWorkspace()
    {
        var directory = CreateTemporaryDirectory();
        var source = Path.Combine(directory, "source.mkv");
        await File.WriteAllTextAsync(source, "source");
        var runner = new QueueRunner([
            new ExternalProcessResult(0, "completed 9\n\u001efatal encode error\n", string.Empty),
            new ExternalProcessResult(0, "fatal encode error\n", string.Empty),
            new ExternalProcessResult(0, string.Empty, string.Empty)
        ]);
        var session = CreateSession(Guid.NewGuid(), runner, source);

        var action = async () => await session.SearchCrfAsync(source, new QualitySettings());

        var thrown = (await action.Should().ThrowAsync<AbAv1StageException>().WithMessage("*exit code 9*")).Which;
        var logPath = Path.Combine(directory, ".video-optimiser", $"{session.RemoteWorkspace!.Split('/').Last()}.crf.remote.log");
        thrown.LogPath.Should().Be(logPath);
        (await File.ReadAllTextAsync(logPath)).Should().Contain("fatal encode error");
        runner.Requests[^1].Arguments[^1].Should().Contain("rm -rf --").And.Contain(session.RemoteWorkspace);
        Directory.Delete(directory, true);
    }

    [Fact]
    public async Task ACrfStageThatFoundNoWorthwhileCrfThrowsTheSkipExceptionAndStillCleansTheWorkspace()
    {
        var directory = CreateTemporaryDirectory();
        var source = Path.Combine(directory, "source.mkv");
        await File.WriteAllTextAsync(source, "source");
        var runner = new QueueRunner([
            new ExternalProcessResult(0, $"completed 1\n\u001e{NoWorthwhileCrfLog.Text}", string.Empty),
            new ExternalProcessResult(0, NoWorthwhileCrfLog.Text, string.Empty),
            new ExternalProcessResult(0, string.Empty, string.Empty)
        ]);
        var session = CreateSession(Guid.NewGuid(), runner, source);

        var action = async () => await session.SearchCrfAsync(source, new QualitySettings { MinimumVmaf = 97, MaxEncodedPercent = 80 });

        var thrown = (await action.Should().ThrowAsync<NoWorthwhileCrfException>()).Which;
        thrown.Best.Should().Be(new CrfAttempt(31, 97.31, 95));
        File.Exists(Path.Combine(directory, ".video-optimiser", $"{session.RemoteWorkspace!.Split('/').Last()}.crf.remote.log")).Should().BeTrue();
        runner.Requests[^1].Arguments[^1].Should().Contain("rm -rf --").And.Contain(session.RemoteWorkspace);
        Directory.Delete(directory, true);
    }

    [Fact]
    public async Task AnotherFailedRemoteStageReportsTheErrorLineOfItsLog()
    {
        var directory = CreateTemporaryDirectory();
        var source = Path.Combine(directory, "source.mkv");
        await File.WriteAllTextAsync(source, "source");
        const string log = "[2026-10-13T10:55:18Z INFO  ab_av1::command::crf_search] starting\nError: ffmpeg exited with status 1\nCommand exited with non-zero status 1\n\tExit status: 1\n";
        var runner = new QueueRunner([
            new ExternalProcessResult(0, $"completed 1\n\u001e{log}", string.Empty),
            new ExternalProcessResult(0, log, string.Empty),
            new ExternalProcessResult(0, string.Empty, string.Empty)
        ]);
        var session = CreateSession(Guid.NewGuid(), runner, source);

        var action = async () => await session.SearchCrfAsync(source, new QualitySettings());

        await action.Should().ThrowAsync<InvalidOperationException>().WithMessage("Remote processing stage failed with exit code 1: ffmpeg exited with status 1");
        Directory.Delete(directory, true);
    }

    [Fact]
    public async Task RepeatedSsh255BecomesRemoteConnectionFailure()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"VideoOptimiser.Remote.{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var source = Path.Combine(directory, "source.mkv");
        await File.WriteAllTextAsync(source, "source");
        var runner = new QueueRunner(Enumerable.Repeat(new ExternalProcessResult(255, string.Empty, "connection refused"), 3));
        var session = CreateSession(Guid.NewGuid(), runner, source);

        var action = async () => await session.StageAsync(source, "fingerprint");

        await action.Should().ThrowAsync<RemoteConnectionException>();
        runner.Requests.Should().HaveCount(3);
        Directory.Delete(directory, true);
    }

    [Fact]
    public async Task CancellationTargetsTheDetachedProcessGroupWithTermThenKill()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"VideoOptimiser.Remote.{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var source = Path.Combine(directory, "source.mkv");
        await File.WriteAllTextAsync(source, "source");
        var runner = new QueueRunner([
            new ExternalProcessResult(0, "running\n\u001e", string.Empty),
            new ExternalProcessResult(0, string.Empty, string.Empty)
        ]);
        var session = CreateSession(Guid.NewGuid(), runner, source);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var action = async () => await session.SearchCrfAsync(source, new QualitySettings(), cancellationToken: cancellation.Token);
        await action.Should().ThrowAsync<OperationCanceledException>();
        await session.CancelAsync();

        var command = runner.Requests[^1].Arguments[^1];
        command.Should().Contain("kill -TERM -- -");
        command.Should().Contain("kill -KILL -- -");
        Directory.Delete(directory, true);
    }

    private static RemoteSshProcessingSession CreateSession(Guid id, IExternalProcessRunner runner, string sourcePath = "C:\\Videos\\movie.mkv", FakeClock? clock = null)
    {
        var settings = new AppSettings
        {
            Processing = new ProcessingSettings
            {
                Mode = "remoteSsh",
                RemoteSsh = new RemoteSshSettings { Host = "video-worker", WorkingDirectory = "/var/tmp/video-optimiser", MinimumCpuCount = 8, MinimumAvailableMemory = "14GiB", MinimumFreeDiskMultiplier = 2.5 }
            }
        };
        clock ??= new FakeClock();
        return new RemoteSshProcessingSession(new JobRecord { Id = id, SourcePath = sourcePath }, settings, runner, _ => Task.CompletedTask, clock.Read, clock.TickAsync);
    }

    private static string CreateTemporaryDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"VideoOptimiser.Remote.{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        return directory;
    }

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

    private sealed class CallbackRunner(Func<ExternalProcessRequest, ExternalProcessResult> callback) : IExternalProcessRunner
    {
        public Task<ExternalProcessResult> RunAsync(ExternalProcessRequest request, CancellationToken cancellationToken = default) => Task.FromResult(callback(request));
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
            if (command.Contains("nproc", StringComparison.Ordinal)) return Task.FromResult(new ExternalProcessResult(0, "8 20000000000 100000000000", string.Empty));
            if (command.Contains("mv -f", StringComparison.Ordinal))
            {
                VerificationObserved = true;
                return Task.FromResult(new ExternalProcessResult(0, string.Empty, string.Empty));
            }
            if (command.Contains("source.mkv.partial", StringComparison.Ordinal))
            {
                if (command.Contains("sha256sum", StringComparison.Ordinal)) return Task.FromResult(new ExternalProcessResult(0, string.Empty, string.Empty));
                if (command.Contains("stat -c", StringComparison.Ordinal))
                {
                    _sftp.TrySetResult(new ExternalProcessResult(0, string.Empty, string.Empty));
                    return Task.FromResult(new ExternalProcessResult(1, string.Empty, "size poll failed"));
                }
                return Task.FromResult(new ExternalProcessResult(0, string.Empty, string.Empty));
            }
            return Task.FromResult(new ExternalProcessResult(0, string.Empty, string.Empty));
        }
    }

    private sealed class CaptureProgress : IProgress<JobProgress>
    {
        public List<JobProgress> Lines { get; } = [];

        public void Report(JobProgress value) => Lines.Add(value);
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

    /// <summary>An SFTP transfer that stays pending until <paramref name="finishAtTick"/>, with the remote partial's size growing per poll.</summary>
    private sealed class UploadRunner : IExternalProcessRunner
    {
        // Continuations run inline, so completing it inside a tick finishes the transfer before the session looks again.
        private readonly TaskCompletionSource<ExternalProcessResult> _sftp = new();
        private readonly Queue<long> _remoteSizes;

        public UploadRunner(FakeClock clock, IEnumerable<long> remoteSizes, int finishAtTick)
        {
            _remoteSizes = new Queue<long>(remoteSizes);
            clock.OnTick = tick => { if (tick == finishAtTick) _sftp.TrySetResult(new ExternalProcessResult(0, string.Empty, string.Empty)); };
        }

        public int SizePolls { get; private set; }

        public Task<ExternalProcessResult> RunAsync(ExternalProcessRequest request, CancellationToken cancellationToken = default)
        {
            if (request.FileName == "sftp") return _sftp.Task;
            var command = request.Arguments[^1];
            if (command.Contains("nproc", StringComparison.Ordinal)) return Ok("8 20000000000 100000000000");
            if (command.Contains("else printf", StringComparison.Ordinal))
            {
                SizePolls++;
                return Ok(_remoteSizes.Dequeue().ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
            return Ok(string.Empty);
        }

        private static Task<ExternalProcessResult> Ok(string output) => Task.FromResult(new ExternalProcessResult(0, output, string.Empty));
    }
}
