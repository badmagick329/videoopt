namespace VideoOptimiser.Application.Eligibility;

/// <summary>
/// The codec every conversion produces, and the one place that says a video already in it is never converted (VOPT-168).
/// The config may not list it in a rule (<c>SettingsValidator</c>), no rule matches it even when one built in code lists it
/// (<see cref="EligibilityEvaluator"/>, which Immich discovery and <c>calibrate</c> use), and <c>queue add &lt;file&gt;</c> and
/// the run refuse it even with <c>--force</c>. <c>--force</c> overrides the user's rules about which videos are worth
/// converting; it does not override this, because an AV1 is the tool's own output or as good as one. Once <c>finalize</c> has
/// deleted or archived the original, the AV1 at its path is the only copy, and converting it would only replace that copy with
/// a second, lossier encode.
/// </summary>
public static class OutputCodec
{
    /// <summary>The name ffprobe reports, compared as the rules compare codecs.</summary>
    public const string Name = "av1";

    /// <summary>Why a video in it is refused: a scan's reason and the stored failure of a job the run refuses.</summary>
    public const string RefusalReason = "The video is already AV1, so converting it again would only lose quality.";

    public static bool IsOutputCodec(string? codec) => string.Equals(codec, Name, StringComparison.OrdinalIgnoreCase);
}
