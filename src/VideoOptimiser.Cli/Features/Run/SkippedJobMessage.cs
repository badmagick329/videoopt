using VideoOptimiser.Application.Configuration;
using VideoOptimiser.Application.Jobs;
using VideoOptimiser.Domain.Jobs;

namespace VideoOptimiser.Cli.Features.Run;

/// <summary>
/// The line for a started video that <c>--local</c> or <c>--remote</c> made a run leave for a later run (VOPT-74). Which command
/// resumes it depends on the config as the file says it, not on the flag, so the line names the mismatch and one command that
/// works. Wording follows the glossary: a worker, and a video left for a later run.
/// </summary>
internal static class SkippedJobMessage
{
    public static string Line(JobRecord job, LoadedConfiguration configuration, CliCommand command)
    {
        var advice = JobCompatibility.AdviseSkipped(job, configuration.Settings, configuration.ConfiguredMode ?? configuration.Settings.Processing.Mode);
        var left = $"{Path.GetFileName(job.SourcePath)} is left for a later run:";
        var jobFlag = (advice.JobMode == ProcessingModes.RemoteSsh ? ProcessingModeOverride.Remote : ProcessingModeOverride.Local).Flag;
        var drop = command.Hint($"queue drop {job.Id:N}");
        return advice.Resume switch
        {
            SkippedJobResume.RunWithoutFlag => $"{left} it started on {advice.JobWhere}. To resume it: {command.Hint("run")}",
            SkippedJobResume.RunWithFlag => $"{left} it started on {advice.JobWhere}, and your config uses {advice.ConfigWhere}. To resume it: {command.Hint($"run {jobFlag}")}",
            SkippedJobResume.ChangeWorkingDirectory => $"{left} it started in another working directory than processing.remoteSsh.workingDirectory. To resume it, set that back, or drop it: {drop}",
            _ => $"{left} it started on {advice.JobWhere}, and your config's worker is {advice.ConfigWhere}. To resume it, set processing.remoteSsh back to the worker it started on, or drop it: {drop}"
        };
    }
}
