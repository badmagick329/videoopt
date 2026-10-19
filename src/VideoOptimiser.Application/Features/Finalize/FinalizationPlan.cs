using VideoOptimiser.Domain.Jobs;

namespace VideoOptimiser.Application.Features.Finalize;

/// <summary>
/// What one finalise takes and what it leaves out for review, chosen from the open jobs. <c>finalize --ready</c>,
/// <c>immich finalize</c> and the TUI's <c>f</c> and <c>F</c> all choose here, so they agree on the videos and their order.
/// Both lists are in run order (<see cref="JobRunOrder"/>): the order the Queue showed them in, so a batch finalises the
/// videos in the order the user saw them convert.
/// </summary>
/// <param name="Ready">The videos to finalise, in order.</param>
/// <param name="LeftOut">The warned videos a bulk finalise skips, so each is finalised on its own after the user watched it.</param>
public sealed record FinalizationPlan(IReadOnlyList<JobRecord> Ready, IReadOnlyList<JobRecord> LeftOut)
{
    /// <summary>Nothing to finalise and nothing left out: no job is finalisable.</summary>
    public bool IsEmpty => Ready.Count == 0 && LeftOut.Count == 0;

    /// <summary><c>finalize --ready</c> and the TUI's <c>F</c>: every finalisable video, folder and Immich alike.</summary>
    public static FinalizationPlan Bulk(IEnumerable<JobRecord> open) => Split(Finalisable(open));

    /// <summary><c>immich finalize</c>: as <see cref="Bulk"/>, over the Immich videos only. A ready folder video is not its business.</summary>
    public static FinalizationPlan BulkImmich(IEnumerable<JobRecord> open) => Split(Finalisable(open).Where(job => job.SourceKind == JobSourceKind.Immich));

    /// <summary>
    /// The TUI's <c>f</c>: the one video, warned or not, since pressing <c>f</c> on its row is the purpose a bulk finalise
    /// waits for. Empty when it is not finalisable now.
    /// </summary>
    public static FinalizationPlan One(IEnumerable<JobRecord> open, Guid id) => new([.. Finalisable(open).Where(job => job.Id == id)], []);

    private static IEnumerable<JobRecord> Finalisable(IEnumerable<JobRecord> open) => JobRunOrder.Sort(open).Where(FinalizationRules.IsFinalisable);

    private static FinalizationPlan Split(IEnumerable<JobRecord> finalisable)
    {
        var jobs = finalisable.ToArray();
        return new([.. jobs.Where(FinalizationRules.IsBulkFinalisable)], [.. jobs.Where(FinalizationRules.IsLeftForReview)]);
    }
}
