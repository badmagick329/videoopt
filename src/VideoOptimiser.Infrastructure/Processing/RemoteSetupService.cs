using System.ComponentModel;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using VideoOptimiser.Application.Configuration;
using VideoOptimiser.Application.Processing;
using VideoOptimiser.Domain;
using VideoOptimiser.Infrastructure.Configuration;

namespace VideoOptimiser.Infrastructure.Processing;

public sealed class RemoteSetupService : IRemoteSetupService, IDisposable
{
    private const string DefaultKeyName = "video-optimiser-hetzner";
    private const string DefaultKeyFile = "video-optimiser-hetzner-ed25519";
    private readonly HetznerSshKeyClient _hetzner;
    private readonly IExternalProcessRunner _processes;
    private readonly IConfigurationLoader _loader;
    private readonly ISettingsValidator _validator;
    private readonly Func<string> _homeDirectory;

    public RemoteSetupService(IExternalProcessRunner processes, IConfigurationLoader loader, ISettingsValidator validator)
        : this(new HetznerSshKeyClient(), processes, loader, validator, () => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile))
    {
    }

    internal RemoteSetupService(
        HetznerSshKeyClient hetzner,
        IExternalProcessRunner processes,
        IConfigurationLoader loader,
        ISettingsValidator validator,
        Func<string> homeDirectory)
    {
        _hetzner = hetzner;
        _processes = processes;
        _loader = loader;
        _validator = validator;
        _homeDirectory = homeDirectory;
    }

    public async Task<ExitCode> RunAsync(
        LoadedConfiguration configuration,
        Func<string, CancellationToken, Task<bool>> confirm,
        IProgress<string> output,
        CancellationToken cancellationToken)
    {
        var settings = configuration.Settings;
        var remote = settings.Processing.RemoteSsh;
        var hetzner = remote.Hetzner;

        if (remote.Lifecycle.Equals(RemoteLifecycleModes.Manual, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(remote.Host))
        {
            output.Report("remote setup configures managed Hetzner workers only. For your own SSH host, see \"Manual SSH lifecycle\" in docs/remote-ssh.md.");
            return ExitCode.InvalidConfiguration;
        }

        // Step 1: the token, proved by the first real call.
        if (string.IsNullOrWhiteSpace(hetzner.ApiTokenEnvironmentVariable))
        {
            output.Report("processing.remoteSsh.hetzner.apiTokenEnvironmentVariable is empty. Set it to a variable name, such as VIDEO_OPTIMISER_HETZNER_TOKEN.");
            return ExitCode.InvalidConfiguration;
        }
        var token = SecretLoader.Load(hetzner.ApiTokenEnvironmentVariable, hetzner.ApiTokenFile);
        if (token is null)
        {
            ReportMissingToken(hetzner, output);
            return ExitCode.InvalidConfiguration;
        }

        IReadOnlyList<HetznerSshKey> listed;
        try
        {
            listed = await _hetzner.ListAsync(token, cancellationToken);
        }
        catch (HetznerSshKeyException exception) when (exception.StatusCode == HttpStatusCode.Unauthorized)
        {
            output.Report($"Hetzner rejected the API token ({TokenSource(hetzner)}). Create a new Read & Write token.");
            return ExitCode.InvalidConfiguration;
        }
        catch (Exception exception) when (IsHetznerFailure(exception, cancellationToken))
        {
            output.Report($"Could not list SSH keys on Hetzner: {exception.Message}");
            return ExitCode.MissingDependency;
        }
        output.Report("Hetzner API token accepted.");

        // Step 2: the key pair.
        var rawProcessing = YamlConfigurationLoader.ParseUnresolved(configuration.RawYaml ?? throw new ArgumentException("The configuration has no source text.", nameof(configuration))).Processing;
        string keyPath;
        string identityValue;
        var created = false;
        if (!string.IsNullOrWhiteSpace(remote.IdentityFile) && File.Exists(remote.IdentityFile))
        {
            keyPath = remote.IdentityFile;
            identityValue = rawProcessing.RemoteSsh.IdentityFile;
        }
        else
        {
            if (!string.IsNullOrWhiteSpace(remote.IdentityFile))
                output.Report($"processing.remoteSsh.identityFile {remote.IdentityFile} does not exist; using the default key instead.");
            var home = _homeDirectory();
            if (string.IsNullOrWhiteSpace(home))
            {
                output.Report("Could not find your home directory. Set processing.remoteSsh.identityFile to an existing SSH key.");
                return ExitCode.InvalidConfiguration;
            }
            keyPath = Path.Combine(home, ".ssh", DefaultKeyFile);
            identityValue = keyPath;
            if (!File.Exists(keyPath))
            {
                // Never overwrite: ssh-keygen would replace a lone .pub without asking.
                if (File.Exists(keyPath + ".pub"))
                {
                    output.Report($"{keyPath}.pub exists without its private key {keyPath}. Move it away or set processing.remoteSsh.identityFile to a key you have.");
                    return ExitCode.InvalidConfiguration;
                }
                var failure = await GenerateKeyAsync(settings.Tools.SshPath, keyPath, output, cancellationToken);
                if (failure is not null) return failure.Value;
                created = true;
            }
        }

        var publicKeyPath = keyPath + ".pub";
        if (!File.Exists(publicKeyPath))
        {
            output.Report($"The public key {publicKeyPath} is missing. Set processing.remoteSsh.identityFile to a key that has a .pub file beside it.");
            return ExitCode.InvalidConfiguration;
        }
        output.Report(created ? $"Created SSH key {keyPath}." : $"Using SSH key {keyPath}.");

        // Step 3: the key in the Hetzner project.
        if (!TryParsePublicKey(await File.ReadAllTextAsync(publicKeyPath, cancellationToken), out var publicKey, out var fingerprint))
        {
            output.Report($"{publicKeyPath} is not an OpenSSH public key: expected \"<type> <base64> [comment]\".");
            return ExitCode.InvalidConfiguration;
        }
        var existing = listed.FirstOrDefault(key => string.Equals(key.Fingerprint, fingerprint, StringComparison.OrdinalIgnoreCase));
        string keyName;
        if (existing is not null)
        {
            keyName = existing.Name;
            output.Report($"Hetzner already has this key as \"{keyName}\".");
        }
        else
        {
            keyName = FirstFreeName(listed);
            try
            {
                await _hetzner.CreateAsync(token, keyName, publicKey, cancellationToken);
            }
            catch (HetznerSshKeyException exception) when (exception.StatusCode == HttpStatusCode.Forbidden)
            {
                output.Report($"The Hetzner API token is read-only. Create a Read & Write token and replace it in {TokenSource(hetzner)}.");
                return ExitCode.InvalidConfiguration;
            }
            catch (HetznerSshKeyException exception) when (exception.StatusCode == HttpStatusCode.Unauthorized)
            {
                output.Report($"Hetzner rejected the API token ({TokenSource(hetzner)}). Create a new Read & Write token.");
                return ExitCode.InvalidConfiguration;
            }
            catch (Exception exception) when (IsHetznerFailure(exception, cancellationToken))
            {
                output.Report($"Could not upload the SSH key to Hetzner: {exception.Message}");
                return ExitCode.MissingDependency;
            }
            output.Report($"Uploaded the key to Hetzner as \"{keyName}\".");
        }

        // Step 4: the config. Built from the unresolved values so the user's relative paths survive.
        rawProcessing.Mode = ProcessingModes.RemoteSsh;
        rawProcessing.RemoteSsh.Lifecycle = RemoteLifecycleModes.Hetzner;
        rawProcessing.RemoteSsh.IdentityFile = identityValue;
        rawProcessing.RemoteSsh.Hetzner.SshKeyName = keyName;
        var block = ProcessingBlock.Render(rawProcessing);
        var path = configuration.Path;
        var backupPath = path + ".bak";
        output.Report(block.TrimEnd('\n'));
        if (!await confirm($"Write this to {path}? A backup goes to {backupPath}. [y/N] ", cancellationToken))
        {
            output.Report($"Config not changed. To use it, paste the block above over the processing: section of {path}.");
            return ExitCode.Success;
        }

        var before = _validator.Validate(settings).Select(Key).ToHashSet(StringComparer.Ordinal);
        File.Copy(path, backupPath, overwrite: true);
        var hasBom = (await File.ReadAllBytesAsync(path, cancellationToken)).AsSpan().StartsWith(Encoding.UTF8.Preamble);
        var edited = ProcessingBlock.Replace(await File.ReadAllTextAsync(path, cancellationToken), block);
        await File.WriteAllTextAsync(path, edited, new UTF8Encoding(hasBom), cancellationToken);

        // The reload is the real check: it sees the file as every other command will.
        var problems = new List<string>();
        LoadedConfiguration? reloaded = null;
        try
        {
            reloaded = await _loader.LoadAsync(path, cancellationToken);
            problems.AddRange(_validator.Validate(reloaded.Settings).Where(diagnostic => !before.Contains(Key(diagnostic))).Select(diagnostic => $"[{diagnostic.Code}] {diagnostic.Message}"));
        }
        catch (InvalidDataException exception)
        {
            problems.Add(exception.Message);
        }
        if (problems.Count > 0)
        {
            File.Copy(backupPath, path, overwrite: true);
            foreach (var problem in problems) output.Report(problem);
            output.Report($"Restored {path} from {backupPath}.");
            return ExitCode.InvalidConfiguration;
        }
        output.Report($"Updated {path}; the previous version is in {backupPath}.");

        // Step 5: the token, key and bootstrap script were all checked above, so nothing is left to verify.
        output.Report(existing is null
            ? "Remote setup is complete. Nothing was created on Hetzner except the SSH key."
            : "Remote setup is complete. Nothing was created on Hetzner.");
        output.Report(reloaded!.Settings.Immich.IsConfigured
            ? $"Next: video-optimiser immich run --first --remote --config \"{path}\""
            : $"Next: video-optimiser run --remote --config \"{path}\"");
        return ExitCode.Success;
    }

    private async Task<ExitCode?> GenerateKeyAsync(string sshPath, string keyPath, IProgress<string> output, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(keyPath)!);
        // The empty passphrase is deliberate: OpenSshConnectionArguments runs ssh with BatchMode=yes, which cannot prompt for one.
        var request = new ExternalProcessRequest(SshKeygenPath(sshPath), ["-t", "ed25519", "-a", "100", "-N", "", "-C", DefaultKeyName, "-f", keyPath]);
        ExternalProcessResult result;
        try
        {
            result = await _processes.RunAsync(request, cancellationToken);
        }
        catch (Win32Exception exception)
        {
            output.Report($"Could not run {request.FileName}: {exception.Message}");
            return ExitCode.MissingDependency;
        }
        if (result.ExitCode == 0) return null;
        var detail = string.IsNullOrWhiteSpace(result.StandardError) ? result.StandardOutput : result.StandardError;
        output.Report($"ssh-keygen failed with exit code {result.ExitCode}: {detail.Trim()}");
        return ExitCode.MissingDependency;
    }

    // ssh-keygen ships beside ssh, so a configured ssh path in a custom folder implies the same folder for it.
    internal static string SshKeygenPath(string sshPath)
    {
        if (string.IsNullOrWhiteSpace(sshPath) || (!sshPath.Contains(Path.DirectorySeparatorChar) && !sshPath.Contains(Path.AltDirectorySeparatorChar))) return "ssh-keygen";
        return Path.Combine(Path.GetDirectoryName(sshPath)!, "ssh-keygen" + Path.GetExtension(sshPath));
    }

    private static string FirstFreeName(IReadOnlyList<HetznerSshKey> keys)
    {
        var taken = keys.Select(key => key.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var name = DefaultKeyName;
        for (var suffix = 2; taken.Contains(name); suffix++) name = $"{DefaultKeyName}-{suffix}";
        return name;
    }

    // Hetzner's fingerprint is the MD5 of the decoded key blob, as lowercase hex pairs joined by colons.
#pragma warning disable CA5351 // MD5 is the format Hetzner reports; it is not used for security here.
    internal static bool TryParsePublicKey(string text, out string publicKey, out string fingerprint)
    {
        publicKey = fingerprint = string.Empty;
        var line = text.Split('\n').Select(candidate => candidate.Trim()).FirstOrDefault(candidate => candidate.Length > 0 && candidate[0] != '#');
        var fields = line?.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
        if (fields is not { Length: >= 2 }) return false;
        var blob = new byte[fields[1].Length];
        if (!Convert.TryFromBase64String(fields[1], blob, out var length) || length == 0) return false;
        publicKey = $"{fields[0]} {fields[1]}";
        fingerprint = string.Join(':', MD5.HashData(blob.AsSpan(0, length)).Select(value => value.ToString("x2", CultureInfo.InvariantCulture)));
        return true;
    }
#pragma warning restore CA5351

    private static string Key(Diagnostic diagnostic) => $"{diagnostic.Code}\n{diagnostic.Message}";

    // A cancelled token is the user's Ctrl+C; any other cancellation is HttpClient's own timeout.
    private static bool IsHetznerFailure(Exception exception, CancellationToken cancellationToken) =>
        exception is HetznerSshKeyException or HttpRequestException or JsonException ||
        (exception is OperationCanceledException && !cancellationToken.IsCancellationRequested);

    private static string TokenSource(HetznerSettings hetzner) =>
        string.IsNullOrWhiteSpace(hetzner.ApiTokenFile) ? hetzner.ApiTokenEnvironmentVariable : $"{hetzner.ApiTokenEnvironmentVariable} or {hetzner.ApiTokenFile}";

    private static void ReportMissingToken(HetznerSettings hetzner, IProgress<string> output)
    {
        output.Report("No Hetzner API token was found.");
        output.Report("Create one in Hetzner Console: open your project, then Security > API Tokens > Generate API token, with permission Read & Write.");
        if (string.IsNullOrWhiteSpace(hetzner.ApiTokenFile))
        {
            output.Report($"Then set the environment variable {hetzner.ApiTokenEnvironmentVariable} to the token.");
            return;
        }
        output.Report($"Then add this line to {hetzner.ApiTokenFile}:");
        output.Report($"{hetzner.ApiTokenEnvironmentVariable}=<your token>");
    }

    public void Dispose() => _hetzner.Dispose();
}
