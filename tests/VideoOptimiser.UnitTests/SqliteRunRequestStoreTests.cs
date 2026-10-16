using FluentAssertions;
using Microsoft.Data.Sqlite;
using VideoOptimiser.Application.Features.Run;
using VideoOptimiser.Infrastructure.Persistence;

namespace VideoOptimiser.UnitTests;

public sealed class SqliteRunRequestStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"VideoOptimiser.Tests.{Guid.NewGuid():N}");
    private readonly string _databasePath;
    private readonly SqliteRunRequestStore _store = new(new SqliteDatabaseInitializer());

    public SqliteRunRequestStoreTests()
    {
        Directory.CreateDirectory(_directory);
        _databasePath = Path.Combine(_directory, "requests.db");
    }

    [Fact]
    public async Task NoRowReadsAsNothingRequested()
    {
        (await _store.ReadAsync(_databasePath, CancellationToken.None)).Should().Be(new RunRequests(false, false, null));
    }

    [Theory]
    [InlineData(false, true, false)]
    [InlineData(true, false, true)]
    public async Task AStopSetsOnlyItsOwnFlag(bool now, bool expectedAfterVideo, bool expectedNow)
    {
        await _store.RequestStopAsync(_databasePath, now, CancellationToken.None);

        (await _store.ReadAsync(_databasePath, CancellationToken.None)).Should().Be(new RunRequests(expectedAfterVideo, expectedNow, null));
    }

    [Fact]
    public async Task AStopAfterTheVideoAndAStopNowAccumulate()
    {
        await _store.RequestStopAsync(_databasePath, now: false, CancellationToken.None);
        await _store.RequestStopAsync(_databasePath, now: true, CancellationToken.None);

        (await _store.ReadAsync(_databasePath, CancellationToken.None)).Should().Be(new RunRequests(true, true, null));
    }

    [Theory]
    [InlineData(WorkerFateChoice.Delete)]
    [InlineData(WorkerFateChoice.Keep)]
    public async Task TheFateRoundTripsAndAStopLeavesItAlone(WorkerFateChoice fate)
    {
        await _store.SetWorkerFateAsync(_databasePath, fate, CancellationToken.None);
        await _store.RequestStopAsync(_databasePath, now: true, CancellationToken.None);

        (await _store.ReadAsync(_databasePath, CancellationToken.None)).Should().Be(new RunRequests(false, true, fate));
    }

    [Fact]
    public async Task ChangingTheFateKeepsAnEarlierStop()
    {
        await _store.ResetAsync(_databasePath, WorkerFateChoice.Delete, CancellationToken.None);
        await _store.RequestStopAsync(_databasePath, now: false, CancellationToken.None);
        await _store.SetWorkerFateAsync(_databasePath, WorkerFateChoice.Keep, CancellationToken.None);

        (await _store.ReadAsync(_databasePath, CancellationToken.None)).Should().Be(new RunRequests(true, false, WorkerFateChoice.Keep));
    }

    [Theory]
    [InlineData(WorkerFateChoice.Keep)]
    [InlineData(null)]
    public async Task ResetClearsEarlierStopsAndRecordsTheFate(WorkerFateChoice? fate)
    {
        await _store.RequestStopAsync(_databasePath, now: false, CancellationToken.None);
        await _store.RequestStopAsync(_databasePath, now: true, CancellationToken.None);
        await _store.SetWorkerFateAsync(_databasePath, WorkerFateChoice.Delete, CancellationToken.None);

        await _store.ResetAsync(_databasePath, fate, CancellationToken.None);

        (await _store.ReadAsync(_databasePath, CancellationToken.None)).Should().Be(new RunRequests(false, false, fate));
    }

    [Fact]
    public async Task ResetOnAFreshDatabaseRecordsTheFate()
    {
        await _store.ResetAsync(_databasePath, WorkerFateChoice.Delete, CancellationToken.None);

        (await _store.ReadAsync(_databasePath, CancellationToken.None)).Should().Be(new RunRequests(false, false, WorkerFateChoice.Delete));
    }

    [Fact]
    public async Task AnUnknownFateTextReadsAsNoChoice()
    {
        await _store.ResetAsync(_databasePath, WorkerFateChoice.Delete, CancellationToken.None);
        await using (var connection = new SqliteConnection($"Data Source={_databasePath};Pooling=False"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "UPDATE run_requests SET worker_fate = 'explode';";
            _ = await command.ExecuteNonQueryAsync();
        }

        (await _store.ReadAsync(_databasePath, CancellationToken.None)).WorkerFate.Should().BeNull();
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
