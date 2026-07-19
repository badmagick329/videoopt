namespace VideoOptimiser.Domain;

public enum DiagnosticCategory
{
    Configuration,
    FileSystem,
    Database,
    Dependency
}

public enum DiagnosticStatus
{
    Pass,
    Warning,
    Fail
}

public sealed record Diagnostic(
    DiagnosticCategory Category,
    DiagnosticStatus Status,
    string Code,
    string Message);
