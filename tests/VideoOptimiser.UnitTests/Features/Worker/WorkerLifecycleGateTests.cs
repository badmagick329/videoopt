using FluentAssertions;

namespace VideoOptimiser.UnitTests.Features.Worker;

/// <summary>
/// The worker commands decide on <c>processing.remoteSsh.lifecycle</c> alone (VOPT-56): with <c>mode: local</c> a billing
/// server must still be deletable. Runs the real CLI against a temporary config; no test reaches Hetzner.
/// </summary>
public sealed class WorkerLifecycleGateTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"VideoOptimiser.Tests.{Guid.NewGuid():N}");

    public WorkerLifecycleGateTests() => Directory.CreateDirectory(_directory);

    [Theory]
    [InlineData("create", "\"manual\"", "it is \"manual\".", false, 3)]
    [InlineData("delete", "\"manual\"", "it is \"manual\".", false, 3)]
    [InlineData("status", "", "it is not set.", true, 0)]
    public async Task AWorkerCommandWithoutTheHetznerLifecycleNamesTheSettingAndItsValue(string verb, string lifecycle, string clause, bool stdout, int exitCode)
    {
        var config = await WriteConfigAsync("remoteSsh", lifecycle);

        var (code, output, error) = await ConsoleCapture.RunCliAsync("worker", verb, "--config", config);

        code.Should().Be(exitCode);
        (stdout ? output : error).Trim().Should().Be($"'worker {verb}' needs processing.remoteSsh.lifecycle set to \"hetzner\" in {config}; {clause}");
        (stdout ? error : output).Should().BeEmpty();
    }

    /// <summary>
    /// The missing token is the first thing the real lifecycle checks, so its error proves the command got past the gate. The
    /// mode override sits before the per-verb dispatch, so <c>delete</c>, the verb that must still stop billing, stands for all.
    /// </summary>
    [Fact]
    public async Task WithModeLocalAndTheHetznerLifecycleTheWorkerCommandReachesTheLifecycle()
    {
        var config = await WriteConfigAsync("local", "\"hetzner\"");

        var (_, _, error) = await ConsoleCapture.RunCliAsync("worker", "delete", "--config", config);

        error.Should().Contain("Hetzner API token 'VO_TEST_NO_SUCH_TOKEN' was not found").And.NotContain("needs processing.remoteSsh.lifecycle");
    }

    private async Task<string> WriteConfigAsync(string mode, string lifecycle)
    {
        var config = Path.Combine(_directory, "config.yaml");
        await File.WriteAllTextAsync(config, string.Join('\n',
            "database:",
            $"  path: '{Path.Combine(_directory, "jobs.db")}'",
            "processing:",
            $"  mode: \"{mode}\"",
            "  remoteSsh:",
            $"    lifecycle: {lifecycle}",
            "    hetzner:",
            "      apiTokenEnvironmentVariable: \"VO_TEST_NO_SUCH_TOKEN\"",
            $"      apiTokenFile: '{Path.Combine(_directory, "missing.env")}'",
            ""));
        return config;
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);
}
