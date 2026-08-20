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
    public void UsesDeterministicGuidWorkspaceAndAttemptPaths()
    {
        var id = Guid.Parse("11111111-2222-3333-4444-555555555555");
        var session = CreateSession(id, new QueueRunner([]));

        session.RemoteWorkspace.Should().Be("/var/tmp/video-optimiser/11111111222233334444555555555555");
        session.RemoteSourcePath.Should().Be("/var/tmp/video-optimiser/11111111222233334444555555555555/source.mkv");
        session.CrfDirectory.Should().EndWith("/crf");
        session.EncodeDirectory(3).Should().EndWith("/encode-3");
    }

    [Theory]
    [InlineData("plain", "'plain'")]
    [InlineData("a'b", "'a'\"'\"'b'")]
    public void PosixQuotePreservesArguments(string value, string expected) => RemoteSshProcessingSession.Quote(value).Should().Be(expected);

    [Fact]
    public void SftpQuoteNormalizesWindowsSeparators() => RemoteSshProcessingSession.SftpQuote("C:\\Video Files\\a.mkv").Should().Be("\"C:/Video Files/a.mkv\"");

    [Fact]
    public void RequiredDiskRoundsUp() => RemoteSshProcessingSession.CalculateRequiredDisk(101, 2.5).Should().Be(253);

    [Fact]
    public async Task RejectsInsufficientRemoteResourcesBeforeTransfer()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"VideoOptimiser.Remote.{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var source = Path.Combine(directory, "source.mkv");
        await File.WriteAllBytesAsync(source, new byte[100]);
        var runner = new QueueRunner([new ExternalProcessResult(0, "2 1024 100000", string.Empty)]);
        var session = CreateSession(Guid.NewGuid(), runner);

        var action = async () => await session.StageAsync(source, "fingerprint");

        await action.Should().ThrowAsync<InvalidOperationException>().WithMessage("Remote host has 2 CPUs*");
        runner.Requests.Should().ContainSingle();
        Directory.Delete(directory, true);
    }

    [Fact]
    public async Task RejectsInsufficientRemoteMemoryBeforeTransfer()
    {
        var directory = CreateTemporaryDirectory();
        var source = Path.Combine(directory, "source.mkv");
        await File.WriteAllBytesAsync(source, new byte[100]);
        var runner = new QueueRunner([new ExternalProcessResult(0, "8 1024 100000", string.Empty)]);
        var session = CreateSession(Guid.NewGuid(), runner, source);

        var action = async () => await session.StageAsync(source, "fingerprint");

        await action.Should().ThrowAsync<InvalidOperationException>().WithMessage("Remote host has 1024 available bytes*");
        runner.Requests.Should().ContainSingle();
        Directory.Delete(directory, true);
    }

    [Fact]
    public async Task RejectsInsufficientRemoteDiskBeforeTransfer()
    {
        var directory = CreateTemporaryDirectory();
        var source = Path.Combine(directory, "source.mkv");
        await File.WriteAllBytesAsync(source, new byte[100]);
        var runner = new QueueRunner([
            new ExternalProcessResult(0, "8 20000000000 200", string.Empty),
            new ExternalProcessResult(0, string.Empty, string.Empty)
        ]);
        var session = CreateSession(Guid.NewGuid(), runner, source);

        var action = async () => await session.StageAsync(source, "fingerprint");

        await action.Should().ThrowAsync<InvalidOperationException>().WithMessage("Remote host has 200 free bytes; 250 are required*");
        runner.Requests.Should().HaveCount(2);
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

        await session.StageAsync(source, "fingerprint");

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
        var session = CreateSession(Guid.NewGuid(), runner);

        await session.StageAsync(source, "fingerprint");

        runner.Requests[0].Arguments[^1].Should().Contain("df -B1 --output=avail").And.NotContain("df -PB1");
        runner.Requests.Should().Contain(request => request.FileName == "sftp" && request.StandardInput!.StartsWith("put ", StringComparison.Ordinal));
        runner.Requests[^1].Arguments[^1].Should().Contain("mv -f --");
        Directory.Delete(directory, true);
    }

    [Fact]
    public async Task ReattachesToACompletedCrfStageWithoutLaunchingAnotherScript()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"VideoOptimiser.Remote.{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var source = Path.Combine(directory, "source.mkv");
        await File.WriteAllTextAsync(source, "source");
        var runner = new QueueRunner([
            new ExternalProcessResult(0, "completed 0\n\u001ecrf 42 successful\n", string.Empty),
            new ExternalProcessResult(0, "crf 42 successful\n", string.Empty)
        ]);
        var session = CreateSession(Guid.NewGuid(), runner, source);

        var result = await session.SearchCrfAsync(source, new QualitySettings());

        result.Crf.Should().Be(42);
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

        var result = await session.EncodeAsync(source, output, 42, new QualitySettings(), 3);

        result.OutputPath.Should().Be(output);
        runner.Requests.Should().HaveCount(3);
        runner.Requests.Should().OnlyContain(request => request.StandardInput == null);
        runner.Requests[^1].Arguments[^1].Should().Contain("test -s");
        Directory.Delete(directory, true);
    }

    [Fact]
    public async Task CorruptResumedDownloadIsRetriedCleanlyBeforeInstallation()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"VideoOptimiser.Remote.{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var source = Path.Combine(directory, "source.mkv");
        var output = Path.Combine(directory, "output.mkv");
        await File.WriteAllTextAsync(source, "source");
        var good = Encoding.UTF8.GetBytes("verified-output");
        var expectedHash = Convert.ToHexString(SHA256.HashData(good)).ToLowerInvariant();
        var transfers = 0;
        var runner = new CallbackRunner(request =>
        {
            if (request.FileName == "ssh") return new ExternalProcessResult(0, expectedHash, string.Empty);
            transfers++;
            File.WriteAllBytes(output + ".partial", transfers == 1 ? Encoding.UTF8.GetBytes("corrupt") : good);
            return new ExternalProcessResult(0, string.Empty, string.Empty);
        });
        var session = CreateSession(Guid.NewGuid(), runner, source);

        await session.RetrieveOutputAsync(output, 1);

        transfers.Should().Be(2);
        File.ReadAllBytes(output).Should().Equal(good);
        File.Exists(output + ".partial").Should().BeFalse();
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

        await action.Should().ThrowAsync<InvalidOperationException>().WithMessage("*exit code 9*");
        var logPath = Path.Combine(directory, ".video-optimiser", $"{session.RemoteWorkspace!.Split('/').Last()}.crf.remote.log");
        (await File.ReadAllTextAsync(logPath)).Should().Contain("fatal encode error");
        runner.Requests[^1].Arguments[^1].Should().Contain("rm -rf --").And.Contain(session.RemoteWorkspace);
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

    private static RemoteSshProcessingSession CreateSession(Guid id, IExternalProcessRunner runner, string sourcePath = "C:\\Videos\\movie.mkv")
    {
        var settings = new AppSettings
        {
            Processing = new ProcessingSettings
            {
                Mode = "remoteSsh",
                RemoteSsh = new RemoteSshSettings { Host = "video-worker", WorkingDirectory = "/var/tmp/video-optimiser", MinimumCpuCount = 8, MinimumAvailableMemory = "14GiB", MinimumFreeDiskMultiplier = 2.5 }
            }
        };
        return new RemoteSshProcessingSession(new JobRecord { Id = id, SourcePath = sourcePath }, settings, runner, _ => Task.CompletedTask);
    }

    private static string CreateTemporaryDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"VideoOptimiser.Remote.{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        return directory;
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
}
