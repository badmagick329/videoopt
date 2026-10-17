using System.Globalization;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using VideoOptimiser.Domain.Jobs;
using VideoOptimiser.Infrastructure.Persistence;
using VideoOptimiser.UnitTests.Features.Queue;

namespace VideoOptimiser.UnitTests.Features.Cost;

/// <summary>
/// <c>cost</c> through the real CLI over a database holding the run records as VOPT-127's stores write them. The clock is the
/// only fake. The figures' branches are <see cref="CostReportTests"/>'; these check the read, the wording and the JSON shape.
/// </summary>
[Collection(ConsoleCapture.Collection)]
public sealed class CostCommandTests : QueueTestBase
{
    private static readonly DateTimeOffset Now = new(2026, 10, 17, 12, 0, 0, TimeSpan.Zero);
    private readonly SqliteRunRecordStore _runs = new(new SqliteDatabaseInitializer());
    private readonly SqliteWorkerBillStore _bills = new(new SqliteDatabaseInitializer());

    protected override void RegisterFakes(IServiceCollection services) => services.AddSingleton<TimeProvider>(new ManualTime(Now));

    [Fact]
    public async Task NothingRecordedSaysSoInOneLine()
    {
        var result = await RunAsync("cost");

        result.Should().Be((0, "Nothing to show yet: the cost appears after a run on a Hetzner worker." + Environment.NewLine, ""));
    }

    /// <summary>The worker came up 10 minutes into the run and is still up, so the run pays from then and the bill counts to now.</summary>
    [Fact]
    public async Task ARunOnAWorkerStillUpShowsTheLifetimeThenTheRun()
    {
        await RecordRunAsync();

        var result = await RunAsync("cost");

        var when = $"{At(-110).ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)} to {At(-20).ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture)}";
        (result.ExitCode, result.Stdout.ReplaceLineEndings("\n"), result.Stderr).Should().Be((0, $"""
            All time: EUR 192.86 per TB freed. Hetzner billed EUR 0.27 for 1 worker, which freed 1.3 GiB; 572.2 MiB more is waiting for review.
            A worker is still up: its bill counts to now and grows until the worker is deleted.
            Runs, newest first:
              {when}  video-optimiser-1  1h 20m  2 videos  EUR 0.18  1.3 GiB freed, 572.2 MiB waiting for review  EUR 128.57 per TB
            For each video's figures, run 'video-optimiser cost --json --config "{Config}"'.

            """.ReplaceLineEndings("\n"), ""));
    }

    [Fact]
    public async Task JsonCarriesTheLifetimeTheRunsAndTheirVideos()
    {
        var (run, holiday, garden) = await RecordRunAsync();

        var result = await RunAsync("cost", "--json");

        result.ExitCode.Should().Be(0);
        using var document = JsonDocument.Parse(result.Stdout);
        var lifetime = document.RootElement.GetProperty("lifetime");
        (lifetime.GetProperty("billedEur").GetDecimal(), lifetime.GetProperty("freedBytes").GetInt64(), lifetime.GetProperty("waitingBytes").GetInt64(), lifetime.GetProperty("eurPerTb").GetDecimal(),
            lifetime.GetProperty("workers").GetInt32(), lifetime.GetProperty("stillBilling").GetBoolean(), lifetime.GetProperty("endEstimated").GetBoolean(), lifetime.GetProperty("priceMissing").GetBoolean())
            .Should().Be((0.27m, 1_400_000_000L, 600_000_000L, 192.8571m, 1, true, false, false));
        var cost = document.RootElement.GetProperty("runs").EnumerateArray().Single();
        (cost.GetProperty("id").GetString(), cost.GetProperty("startedUtc").GetString(), cost.GetProperty("endUtc").GetString(), cost.GetProperty("workerServerId").GetInt64(), cost.GetProperty("workerName").GetString(),
            cost.GetProperty("hourlyGrossEur").GetDecimal(), cost.GetProperty("workerSeconds").GetInt64(), cost.GetProperty("eur").GetDecimal(), cost.GetProperty("freedBytes").GetInt64(),
            cost.GetProperty("waitingBytes").GetInt64(), cost.GetProperty("eurPerTb").GetDecimal(), cost.GetProperty("videoCount").GetInt32())
            .Should().Be((run.ToString("N"), "2026-10-17T10:10:00+00:00", "2026-10-17T11:40:00+00:00", 1L, "video-optimiser-1", 0.135m, 4800L, 0.18m, 1_400_000_000L, 600_000_000L, 128.5714m, 2));
        cost.GetProperty("videos").EnumerateArray().Select(Video).Should().Equal(
            (holiday.Id.ToString("N"), "holiday.mp4", 1800L, 0.0675m, 1_400_000_000L, "freed", "Completed", 48.2143m, false),
            (garden.Id.ToString("N"), "garden.mp4", 1500L, 0.0563m, 600_000_000L, "waitingForReview", "ReadyToFinalize", (decimal?)null, false));
    }

    private static (string, string?, long, decimal, long, string?, string?, decimal?, bool) Video(JsonElement video) => (
        video.GetProperty("jobId").GetString()!, video.GetProperty("file").GetString(), video.GetProperty("workerSeconds").GetInt64(), video.GetProperty("eur").GetDecimal(),
        video.GetProperty("savedBytes").GetInt64(), video.GetProperty("state").GetString(), video.GetProperty("status").GetString(),
        video.GetProperty("eurPerTb").ValueKind == JsonValueKind.Null ? null : video.GetProperty("eurPerTb").GetDecimal(), video.GetProperty("priceMissing").GetBoolean());

    /// <summary>
    /// A run from 10:10 to 11:40 on a worker created at 10:20 at EUR 0.135 an hour and still up at noon: holiday.mp4 converted
    /// for 30 minutes and was finalised, garden.mp4 for 25 and waits for review.
    /// </summary>
    private async Task<(Guid Run, JobRecord Holiday, JobRecord Garden)> RecordRunAsync()
    {
        var run = Guid.NewGuid();
        await _runs.AddRunAsync(DatabasePath, run, "video-optimiser run", At(-110), CancellationToken.None);
        await _bills.AddAsync(DatabasePath, new WorkerBillEntry(1, "video-optimiser-1", "cpx42", "hel1", 0.135m, At(-100)), At(-100), CancellationToken.None);
        await _runs.SetRunWorkerAsync(DatabasePath, run, 1, CancellationToken.None);
        var holiday = await JobAsync("holiday.mp4", JobStatus.Completed, 2_000_000_000, 600_000_000);
        var garden = await JobAsync("garden.mp4", JobStatus.ReadyToFinalize, 1_000_000_000, 400_000_000);
        await _runs.CloseSpanAsync(DatabasePath, await _runs.OpenSpanAsync(DatabasePath, run, holiday.Id, 1, At(-80), CancellationToken.None), At(-50), CancellationToken.None);
        await _runs.CloseSpanAsync(DatabasePath, await _runs.OpenSpanAsync(DatabasePath, run, garden.Id, 1, At(-50), CancellationToken.None), At(-25), CancellationToken.None);
        await _runs.EndRunAsync(DatabasePath, run, At(-20), CancellationToken.None);
        return (run, holiday, garden);
    }

    private async Task<JobRecord> JobAsync(string name, JobStatus status, long sourceBytes, long outputBytes) =>
        await Repository.CreateAsync(DatabasePath, new JobRecord { Id = Guid.NewGuid(), SourcePath = await FileAsync(Root, name), SourceFingerprint = "original", Status = status, SourceSizeBytes = sourceBytes, OutputSizeBytes = outputBytes });

    private static DateTimeOffset At(int minutes) => Now.AddMinutes(minutes);
}
