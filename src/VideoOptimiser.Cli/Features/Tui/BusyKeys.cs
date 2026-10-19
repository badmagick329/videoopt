namespace VideoOptimiser.Cli.Features.Tui;

/// <summary>
/// The <see cref="TuiEffect.BusyKey"/>s. Effects hold them and <see cref="TuiUpdate"/> and <see cref="TuiView"/> read them back,
/// so every one is a name here: a key mistyped on one side would be held and never freed, and quitting would ask forever.
/// </summary>
internal static class BusyKeys
{
    /// <summary>Listing the servers, creating or deleting the worker: one at a time, since each changes or reads the same worker.</summary>
    public const string Worker = "worker";

    public const string Run = "run";
    public const string Stop = "stop";
    public const string Fate = "fate";
    public const string Dismiss = "dismiss";

    /// <summary>Preparing a finalise and the batch itself, so a second finalise waits for the first.</summary>
    public const string Finalise = "finalise";

    /// <summary>A queue verb on one job: verbs on different jobs may overlap.</summary>
    public static string Queue(Guid id) => $"queue:{id:N}";
}
