using VideoOptimiser.Application.Jobs;
using VideoOptimiser.Domain.Jobs;

namespace VideoOptimiser.UnitTests;

/// <summary>
/// The real repository, with a seam per method: a test overrides one to stage a race, such as another terminal changing a
/// job between a command's read and its write.
/// </summary>
internal class DelegatingJobRepository(IJobRepository inner) : IJobRepository
{
    protected IJobRepository Inner { get; } = inner;

    public virtual Task<JobRecord?> FindOpenBySourceAsync(string databasePath, string sourcePath, CancellationToken cancellationToken = default) => Inner.FindOpenBySourceAsync(databasePath, sourcePath, cancellationToken);
    public virtual Task<JobRecord?> GetAsync(string databasePath, Guid id, CancellationToken cancellationToken = default) => Inner.GetAsync(databasePath, id, cancellationToken);
    public virtual Task<JobRecord> CreateAsync(string databasePath, JobRecord job, CancellationToken cancellationToken = default) => Inner.CreateAsync(databasePath, job, cancellationToken);
    public virtual Task UpdateAsync(string databasePath, JobRecord job, CancellationToken cancellationToken = default) => Inner.UpdateAsync(databasePath, job, cancellationToken);
    public virtual Task<bool> SetHeldAsync(string databasePath, JobRecord job, bool held, CancellationToken cancellationToken = default) => Inner.SetHeldAsync(databasePath, job, held, cancellationToken);
    public virtual Task<bool> RunNextAsync(string databasePath, JobRecord job, CancellationToken cancellationToken = default) => Inner.RunNextAsync(databasePath, job, cancellationToken);
    public virtual Task<bool> DropAsync(string databasePath, JobRecord job, CancellationToken cancellationToken = default) => Inner.DropAsync(databasePath, job, cancellationToken);
    public virtual Task<bool> RejectAsync(string databasePath, JobRecord job, CancellationToken cancellationToken = default) => Inner.RejectAsync(databasePath, job, cancellationToken);
    public virtual Task<bool> DismissAsync(string databasePath, JobRecord job, CancellationToken cancellationToken = default) => Inner.DismissAsync(databasePath, job, cancellationToken);
    public virtual Task<int> DismissAllFailedAsync(string databasePath, CancellationToken cancellationToken = default) => Inner.DismissAllFailedAsync(databasePath, cancellationToken);
    public virtual Task<IReadOnlyList<JobRecord>> ListAsync(string databasePath, bool terminal, CancellationToken cancellationToken = default) => Inner.ListAsync(databasePath, terminal, cancellationToken);
    public virtual Task MarkActiveJobsInterruptedAsync(string databasePath, bool includeFinalizing, CancellationToken cancellationToken = default) => Inner.MarkActiveJobsInterruptedAsync(databasePath, includeFinalizing, cancellationToken);
    public virtual Task<JobRecord?> FindOpenByImmichAssetAsync(string databasePath, string assetId, CancellationToken cancellationToken = default) => Inner.FindOpenByImmichAssetAsync(databasePath, assetId, cancellationToken);
    public virtual Task<IReadOnlyList<JobRecord>> ListCompletedImmichAsync(string databasePath, CancellationToken cancellationToken = default) => Inner.ListCompletedImmichAsync(databasePath, cancellationToken);
    public virtual Task<IReadOnlyDictionary<string, ImmichSkipRecord>> ListImmichSkipsAsync(string databasePath, CancellationToken cancellationToken = default) => Inner.ListImmichSkipsAsync(databasePath, cancellationToken);
    public virtual Task PutImmichSkipAsync(string databasePath, ImmichSkipRecord skip, CancellationToken cancellationToken = default) => Inner.PutImmichSkipAsync(databasePath, skip, cancellationToken);

    /// <summary>Changes the job as another terminal would, so every copy read before is stale.</summary>
    protected async Task ChangeBehindTheCommandAsync(string databasePath, Guid jobId, CancellationToken cancellationToken)
    {
        if (!await Inner.RunNextAsync(databasePath, (await Inner.GetAsync(databasePath, jobId, cancellationToken))!, cancellationToken)) throw new InvalidOperationException("The staged race did not land.");
    }
}
