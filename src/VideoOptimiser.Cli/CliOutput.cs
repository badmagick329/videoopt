using VideoOptimiser.Application.Configuration;
using VideoOptimiser.Application.Features.Worker;
using VideoOptimiser.Application.Immich;
using VideoOptimiser.Application.Tools;
using VideoOptimiser.Cli;
using VideoOptimiser.Domain;
using VideoOptimiser.Domain.Jobs;

/// <summary>What every command prints with: the console styles, errors, and how a failure that ended a command reads.</summary>
internal static class CliOutput
{
    // Chosen once from the console before the command runs, because it depends on how this process was started
    // (terminal or redirected, per stream) and on --json. The writers take them as arguments so tests can pick their own.
    // Async-local like CliConsole, since --json differs from run to run and parallel runs must not overwrite each other's.
    private static readonly AsyncLocal<TerminalStyle?> OutputStyleValue = new();
    private static readonly AsyncLocal<TerminalStyle?> ErrorStyleValue = new();

    internal static TerminalStyle OutputStyle
    {
        get => OutputStyleValue.Value ?? TerminalStyle.Plain;
        set => OutputStyleValue.Value = value;
    }

    internal static TerminalStyle ErrorStyle
    {
        get => ErrorStyleValue.Value ?? TerminalStyle.Plain;
        set => ErrorStyleValue.Value = value;
    }

    /// <summary>An error or a refusal, in red on a terminal. Usage text and other help are written as they are.</summary>
    internal static void WriteError(string? text) => CliConsole.Error.WriteLine(ErrorStyle.Red(text ?? string.Empty));

    /// <summary>Prints each problem the settings have, as an error, and says whether there were any.</summary>
    internal static bool ReportInvalidSettings(ISettingsValidator validator, LoadedConfiguration configuration)
    {
        var diagnostics = validator.Validate(configuration.Settings);
        foreach (var diagnostic in diagnostics) WriteError($"[{diagnostic.Code}] {diagnostic.Message}");
        return diagnostics.Count > 0;
    }

    /// <summary>Whether Immich is configured; says what to set first when it is not. <c>immich run</c> and <c>immich finalize</c> both start here.</summary>
    internal static bool RequireImmich(LoadedConfiguration configuration, CliCommand command)
    {
        if (configuration.Settings.Immich.IsConfigured) return true;
        WriteError($"Set immich.baseUrl in the configuration first; '{command.Hint("doctor")}' then checks the connection and the API key.");
        return false;
    }

    /// <summary>The exit code for an Immich failure: the configuration when the key or its permissions are wrong, otherwise a processing failure.</summary>
    internal static int ImmichFailureExitCode(ImmichApiException exception) =>
        exception.Code is "ImmichApiKeyMissing" or "ImmichApiKeyRejected" or "ImmichPermissionMissing" or ImmichTrash.DisabledCode
            ? (int)ExitCode.InvalidConfiguration
            : (int)ExitCode.ProcessingFailure;

    /// <summary>Says what an error that ended a command means, and returns the exit code for it.</summary>
    internal static int ReportFailure(Exception exception)
    {
        switch (exception)
        {
            case FileNotFoundException:
                WriteError(exception.Message);
                return (int)ExitCode.InvalidConfiguration;
            case InvalidDataException:
                WriteError($"Configuration could not be read: {exception.Message}");
                return (int)ExitCode.InvalidConfiguration;
            case MediaProbeException:
                WriteError(exception.Message);
                return (int)ExitCode.ProcessingFailure;
            case ImmichApiException immich:
                WriteError(immich.Message);
                return ImmichFailureExitCode(immich);
            case JobChangedException:
                // A queue verb or a run from another terminal changed the job first; its change stands.
                WriteError(exception.Message);
                return (int)ExitCode.GeneralFailure;
            case RemoteWorkerCreationDeclinedException:
                // A declined price is the user's choice, as with a declined finalize, not a failure. Jobs stay as they were.
                CliConsole.WriteLine(exception.Message);
                return (int)ExitCode.Success;
            case RemoteWorkerCapacityUnavailableException:
                WriteError(exception.Message);
                WriteError("Check server type and location availability in Hetzner Console: https://console.hetzner.com/");
                return (int)ExitCode.ProcessingFailure;
            case RemoteWorkerCreationUnconfirmedException or RemoteWorkerBootstrapFailedException:
                WriteError(exception.Message);
                return (int)ExitCode.ProcessingFailure;
            default:
                WriteError($"Unexpected failure: {exception.GetBaseException().Message}");
                return (int)ExitCode.GeneralFailure;
        }
    }
}

internal sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
{
    public void Report(T value) => report(value);
}
