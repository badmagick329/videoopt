using System.Globalization;

namespace VideoOptimiser.Cli.Features.Tui;

/// <summary>The size of the frame <c>tui --snapshot</c> prints, as <c>--size WIDTHxHEIGHT</c> gives it.</summary>
internal readonly record struct FrameSize(int Width, int Height)
{
    public static FrameSize Default => new(140, 40);

    // A frame is built in memory and printed whole; this keeps a typo from asking for a million rows.
    private const int Largest = 500;

    public static bool TryParse(string? text, out FrameSize size)
    {
        size = default;
        var parts = text?.Split('x', 'X') ?? [];
        if (parts.Length != 2
            || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var width)
            || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var height)
            || width is < 1 or > Largest
            || height is < 1 or > Largest)
        {
            return false;
        }
        size = new FrameSize(width, height);
        return true;
    }
}
