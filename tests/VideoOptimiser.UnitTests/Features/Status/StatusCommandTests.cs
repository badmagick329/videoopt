using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using VideoOptimiser.Application.Features.Worker;
using VideoOptimiser.Application.Jobs;
using VideoOptimiser.Domain.Jobs;
using VideoOptimiser.Infrastructure.Persistence;
using VideoOptimiser.UnitTests.Features.Queue;
using static VideoOptimiser.UnitTests.RunCli;

namespace VideoOptimiser.UnitTests.Features.Status;

/// <summary>
/// The status card, one test per state it can be in, with the whole transcript and exit code: what is in the queue, what runs,
/// what bills, and the next commands in the order they matter. The worker, the clock and the locks are the only fakes; the
/// queue is the real database. <c>queue list</c> keeps the job list, so none of these lists a job except the ones converting.
/// </summary>
public sealed class StatusCommandTests : QueueTestBase
{
    private static readonly DateTimeOffset Now = new(2026, 10, 15, 12, 0, 0, TimeSpan.Zero);
    private static readonly string[] HetznerInLocalMode = ["processing:", "  remoteSsh:", "    lifecycle: \"hetzner\""];

    private readonly FakeWorkerLifecycle _worker = new();

    protected override void RegisterFakes(IServiceCollection services)
    {
        services.AddSingleton<IRemoteWorkerLifecycle>(_worker);
        services.AddSingleton<TimeProvider>(new FixedTime(Now));
    }

    [Fact]
    public async Task AnEmptyQueueWithNothingToAddFromPointsAtTheList()
    {
        var result = await RunAsync("status");

        result.Should().Be((0, Card("Queue: empty.", "Run: none.", null, ("queue list", "lists the videos in the queue")), ""));
    }

    [Fact]
    public async Task AnEmptyQueueOffersEachSourceItCanAddFrom()
    {
        await WriteConfigAsync([.. Watch], immichConfigured: true);

        var result = await RunAsync("status");

        result.Should().Be((0, Card("Queue: empty.", "Run: none.", null, ("queue add", "queues new videos from your watch folders"), ("queue add immich", "queues new videos from Immich")), ""));
    }

    [Theory]
    [InlineData(1, 0, "Queue: 1 waiting.", "converts the video in the queue")]
    [InlineData(2, 1, "Queue: 2 waiting, 1 stopped.", "converts the 3 videos in the queue")]
    public async Task QueuedVideosAreWhatToRunNext(int waiting, int stopped, string queueLine, string description)
    {
        for (var index = 0; index < waiting; index++) await QueuedAsync($"clip-{index}.mp4");
        for (var index = 0; index < stopped; index++) await StopAsync(await QueuedAsync($"stopped-{index}.mp4"), JobStatus.Encoding);

        var result = await RunAsync("status");

        result.Should().Be((0, Card(queueLine, "Run: none.", null, ("run", description)), ""));
    }

    /// <summary>The run is held in this process, as its lock file would be held by another; the card reads the file without taking the lock.</summary>
    [Fact]
    public async Task ARunGoingShowsWhenItStartedAndWhatItConverts()
    {
        var converting = await QueuedAsync("clip-a.mp4");
        await MoveAsync(converting, JobStatus.Encoding);
        await QueuedAsync("clip-b.mp4");
        using var run = DatabaseLock.TryAcquire(DatabaseLock.RunPath(DatabasePath), LockHolders.Run, out _);
        var holder = DatabaseLock.ReadLiveHolder(DatabaseLock.RunPath(DatabasePath))!;

        var result = await RunAsync("status");

        result.Should().Be((0, Card("Queue: 1 converting, 1 waiting.", $"Run: video-optimiser run, started {CliLocks.FormatStarted(holder.StartedUtc)} (process {Environment.ProcessId}), converting clip-a.mp4.", null, ("queue list", "lists the videos in the queue")), ""));
    }

    /// <summary><c>immich run</c> is a run too, and a run is printed under the name it recorded; with nothing converting yet it names no video.</summary>
    [Fact]
    public async Task ARunWithNothingConvertingYetNamesItselfAsItRecordedAndOffersNothingToRun()
    {
        await QueuedAsync("clip-a.mp4");
        using var run = DatabaseLock.TryAcquire(DatabaseLock.RunPath(DatabasePath), LockHolders.ImmichRun, out _);
        var holder = DatabaseLock.ReadLiveHolder(DatabaseLock.RunPath(DatabasePath))!;

        var result = await RunAsync("status");

        result.Should().Be((0, Card("Queue: 1 waiting.", $"Run: video-optimiser immich run, started {CliLocks.FormatStarted(holder.StartedUtc)} (process {Environment.ProcessId}).", null, ("queue list", "lists the videos in the queue")), ""));
    }

    /// <summary>A background run has no command a person typed; the card says what it is, and still names the video it converts.</summary>
    [Fact]
    public async Task ABackgroundRunIsNamedAsOneAndStillShowsWhatItConverts()
    {
        var converting = await QueuedAsync("clip-a.mp4");
        await MoveAsync(converting, JobStatus.Encoding);
        using var run = DatabaseLock.TryAcquire(DatabaseLock.RunPath(DatabasePath), LockHolders.BackgroundRun, out _);
        var holder = DatabaseLock.ReadLiveHolder(DatabaseLock.RunPath(DatabasePath))!;

        var result = await RunAsync("status");

        result.Should().Be((0, Card("Queue: 1 converting.", $"Run: background run, started {CliLocks.FormatStarted(holder.StartedUtc)} (process {Environment.ProcessId}), converting clip-a.mp4.", null, ("queue list", "lists the videos in the queue")), ""));
    }

    [Theory]
    [InlineData(LockHolders.BackgroundRun, true)]
    [InlineData(LockHolders.Run, false)]
    public async Task TheJsonRunSaysWhetherItIsABackgroundRun(string holderName, bool background)
    {
        using var run = DatabaseLock.TryAcquire(DatabaseLock.RunPath(DatabasePath), holderName, out _);

        var result = await RunAsync("status", "--json");

        var json = JsonDocument.Parse(result.Stdout).RootElement.GetProperty("run");
        (json.GetProperty("command").GetString(), json.GetProperty("background").GetBoolean()).Should().Be((holderName, background));
    }

    [Fact]
    public async Task SeveralVideosConvertingAreNamedInRunOrder()
    {
        var first = await QueuedAsync("clip-a.mp4");
        var second = await QueuedAsync("clip-b.mp4");
        await MoveAsync(first, JobStatus.CrfSearching);
        await MoveAsync(second, JobStatus.Encoding);
        using var run = DatabaseLock.TryAcquire(DatabaseLock.RunPath(DatabasePath), LockHolders.Run, out _);
        var holder = DatabaseLock.ReadLiveHolder(DatabaseLock.RunPath(DatabasePath))!;

        var result = await RunAsync("status");

        result.Stdout.Split(Environment.NewLine)[1].Should().Be($"Run: video-optimiser run, started {CliLocks.FormatStarted(holder.StartedUtc)} (process {Environment.ProcessId}), converting clip-a.mp4, clip-b.mp4.");
    }

    [Fact]
    public async Task ABillingWorkerWithNoRunGoingIsOfferedForDeletionAfterTheQueue()
    {
        await WriteConfigAsync([.. HetznerInLocalMode, .. Watch]);
        await QueuedAsync("clip-a.mp4");
        _worker.Check = new WorkerCheck(CliFakes.Worker with { CreatedUtc = Now - new TimeSpan(2, 5, 0) }, 0.0119m, null);

        var result = await RunAsync("status");

        result.Should().Be((0, Card("Queue: 1 waiting.", "Run: none.", "Worker: video-optimiser-1 (cx43 in hel1), up 2h 05m, billing EUR 0.012/h incl. VAT.", ("run", "converts the video in the queue"), ("worker delete", "stops the worker billing")), ""));
        _worker.DatabasePaths.Should().Equal(DatabasePath);
    }

    [Fact]
    public async Task AWorkerWithoutAPriceOrAnAgeBillsHourly()
    {
        await WriteConfigAsync(HetznerInLocalMode);
        _worker.Check = new WorkerCheck(CliFakes.Worker with { ServerType = "", Location = "" }, null, null);

        var result = await RunAsync("status");

        result.Stdout.Split(Environment.NewLine)[2].Should().Be("Worker: video-optimiser-1, billing hourly.");
    }

    [Fact]
    public async Task AWorkerWithATypeButNoLocationShowsTheType()
    {
        await WriteConfigAsync(HetznerInLocalMode);
        _worker.Check = new WorkerCheck(CliFakes.Worker with { Location = "", CreatedUtc = Now - TimeSpan.FromMinutes(7) }, 0.0119m, null);

        var result = await RunAsync("status");

        result.Stdout.Split(Environment.NewLine)[2].Should().Be("Worker: video-optimiser-1 (cx43), up 7m 00s, billing EUR 0.012/h incl. VAT.");
    }

    [Fact]
    public async Task NoWorkerAtHetznerSaysSoAndOffersNothingToDelete()
    {
        await WriteConfigAsync(HetznerInLocalMode);
        _worker.Exists = false;

        var result = await RunAsync("status");

        result.Should().Be((0, Card("Queue: empty.", "Run: none.", "Worker: none.", ("queue list", "lists the videos in the queue")), ""));
    }

    [Fact]
    public async Task AWorkerHetznerWasNotAskedAboutIsShownAsRecordedWithTheReason()
    {
        await WriteConfigAsync([.. RunCli.ManagedHetzner]);
        _worker.Check = new WorkerCheck(CliFakes.Worker, null, "no API token is set");

        var result = await RunAsync("status");

        result.Should().Be((0, Card("Queue: empty.", "Run: none.", "Worker: video-optimiser-1 (cx43 in hel1), as recorded on this PC; Hetzner wasn't checked: no API token is set.", ("worker delete", "stops the worker billing")), ""));
    }

    /// <summary>A Hetzner message already ends in a full stop; the card does not add a second.</summary>
    [Fact]
    public async Task AReasonThatEndsInAFullStopIsNotGivenAnother()
    {
        await WriteConfigAsync(HetznerInLocalMode);
        _worker.Check = new WorkerCheck(null, null, "No such host is known.");

        var result = await RunAsync("status");

        result.Should().Be((0, Card("Queue: empty.", "Run: none.", "Worker: none recorded on this PC; Hetzner wasn't checked: No such host is known.", ("queue list", "lists the videos in the queue")), ""));
    }

    [Fact]
    public async Task AGoneServerWhoseIpv4StillBillsSaysSo()
    {
        await WriteConfigAsync(HetznerInLocalMode);
        _worker.Check = new WorkerCheck(CliFakes.Worker with { Status = "server-missing-primary-ip-retained" }, null, null);

        var result = await RunAsync("status");

        result.Should().Be((0, Card("Queue: empty.", "Run: none.", "Worker: video-optimiser-1 is gone from Hetzner, but its IPv4 address still bills.", ("worker delete", "stops the worker billing")), ""));
    }

    [Theory]
    [InlineData(1, "Queue: 1 ready to finalise.", "finalises the ready video; review it first")]
    [InlineData(2, "Queue: 2 ready to finalise.", "finalises the 2 ready videos; review them first")]
    public async Task ReadyVideosAreFinalisedFirst(int ready, string queueLine, string description)
    {
        for (var index = 0; index < ready; index++) await ReadyAsync(await QueuedAsync($"clip-{index}.mp4"));

        var result = await RunAsync("status");

        result.Should().Be((0, Card(queueLine, "Run: none.", null, ("finalize --ready", description)), ""));
    }

    /// <summary><c>finalize --ready</c> leaves warned videos out, so the card counts them apart and points at their warnings.</summary>
    [Theory]
    [InlineData(1, 1, "Queue: 2 ready to finalise (1 to check).", "finalises the ready video without warnings; review it first", "shows the warnings on 1 ready video; watch it, then finalise it on its own")]
    [InlineData(0, 2, "Queue: 2 ready to finalise (2 to check).", null, "shows the warnings on 2 ready videos; watch each, then finalise it on its own")]
    public async Task WarnedReadyVideosAreCountedApartAndLeftForQueueList(int clean, int warned, string queueLine, string? finaliseDescription, string checkDescription)
    {
        for (var index = 0; index < clean; index++) await ReadyAsync(await QueuedAsync($"clip-{index}.mp4"));
        for (var index = 0; index < warned; index++) await ReadyAsync(await QueuedAsync($"warned-{index}.mp4", job => job.ValidationWarnings = ["The decode test failed."]));

        var result = await RunAsync("status");

        (string, string)[] next = finaliseDescription is null ? [("queue list", checkDescription)] : [("finalize --ready", finaliseDescription), ("queue list", checkDescription)];
        result.Should().Be((0, Card(queueLine, "Run: none.", null, next), ""));
    }

    /// <summary>A finalise that is going is already doing what the card would offer, so it offers the rest.</summary>
    [Fact]
    public async Task AFinaliseGoingIsNotOfferedAgain()
    {
        await MoveAsync(await QueuedAsync("clip-a.mp4"), JobStatus.Encoding, JobStatus.Validating, JobStatus.ReadyToFinalize, JobStatus.Finalizing);
        await QueuedAsync("clip-b.mp4");
        using var finalise = DatabaseLock.TryAcquire(DatabaseLock.FinalizePath(DatabasePath), "finalize", out _);

        var result = await RunAsync("status");

        result.Should().Be((0, Card("Queue: 1 waiting, 1 ready to finalise.", "Run: none.", null, ("run", "converts the video in the queue")), ""));
    }

    /// <summary>Every command the card suggests names the configuration it was typed with, so it works when pasted.</summary>
    [Fact]
    public async Task EveryNextCommandCarriesTheTypedConfig()
    {
        await WriteConfigAsync(HetznerInLocalMode);
        await ReadyAsync(await QueuedAsync("clip-a.mp4"));
        await QueuedAsync("clip-b.mp4");
        await HeldAsync(await QueuedAsync("clip-c.mp4"));

        var result = await RunAsync("status");

        result.Should().Be((0, Card("Queue: 1 waiting, 1 held, 1 ready to finalise.", "Run: none.", "Worker: video-optimiser-1 (cx43 in hel1), as recorded on this PC; Hetzner wasn't checked: no API token is set.".Replace("as recorded on this PC; Hetzner wasn't checked: no API token is set", "billing EUR 0.012/h incl. VAT"), ("finalize --ready", "finalises the ready video; review it first"), ("run", "converts the video in the queue"), ("worker delete", "stops the worker billing")), ""));
        result.Stdout.Split(Environment.NewLine).Where(line => line.StartsWith("  video-optimiser ", StringComparison.Ordinal))
            .Should().OnlyContain(line => line.Contains(ConfigArgument, StringComparison.Ordinal));
    }

    [Fact]
    public async Task WithoutTheHetznerLifecycleTheCardHasNoWorkerPartAndNeverAsksHetzner()
    {
        _worker.Exists = true;

        var result = await RunAsync("status");

        result.Stdout.Should().NotContain("Worker");
        _worker.DatabasePaths.Should().BeEmpty();
    }

    [Fact]
    public async Task TheJsonCardOfABusyDatabaseHasEveryField()
    {
        await WriteConfigAsync([.. HetznerInLocalMode, .. Watch]);
        var converting = await QueuedAsync("clip-a.mp4");
        await MoveAsync(converting, JobStatus.Encoding);
        await QueuedAsync("clip-b.mp4");
        await HeldAsync(await QueuedAsync("clip-c.mp4"));
        await ReadyAsync(await QueuedAsync("clip-d.mp4"));
        var created = Now - new TimeSpan(2, 5, 0);
        _worker.Check = new WorkerCheck(CliFakes.Worker with { CreatedUtc = created }, 0.0119m, null);
        using var run = DatabaseLock.TryAcquire(DatabaseLock.RunPath(DatabasePath), LockHolders.Run, out _);
        var holder = DatabaseLock.ReadLiveHolder(DatabaseLock.RunPath(DatabasePath))!;

        var result = await RunAsync("status", "--json");

        var expected = $$"""
            {
              "queue": {
                "converting": 1,
                "waiting": 1,
                "stopped": 0,
                "held": 1,
                "readyToFinalise": 1,
                "toCheck": 0
              },
              "run": {
                "command": "video-optimiser run",
                "startedUtc": {{Json(holder.StartedUtc)}},
                "processId": {{Environment.ProcessId}},
                "background": false,
                "converting": [
                  {
                    "id": "{{converting.Id:N}}",
                    "file": "clip-a.mp4",
                    "status": "Encoding"
                  }
                ]
              },
              "worker": {
                "exists": true,
                "hetznerChecked": true,
                "notCheckedReason": null,
                "name": "video-optimiser-1",
                "serverId": 42,
                "status": "running",
                "serverType": "cx43",
                "location": "hel1",
                "createdUtc": {{Json(created)}},
                "uptimeSeconds": 7500,
                "hourlyGrossEur": 0.0119
              },
              "next": [
                {{Json($"video-optimiser finalize --ready{ConfigArgument}")}}
              ]
            }
            """;
        result.Should().Be((0, expected.ReplaceLineEndings() + Environment.NewLine, ""));
    }

    [Fact]
    public async Task TheJsonCardOfAnEmptyDatabaseHasNullsKept()
    {
        await WriteConfigAsync(Watch);

        var result = await RunAsync("status", "--json");

        var expected = $$"""
            {
              "queue": {
                "converting": 0,
                "waiting": 0,
                "stopped": 0,
                "held": 0,
                "readyToFinalise": 0,
                "toCheck": 0
              },
              "run": null,
              "worker": null,
              "next": [
                {{Json($"video-optimiser queue add{ConfigArgument}")}}
              ]
            }
            """;
        result.Should().Be((0, expected.ReplaceLineEndings() + Environment.NewLine, ""));
    }

    /// <summary>A worker Hetzner wasn't asked about: <c>exists</c> is still true, the server's own fields are the recorded ones, and there is no price or age.</summary>
    [Fact]
    public async Task TheJsonWorkerSaysWhyHetznerWasNotChecked()
    {
        await WriteConfigAsync(HetznerInLocalMode);
        _worker.Check = new WorkerCheck(CliFakes.Worker with { Status = RemoteWorkerStatuses.Recorded }, null, "no API token is set");

        var result = await RunAsync("status", "--json");

        using var document = JsonDocument.Parse(result.Stdout);
        var worker = document.RootElement.GetProperty("worker");
        worker.GetProperty("exists").GetBoolean().Should().BeTrue();
        worker.GetProperty("hetznerChecked").GetBoolean().Should().BeFalse();
        worker.GetProperty("notCheckedReason").GetString().Should().Be("no API token is set");
        worker.GetProperty("status").GetString().Should().Be("recorded");
        worker.GetProperty("uptimeSeconds").ValueKind.Should().Be(JsonValueKind.Null);
        worker.GetProperty("hourlyGrossEur").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task TheJsonWorkerIsNullServerFieldsWhenHetznerHasNone()
    {
        await WriteConfigAsync(HetznerInLocalMode);
        _worker.Exists = false;

        var result = await RunAsync("status", "--json");

        using var document = JsonDocument.Parse(result.Stdout);
        var worker = document.RootElement.GetProperty("worker");
        worker.GetProperty("exists").GetBoolean().Should().BeFalse();
        worker.GetProperty("hetznerChecked").GetBoolean().Should().BeTrue();
        foreach (var field in new[] { "notCheckedReason", "name", "serverId", "status", "serverType", "location", "createdUtc", "uptimeSeconds", "hourlyGrossEur" })
            worker.GetProperty(field).ValueKind.Should().Be(JsonValueKind.Null, field);
    }

    [Fact]
    public async Task TheBareCommandPrintsTheCard()
    {
        await QueuedAsync("clip-a.mp4");

        var result = await RunAsync();

        result.Should().Be((0, Card("Queue: 1 waiting.", "Run: none.", null, ("run", "converts the video in the queue")), ""));
    }

    [Fact]
    public async Task TheBareCommandWithNoConfigurationSaysHowToCreateOne()
    {
        var missing = Path.Combine(Workspace, "missing.yaml");

        var result = await ConsoleCapture.RunCliAsync("--config", missing);

        result.Should().Be((3, "", Transcript(
            $"The configuration file selected by --config does not exist: '{missing}'.",
            $"To create one, run 'video-optimiser config init --config \"{missing}\"'; 'video-optimiser --help' lists every command.")));
    }

    /// <summary>The hint is for someone who typed nothing; a typed <c>status</c> already knew what it wanted.</summary>
    [Fact]
    public async Task ATypedStatusWithNoConfigurationOnlyReportsIt()
    {
        var missing = Path.Combine(Workspace, "missing.yaml");

        var result = await ConsoleCapture.RunCliAsync("status", "--config", missing);

        result.Should().Be((3, "", Transcript($"The configuration file selected by --config does not exist: '{missing}'.")));
    }

    private async Task ReadyAsync(JobRecord job) => await MoveAsync(job, JobStatus.Encoding, JobStatus.Validating, JobStatus.ReadyToFinalize);

    private string[] Watch => ["watch:", "  roots:", $"    - path: '{Root}'"];

    /// <summary>The card: queue and run lines, the worker line when there is one, then the Next block.</summary>
    private string Card(string queue, string run, string? worker, params (string Verb, string Description)[] next) =>
        Transcript([queue, run, .. worker is null ? Array.Empty<string>() : new[] { worker }, "Next:", .. NextRows(next)]);

    /// <summary>The Next rows: two spaces, the command, then the description in a column padded to the longest command plus three.</summary>
    private string[] NextRows(params (string Verb, string Description)[] rows)
    {
        var commands = rows.Select(row => $"video-optimiser {row.Verb}{ConfigArgument}").ToArray();
        var width = commands.Max(command => command.Length) + 3;
        return [.. rows.Select((row, index) => $"  {commands[index].PadRight(width)}{row.Description}")];
    }

    private static string Json<T>(T value) => JsonSerializer.Serialize(value);

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
