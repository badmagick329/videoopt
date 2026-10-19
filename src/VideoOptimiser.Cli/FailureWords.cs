using VideoOptimiser.Domain.Jobs;

namespace VideoOptimiser.Cli;

/// <summary>
/// What a job's failure category reads as in a list: <c>EncodeFailed</c> and its kin are names for code and for
/// <c>--json</c>, so a person sees a phrase instead. Shared by the run's <c>Failed jobs:</c> list and by <c>history</c>.
/// </summary>
internal static class FailureWords
{
    // A category whose message already says what happened has no prefix: "Original does not exist: ..." needs no "the original is missing:" before it.
    private static readonly Dictionary<string, string?> Prefixes = new(StringComparer.Ordinal)
    {
        [FailureCategories.ProcessingFailed] = "failed",
        [FailureCategories.StagingFailed] = "the upload failed",
        [FailureCategories.CrfSearchFailed] = "the CRF search failed",
        [FailureCategories.EncodeFailed] = "encoding failed",
        [FailureCategories.DownloadFailed] = "the download failed",
        [FailureCategories.ValidationFailed] = "validation failed",
        [FailureCategories.FinalizationFailed] = "finalising failed",
        [FailureCategories.ManualInterventionRequired] = "needs your review",
        [FailureCategories.Ineligible] = "doesn't fit your rules",
        [FailureCategories.RemoteUnavailable] = "the worker couldn't be reached",
        [FailureCategories.StageStalled] = null,
        [FailureCategories.SourceMissing] = null,
        [FailureCategories.SourceNotReady] = null,
        [FailureCategories.SourceOutsideWatchRoot] = null,
        [FailureCategories.SourceChanged] = null,
        [FailureCategories.RemoteConfigurationChanged] = null,
        [FailureCategories.NotWorthConverting] = null,
        [FailureCategories.UnsupportedAudio] = null,
        [FailureCategories.FinalizationIncomplete] = null,
        [FailureCategories.Interrupted] = null
    };

    /// <summary>
    /// The category and message as one phrase, or null when the status already says it all (a dropped or rejected video). A sentence
    /// starts with a capital, for a line of its own; otherwise it continues a line, as in <c>history</c>.
    /// </summary>
    public static string? Describe(string? category, string? message, bool sentence)
    {
        if (category is FailureCategories.Cancelled or FailureCategories.Rejected) return null;
        if (!Prefixes.TryGetValue(category ?? string.Empty, out var prefix) || prefix is null) return message;
        if (sentence) prefix = char.ToUpperInvariant(prefix[0]) + prefix[1..];
        return message is null ? prefix : $"{prefix}: {message}";
    }
}
