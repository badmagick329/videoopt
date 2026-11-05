using System.Globalization;
using VideoOptimiser.Application.Tools;

namespace VideoOptimiser.Infrastructure.Tools;

/// <summary>
/// ab-av1's arguments for a <see cref="VideoPlan"/>, shared by <c>crf-search</c> and <c>encode</c> so the search scores and
/// sizes the frames the encode will make. Nothing for a video left as it is, so its arguments stay as they were before
/// VOPT-199.
/// </summary>
internal static class AbAv1VideoPlanArguments
{
    // ab-av1's own default, pinned because the reference filter below depends on it.
    private const string VmafFps = "25";

    public static IEnumerable<string> ForEncode(VideoPlan plan)
    {
        if (plan.Filter is null) yield break;
        yield return "--vfilter";
        yield return plan.Filter;
        if (plan.KeyframeInterval is not { } keyframes) yield break;
        yield return "--keyint";
        yield return keyframes.ToString(CultureInfo.InvariantCulture);
        // ab-av1 turns scene change detection on only with its own keyframe interval, which this one replaces.
        yield return "--scd";
        yield return "true";
    }

    /// <summary>
    /// The encode's arguments, plus a VMAF reference that pairs frame for frame with the sample. ab-av1 retimes both inputs
    /// to <c>-r 25</c> before filtering, so the sample's frames, already one per field, come out at 25 fps while the
    /// reference's fields come out of the same filter at 50: VMAF then compared each frame with the wrong field and scored a
    /// real 1080i concert 75 instead of 90. Analysing at the files' own timestamps (<c>--vmaf-fps 0</c>) misaligned the same
    /// way, because the sample's are rounded to Matroska's milliseconds. So the reference is restamped one frame per 1/25 s.
    /// </summary>
    public static IEnumerable<string> ForSearch(VideoPlan plan)
    {
        if (plan.Filter is null) yield break;
        foreach (var argument in ForEncode(plan)) yield return argument;
        yield return "--reference-vfilter";
        yield return $"{plan.Filter},setpts=N/({VmafFps}*TB)";
        yield return "--vmaf-fps";
        yield return VmafFps;
    }
}
