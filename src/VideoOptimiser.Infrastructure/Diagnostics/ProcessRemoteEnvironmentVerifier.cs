using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using VideoOptimiser.Application.Configuration;
using VideoOptimiser.Application.Diagnostics;
using VideoOptimiser.Domain;

namespace VideoOptimiser.Infrastructure.Diagnostics;

public sealed class ProcessRemoteEnvironmentVerifier : IRemoteEnvironmentVerifier
{
    private const string Prefix = "video-optimiser-doctor:";
    private static readonly Version MinimumAbAv1Version = new(0, 10, 4);

    public async Task<IReadOnlyList<Diagnostic>> VerifyAsync(
        string sshPath,
        RemoteSshSettings settings,
        CancellationToken cancellationToken = default)
    {
        var minimumMemoryIsValid = HumanReadableValues.TryParseSize(settings.MinimumAvailableMemory, out var minimumMemoryBytes);
        if (string.IsNullOrWhiteSpace(sshPath) || string.IsNullOrWhiteSpace(settings.Host) || !minimumMemoryIsValid)
        {
            return [Failure("RemoteSshUnavailable", "Remote SSH diagnostics cannot run because their configuration is invalid.")];
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = sshPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var argument in BuildArguments(settings))
        {
            startInfo.ArgumentList.Add(argument);
        }

        try
        {
            using var process = new Process { StartInfo = startInfo };
            if (!process.Start())
            {
                return [Failure("RemoteSshUnavailable", $"Could not start '{sshPath}'.")];
            }

            var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
            try
            {
                await process.WaitForExitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                TryKill(process);
                throw;
            }

            var output = await outputTask;
            var error = (await errorTask).Trim();
            if (process.ExitCode != 0)
            {
                return [Failure("RemoteSshUnavailable", $"SSH connection to '{settings.Host}' failed with exit code {process.ExitCode}: {FirstLine(error)}")];
            }

            return ParseProbeOutput(output, settings, minimumMemoryBytes);
        }
        catch (Win32Exception exception)
        {
            return [Failure("RemoteSshUnavailable", exception.Message)];
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return [Failure("RemoteSshUnavailable", exception.Message)];
        }
    }

    internal static IReadOnlyList<string> BuildArguments(RemoteSshSettings settings)
    {
        var remoteCommand = $"bash -lc {PosixQuote(BuildProbeScript(settings.WorkingDirectory))}";
        return ["-o", "BatchMode=yes", "-o", "ConnectTimeout=10", "--", settings.Host, remoteCommand];
    }

    internal static IReadOnlyList<Diagnostic> ParseProbeOutput(string output, RemoteSshSettings settings, long minimumMemoryBytes)
    {
        var values = output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(line => line.StartsWith(Prefix, StringComparison.Ordinal))
            .Select(line => line[Prefix.Length..].Split('=', 2))
            .Where(parts => parts.Length == 2)
            .GroupBy(parts => parts[0], StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Last()[1], StringComparer.Ordinal);

        var diagnostics = new List<Diagnostic>
        {
            Pass("RemoteSshAvailable", $"SSH connection to '{settings.Host}' succeeded using non-interactive authentication.")
        };

        foreach (var (key, displayName) in RemoteTools)
        {
            var available = values.GetValueOrDefault(key) == "1";
            diagnostics.Add(available
                ? Pass("RemoteDependencyAvailable", $"Remote {displayName} is available.")
                : Failure("RemoteDependencyUnavailable", $"Remote {displayName} is unavailable."));
        }

        AddAbAv1VersionDiagnostic(values, diagnostics);

        foreach (var (key, displayName) in FfmpegFeatures)
        {
            var available = values.GetValueOrDefault(key) == "1";
            diagnostics.Add(available
                ? Pass("RemoteFfmpegFeatureAvailable", $"Remote FFmpeg exposes {displayName}.")
                : Failure("RemoteFfmpegFeatureUnavailable", $"Remote FFmpeg does not expose {displayName}."));
        }

        var av1DecoderAvailable = values.GetValueOrDefault("ffmpeg_libdav1d") == "1";
        diagnostics.Add(av1DecoderAvailable
            ? Pass("RemoteAv1DecoderAvailable", "Remote FFmpeg exposes the required libdav1d software AV1 decoder.")
            : Failure("RemoteAv1DecoderUnavailable", "Remote FFmpeg does not expose the required libdav1d software AV1 decoder; AV1 source decoding and CRF search cannot run remotely."));

        if (TryReadLong(values, "cpu_count", out var cpuCount))
        {
            diagnostics.Add(cpuCount >= settings.MinimumCpuCount
                ? Pass("RemoteCpuSufficient", $"Remote host has {cpuCount} CPUs; minimum is {settings.MinimumCpuCount}.")
                : Failure("RemoteCpuInsufficient", $"Remote host has {cpuCount} CPUs; minimum is {settings.MinimumCpuCount}."));
        }
        else
        {
            diagnostics.Add(Failure("RemoteCpuUnavailable", "Remote CPU count could not be determined."));
        }

        if (TryReadLong(values, "available_memory_bytes", out var availableMemoryBytes))
        {
            diagnostics.Add(availableMemoryBytes >= minimumMemoryBytes
                ? Pass("RemoteMemorySufficient", $"Remote host has {availableMemoryBytes} bytes of available memory; minimum is {minimumMemoryBytes} bytes.")
                : Failure("RemoteMemoryInsufficient", $"Remote host has {availableMemoryBytes} bytes of available memory; minimum is {minimumMemoryBytes} bytes."));
        }
        else
        {
            diagnostics.Add(Failure("RemoteMemoryUnavailable", "Remote available memory could not be determined."));
        }

        diagnostics.Add(values.GetValueOrDefault("workspace_writable") == "1"
            ? new Diagnostic(DiagnosticCategory.FileSystem, DiagnosticStatus.Pass, "RemoteWorkspaceWritable", $"Remote working directory is writable: {settings.WorkingDirectory}")
            : new Diagnostic(DiagnosticCategory.FileSystem, DiagnosticStatus.Fail, "RemoteWorkspaceUnavailable", $"Remote working directory is not writable: {settings.WorkingDirectory}"));

        if (TryReadLong(values, "workspace_free_bytes", out var freeBytes))
        {
            diagnostics.Add(new Diagnostic(
                DiagnosticCategory.FileSystem,
                freeBytes > 0 ? DiagnosticStatus.Pass : DiagnosticStatus.Fail,
                freeBytes > 0 ? "RemoteDiskSpaceAvailable" : "RemoteDiskSpaceUnavailable",
                $"Remote working directory has {freeBytes} bytes free; each job requires {settings.MinimumFreeDiskMultiplier.ToString(CultureInfo.InvariantCulture)}x its source size."));
        }
        else
        {
            diagnostics.Add(new Diagnostic(DiagnosticCategory.FileSystem, DiagnosticStatus.Fail, "RemoteDiskSpaceUnavailable", "Remote working-directory free space could not be determined."));
        }

        return diagnostics;
    }

    private static void AddAbAv1VersionDiagnostic(Dictionary<string, string> values, List<Diagnostic> diagnostics)
    {
        var reportedVersion = values.GetValueOrDefault("ab_av1_version");
        if (!TryParseAbAv1Version(reportedVersion, out var version))
        {
            diagnostics.Add(Failure(
                "RemoteAbAv1VersionUnavailable",
                $"Remote ab-av1 version could not be determined; minimum required version is {MinimumAbAv1Version}."));
            return;
        }

        diagnostics.Add(version >= MinimumAbAv1Version
            ? Pass("RemoteAbAv1VersionSufficient", $"Remote ab-av1 version {version} meets the minimum required version {MinimumAbAv1Version}.")
            : Failure("RemoteAbAv1VersionInsufficient", $"Remote ab-av1 version {version} is too old; minimum required version is {MinimumAbAv1Version}."));
    }

    internal static bool TryParseAbAv1Version(string? value, out Version version)
    {
        version = new Version();
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var normalized = value.Trim();
        if (normalized.StartsWith("v", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized[1..];
        }

        var components = normalized.Split('.', StringSplitOptions.None);
        if (components.Length != 3 || components.Any(component => !int.TryParse(component, NumberStyles.None, CultureInfo.InvariantCulture, out _)))
        {
            return false;
        }

        version = new Version(
            int.Parse(components[0], CultureInfo.InvariantCulture),
            int.Parse(components[1], CultureInfo.InvariantCulture),
            int.Parse(components[2], CultureInfo.InvariantCulture));
        return true;
    }

    private static string BuildProbeScript(string workingDirectory)
    {
        var workspace = PosixQuote(workingDirectory);
        return $$"""
            probe_tool() { if command -v "$1" >/dev/null 2>&1; then printf '{{Prefix}}tool_%s=1\n' "$2"; else printf '{{Prefix}}tool_%s=0\n' "$2"; fi; }
            probe_tool bash bash
            probe_tool nohup nohup
            probe_tool setsid setsid
            probe_tool kill kill
            probe_tool sha256sum sha256sum
            if [ -x /usr/bin/time ]; then printf '{{Prefix}}tool_time=1\n'; else printf '{{Prefix}}tool_time=0\n'; fi
            probe_tool ab-av1 ab_av1
            ab_av1_version=''
            if command -v ab-av1 >/dev/null 2>&1; then
                ab_av1_version_output=$(ab-av1 --version 2>&1)
                ab_av1_status=$?
                if [ "$ab_av1_status" -eq 0 ]; then
                    ab_av1_version=$(printf '%s\n' "$ab_av1_version_output" | awk '{ for (i = 1; i <= NF; i++) if ($i ~ /^[vV]?[0-9]+\.[0-9]+\.[0-9]+$/) { print $i; exit } }')
                fi
            fi
            printf '{{Prefix}}ab_av1_version=%s\n' "$ab_av1_version"
            probe_tool ffmpeg ffmpeg
            probe_tool ffprobe ffprobe
            if command -v ffmpeg >/dev/null 2>&1 && ffmpeg -hide_banner -encoders 2>/dev/null | grep -q '[[:space:]]libsvtav1[[:space:]]'; then printf '{{Prefix}}ffmpeg_libsvtav1=1\n'; else printf '{{Prefix}}ffmpeg_libsvtav1=0\n'; fi
            if command -v ffmpeg >/dev/null 2>&1 && ffmpeg -hide_banner -filters 2>/dev/null | grep -q '[[:space:]]libvmaf[[:space:]]'; then printf '{{Prefix}}ffmpeg_libvmaf=1\n'; else printf '{{Prefix}}ffmpeg_libvmaf=0\n'; fi
            if command -v ffmpeg >/dev/null 2>&1 && ffmpeg -hide_banner -pix_fmts 2>/dev/null | grep -q '[[:space:]]yuv420p10le[[:space:]]'; then printf '{{Prefix}}ffmpeg_yuv420p10le=1\n'; else printf '{{Prefix}}ffmpeg_yuv420p10le=0\n'; fi
            if command -v ffmpeg >/dev/null 2>&1 && ffmpeg -hide_banner -decoders 2>/dev/null | grep -q '[[:space:]]libdav1d[[:space:]]'; then printf '{{Prefix}}ffmpeg_libdav1d=1\n'; else printf '{{Prefix}}ffmpeg_libdav1d=0\n'; fi
            cpu_count=$(getconf _NPROCESSORS_ONLN 2>/dev/null || true)
            available_memory_bytes=$(awk '/^MemAvailable:/ { printf "%.0f", $2 * 1024 }' /proc/meminfo 2>/dev/null || true)
            printf '{{Prefix}}cpu_count=%s\n' "$cpu_count"
            printf '{{Prefix}}available_memory_bytes=%s\n' "$available_memory_bytes"
            workspace={{workspace}}
            probe="$workspace/.video-optimiser-doctor-$$"
            if mkdir -p -- "$workspace" 2>/dev/null && touch -- "$probe" 2>/dev/null && rm -f -- "$probe" 2>/dev/null; then printf '{{Prefix}}workspace_writable=1\n'; else printf '{{Prefix}}workspace_writable=0\n'; fi
            workspace_free_bytes=$(df -Pk -- "$workspace" 2>/dev/null | awk 'NR == 2 { printf "%.0f", $4 * 1024 }')
            printf '{{Prefix}}workspace_free_bytes=%s\n' "$workspace_free_bytes"
            """;
    }

    private static string PosixQuote(string value) => "'" + value.Replace("'", "'\"'\"'", StringComparison.Ordinal) + "'";

    private static bool TryReadLong(Dictionary<string, string> values, string key, out long value)
    {
        value = 0;
        return values.TryGetValue(key, out var text) && long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value);
    }

    private static Diagnostic Pass(string code, string message) => new(DiagnosticCategory.Dependency, DiagnosticStatus.Pass, code, message);

    private static Diagnostic Failure(string code, string message) => new(DiagnosticCategory.Dependency, DiagnosticStatus.Fail, code, message);

    private static string FirstLine(string value)
    {
        var line = value.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
        return string.IsNullOrWhiteSpace(line) ? "No error detail was returned." : line;
    }

    private static void TryKill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
        }
    }

    private static readonly (string Key, string DisplayName)[] RemoteTools =
    [
        ("tool_bash", "bash"),
        ("tool_nohup", "nohup"),
        ("tool_setsid", "setsid"),
        ("tool_kill", "kill"),
        ("tool_sha256sum", "sha256sum"),
        ("tool_time", "/usr/bin/time"),
        ("tool_ab_av1", "ab-av1"),
        ("tool_ffmpeg", "ffmpeg"),
        ("tool_ffprobe", "ffprobe")
    ];

    private static readonly (string Key, string DisplayName)[] FfmpegFeatures =
    [
        ("ffmpeg_libsvtav1", "libsvtav1"),
        ("ffmpeg_libvmaf", "libvmaf"),
        ("ffmpeg_yuv420p10le", "yuv420p10le")
    ];
}
