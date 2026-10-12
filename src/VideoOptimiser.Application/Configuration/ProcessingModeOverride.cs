namespace VideoOptimiser.Application.Configuration;

/// <summary>
/// <c>--local</c> or <c>--remote</c>: <c>processing.mode</c> for one run, so trying a video on this PC before a paid remote batch
/// needs no YAML edit to make and then undo. It changes only the loaded settings; nothing writes them back to the file.
/// </summary>
public sealed record ProcessingModeOverride(string Flag, string Mode)
{
    public static readonly ProcessingModeOverride Local = new("--local", ProcessingModes.Local);
    public static readonly ProcessingModeOverride Remote = new("--remote", ProcessingModes.RemoteSsh);

    /// <summary>Settings validation runs afterwards, so <c>--remote</c> without a usable <c>remoteSsh</c> block fails as invalid configuration.</summary>
    public void Apply(AppSettings settings) => settings.Processing.Mode = Mode;
}
