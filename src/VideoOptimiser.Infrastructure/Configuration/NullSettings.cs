using VideoOptimiser.Application.Configuration;

namespace VideoOptimiser.Infrastructure.Configuration;

/// <summary>
/// Decides, setting by setting, what a YAML null (<c>~</c>, <c>null</c>, or nothing after the colon) means, so none gets past
/// the loader (VOPT-164). The deserialiser writes a null over a property's default, and the validator and everything after it
/// read settings as never null. A null text setting or list item is an error naming its path, except where noted; a null list
/// becomes empty, and the validator decides whether empty is allowed. Sections are filled in before this runs.
/// A null number or switch never gets here: <see cref="YamlErrorDescriber"/> names it while deserialising.
/// A new text or list setting needs its line here; <c>ParseUnresolvedLetsNoNullThrough</c> fails until it has one.
/// </summary>
internal static class NullSettings
{
    public static string Message(string path) => $"{path} can't be empty.";

    /// <exception cref="InvalidDataException">One or more settings were null; the message names each one.</exception>
    public static void Resolve(AppSettings settings)
    {
        var empty = new List<string>();

        var tools = settings.Tools;
        Require(tools.AbAv1Path, "tools.abAv1Path");
        Require(tools.FfmpegPath, "tools.ffmpegPath");
        Require(tools.FfprobePath, "tools.ffprobePath");
        Require(tools.SshPath, "tools.sshPath");
        Require(tools.SftpPath, "tools.sftpPath");

        Require(settings.Processing.Mode, "processing.mode");
        var remote = settings.Processing.RemoteSsh;
        // Optional, and empty means unset: config init writes "" for each, and the validator says when the mode needs one.
        remote.Host ??= string.Empty;
        remote.User ??= string.Empty;
        remote.IdentityFile ??= string.Empty;
        remote.KnownHostsFile ??= string.Empty;
        Require(remote.Lifecycle, "processing.remoteSsh.lifecycle");
        Require(remote.WorkingDirectory, "processing.remoteSsh.workingDirectory");
        Require(remote.MinimumAvailableMemory, "processing.remoteSsh.minimumAvailableMemory");
        Require(remote.StageSilenceTimeout, "processing.remoteSsh.stageSilenceTimeout");

        var hetzner = remote.Hetzner;
        Require(hetzner.ApiTokenEnvironmentVariable, "processing.remoteSsh.hetzner.apiTokenEnvironmentVariable");
        hetzner.ApiTokenFile ??= string.Empty;
        // An explicit empty value must fail validation rather than quietly become the default list.
        hetzner.ServerTypes = Items(hetzner.ServerTypes, "processing.remoteSsh.hetzner.serverTypes");
        // Unlike serverTypes, a null list has always meant the default locations; kept so such a config still loads.
        hetzner.Locations = Items(hetzner.Locations ?? ["hel1", "fsn1", "nbg1"], "processing.remoteSsh.hetzner.locations");
        Require(hetzner.Image, "processing.remoteSsh.hetzner.image");
        hetzner.SshKeyName ??= string.Empty;
        Require(hetzner.ServerNamePrefix, "processing.remoteSsh.hetzner.serverNamePrefix");
        Require(hetzner.BootstrapScriptPath, "processing.remoteSsh.hetzner.bootstrapScriptPath");
        Require(hetzner.BootstrapTimeout, "processing.remoteSsh.hetzner.bootstrapTimeout");

        Require(settings.Database.Path, "database.path");

        // Empty is valid: an Immich-only setup has no folders, and the validator asks for one otherwise.
        settings.Watch.Roots ??= [];
        for (var index = 0; index < settings.Watch.Roots.Count; index++)
        {
            var path = $"watch.roots[{index}]";
            if (settings.Watch.Roots[index] is not { } root) empty.Add(path);
            else Require(root.Path, $"{path}.path");
        }

        var immich = settings.Immich;
        // Empty baseUrl turns Immich off. The other three already loaded as empty before VOPT-164, and the validator checks them once Immich is on.
        immich.BaseUrl ??= string.Empty;
        immich.ApiKeyEnvironmentVariable ??= string.Empty;
        immich.ApiKeyFile ??= string.Empty;
        immich.StagingDirectory ??= string.Empty;

        var eligibility = settings.Eligibility;
        // Kept empty, as before VOPT-164: no folder video is then eligible, which is what an empty list says.
        eligibility.Extensions = Items(eligibility.Extensions, "eligibility.extensions");
        // Empty fails validation with its own message.
        eligibility.Rules ??= [];
        for (var index = 0; index < eligibility.Rules.Count; index++)
        {
            var path = $"eligibility.rules[{index}]";
            if (eligibility.Rules[index] is not { } rule)
            {
                empty.Add(path);
                continue;
            }

            // Empty fails validation with its own message.
            rule.Codecs = Items(rule.Codecs, $"{path}.codecs");
            Require(rule.Resolution, $"{path}.resolution");
            Require(rule.MinimumVideoBitrate, $"{path}.minimumVideoBitrate");
            Require(rule.MinimumFileSize, $"{path}.minimumFileSize");
        }
        // Empty is valid for the three exclusion lists: nothing is excluded.
        eligibility.ExcludedExtensions = Items(eligibility.ExcludedExtensions, "eligibility.excludedExtensions");
        eligibility.ExcludedNamePatterns = Items(eligibility.ExcludedNamePatterns, "eligibility.excludedNamePatterns");
        eligibility.ExcludedDirectories = Items(eligibility.ExcludedDirectories, "eligibility.excludedDirectories");

        var quality = settings.Quality;
        Require(quality.Encoder, "quality.encoder");
        Require(quality.PixelFormat, "quality.pixelFormat");
        Require(quality.UnsupportedAudio, "quality.unsupportedAudio");
        Require(quality.CrfSearch.SampleDuration, "quality.crfSearch.sampleDuration");

        Require(settings.Savings.MinimumBytesSaved, "savings.minimumBytesSaved");

        // Both already loaded as empty before VOPT-164: the validator rejects an empty action, and an empty archiveDirectory is unset.
        settings.Original.Action ??= string.Empty;
        settings.Original.ArchiveDirectory ??= string.Empty;

        Require(settings.Validation.DecodeTest, "validation.decodeTest");

        if (empty.Count > 0) throw new InvalidDataException(string.Join(" ", empty.Select(Message)));

        void Require(string? value, string path)
        {
            if (value is null) empty.Add(path);
        }

        List<string> Items(List<string>? list, string path)
        {
            list ??= [];
            for (var index = 0; index < list.Count; index++)
            {
                Require(list[index], $"{path}[{index}]");
            }

            return list;
        }
    }
}
