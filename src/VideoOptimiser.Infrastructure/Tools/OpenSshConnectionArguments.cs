using VideoOptimiser.Application.Configuration;

namespace VideoOptimiser.Infrastructure.Tools;

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
            "-o", $"ConnectTimeout={connectTimeoutSeconds}",
            // ConnectTimeout covers only the handshake. Without keepalives, a worker that vanishes mid-session leaves ssh or
            // sftp waiting forever (VOPT-55); with them a dead link exits 255 within about a minute and takes the normal
            // retry. They probe only after 15 s with nothing received, so a long live transfer is never cut off.
            "-o", "ServerAliveInterval=15",
            "-o", "ServerAliveCountMax=4"
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
