using VideoOptimiser.Application.Configuration;
using VideoOptimiser.Application.TemporaryOutputs;

namespace VideoOptimiser.Application.Features.Validate;

public interface IOutputValidationService
{
    Task<ValidationReport> ValidateAsync(OutputManifest manifest, AppSettings settings, CancellationToken cancellationToken = default);
}
