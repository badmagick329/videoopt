using VideoOptimiser.Application.Configuration;

namespace VideoOptimiser.Application.Immich;

public sealed record ImmichServerVersion(int Major, int Minor, int Patch)
{
    public override string ToString() => $"{Major}.{Minor}.{Patch}";
}

public sealed record ImmichUser(string Id, string Name);

public interface IImmichClient
{
    Task<ImmichServerVersion> GetServerVersionAsync(CancellationToken cancellationToken = default);
    Task<ImmichUser> GetCurrentUserAsync(CancellationToken cancellationToken = default);
}

public interface IImmichClientFactory
{
    IImmichClient Create(ImmichSettings settings);
}

/// <summary>
/// An Immich request that failed, with a message that says what to fix. <see cref="Code"/> is the diagnostic code doctor
/// reports. The message names the key's environment variable, never the key.
/// </summary>
public sealed class ImmichApiException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}
