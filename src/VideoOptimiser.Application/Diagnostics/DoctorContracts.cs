using VideoOptimiser.Application.Configuration;
using VideoOptimiser.Domain;

namespace VideoOptimiser.Application.Diagnostics;

public sealed record ToolVerificationResult(string Name, bool IsAvailable, string Detail);

public interface IToolVerifier
{
    Task<ToolVerificationResult> VerifyAsync(string name, string executable, string versionArgument, CancellationToken cancellationToken = default);
    Task<ToolVerificationResult> VerifyPresenceAsync(string name, string executable, string availabilityArgument, CancellationToken cancellationToken = default);

    /// <summary>Returns the tool's standard output, or null when it cannot start or exits non-zero.</summary>
    Task<string?> ReadOutputAsync(string executable, IReadOnlyList<string> arguments, CancellationToken cancellationToken = default);
}

public interface IRemoteEnvironmentVerifier
{
    Task<IReadOnlyList<Diagnostic>> VerifyAsync(
        string sshPath,
        RemoteSshSettings settings,
        CancellationToken cancellationToken = default);
}

public interface IDatabaseInitializer
{
    Task InitializeAsync(string databasePath, CancellationToken cancellationToken = default);
}

/// <summary>The database predates this build's schema. A reader that must not write cannot migrate it, so it stops.</summary>
public sealed class DatabaseOutdatedException(string databasePath) : Exception($"{databasePath} needs a migration.")
{
    public string DatabasePath { get; } = databasePath;
}

public sealed record DoctorReport(IReadOnlyList<Diagnostic> Diagnostics)
{
    public bool HasConfigurationFailures => Diagnostics.Any(d => d.Category == DiagnosticCategory.Configuration && d.Status == DiagnosticStatus.Fail);
    public bool HasDependencyFailures => Diagnostics.Any(d => d.Category == DiagnosticCategory.Dependency && d.Status == DiagnosticStatus.Fail);
    public bool HasFailures => Diagnostics.Any(d => d.Status == DiagnosticStatus.Fail);

    public ExitCode ExitCode => HasConfigurationFailures
        ? VideoOptimiser.Domain.ExitCode.InvalidConfiguration
        : HasDependencyFailures
            ? VideoOptimiser.Domain.ExitCode.MissingDependency
            : HasFailures
                ? VideoOptimiser.Domain.ExitCode.GeneralFailure
                : VideoOptimiser.Domain.ExitCode.Success;
}

public interface IDoctorService
{
    Task<DoctorReport> RunAsync(LoadedConfiguration configuration, CancellationToken cancellationToken = default);
}
