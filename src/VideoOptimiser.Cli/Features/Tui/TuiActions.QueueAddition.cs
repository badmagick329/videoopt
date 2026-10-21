using VideoOptimiser.Application.Configuration;
using VideoOptimiser.Application.Eligibility;
using VideoOptimiser.Application.Features.ImmichRun;
using VideoOptimiser.Application.Features.Queue;
using VideoOptimiser.Application.Immich;
using VideoOptimiser.Cli.Features.Queue;
using VideoOptimiser.Cli.Features.Run;
using VideoOptimiser.Domain;
using VideoOptimiser.Domain.Jobs;

namespace VideoOptimiser.Cli.Features.Tui;

internal sealed partial class TuiActions
{
    /// <summary>
    /// Uses the CLI's discovery services, without taking the run lock: a running conversion can pick up new jobs. Progress
    /// goes straight to Activity so a later error or cancellation cannot hide work the re-upload guard already did.
    /// </summary>
    public async Task<QueueAdditionDoneMsg> AddVideosAsync(AddVideos effect, AppSettings settings, Action<LogKind, string> log, CancellationToken token)
    {
        try
        {
            token.ThrowIfCancellationRequested();
            return effect.Source switch
            {
                QueueAdditionSource.WatchFolders => await AddWatchFoldersAsync(effect, settings, log, token),
                QueueAdditionSource.Immich => await AddImmichAsync(effect, settings, log, token),
                QueueAdditionSource.File => await AddFilesAsync(effect, settings, log, token),
                _ => throw new InvalidOperationException($"Unknown queue addition source: {effect.Source}.")
            };
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            return new QueueAdditionDoneMsg(effect, Done(LogKind.Warning, "Stopped adding videos. Videos already queued stay queued."));
        }
        catch (Exception exception)
        {
            // An external timeout has not cancelled our token: report it as an error and still free the addition's busy key.
            return new QueueAdditionDoneMsg(effect, Done(LogKind.Error, $"Couldn't add videos: {OneLine(exception)}"));
        }
    }

    private async Task<QueueAdditionDoneMsg> AddWatchFoldersAsync(AddVideos effect, AppSettings settings, Action<LogKind, string> log, CancellationToken token)
    {
        if (discovery.Refusal(settings) is QueueDiscoveryRefusal.NoWatchRoots)
            return new(effect, Done(LogKind.Warning, "Adding videos needs at least one watch.roots entry. For Immich videos, choose Immich."));

        log(LogKind.Info, $"Scanning {OutputWords.Count(settings.Watch.Roots.Count, "watch root", "watch roots")} for videos to convert; files not seen before are probed with ffprobe.");
        using var output = new AdditionLogWriter(log);
        var result = await discovery.DiscoverAsync(settings.Database.Path, settings, first: false, new QueueDiscoverConsole(output), token);
        foreach (var folder in result.QueuedPaths.GroupBy(path => Path.GetDirectoryName(path) ?? string.Empty, StringComparer.OrdinalIgnoreCase))
        {
            log(LogKind.Success, $"Queued in {folder.Key}:");
            foreach (var path in folder) log(LogKind.Detail, Path.GetFileName(path));
        }
        return new(effect, AdditionSummary(result, settings));
    }

    private async Task<QueueAdditionDoneMsg> AddImmichAsync(AddVideos effect, AppSettings settings, Action<LogKind, string> log, CancellationToken token)
    {
        if (effect.Limit is <= 0) return new(effect, Done(LogKind.Warning, "The Immich limit must be a positive whole number."));
        if (!settings.Immich.IsConfigured) return new(effect, Done(LogKind.Warning, "Set immich.baseUrl before adding videos from Immich."));
        var invalid = validator.Validate(settings).Where(diagnostic => diagnostic.Status == DiagnosticStatus.Fail && diagnostic.Code.Contains("Immich", StringComparison.Ordinal)).ToArray();
        if (invalid.Length > 0) return new(effect, new ActionResult([.. invalid.Select(diagnostic => new LogLine(LogKind.Error, diagnostic.Message))], invalid[0].Message));

        string? candidateName = null;
        var result = await immich.AddToQueueAsync(settings.Database.Path, settings, effect.Limit,
            new AdditionProgress<ScanProgress>(update => log(update.Stage == "Issue" ? LogKind.Error : LogKind.Info, ReuploadGuardReport.FormatProgress(update))),
            new AdditionProgress<ImmichProgress>(update =>
            {
                if (update is ImmichDiscoveryProgress.CandidateDownloading candidate) candidateName = candidate.FileName;
                AdditionImmichProgress(update, candidateName, log);
            }), token);
        if (ReuploadGuardReport.Summary(result.Guard) is { } guard) log(result.Guard.Issues.Count > 0 ? LogKind.Warning : LogKind.Info, guard);
        foreach (var line in ImmichDiscoveryConsole.Summary(result.Discovery, effect.Limit).Where(line => line.Length > 0)) log(LogKind.Info, line);
        var found = result.Discovery;
        var summary = AdditionSummary(new QueueDiscoveryResult([.. found.Queued.Select(job => job.SourcePath)], found.AlreadyQueued,
            found.Issues + result.Guard.Issues.Count, AlreadyConverted: found.AlreadyConverted, PreviouslyFailed: found.PreviouslyFailed,
            PreviouslySkipped: found.PreviouslySkipped, Held: found.Held, ReadyToFinalize: found.ReadyToFinalize, PreviouslyRejected: found.PreviouslyRejected), settings);
        if (found.Excluded > 0)
        {
            var notice = $"{summary.Notice} Left alone: {found.Excluded}.";
            summary = summary with { Lines = [new(LogKind.Warning, notice), .. summary.Lines.Skip(1)], Notice = notice };
        }
        return new(effect, summary);
    }

    /// <summary>
    /// One path keeps the single-file result: its line, where the run takes it and how many videos are ahead of it. Several are
    /// added in order, each line logged as its file finishes, so a cancellation or a failure later in the batch cannot hide
    /// what is already queued; a tally ends the batch, and the token is checked between files.
    /// </summary>
    private async Task<QueueAdditionDoneMsg> AddFilesAsync(AddVideos effect, AppSettings settings, Action<LogKind, string> log, CancellationToken token)
    {
        var paths = effect.Paths ?? [];
        if (paths.Count == 0 || paths.All(string.IsNullOrWhiteSpace)) return new(effect, Done(LogKind.Warning, "Enter a file path."));
        if (paths.Count == 1)
        {
            var one = await AddFileAsync(paths[0], effect.Force, alone: true, settings, token);
            if (one.Queued is not { } job) return new(effect, Done(one.Kind, one.Text), one.ForceCandidate is { } path ? [path] : null);
            var ahead = await discovery.AheadOfAsync(settings.Database.Path, job, token);
            var lines = new List<LogLine> { new(LogKind.Success, one.Text), new(LogKind.Info, AdditionNextRun(settings)) };
            if (ahead > 0) lines.Add(new(LogKind.Info, $"{(ahead == 1 ? "1 video is" : $"{ahead} videos are")} ahead of it; select it in Queue and press n to put it first."));
            return new(effect, new ActionResult(lines, one.Text));
        }

        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        var candidates = new List<string>();
        var refused = false;
        foreach (var path in paths)
        {
            token.ThrowIfCancellationRequested();
            var added = await AddFileAsync(path, effect.Force, alone: false, settings, token);
            log(added.Kind, added.Text);
            counts[added.Tally] = counts.GetValueOrDefault(added.Tally) + 1;
            if (added.ForceCandidate is { } candidate) candidates.Add(candidate);
            refused |= added.Kind is LogKind.Warning or LogKind.Error;
        }
        var summary = string.Join(' ', BatchTallies.Where(label => label == QueuedTally || counts.ContainsKey(label)).Select(label => $"{label}: {counts.GetValueOrDefault(label)}."));
        var result = new List<LogLine> { new(refused ? LogKind.Warning : LogKind.Success, summary) };
        if (counts.ContainsKey(QueuedTally)) result.Add(new(LogKind.Info, AdditionNextRun(settings)));
        return new(effect, new ActionResult(result, summary), candidates);
    }

    private const string QueuedTally = "Queued";

    /// <summary>A batch's tally, in this order: the watch-folder scan's labels where a file's outcome has one, then the refusals.</summary>
    private static readonly string[] BatchTallies =
    [
        QueuedTally, "Already queued", "Held", "Ready to finalise", "Already converted", "Previously failed or cancelled",
        "Previously skipped", "Previously rejected", "Doesn't fit your rules", "Can't be queued"
    ];

    /// <param name="Tally">Its label in <see cref="BatchTallies"/>.</param>
    /// <param name="ForceCandidate">The full path when only the watch roots or the rules refused it, which force may lift.</param>
    /// <param name="Queued">The new job, when it was queued.</param>
    private sealed record FileAddition(string Text, LogKind Kind, string Tally, string? ForceCandidate = null, JobRecord? Queued = null);

    private async Task<FileAddition> AddFileAsync(string entered, bool force, bool alone, AppSettings settings, CancellationToken token)
    {
        string path;
        try
        {
            path = Path.GetFullPath(entered);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            // In a batch the line must say which path it was; the rest of the batch still goes.
            return new(alone ? $"The file path is invalid: {OneLine(exception)}" : $"The file path {entered} is invalid: {OneLine(exception)}", LogKind.Warning, "Can't be queued");
        }
        var result = await discovery.AddFileAsync(settings.Database.Path, settings, path, force, token);
        var (name, job) = (Path.GetFileName(result.Path), result.Job);
        var text = result.Outcome switch
        {
            QueueAddOutcome.Queued => $"Queued {name} as job {job!.Id:N}{(result.OutsideRules ? ", outside your rules (--force)" : string.Empty)}.",
            QueueAddOutcome.Missing => $"Original does not exist: {result.Path}",
            QueueAddOutcome.MadeByTool => $"{result.Path} is a file video-optimiser made, so it can't be queued.",
            QueueAddOutcome.Archived => $"{result.Path} is an original video-optimiser archived, so it can't be queued.",
            QueueAddOutcome.AlreadyAv1 => $"{name} is already AV1, so it can't be queued.",
            QueueAddOutcome.OpenJob when result.OpenState == OpenJobState.LastFinaliseFailed => $"{name}'s last finalise failed (job {job!.Id:N}): {job.FailureMessage}",
            QueueAddOutcome.OpenJob => $"{name} already has job {job!.Id:N} ({QueueWords.OpenStatusPhrase(job, result.OpenState!.Value)}).",
            QueueAddOutcome.Settled => SettledAddition(name, job!),
            QueueAddOutcome.OutsideRoots => $"{result.Path} is outside your watch roots.",
            QueueAddOutcome.Ineligible or QueueAddOutcome.TooSmall => $"{name} doesn't fit your rules: {QueueWords.Fragment(result.Reason!)}.",
            _ => $"Can't queue {name}: {QueueWords.Fragment(result.Reason!)}."
        };
        if (result.Outcome == QueueAddOutcome.Queued) return new(text, LogKind.Success, QueuedTally, Queued: job);
        var kind = result.Outcome switch
        {
            QueueAddOutcome.OpenJob or QueueAddOutcome.Settled => LogKind.Info,
            QueueAddOutcome.Missing or QueueAddOutcome.Unreadable or QueueAddOutcome.ProbeFailed => LogKind.Error,
            _ => LogKind.Warning
        };
        var tally = result.Outcome switch
        {
            QueueAddOutcome.OpenJob => result.OpenState switch
            {
                OpenJobState.Held => "Held",
                OpenJobState.ReadyToFinalize or OpenJobState.LastFinaliseFailed => "Ready to finalise",
                _ => "Already queued"
            },
            QueueAddOutcome.Settled => job!.Status switch
            {
                JobStatus.Completed => "Already converted",
                JobStatus.Skipped => "Previously skipped",
                JobStatus.Rejected => "Previously rejected",
                _ => "Previously failed or cancelled"
            },
            QueueAddOutcome.OutsideRoots or QueueAddOutcome.Ineligible or QueueAddOutcome.TooSmall => "Doesn't fit your rules",
            _ => "Can't be queued"
        };
        // Only the watch roots and the rules are the user's to lift; the size cap and every protection stay.
        var forceable = result.Outcome is QueueAddOutcome.OutsideRoots or QueueAddOutcome.Ineligible;
        return new(text, kind, tally, forceable ? result.Path : null);
    }

    private static string SettledAddition(string name, JobRecord job)
    {
        var text = job.Status switch
        {
            JobStatus.Completed => $"{name} was already converted (job {job.Id:N}).",
            JobStatus.Cancelled => $"{name} was dropped before (job {job.Id:N}).",
            JobStatus.Skipped when QueueWords.SkippedForAudio(job) => $"{name} was skipped before for its audio (job {job.Id:N}).",
            JobStatus.Skipped => $"{name} was not worth converting before (job {job.Id:N}).",
            JobStatus.Rejected => $"{name} was rejected before (job {job.Id:N}).",
            _ => $"{name} failed before (job {job.Id:N})."
        };
        return job.Status == JobStatus.Completed ? text : $"{text} To try it again: video-optimiser queue retry {job.Id:N}";
    }

    private ActionResult AdditionSummary(QueueDiscoveryResult result, AppSettings settings)
    {
        var counts = new List<string> { $"Queued: {result.QueuedPaths.Count}." };
        void Add(int count, string label) { if (count > 0) counts.Add($"{label}: {count}."); }
        Add(result.AlreadyQueued, "Already queued");
        Add(result.Held, "Held");
        Add(result.ReadyToFinalize, "Ready to finalise");
        Add(result.AlreadyConverted, "Already converted");
        Add(result.PreviouslyFailed, "Previously failed or cancelled");
        Add(result.PreviouslySkipped, "Previously skipped");
        Add(result.PreviouslyRejected, "Previously rejected");
        Add(result.Issues, "Issues");
        var summary = string.Join(' ', counts);
        var lines = new List<LogLine> { new(result.Issues > 0 ? LogKind.Warning : LogKind.Success, summary) };
        if (result.PreviouslyFailed > 0) lines.Add(new(LogKind.Info, "Failed or cancelled originals stay out of the queue until they change. Select one in Failed and press t to try it again."));
        if (result.PreviouslySkipped > 0) lines.Add(new(LogKind.Info, "Skipped originals (AV1 would not save enough) are checked again when the file or the quality settings change; queue retry queues one now."));
        if (result.PreviouslyRejected > 0) lines.Add(new(LogKind.Info, "Rejected originals stay out of the queue until they change; queue retry queues one again."));
        if (result.QueuedPaths.Count + result.AlreadyQueued > 0) lines.Add(new(LogKind.Info, AdditionNextRun(settings)));
        if (result.ReadyToFinalize > 0) lines.Add(new(LogKind.Info, "Videos are ready to finalise. Watch them in Review, then press f to finalise one or F for all ready videos without warnings."));
        return new ActionResult(lines, summary);
    }

    private string AdditionNextRun(AppSettings settings) => discovery.NextRun(settings.Database.Path) switch
    {
        NextRun.NoRun => "Next: press s to start a run.",
        NextRun.RunTakesThem => "The run going now picks them up.",
        _ => "Next: press s once the run going now ends."
    };

    private static void AdditionImmichProgress(ImmichProgress update, string? candidateName, Action<LogKind, string> log)
    {
        switch (update)
        {
            case ImmichDiscoveryProgress.Started started:
                log(LogKind.Info, started.Limit is { } limit ? $"Looking in Immich for up to {OutputWords.Count(limit, "new video", "new videos")} to convert." : "Looking in Immich for every video that fits your rules.");
                if (started.StaleSkips > 0) log(LogKind.Info, $"{OutputWords.Count(started.StaleSkips, "video that didn't", "videos that didn't")} fit your earlier rules {(started.StaleSkips == 1 ? "is" : "are")} checked again under the current ones when the search reaches {(started.StaleSkips == 1 ? "it" : "them")}.");
                break;
            case ImmichDiscoveryProgress.CandidateDownloading candidate:
                log(LogKind.Info, $"Downloading and checking: {candidate.FileName} ({candidate.Taken:yyyy-MM-dd}). If it doesn't fit your rules, the download is deleted.");
                break;
            case ImmichDiscoveryProgress.CandidateJudged judged:
                var facts = judged.Facts is { } measured ? $"{measured.Codec} {ImmichDiscoveryConsole.Resolution(measured.Width, measured.Height)} {ImmichDiscoveryConsole.Bitrate(measured.VideoBitsPerSecond)} {HumanReadableValues.FormatSize(measured.SizeBytes)}; " : string.Empty;
                var verdict = judged.Outcome switch
                {
                    CandidateOutcome.Queued => "queued",
                    CandidateOutcome.Issue => $"issue: {judged.Detail}; {judged.NextStep}",
                    _ => $"doesn't fit: {judged.Detail}"
                };
                log(judged.Outcome == CandidateOutcome.Queued ? LogKind.Success : judged.Outcome == CandidateOutcome.Issue ? LogKind.Error : LogKind.Warning, $"{candidateName}: {facts}{verdict}.");
                break;
            case ImmichDiscoveryProgress.AssetExcluded excluded:
                log(LogKind.Warning, $"Left alone: {excluded.FileName} (Immich's search should not have returned it: it {excluded.Reason}).");
                break;
            case ImmichProgress.AssetIssue issue:
                log(LogKind.Error, $"Issue: {issue.FileName}: {issue.Message}.");
                break;
        }
    }

    /// <summary>Direct callbacks preserve progress ordering; Progress&lt;T&gt; would post later through a captured context.</summary>
    private sealed class AdditionProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }

    /// <summary>The scan's existing throttled, grouped progress writes into Activity without touching global console state.</summary>
    private sealed class AdditionLogWriter(Action<LogKind, string> log) : TextWriter
    {
        public override System.Text.Encoding Encoding => System.Text.Encoding.UTF8;
        public override void WriteLine(string? value)
        {
            if (!string.IsNullOrWhiteSpace(value)) log(value.Contains("Issue:", StringComparison.Ordinal) ? LogKind.Warning : LogKind.Info, RunEventWords.Clean(value));
        }
    }
}
