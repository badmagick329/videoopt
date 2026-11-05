namespace VideoOptimiser.Application.Tools;

/// <summary>
/// FFmpeg <c>idet</c>'s single-frame verdicts over a stretch of the video: how many frames looked combed with the top or the
/// bottom field first, how many looked progressive, and how many it could not call (flat or black frames).
/// </summary>
public sealed record FieldCounts(int TopFieldFirst, int BottomFieldFirst, int Progressive, int Undetermined);

/// <summary>
/// Looks at the frames themselves for combing. A port of its own beside <see cref="IMediaProbe"/> because it decodes video,
/// which takes seconds, where ffprobe reads only headers; so only the run asks, and only about a video whose stream says it
/// is interlaced (<see cref="VideoPlan"/>).
/// </summary>
public interface IInterlaceDetector
{
    Task<FieldCounts> DetectAsync(string path, double? durationSeconds, CancellationToken cancellationToken = default);
}
