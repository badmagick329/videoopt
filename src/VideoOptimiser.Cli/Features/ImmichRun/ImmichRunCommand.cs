using VideoOptimiser.Application.Configuration;
using VideoOptimiser.Application.Eligibility;
using VideoOptimiser.Application.Features.ImmichRun;
using VideoOptimiser.Application.Features.Queue;
using VideoOptimiser.Application.Jobs;
using VideoOptimiser.Cli.Features.Run;
using VideoOptimiser.Domain;
using static CliOutput;
using static VideoOptimiser.Cli.Features.Queue.QueueWords;

namespace VideoOptimiser.Cli.Features.ImmichRun;

/// <summary>
/// <c>queue add immich</c> and <c>immich run</c>. Each round runs by hand: <c>immich run</c> queues new videos from Immich and
/// converts the whole queue as <c>run</c> does, the user reviews, <c>immich finalize</c> or <c>finalize --ready</c> replaces
/// the originals. Changed wording goes through the humanizer gate (docs index).
/// </summary>
internal sealed class ImmichRunCommand(ISettingsValidator settingsValidator, IImmichRunService immich, IQueueDiscovery queueDiscovery, RunConversion conversion, RunWorkerEnd workerEnd)
{
    public Task<int> RunAsync(LoadedConfiguration configuration, CliCommand command, CancellationToken cancellationToken) =>
        workerEnd.GuardAsync(configuration, command, () => ExecuteAsync(configuration, command, cancellationToken));

    /// <summary><c>queue add immich</c>: the discovery <c>immich run</c> starts with, then what to run next.</summary>
    public async Task<int> AddAsync(LoadedConfiguration configuration, CliCommand command, CancellationToken cancellationToken)
    {
        if (!RequireImmich(configuration, command) || ReportInvalidSettings(settingsValidator, configuration)) return (int)ExitCode.InvalidConfiguration;
        var (guard, discovery) = await QueueNewVideosAsync(configuration, command, command.Limit, cancellationToken);
        if (discovery.Queued.Count + discovery.AlreadyQueued > 0) Console.WriteLine(NextRunLine(queueDiscovery.NextRun(configuration.Settings.Database.Path), command));
        if (discovery.ReadyToFinalize > 0) Console.WriteLine(NextSteps.ReadyToFinaliseLine(discovery.ReadyToFinalize, command));
        return (int)(NeedsLook(guard, discovery) ? ExitCode.PartialSuccess : ExitCode.Success);
    }

    private async Task<int> ExecuteAsync(LoadedConfiguration configuration, CliCommand command, CancellationToken cancellationToken)
    {
        if (!RequireImmich(configuration, command) || ReportInvalidSettings(settingsValidator, configuration)) return (int)ExitCode.InvalidConfiguration;
        // Before discovery, so a second run is refused before it downloads anything.
        using var runLock = workerEnd.TakeRunLock(configuration, LockHolders.ImmichRun);
        if (runLock is null) return (int)ExitCode.GeneralFailure;
        // Discovery downloads originals, so the banner comes first.
        await conversion.PrintBannerAsync(configuration, command.ModeOverride, cancellationToken);
        var (guard, discovery) = await QueueNewVideosAsync(configuration, command, command.Limit, cancellationToken);

        ExitCode exitCode;
        try
        {
            exitCode = await conversion.ConvertQueueAsync(configuration, command, () => PrintGuardSummary(guard), cancellationToken);
        }
        catch
        {
            // Ctrl+C or an error during hours of encoding: the guard's lines have long scrolled away.
            PrintGuardSummary(guard);
            throw;
        }

        // Guard hits are the guard working; its issues, discovery issues and exclusions each need the user to look.
        return (int)(exitCode == ExitCode.Success && NeedsLook(guard, discovery) ? ExitCode.PartialSuccess : exitCode);
    }

    private static bool NeedsLook(ImmichReuploadGuardResult guard, ImmichDiscoveryResult discovery) =>
        guard.Issues.Count > 0 || discovery.Issues > 0 || discovery.Excluded > 0;

    /// <summary>The guard, then discovery, with their lines, ending with the counts of the queue as <c>queue add</c> prints them.</summary>
    private async Task<(ImmichReuploadGuardResult Guard, ImmichDiscoveryResult Discovery)> QueueNewVideosAsync(LoadedConfiguration configuration, CliCommand command, int? limit, CancellationToken cancellationToken)
    {
        var settings = configuration.Settings;
        // Guard reports are printed as they arrive and kept: when a later error ends the run, the next run sees the trashed
        // re-uploads as trashed and stays silent, so this run is the only one that can tell the user to delete them.
        var guardReports = new List<ScanProgress>();
        var guardProgress = new InlineProgress<ScanProgress>(update =>
        {
            guardReports.Add(update);
            Console.WriteLine(ReuploadGuardReport.FormatProgress(update));
        });
        var discoveryProgress = new ImmichDiscoveryConsole(Console.Out, OutputStyle, Console.Error, ErrorStyle);

        ImmichQueueAddition added;
        try
        {
            added = await immich.AddToQueueAsync(settings.Database.Path, settings, limit, guardProgress, discoveryProgress, cancellationToken);
        }
        catch
        {
            // A download that Ctrl+C or an error cut short leaves its line open; the message that follows needs its own.
            discoveryProgress.FinishLine();
            if (guardReports.Count == 0) throw;
            Console.WriteLine();
            Console.WriteLine("Before the run ended, the re-upload guard reported:");
            foreach (var report in guardReports) Console.WriteLine(ReuploadGuardReport.FormatProgress(report));
            throw;
        }

        var (guard, discovery) = (added.Guard, added.Discovery);
        if (ReuploadGuardReport.Summary(guard) is { } guardLine) Console.WriteLine(guardLine);
        foreach (var line in ImmichDiscoveryConsole.Summary(discovery, limit)) Console.WriteLine(line);
        if (discovery.Excluded > 0) WriteError($"Immich's search returned {OutputWords.Count(discovery.Excluded, "video", "videos")} it should have left out. They were left alone, but this Immich version's search may behave differently: check before running again.");
        QueueDiscoverConsole.WriteCounts(Console.Out, new QueueDiscoveryResult([.. discovery.Queued.Select(job => job.SourcePath)], discovery.AlreadyQueued, Issues: 0, Held: discovery.Held, ReadyToFinalize: discovery.ReadyToFinalize), command);
        return (guard, discovery);
    }

    private static void PrintGuardSummary(ImmichReuploadGuardResult guard)
    {
        if (guard.Trashed.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("The phone uploaded these converted originals again. They are back in Immich's trash; delete them from the phone:");
            foreach (var hit in guard.Trashed) Console.WriteLine($"  {hit.OriginalFileName}  taken {hit.TakenLocal:yyyy-MM-dd HH:mm}");
        }

        if (guard.Restored.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("These converted originals are out of Immich's trash, so both versions exist. The guard left them alone:");
            foreach (var hit in guard.Restored) Console.WriteLine($"  {hit.OriginalFileName}  taken {hit.TakenLocal:yyyy-MM-dd HH:mm}");
        }

        if (guard.Issues.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("Re-upload guard issues:");
            foreach (var issue in guard.Issues) Console.WriteLine($"  {issue.Reason}");
        }
    }
}
