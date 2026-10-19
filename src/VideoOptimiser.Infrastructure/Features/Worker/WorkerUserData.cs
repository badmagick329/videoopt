using System.Text;

namespace VideoOptimiser.Infrastructure.Features.Worker;

/// <summary>
/// The cloud-init user data a new worker is created with: a cloud-config part that installs its SSH host key, then the
/// bootstrap script, as a MIME multipart message, which is how cloud-init takes both at once.
/// </summary>
/// <remarks>
/// The key goes in cloud-config's <c>ssh_keys</c> rather than being installed by the script with an sshd restart. cloud-init
/// writes <c>ssh_keys</c> in its init stage, which systemd runs before sshd first listens, so the worker never serves any
/// other key and the first readiness probe can check strictly. The script runs only in cloud-init's final stage, minutes
/// after sshd is up, so a key it installed would leave a window in which sshd served the image's own generated key.
/// <para>
/// The script part is base64, so none of its lines can read as a MIME boundary and any non-ASCII byte in a custom script
/// survives cloud-init's email parser unchanged. Every line ends in LF.
/// </para>
/// </remarks>
internal static class WorkerUserData
{
    /// <summary>Hetzner's limit on a server's user data.</summary>
    internal const int MaxBytes = 32 * 1024;

    // Has '-', which base64 never contains, and the PEM's dashed lines sit indented, so no line of the parts matches it.
    private const string Boundary = "video-optimiser-worker-user-data";
    private const int Base64LineLength = 76;

    public static string Build(string script, WorkerHostKey hostKey)
    {
        var text = new StringBuilder();
        text.Append("Content-Type: multipart/mixed; boundary=\"" + Boundary + "\"\n");
        text.Append("MIME-Version: 1.0\n");
        text.Append('\n');

        text.Append("--" + Boundary + "\n");
        text.Append("Content-Type: text/cloud-config; charset=\"us-ascii\"\n");
        text.Append("MIME-Version: 1.0\n");
        text.Append("Content-Transfer-Encoding: 7bit\n");
        text.Append("Content-Disposition: attachment; filename=\"host-key.cfg\"\n");
        text.Append('\n');
        text.Append("#cloud-config\n");
        text.Append("ssh_keys:\n");
        // A literal block scalar keeps the PEM's lines as they are; each sits four spaces in, under its key.
        text.Append("  ecdsa_private: |\n");
        foreach (var line in hostKey.PrivateKeyPem.ReplaceLineEndings("\n").TrimEnd('\n').Split('\n'))
            text.Append("    ").Append(line).Append('\n');
        // The algorithm name and base64 hold no quote or backslash, so double quotes need no escaping.
        text.Append("  ecdsa_public: \"").Append(hostKey.PublicKey).Append("\"\n");

        text.Append("--" + Boundary + "\n");
        text.Append("Content-Type: text/x-shellscript; charset=\"utf-8\"\n");
        text.Append("MIME-Version: 1.0\n");
        text.Append("Content-Transfer-Encoding: base64\n");
        text.Append("Content-Disposition: attachment; filename=\"bootstrap-remote-worker.sh\"\n");
        text.Append('\n');
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(script));
        for (var start = 0; start < encoded.Length; start += Base64LineLength)
            text.Append(encoded, start, Math.Min(Base64LineLength, encoded.Length - start)).Append('\n');

        text.Append("--" + Boundary + "--\n");
        return text.ToString();
    }
}
