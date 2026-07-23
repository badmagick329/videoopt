using YamlDotNet.Serialization;

namespace VideoOptimiser.Application.Configuration;

[YamlStaticContext]
[YamlSerializable(typeof(AppSettings))]
public partial class VideoOptimiserYamlContext : StaticContext
{
}
