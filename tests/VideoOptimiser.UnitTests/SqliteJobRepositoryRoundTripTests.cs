using System.Reflection;
using FluentAssertions;
using VideoOptimiser.Domain.Jobs;
using VideoOptimiser.Infrastructure.Persistence;

namespace VideoOptimiser.UnitTests;

/// <summary>
/// VOPT-149: every <see cref="JobRecord"/> property survives the store, through the INSERT and then the UPDATE. The
/// properties are filled and compared by reflection, so one added later without its column, or with a column half wired
/// (written but not read, or the reverse), fails here instead of losing its value silently.
/// </summary>
public sealed class SqliteJobRepositoryRoundTripTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"VideoOptimiser.Tests.{Guid.NewGuid():N}");
    private readonly string _databasePath;
    private readonly SqliteJobRepository _repository = new(new SqliteDatabaseInitializer());

    private static readonly PropertyInfo[] Settable = typeof(JobRecord).GetProperties().Where(property => property.CanWrite).ToArray();

    /// <summary>Not filled before the INSERT, which leaves them to the column defaults (the store sets the timestamps and version itself).</summary>
    private static readonly HashSet<string> NotInserted =
    [
        // Only the queue verbs write these, never CreateAsync or UpdateAsync, so a run's copy cannot undo a hold or a "run next".
        nameof(JobRecord.Held),
        nameof(JobRecord.Priority),
        // Only the dismiss verbs set it, on a failed job.
        nameof(JobRecord.DismissedUtc)
    ];

    /// <summary>Not filled again before the UPDATE: what the INSERT does not write, and what a job keeps from its creation.</summary>
    private static readonly HashSet<string> NotUpdated =
    [
        .. NotInserted,
        // The row the UPDATE targets.
        nameof(JobRecord.Id),
        // The compare-and-swap's input: the UPDATE requires it unchanged.
        nameof(JobRecord.RowVersion),
        // Stamped once by CreateAsync.
        nameof(JobRecord.CreatedUtc),
        // Only queue add --force sets it, at creation.
        nameof(JobRecord.Forced)
    ];

    public SqliteJobRepositoryRoundTripTests()
    {
        Directory.CreateDirectory(_directory);
        _databasePath = Path.Combine(_directory, "jobs.db");
    }

    [Fact]
    public async Task EveryJobPropertyReadsBackAsWrittenThroughCreateAndUpdate()
    {
        var job = new JobRecord();
        Fill(job, variant: 1, skip: NotInserted);
        job.Status = JobStatus.Encoding;
        await _repository.CreateAsync(_databasePath, job);
        AssertSame((await _repository.GetAsync(_databasePath, job.Id))!, job);

        Fill(job, variant: 2, skip: NotUpdated);
        job.Status = JobStatus.Validating;
        await _repository.UpdateAsync(_databasePath, job);
        AssertSame((await _repository.GetAsync(_databasePath, job.Id))!, job);
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private static void Fill(JobRecord job, int variant, HashSet<string> skip)
    {
        foreach (var property in Settable.Where(property => !skip.Contains(property.Name)))
        {
            var value = ValueFor(property, variant);
            value.Should().NotBe(DefaultOf(property.PropertyType), $"{property.Name} must be written with a value its default would not hide");
            property.SetValue(job, value);
        }
    }

    // A property of a type not listed here fails the test: give it a case, and the store a column.
    private static object ValueFor(PropertyInfo property, int variant)
    {
        var type = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
        var stamp = new DateTimeOffset(2026, 10, 10 + variant, 8, 30, 15, TimeSpan.FromHours(variant));
        return type switch
        {
            _ when type == typeof(Guid) => Guid.NewGuid(),
            _ when type == typeof(string) => $"{property.Name}-{variant}",
            _ when type == typeof(int) => 10 + variant,
            _ when type == typeof(long) => 5_000_000_000L + variant,
            _ when type == typeof(decimal) => 42.25m + variant,
            _ when type == typeof(bool) => true,
            _ when type == typeof(DateTimeOffset) => stamp.AddMinutes(property.Name.Length),
            _ when type == typeof(JobStatus) => JobStatus.Interrupted,
            _ when type == typeof(JobSourceKind) => JobSourceKind.Immich,
            _ when type == typeof(IReadOnlyList<string>) => new[] { $"{property.Name} line 1-{variant}", $"{property.Name} line 2-{variant}" },
            _ when type == typeof(VideoFacts) => new VideoFacts($"codec-{variant}", 1920 + variant, 1080 + variant, 18_000_000 + variant, 40.5 + variant, 90_000_000 + variant),
            _ => throw new NotSupportedException($"JobRecord.{property.Name} has a type ({property.PropertyType}) this test cannot fill.")
        };
    }

    private static object? DefaultOf(Type type) => type.IsValueType && Nullable.GetUnderlyingType(type) is null ? Activator.CreateInstance(type) : null;

    private static void AssertSame(JobRecord stored, JobRecord written)
    {
        foreach (var property in Settable)
        {
            property.GetValue(stored).Should().BeEquivalentTo(property.GetValue(written), options => options.WithStrictOrdering(), $"JobRecord.{property.Name} must read back as written");
        }
    }
}
