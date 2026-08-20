using VideoOptimiser.Application.Configuration;

namespace VideoOptimiser.Infrastructure.Processing;

internal static class OpenSshConnectionArguments
{
    public static List<string> Build(
        RemoteSshSettings settings,
        bool acceptNewHostKey = false,
        int connectTimeoutSeconds = 15,
        string? userOverride = null)
    {
        var arguments = new List<string>
        {
            "-o", "BatchMode=yes",
            "-o", $"ConnectTimeout={connectTimeoutSeconds}"
        };
        var user = userOverride ?? settings.User;
        if (!string.IsNullOrWhiteSpace(user))
        {
            arguments.Add("-o");
            arguments.Add($"User={user}");
        }
        if (!string.IsNullOrWhiteSpace(settings.IdentityFile))
        {
            arguments.Add("-i");
            arguments.Add(settings.IdentityFile);
            arguments.Add("-o");
            arguments.Add("IdentitiesOnly=yes");
        }
        if (!string.IsNullOrWhiteSpace(settings.KnownHostsFile))
        {
            arguments.Add("-o");
            arguments.Add($"UserKnownHostsFile={settings.KnownHostsFile}");
            arguments.Add("-o");
            arguments.Add($"StrictHostKeyChecking={(acceptNewHostKey ? "accept-new" : "yes")}");
        }
        return arguments;
    }
}
