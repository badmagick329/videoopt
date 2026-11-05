using System.Globalization;

namespace VideoOptimiser.Application.Tools;

/// <summary>
/// What the encode does to the picture before AV1 sees it: nothing, or deinterlacing an interlaced original (VOPT-199). AV1
/// has no interlaced coding, so woven fields encoded as progressive frames keep their combing for good.
/// It travels on the job's <see cref="Configuration.QualitySettings"/> copy, like the size cap, because the CRF search and
/// the encode, local or on the worker, must filter alike: the search scores and sizes the frames the encode will make.
/// </summary>
/// <param name="Filter">The FFmpeg filter, or null to leave the picture as it is.</param>
/// <param name="KeyframeInterval">Frames between keyframes, when ab-av1's default must be replaced: see <see cref="For"/>.</param>
/// <param name="Note">The job's note saying what happened, for the person reviewing it.</param>
public sealed record VideoPlan(string? Filter, int? KeyframeInterval, string? Note)
{
    public static VideoPlan AsIs { get; } = new(null, null, null);

    /// <summary>The share of idet's called frames that must look combed with the same field first. In the VOPT-199 survey,
    /// interlaced originals scored above 99%, or about 80% for broadcasts with one clean frame in every five; progressive
    /// ones carried in an interlaced stream scored at most 15% on either parity. Halfway leaves room both ways.</summary>
    internal const double CombedThreshold = 0.5;

    // ab-av1 puts a keyframe every 10 s of input frames on a video longer than 3 minutes, and turns svt-av1's scene change
    // detection on with it; shorter videos get neither.
    private const double DefaultKeyframeSeconds = 10;
    private const double DefaultKeyframeFromSeconds = 180;

    /// <summary>Whether the stream says it is interlaced, so <see cref="For"/> needs idet's counts. A stream that doesn't is
    /// never checked: in the survey, the few such videos idet called combed held stylised scanlines, or an upscaled broadcast
    /// with the combing baked in, which <c>bwdif</c> cannot undo.</summary>
    public static bool StreamSaysInterlaced(MediaInfo media) => media.PrimaryVideoFieldOrder is "tt" or "bb" or "tb" or "bt";

    /// <summary>
    /// Deinterlaces only when the stream is flagged interlaced and idet agrees, finding most frames combed with the same field
    /// first: a flag alone misfires on progressive content carried in an interlaced stream, which <c>bwdif</c> would only
    /// soften and double in rate. The parity is idet's too, not the flag's: x264 output muxed to Matroska reads <c>tb</c>
    /// (bottom field shown first) for content woven top field first, and the wrong parity plays each pair of fields backwards.
    /// <paramref name="fields"/> is null when idet was not asked.
    /// </summary>
    public static VideoPlan For(MediaInfo media, FieldCounts? fields)
    {
        if (!StreamSaysInterlaced(media) || fields is null || DominantParity(fields) is not { } parity) return AsIs;
        // One frame per field, so motion stays as smooth as it was on a television, and every field's detail is kept.
        var filter = $"bwdif=mode=send_field:parity={parity}:deint=all";
        // ab-av1 counts its default 10 s in the input's frames, which bwdif doubles, so it would put keyframes 5 s apart.
        var keyframes = media is { DurationSeconds: > DefaultKeyframeFromSeconds, PrimaryVideoFrameRate: { } rate }
            ? (int)Math.Round(DefaultKeyframeSeconds * rate * 2, MidpointRounding.AwayFromZero)
            : (int?)null;
        return new VideoPlan(filter, keyframes, Describe(media));
    }

    /// <summary><c>tff</c> or <c>bff</c> when that many of the frames idet could call were combed with that field first.</summary>
    private static string? DominantParity(FieldCounts fields)
    {
        var called = fields.TopFieldFirst + fields.BottomFieldFirst + fields.Progressive;
        if (called == 0) return null;
        if (fields.TopFieldFirst >= CombedThreshold * called) return "tff";
        return fields.BottomFieldFirst >= CombedThreshold * called ? "bff" : null;
    }

    private static string Describe(MediaInfo media)
    {
        if (media is not { PrimaryVideoHeight: { } height, PrimaryVideoFrameRate: { } rate }) return "Deinterlaced, with one frame per field.";
        var lines = height.ToString(CultureInfo.InvariantCulture);
        return $"Deinterlaced from {lines}i{Rate(rate)} to {lines}p{Rate(rate * 2)}.";
    }

    private static string Rate(double rate) => rate.ToString("0.##", CultureInfo.InvariantCulture);
}
