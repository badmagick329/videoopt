namespace VideoOptimiser.Application.Tools;

/// <summary>
/// The VMAF model every CRF search names, local or on the worker (VOPT-202). ab-av1 0.12.0 defaults to the v1.0.16 models
/// and falls back to v0.6.1 only when libvmaf lacks them, so an FFmpeg or libvmaf upgrade on either machine would silently
/// change how the same <c>minimumVmaf</c> is judged. Pinning keeps a model change deliberate: adopting v1.0.16 needs
/// <c>calibrate</c>, a retuned <c>minimumVmaf</c> and a QualityFingerprint change.
/// </summary>
public static class VmafModels
{
    public const string Standard = "vmaf_v0.6.1";
    public const string FourK = "vmaf_4k_v0.6.1";

    // ab-av1 0.11.7's own switch: the 4K model only when the encoded sample is wider than 2560 and taller than 1440.
    private const int FourKMinimumWidthExclusive = 2560;
    private const int FourKMinimumHeightExclusive = 1440;

    /// <summary>
    /// The model ab-av1 0.11.7 chose for this video. It judged the encoded sample's stored size, and FFmpeg turns a video with
    /// a quarter-turn display rotation upright while encoding the sample, so a portrait phone video's 3840×2160 is 2160×3840
    /// there and got the standard model. An unknown size gets the standard model too: it is ab-av1's choice up to 1440p, the
    /// common case.
    /// </summary>
    public static string For(MediaInfo media)
    {
        if (media is not { PrimaryVideoWidth: { } width, PrimaryVideoHeight: { } height }) return Standard;
        var (encodedWidth, encodedHeight) = IsQuarterTurn(media.PrimaryVideoRotation) ? (height, width) : (width, height);
        return encodedWidth > FourKMinimumWidthExclusive && encodedHeight > FourKMinimumHeightExclusive ? FourK : Standard;
    }

    /// <summary>90, -90, 270 or -270: an odd multiple of 90 degrees, which swaps the picture's width and height.</summary>
    private static bool IsQuarterTurn(int? rotation) => rotation is { } degrees && Math.Abs(degrees) % 180 == 90;
}
