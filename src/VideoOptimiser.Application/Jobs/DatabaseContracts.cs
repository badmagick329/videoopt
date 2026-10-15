
namespace VideoOptimiser.Application.Jobs;

public interface IDatabaseInitializer
{
    Task InitializeAsync(string databasePath, CancellationToken cancellationToken = default);
}

/// <summary>The database predates this build's schema. A reader that must not write cannot migrate it, so it stops.</summary>
public sealed class DatabaseOutdatedException(string databasePath) : Exception($"{databasePath} needs a migration.")
{
    public string DatabasePath { get; } = databasePath;
}
