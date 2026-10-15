namespace VideoOptimiser.Application;

/// <summary>
/// How a message names a command the user could run next. The command line is parsed in the CLI, but advice is also worded
/// in the application layer, and both must print the same text: a <c>--config</c> the user typed has to follow every
/// suggested command, or the user runs it against another configuration.
/// </summary>
public interface ICommandHints
{
    /// <summary>The full command for <paramref name="verb"/> (<c>queue drop &lt;id&gt;</c>, <c>run --remote</c>), with <c>--config</c> only when the running command was given one.</summary>
    string Command(string verb);
}
