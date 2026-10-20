using VideoOptimiser.Application.Configuration;

namespace VideoOptimiser.Infrastructure.Configuration;

public sealed class ProcessEnvironmentVariables : IEnvironmentVariables
{
    public static readonly ProcessEnvironmentVariables Instance = new();

    public string? Read(string name) => Environment.GetEnvironmentVariable(name);
}
