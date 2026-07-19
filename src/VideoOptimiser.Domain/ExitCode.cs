namespace VideoOptimiser.Domain;

public enum ExitCode
{
    Success = 0,
    GeneralFailure = 1,
    InvalidArguments = 2,
    InvalidConfiguration = 3,
    MissingDependency = 4,
    NoEligibleFiles = 5,
    ProcessingFailure = 6,
    ValidationFailure = 7,
    FinalisationFailure = 8,
    Cancelled = 9,
    PartialSuccess = 10
}
