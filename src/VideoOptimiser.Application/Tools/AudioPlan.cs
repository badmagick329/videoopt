namespace VideoOptimiser.Application.Tools;

/// <summary>An audio stream the encode leaves out. <see cref="Index"/> is audio-relative in the original (the N of <c>0:a:N</c>);
/// <see cref="Label"/> names its codec for the person, from the container's tag when FFmpeg has no name for it.</summary>
public sealed record DroppedAudioTrack(int Index, string Label);

/// <summary>
/// What the encode does with each audio stream of an original: ab-av1 copies audio, so a stream is copied unless it is dropped
/// or re-encoded to FLAC. The single source of truth for both, read by the run (encode arguments, the job's note, the skips) and
/// by validation (the expected audio stream count), so they cannot disagree about which streams the AV1 should have.
/// </summary>
/// <param name="FlacOutputStreams">Output-relative indexes of the PCM streams to re-encode. They differ from the original's
/// indexes once an earlier stream is dropped: <c>-map -0:a:N</c> names an input stream, <c>-c:a:N</c> an output one.</param>
public sealed record AudioPlan(IReadOnlyList<DroppedAudioTrack> Dropped, IReadOnlyList<int> FlacOutputStreams, int SourceAudioStreamCount)
{
    public static AudioPlan For(MediaInfo media)
    {
        var streams = media.AudioTracks ?? [];
        var dropped = new List<DroppedAudioTrack>();
        var flac = new List<int>();
        var output = 0;
        for (var index = 0; index < streams.Count; index++)
        {
            if (IsUnsupported(streams[index]))
            {
                dropped.Add(new DroppedAudioTrack(index, Label(streams[index])));
                continue;
            }

            if (streams[index].Codec.StartsWith("pcm_", StringComparison.OrdinalIgnoreCase)) flac.Add(output);
            output++;
        }

        return new AudioPlan(dropped, flac, media.AudioStreamCount);
    }

    public int KeptAudioStreamCount => SourceAudioStreamCount - Dropped.Count;

    /// <summary>The dropped tracks as people count them, from 1: <c>audio track 2 (apac)</c>, <c>audio tracks 2 and 3 (apac)</c>.</summary>
    public string DroppedTracks
    {
        get
        {
            var numbers = Dropped.Select(stream => (stream.Index + 1).ToString(System.Globalization.CultureInfo.InvariantCulture)).ToArray();
            var labels = string.Join(", ", Dropped.Select(stream => stream.Label).Distinct(StringComparer.OrdinalIgnoreCase));
            return numbers.Length == 1
                ? $"audio track {numbers[0]} ({labels})"
                : $"audio tracks {string.Join(", ", numbers[..^1])} and {numbers[^1]} ({labels})";
        }
    }

    /// <summary>The AV1 would have no audio although the original has some. Such a video is skipped, never encoded: validation
    /// would expect no audio and pass it, and finalise would put a silent video in place of the original.</summary>
    public bool LeavesNoAudio => Dropped.Count > 0 && KeptAudioStreamCount == 0;

    /// <summary>
    /// A stream FFmpeg cannot identify cannot be copied: the muxer has no tag to write for it and refuses the whole output.
    /// iPhone 16 spatial audio (APAC) is the known case: FFmpeg 8.x gives it no name, 9.x names it apple_apac, and no release
    /// decodes it (VOPT-125). It travels beside a stereo AAC mix of the same recording, which is kept.
    /// </summary>
    private static bool IsUnsupported(AudioTrack stream) =>
        stream.Codec is "unknown" or "none" || stream.Codec.Equals("apple_apac", StringComparison.OrdinalIgnoreCase);

    // Matroska reports an unknown stream's tag as bytes such as [0][0][0][0], which names nothing.
    private static string Label(AudioTrack stream) =>
        stream.Tag is { Length: > 0 } tag && tag.All(char.IsAsciiLetterOrDigit) ? tag : stream.Codec;
}
