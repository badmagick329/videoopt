namespace VideoOptimiser.Application.TemporaryOutputs;

/// <summary>
/// Chooses the container of the AV1 output from the original's extension. FFmpeg's <c>mov</c> (.mov) and <c>ipod</c> (.m4v)
/// muxers refuse AV1, so those sources are written as MP4; the other MP4 family member already takes it. Anything else goes
/// to Matroska, which accepts any codec the original can carry, including the SRT and PGS subtitles MP4 cannot hold.
/// </summary>
public static class OutputContainer
{
    public static string ExtensionFor(string sourcePath)
    {
        var extension = Path.GetExtension(sourcePath);
        var mp4 = extension.Equals(".mp4", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".mov", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".m4v", StringComparison.OrdinalIgnoreCase);
        // An all-capitals extension (IMG_0897.MOV from a camera) stays capitals so the pair reads as one name.
        var upper = extension.Length > 1 && extension.All(character => !char.IsLetter(character) || char.IsUpper(character));
        var chosen = mp4 ? ".mp4" : ".mkv";
        return upper ? chosen.ToUpperInvariant() : chosen;
    }
}
