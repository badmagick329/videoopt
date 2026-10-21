using System.Globalization;

namespace VideoOptimiser.Cli.Features.Tui;

internal static partial class TuiUpdate
{
    private const int MaxQueueInput = 32_767;
    private const string CancellingAddition = "Cancelling queue addition.";

    private static TuiStep AddKey(TuiModel m)
    {
        if (BlockedBy(m, TuiKeys.Queue.Add) is { } blocked) return Told(m, blocked);
        var roots = m.Sources.WatchRoots;
        // The labels say what choosing does; the explanation, from the loaded config, says what it will act on.
        var scan = roots.Count switch
        {
            0 => "No watch folders are set up. Add one to watch.roots in your config.",
            1 => "Scans your watch folder now and queues the videos that fit your rules:",
            _ => $"Scans your {roots.Count} watch folders now and queues the videos that fit your rules:"
        };
        var immich = m.Sources.ImmichConfigured
            ? "Asks how many to queue. Each video is downloaded to check it and kept only if queued."
            : "Immich isn't set up. Set immich.baseUrl in your config to use it.";
        return Asking(m, new TuiChoice("Add videos", "Adding only queues videos. A run converts them.",
        [
            new ChoiceOption(ScanLabel, new AddVideos(QueueAdditionSource.WatchFolders), Explanation: scan, ListLine: string.Join(", ", roots)),
            new ChoiceOption(ImmichLabel, null, new TuiTextInput(QueueAdditionSource.Immich, "1"), Explanation: immich),
            new ChoiceOption(FilesLabel, null, new TuiTextInput(QueueAdditionSource.File, ""), Explanation: "Asks for one or more file paths, one per line, and queues those files.")
        ], 0));
    }

    /// <summary>The chooser's labels; the text dialogs and the refusals name the choice by the same words.</summary>
    internal const string ScanLabel = "Scan your watch folders";
    internal const string ImmichLabel = "Search Immich";
    internal const string FilesLabel = "Add files by path";

    private static TuiStep OnQueueInputKey(TuiModel m, TuiTextInput input, ConsoleKeyInfo key, bool moreBuffered)
    {
        // A line break inside a paste (Explorer's Copy as path puts one between paths) starts the next path; only a typed Enter
        // submits. Conhost can also deliver a line feed as Ctrl+Enter. A pasted CRLF makes two breaks, and blank lines are dropped.
        if (input.Source == QueueAdditionSource.File && (key.KeyChar == '\n' || (key.Key == ConsoleKey.Enter && moreBuffered)))
            return Insert(m, input, "\n");
        switch (key.Key)
        {
            case ConsoleKey.Escape:
                return TuiStep.Of(m with { Dialog = null });
            case ConsoleKey.Backspace:
                var remove = input.Text.Length >= 2 && char.IsLowSurrogate(input.Text[^1]) && char.IsHighSurrogate(input.Text[^2]) ? 2 : Math.Min(1, input.Text.Length);
                return TuiStep.Of(m with { Dialog = input with { Text = input.Text[..^remove], Problem = null } });
            case ConsoleKey.Enter:
                return SubmitQueueInput(m, input);
        }
        // AltGr arrives as Ctrl+Alt on Windows; its printable characters include path separators on some layouts.
        if (char.IsControl(key.KeyChar) || (key.Modifiers.HasFlag(ConsoleModifiers.Control) && !key.Modifiers.HasFlag(ConsoleModifiers.Alt))) return TuiStep.Of(m);
        return Insert(m, input, key.KeyChar.ToString());
    }

    /// <summary>The cap is on the whole entry, line breaks included.</summary>
    private static TuiStep Insert(TuiModel m, TuiTextInput input, string text)
    {
        if (input.Text.Length + text.Length > MaxQueueInput)
            return TuiStep.Of(m with { Dialog = input with { Problem = $"Keep the entry within {MaxQueueInput:N0} characters." } });
        return TuiStep.Of(m with { Dialog = input with { Text = input.Text + text, Problem = null } });
    }

    private static TuiStep SubmitQueueInput(TuiModel m, TuiTextInput input)
    {
        AddVideos effect;
        if (input.Source == QueueAdditionSource.Immich)
        {
            var text = input.Text.Trim();
            int? limit = null;
            if (text.Length > 0)
            {
                if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var count) || count <= 0)
                    return TuiStep.Of(m with { Dialog = input with { Problem = "Enter a positive whole number, or leave blank for all eligible videos." } });
                limit = count;
            }
            effect = new AddVideos(QueueAdditionSource.Immich, Limit: limit);
        }
        else
        {
            var paths = PathsIn(input.Text);
            if (paths.Count == 0) return TuiStep.Of(m with { Dialog = input with { Problem = "Enter a file path." } });
            effect = new AddVideos(QueueAdditionSource.File, paths);
        }
        return Emit(m with { Dialog = null }, effect);
    }

    /// <summary>
    /// The paths in the box, one per line, in the order given, each once. The dialog's hint counts them too, so it says what
    /// Enter would add.
    /// </summary>
    internal static IReadOnlyList<string> PathsIn(string text)
    {
        var paths = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in text.Split(['\n', '\r'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            foreach (var path in QuotedPaths(line) ?? [Unquoted(line)])
                if (!string.IsNullOrWhiteSpace(path) && seen.Add(SamePathKey(path))) paths.Add(path);
        return paths;
    }

    /// <summary>
    /// A line of nothing but quoted paths, such as <c>"C:\Videos\clip-a.mp4" "C:\Videos\clip-b.mp4"</c>, or null for any other line. That is how a
    /// paste reads if a terminal ever delivers its line breaks unbuffered, as typed Enters, so the paths must not run together.
    /// </summary>
    private static List<string>? QuotedPaths(string line)
    {
        var paths = new List<string>();
        for (var at = 0; at < line.Length; at++)
        {
            if (char.IsWhiteSpace(line[at])) continue;
            var close = line[at] == '"' ? line.IndexOf('"', at + 1) : -1;
            if (close < 0) return null;
            paths.Add(line[(at + 1)..close]);
            at = close;
        }
        return paths;
    }

    // Explorer's Copy as path surrounds Windows paths with quotes; those quotes aren't part of the file name. An unquoted path
    // keeps its spaces.
    private static string Unquoted(string line) => line.Length >= 2 && line[0] == '"' && line[^1] == '"' ? line[1..^1] : line;

    /// <summary>
    /// Windows paths ignore case, and a relative path is the same file as its full form. A path GetFullPath refuses is its own
    /// key here; the effect reports it on its own line.
    /// </summary>
    private static string SamePathKey(string path)
    {
        try
        {
            return Path.GetFullPath(path);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return path;
        }
    }

    private static TuiStep CancelAddition(TuiModel m)
    {
        if (m.Busy[AddVideos.Key] == CancellingAddition) return Told(m, CancellingAddition);
        return new TuiStep(Tell(m with { Busy = m.Busy.SetItem(AddVideos.Key, CancellingAddition) }, CancellingAddition), [new CancelQueueAddition()]);
    }

    private static TuiStep OnQueueAdditionDone(TuiModel m, QueueAdditionDoneMsg done)
    {
        var cancelled = m.Busy.TryGetValue(AddVideos.Key, out var working) && working == CancellingAddition;
        var step = OnActionDone(m, new ActionDoneMsg(AddVideos.Key, done.Result));
        if (cancelled || done.ForceCandidates is not { Count: > 0 } candidates || done.Asked is not { Source: QueueAdditionSource.File, Force: false }) return step;
        // One question for the whole batch, and its yes forces only the files the rules refused, never the batch again.
        var forced = new AddVideos(QueueAdditionSource.File, candidates, Force: true);
        var confirm = candidates.Count == 1
            ? new TuiConfirm($"Add {Path.GetFileName(candidates[0])} despite the rules?", $"File: {candidates[0]}\nThe file did not match the eligibility rules. {ForceScope}", forced)
            : new TuiConfirm($"Add {candidates.Count} files despite the rules?", $"Files: {NamedFiles(candidates)}\nThese files did not match the eligibility rules. {ForceScope}", forced);
        var again = candidates.Count == 1 ? "the file" : "the files";
        return step with { Model = OpenAnswer(step.Model, confirm, $"The force question arrived while another was open. Press a and enter {again} again.") };
    }

    private const string ForceScope = "Force bypasses those rules; AV1 and file safety checks still apply.";

    /// <summary>The first few file names, then how many more, so a long batch's question still fits its dialog.</summary>
    private static string NamedFiles(IReadOnlyList<string> paths)
    {
        const int named = 5;
        string[] items = [.. paths.Take(named).Select(path => Path.GetFileName(path)), .. paths.Count > named ? [$"{paths.Count - named} more"] : Array.Empty<string>()];
        return $"{string.Join(", ", items[..^1])} and {items[^1]}";
    }
}
