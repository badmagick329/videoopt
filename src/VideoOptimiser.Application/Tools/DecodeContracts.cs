namespace VideoOptimiser.Application.Tools;

/// <summary>A span of a video to decode; null bounds decode from the start or to the end.</summary>
public sealed record DecodeWindow(double? StartSeconds, double? DurationSeconds)
{
    public static DecodeWindow Whole { get; } = new(null, null);
}

public sealed record DecodeTestResult(bool Passed, string? Error);

public interface IMediaDecodeTester
{
    Task<DecodeTestResult> DecodeAsync(string path, DecodeWindow window, CancellationToken cancellationToken = default);
}
