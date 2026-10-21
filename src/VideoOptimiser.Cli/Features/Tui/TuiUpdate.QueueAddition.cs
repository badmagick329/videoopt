using System.Globalization;

namespace VideoOptimiser.Cli.Features.Tui;

internal static partial class TuiUpdate
{
    private const int MaxQueueInput = 32_767;
    private const string CancellingAddition = "Cancelling queue addition.";

    private static TuiStep AddKey(TuiModel m)
    {
        if (m.Busy.TryGetValue(AddVideos.Key, out var working)) return Told(m, working);
        return Asking(m, new TuiChoice("Add videos", "Choose where to look. Videos join the queue; a run can keep going.",
        [
            new ChoiceOption("Watch folders", new AddVideos(QueueAdditionSource.WatchFolders)),
            new ChoiceOption("Immich", null, new TuiTextInput(QueueAdditionSource.Immich, "1")),
            new ChoiceOption("File path", null, new TuiTextInput(QueueAdditionSource.File, ""))
        ], 0));
    }

    private static TuiStep OnQueueInputKey(TuiModel m, TuiTextInput input, ConsoleKeyInfo key)
    {
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
        if (input.Text.Length >= MaxQueueInput)
            return TuiStep.Of(m with { Dialog = input with { Problem = $"Keep the entry within {MaxQueueInput:N0} characters." } });
        return TuiStep.Of(m with { Dialog = input with { Text = input.Text + key.KeyChar, Problem = null } });
    }

    private static TuiStep SubmitQueueInput(TuiModel m, TuiTextInput input)
    {
        var text = input.Text.Trim();
        AddVideos effect;
        if (input.Source == QueueAdditionSource.Immich)
        {
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
            // Explorer's Copy as path surrounds Windows paths with quotes; those quotes aren't part of the file name.
            if (text.Length >= 2 && text[0] == '"' && text[^1] == '"') text = text[1..^1];
            if (string.IsNullOrWhiteSpace(text)) return TuiStep.Of(m with { Dialog = input with { Problem = "Enter a file path." } });
            effect = new AddVideos(QueueAdditionSource.File, Path: text);
        }
        return Emit(m with { Dialog = null }, effect);
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
        if (cancelled || !done.OfferForce || done.Asked is not { Source: QueueAdditionSource.File, Force: false }) return step;
        var confirm = new TuiConfirm($"Add {Path.GetFileName(done.Asked.Path)} despite the rules?",
            $"File: {done.Asked.Path}\nThe file did not match the eligibility rules. Force bypasses those rules; AV1 and file safety checks still apply.",
            done.Asked with { Force = true });
        return step with { Model = OpenAnswer(step.Model, confirm, "The force question arrived while another was open. Press a and enter the file again.") };
    }
}
