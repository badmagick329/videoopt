using VideoOptimiser.Application.Configuration;
using VideoOptimiser.Application.Tools;
using VideoOptimiser.Domain;

namespace VideoOptimiser.Application.Eligibility;

/// <summary>
/// The largest share of the original, in whole percent, a video's encode may keep: the lower of
/// <c>quality.maxEncodedPercent</c> and the share that still saves <c>savings.minimumBytesSaved</c>. ab-av1 gets it as
/// <c>--max-encoded-percent</c>, so the search itself rejects a CRF that would save too little, and a cap of 0 or less
/// means no CRF can, so the video is skipped before anything is uploaded or searched.
/// </summary>
/// <param name="MeasuredBytes">What ab-av1 compares an encode against. When it cuts samples, which drop audio and
/// subtitles, that is the video stream (bitrate × duration ÷ 8), and the audio, copied as it is, saves nothing. When it
/// tests the whole video, it is the file, as it is when the stream's size is unknown.</param>
/// <param name="MeasuredIsFile">True when <paramref name="MeasuredBytes"/> is the file's size.</param>
/// <param name="SetByMinimumBytes">True only when the saving set a cap strictly below <c>quality.maxEncodedPercent</c>.</param>
public sealed record SizeCap(int Percent, bool SetByMinimumBytes, long MeasuredBytes, bool MeasuredIsFile, long MinimumBytes, string MinimumText)
{
    public bool RulesOutEverything => Percent <= 0;

    /// <summary>The bytes an encode at <paramref name="encodedPercent"/> of the measured size would save.</summary>
    public long BytesSaved(double encodedPercent) => MeasuredBytes - (long)(encodedPercent / 100 * MeasuredBytes);

    public static SizeCap For(MediaInfo media, long fileBytes, QualitySettings quality, SavingsSettings savings)
    {
        if (!HumanReadableValues.TryParseSize(savings.MinimumBytesSaved, out var minimumBytes))
            throw new InvalidOperationException("savings.minimumBytesSaved must be validated before the size cap is computed.");

        var sampleCount = CrfSampleCountCalculator.Calculate(media.DurationSeconds, quality.CrfSearch.SampleCount);
        var wholeVideo = HumanReadableValues.TryParseDuration(quality.CrfSearch.SampleDuration, out var sampleDuration)
            && CrfSampleCountCalculator.TestsWholeVideo(media.DurationSeconds, sampleCount, sampleDuration);
        var isFile = wholeVideo || media.PrimaryVideoBitrate is not > 0 || media.DurationSeconds is not > 0;
        var measured = Math.Max(1, isFile ? fileBytes : (long)(media.PrimaryVideoBitrate!.Value * media.DurationSeconds!.Value / 8));
        var fromBytes = (int)Math.Clamp(Math.Floor((double)(measured - minimumBytes) / measured * 100), int.MinValue, int.MaxValue);
        return fromBytes < quality.MaxEncodedPercent
            ? new SizeCap(fromBytes, true, measured, isFile, minimumBytes, savings.MinimumBytesSaved)
            : new SizeCap(quality.MaxEncodedPercent, false, measured, isFile, minimumBytes, savings.MinimumBytesSaved);
    }
}
