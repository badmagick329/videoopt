using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using VideoOptimiser.Application.Configuration;
using VideoOptimiser.Application.Features.Worker;
using VideoOptimiser.Cli;
using VideoOptimiser.Infrastructure.Configuration;
using VideoOptimiser.Infrastructure.Features.Worker;
using VideoOptimiser.Infrastructure.Persistence;
using VideoOptimiser.Infrastructure.Tools;

namespace VideoOptimiser.UnitTests.Features.Worker;

public sealed class HetznerRemoteWorkerLifecycleTests
{
    [Theory]
    [InlineData("OTHER=value\nVIDEO_OPTIMISER_HETZNER_TOKEN=old\nexport VIDEO_OPTIMISER_HETZNER_TOKEN=\"last\"\n")]
    [InlineData("# comment\nexport VIDEO_OPTIMISER_HETZNER_TOKEN='last'\nOTHER=value\n")]
    public void DotEnvReaderStripsExportAndQuotesFromTheLastAssignmentOfTheKey(string content)
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, content);

            SecretLoader.ReadDotEnvValue(path, "VIDEO_OPTIMISER_HETZNER_TOKEN").Should().Be("last");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task EnsureLoadsTokenFromDotEnvWhenEnvironmentVariableIsAbsent()
    {
        using var fixture = new Fixture();
        fixture.UseDotEnvToken();
        using var lifecycle = fixture.CreateLifecycle();

        var worker = await lifecycle.EnsureReadyAsync(fixture.Settings, fixture.DatabasePath);

        worker.ServerId.Should().Be(42);
    }

    [Fact]
    public async Task EnsureCreatesLabelledServerEnablesIpAutoDeleteAndConfiguresSsh()
    {
        using var fixture = new Fixture();
        // A Windows checkout has CRLF in the script; cloud-init runs it with bash, which fails on CR.
        File.WriteAllText(fixture.Settings.Processing.RemoteSsh.Hetzner.BootstrapScriptPath, "#!/usr/bin/env bash\r\ntouch /opt/video-optimiser/.bootstrap-complete\r\n");
        using var lifecycle = fixture.CreateLifecycle();

        var worker = await lifecycle.EnsureReadyAsync(fixture.Settings, fixture.DatabasePath);

        worker.ServerId.Should().Be(42);
        worker.Address.Should().Be("203.0.113.42");
        fixture.Settings.Processing.RemoteSsh.Host.Should().Be("203.0.113.42");
        fixture.Settings.Processing.RemoteSsh.User.Should().Be("videoopt");
        fixture.Settings.Processing.RemoteSsh.KnownHostsFile.Should().EndWith(".hetzner-42.known_hosts");
        fixture.Handler.CreatePayload.Should().NotBeNull();
        fixture.Handler.CreatePayload!.RootElement.GetProperty("labels").GetProperty("video-optimiser-managed").GetString().Should().Be("true");
        fixture.Handler.CreatePayload.RootElement.GetProperty("ssh_keys")[0].GetString().Should().Be("video-optimiser-hetzner-cx43");
        fixture.Handler.CreatePayload.RootElement.GetProperty("public_net").GetProperty("enable_ipv4").GetBoolean().Should().BeTrue();
        fixture.Handler.CreatePayload.RootElement.GetProperty("public_net").GetProperty("enable_ipv6").GetBoolean().Should().BeFalse();
        fixture.Handler.CreatePayload.RootElement.GetProperty("user_data").GetString().Should().Be("#!/usr/bin/env bash\ntouch /opt/video-optimiser/.bootstrap-complete\n");
        fixture.Handler.AutoDeleteUpdated.Should().BeTrue();
        fixture.Processes.Requests.Should().HaveCount(2);
        fixture.Processes.Requests[0].Arguments.Should().Contain("StrictHostKeyChecking=accept-new");
        fixture.Processes.Requests[0].Arguments.Should().ContainInOrder("-o", "User=root");
        fixture.Processes.Requests[1].Arguments.Should().ContainInOrder("-o", "User=videoopt");
        fixture.Settings.Processing.RemoteSsh.User.Should().Be("videoopt");
        File.Exists(fixture.DatabasePath + ".hetzner-worker.json").Should().BeTrue();
    }

    [Fact]
    public async Task EnsurePrefiltersUnavailableLocationsAndPreservesConfiguredOrder()
    {
        using var fixture = new Fixture();
        fixture.Settings.Processing.RemoteSsh.Hetzner.Locations = ["hel1", "fsn1", "nbg1"];
        fixture.Handler.LocationAvailability["hel1"] = (false, true);
        fixture.Handler.LocationAvailability["fsn1"] = (true, false);
        fixture.Handler.LocationAvailability["nbg1"] = (true, true);
        using var lifecycle = fixture.CreateLifecycle();
        var messages = new List<string>();

        await lifecycle.EnsureReadyAsync(fixture.Settings, fixture.DatabasePath, new SynchronousProgress(messages.Add));

        fixture.Handler.CreateLocations.Should().ContainSingle().Which.Should().Be("fsn1");
        messages.Should().Contain("Checking Hetzner capacity for cx43 in hel1, fsn1, or nbg1, up to EUR 0.30/h incl. VAT.");
        messages.Should().Contain("Hetzner cx43 is available in fsn1 and nbg1; unavailable in hel1.");
        messages.Should().Contain("Creating Hetzner cx43 in fsn1 (EUR 0.031/h incl. VAT).");
        messages.Should().NotContain(message => message.Contains("recommended", StringComparison.OrdinalIgnoreCase));
        messages.Should().NotContain(message => message.Contains("available=False", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("resource_unavailable")]
    [InlineData("placement_unavailable")]
    public async Task EnsureCleansUpAllocatedWorkerWhenProvisioningPlacementFails(string actionCode)
    {
        using var fixture = new Fixture { AutoDeletePrimaryIpWithServer = false };
        fixture.Settings.Processing.RemoteSsh.Hetzner.Locations = ["hel1", "fsn1"];
        fixture.Handler.ActionFailures.Enqueue((actionCode, "error during placement"));
        using var lifecycle = fixture.CreateLifecycle();

        await lifecycle.EnsureReadyAsync(fixture.Settings, fixture.DatabasePath);

        fixture.Handler.CreateLocations.Should().Equal("hel1", "fsn1");
        fixture.Handler.SecondCreateObservedCleanResources.Should().BeTrue();
        fixture.Handler.ServerExists.Should().BeTrue();
        fixture.Handler.PrimaryIpExists.Should().BeTrue();
        fixture.Handler.PrimaryIpDeleted.Should().BeTrue();
        File.Exists(fixture.DatabasePath + ".hetzner-worker.json").Should().BeTrue();
    }

    [Fact]
    public async Task AMalformedErrorBodyStillNamesTheStatusAndWhatHetznerSent()
    {
        using var fixture = new Fixture();
        // Valid JSON, but not the documented shape: the code is a number and the message an array.
        const string body = """{"error":{"code":400,"message":["bad request"]}}""";
        fixture.Handler.CreateErrorBody = body;
        using var lifecycle = fixture.CreateLifecycle();

        var action = () => lifecycle.EnsureReadyAsync(fixture.Settings, fixture.DatabasePath);

        await action.Should().ThrowAsync<InvalidOperationException>().WithMessage($"Hetzner API returned 400 (BadRequest): {body}");
        fixture.Handler.CreateCount.Should().Be(1);
    }

    [Fact]
    public async Task EnsureDoesNotCreateWhenPreflightReportsNoConfiguredAvailability()
    {
        using var fixture = new Fixture();
        fixture.Settings.Processing.RemoteSsh.Hetzner.Locations = ["hel1", "fsn1"];
        fixture.Handler.LocationAvailability["hel1"] = (false, false);
        fixture.Handler.LocationAvailability["fsn1"] = (false, false);
        using var lifecycle = fixture.CreateLifecycle();

        var action = () => lifecycle.EnsureReadyAsync(fixture.Settings, fixture.DatabasePath);

        var exception = await action.Should().ThrowAsync<RemoteWorkerCapacityUnavailableException>();
        exception.Which.Outcomes.Should().ContainSingle().Which.Should().BeEquivalentTo(
            new RemoteWorkerTypeOutcome("cx43", ["hel1", "fsn1"], "unavailable in hel1 and fsn1"));
        fixture.Handler.CreateCount.Should().Be(0);
        File.Exists(fixture.DatabasePath + ".hetzner-worker.json").Should().BeFalse();
    }

    [Theory]
    [InlineData("{}", "*did not contain a server_types array*no worker was created*")]
    [InlineData("{\"server_types\":[{\"name\":\"cx43\",\"architecture\":\"x86\",\"cores\":8,\"memory\":16,\"locations\":[{\"name\":\"hel1\"}]}]}", "*did not contain a boolean available field*no worker was created*")]
    public async Task EnsureDoesNotCreateWhenTheServerTypePreflightResponseIsUnusable(string response, string expectedMessage)
    {
        using var fixture = new Fixture();
        fixture.Handler.ServerTypePreflightResponse = response;
        using var lifecycle = fixture.CreateLifecycle();

        var action = () => lifecycle.EnsureReadyAsync(fixture.Settings, fixture.DatabasePath);

        await action.Should().ThrowAsync<InvalidOperationException>().WithMessage(expectedMessage);
        fixture.Handler.CreateCount.Should().Be(0);
    }

    [Fact]
    public async Task EnsureFallsThroughToTheNextServerTypeWhenTheFirstIsSoldOut()
    {
        using var fixture = new Fixture();
        fixture.Settings.Processing.RemoteSsh.Hetzner.ServerTypes = ["cx43", "cx53", "cpx42"];
        fixture.Handler.Catalogue["cx43"].SoldOut();
        fixture.Handler.Catalogue["cx53"].SoldOut();
        using var lifecycle = fixture.CreateLifecycle();
        var messages = new List<string>();

        var worker = await lifecycle.EnsureReadyAsync(fixture.Settings, fixture.DatabasePath, new SynchronousProgress(messages.Add));

        fixture.Handler.ServerTypeRequests.Should().Be(1);
        fixture.Handler.CreateServerTypes.Should().Equal("cpx42");
        fixture.Handler.CreateLocations.Should().Equal("hel1");
        messages.Should().ContainInOrder(
            "Skipping Hetzner cx43: unavailable in hel1, fsn1, and nbg1.",
            "Skipping Hetzner cx53: unavailable in hel1, fsn1, and nbg1.",
            "Hetzner cpx42 is available in hel1, fsn1, and nbg1.",
            "Creating Hetzner cpx42 in hel1 (EUR 0.134/h incl. VAT).");
        worker.ServerType.Should().Be("cpx42");
        worker.Location.Should().Be("hel1");
        File.ReadAllText(fixture.DatabasePath + ".hetzner-worker.json").Should().Contain("\"serverType\": \"cpx42\"");
    }

    [Fact]
    public async Task CreationIsQuotedFromTheFirstCandidateAndTheMostExpensiveAfterSkippedTypes()
    {
        using var fixture = new Fixture();
        fixture.Settings.Processing.RemoteSsh.Hetzner.ServerTypes = ["cx43", "cx53", "ccx33", "cpx42"];
        fixture.Handler.Catalogue["cx43"].SoldOut();
        using var lifecycle = fixture.CreateLifecycle();
        var quotes = new List<RemoteWorkerCreationQuote>();

        await lifecycle.EnsureReadyAsync(fixture.Settings, fixture.DatabasePath, confirmCreation: (quote, _) =>
        {
            quotes.Add(quote);
            fixture.Handler.CreateCount.Should().Be(0, "the quote is asked about before anything bills");
            return Task.FromResult(true);
        });

        quotes.Should().Equal(new RemoteWorkerCreationQuote(
            new RemoteWorkerCreationOption("cx53", "hel1", 0.057m),
            new RemoteWorkerCreationOption("ccx33", "hel1", 0.266m)));
        fixture.Handler.CreateServerTypes.Should().Equal("cx53");
    }

    [Fact]
    public async Task DecliningTheQuoteCreatesNothingAndLeavesNoWorkerState()
    {
        using var fixture = new Fixture();
        using var lifecycle = fixture.CreateLifecycle();

        var action = () => lifecycle.EnsureReadyAsync(fixture.Settings, fixture.DatabasePath, confirmCreation: (_, _) => Task.FromResult(false));

        await action.Should().ThrowAsync<RemoteWorkerCreationDeclinedException>().WithMessage("No worker was created.");
        fixture.Handler.CreateCount.Should().Be(0);
        fixture.Handler.ServerExists.Should().BeFalse();
        File.Exists(fixture.DatabasePath + ".hetzner-worker.json").Should().BeFalse();
        fixture.Processes.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task ReusingAWorkerNeverAsksAboutCreation()
    {
        using var fixture = new Fixture();
        using (var first = fixture.CreateLifecycle()) await first.EnsureReadyAsync(fixture.Settings, fixture.DatabasePath);
        var asked = 0;

        using var second = fixture.CreateLifecycle();
        var worker = await second.EnsureReadyAsync(fixture.Settings, fixture.DatabasePath, confirmCreation: (_, _) =>
        {
            asked++;
            return Task.FromResult(false);
        });

        asked.Should().Be(0);
        worker.ServerId.Should().Be(42);
        fixture.Handler.CreateCount.Should().Be(1);
    }

    [Fact]
    public async Task ListingOffersEveryCandidateInCreationOrderWithoutTheLockOrAServer()
    {
        using var fixture = new Fixture();
        // ccx43 is over the cap in every location; cx43 is sold out in fsn1 only.
        fixture.Settings.Processing.RemoteSsh.Hetzner.ServerTypes = ["cx43", "ccx43", "cx53"];
        fixture.Handler.Catalogue["cx43"].Locations["fsn1"] = (false, false);
        using var lifecycle = fixture.CreateLifecycle();
        // A run that is bootstrapping holds this lock for minutes; the list the worker dialog shows must not wait for it.
        await using var held = await WorkerStateStore.AcquireLockAsync(fixture.DatabasePath, null, CancellationToken.None);

        var options = await lifecycle.ListCreationOptionsAsync(fixture.Settings).WaitAsync(TimeSpan.FromSeconds(10));

        options.Should().Equal(
            new RemoteWorkerCreationOption("cx43", "hel1", 0.031m),
            new RemoteWorkerCreationOption("cx43", "nbg1", 0.031m),
            new RemoteWorkerCreationOption("cx53", "hel1", 0.057m),
            new RemoteWorkerCreationOption("cx53", "fsn1", 0.057m),
            new RemoteWorkerCreationOption("cx53", "nbg1", 0.057m));
        fixture.Handler.CreateCount.Should().Be(0);
        File.Exists(fixture.DatabasePath + ".hetzner-worker.json").Should().BeFalse();
    }

    [Fact]
    public async Task ListingWithNoCandidateThrowsWhatCreationThrows()
    {
        using var fixture = new Fixture();
        fixture.Handler.Catalogue["cx43"].SoldOut();
        using var lifecycle = fixture.CreateLifecycle();

        var action = () => lifecycle.ListCreationOptionsAsync(fixture.Settings);

        var exception = await action.Should().ThrowAsync<RemoteWorkerCapacityUnavailableException>();
        exception.Which.Outcomes.Should().ContainSingle().Which.Should().BeEquivalentTo(
            new RemoteWorkerTypeOutcome("cx43", ["hel1", "fsn1", "nbg1"], "unavailable in hel1, fsn1, and nbg1"));
    }

    [Fact]
    public async Task ListingWithoutAnApiTokenThrowsTheErrorEnsureThrows()
    {
        using var fixture = new Fixture();
        fixture.RemoveToken();
        using var lifecycle = fixture.CreateLifecycle();

        var action = () => lifecycle.ListCreationOptionsAsync(fixture.Settings);

        await action.Should().ThrowAsync<InvalidOperationException>().WithMessage("Hetzner API token*was not found*");
        fixture.Handler.RequestCount.Should().Be(0);
    }

    [Fact]
    public async Task CreatingAChosenOptionMakesThatServerAndReturnsWithoutWaitingForBootstrap()
    {
        using var fixture = new Fixture();
        fixture.Settings.Processing.RemoteSsh.Hetzner.ServerTypes = ["cx43", "cx53"];
        using var lifecycle = fixture.CreateLifecycle();
        var messages = new List<string>();

        var worker = await lifecycle.CreateAsync(fixture.Settings, fixture.DatabasePath, new RemoteWorkerCreationOption("cx53", "fsn1", 0.057m), new SynchronousProgress(messages.Add));

        fixture.Handler.CreateServerTypes.Should().Equal("cx53");
        fixture.Handler.CreateLocations.Should().Equal("fsn1");
        worker.Status.Should().Be(RemoteWorkerStatuses.Bootstrapping);
        worker.ServerType.Should().Be("cx53");
        worker.Location.Should().Be("fsn1");
        // The readiness wait would ssh to the new server; the next run does that, not the dialog.
        fixture.Processes.Requests.Should().BeEmpty();
        messages.Should().Contain("Creating Hetzner cx53 in fsn1 (EUR 0.057/h incl. VAT).");
        messages.Should().Contain($"Created the Hetzner worker {worker.Name}; it is bootstrapping.");
        messages.Should().NotContain(message => message.StartsWith("Bootstrapping the worker", StringComparison.Ordinal));
        File.ReadAllText(fixture.DatabasePath + ".hetzner-worker.json").Should().Contain("\"serverType\": \"cx53\"");
    }

    [Fact]
    public async Task ARunStartedAfterACreateReusesTheServerAndWaitsForItsBootstrap()
    {
        using var fixture = new Fixture();
        using var lifecycle = fixture.CreateLifecycle();
        await lifecycle.CreateAsync(fixture.Settings, fixture.DatabasePath, new RemoteWorkerCreationOption("cx43", "hel1", 0.031m));

        var worker = await lifecycle.EnsureReadyAsync(fixture.Settings, fixture.DatabasePath);

        fixture.Handler.CreateCount.Should().Be(1);
        worker.Status.Should().Be("ready");
        fixture.Processes.Requests.Should().NotBeEmpty();
    }

    [Theory]
    [InlineData("sold out")]
    [InlineData("over the cap")]
    public async Task CreatingAnOptionThatIsNoLongerACandidateThrowsAndNeverFallsBack(string reason)
    {
        using var fixture = new Fixture();
        fixture.Settings.Processing.RemoteSsh.Hetzner.ServerTypes = ["cx43", "cx53"];
        // cx43 stays available throughout, so a fallback to it would be the bug.
        if (reason == "sold out") fixture.Handler.Catalogue["cx53"].SoldOut();
        else fixture.Handler.Catalogue["cx53"].PriceOverrides["hel1"] = 0.90m;
        using var lifecycle = fixture.CreateLifecycle();

        var action = () => lifecycle.CreateAsync(fixture.Settings, fixture.DatabasePath, new RemoteWorkerCreationOption("cx53", "hel1", 0.057m));

        var exception = await action.Should().ThrowAsync<RemoteWorkerCapacityUnavailableException>();
        exception.Which.Outcomes.Should().ContainSingle().Which.ServerType.Should().Be("cx53");
        exception.Which.Message.Should().Contain("cx53");
        fixture.Handler.CreateCount.Should().Be(0);
        File.Exists(fixture.DatabasePath + ".hetzner-worker.json").Should().BeFalse();
    }

    [Fact]
    public async Task CreatingAChosenOptionHetznerRefusesLeavesNothingAndTriesNoOtherLocation()
    {
        using var fixture = new Fixture();
        fixture.Handler.CreateFailures.Enqueue((HttpStatusCode.PreconditionFailed, "resource_unavailable", "hel capacity"));
        using var lifecycle = fixture.CreateLifecycle();

        var action = () => lifecycle.CreateAsync(fixture.Settings, fixture.DatabasePath, new RemoteWorkerCreationOption("cx43", "hel1", 0.031m));

        var exception = await action.Should().ThrowAsync<RemoteWorkerCapacityUnavailableException>();
        exception.Which.AttemptedFailures.Should().ContainSingle().Which.Should().Contain("cx43 in hel1");
        exception.Which.Message.Should().Contain("placement failed in hel1");
        fixture.Handler.CreateCount.Should().Be(1);
        File.Exists(fixture.DatabasePath + ".hetzner-worker.json").Should().BeFalse();
    }

    [Fact]
    public async Task CreatingAChosenOptionWhoseProvisioningFailsDeletesTheServerAndItsIpAndTriesNoOtherLocation()
    {
        using var fixture = new Fixture { AutoDeletePrimaryIpWithServer = false };
        fixture.Handler.ActionFailures.Enqueue(("resource_unavailable", "error during placement"));
        using var lifecycle = fixture.CreateLifecycle();

        var action = () => lifecycle.CreateAsync(fixture.Settings, fixture.DatabasePath, new RemoteWorkerCreationOption("cx43", "hel1", 0.031m));

        await action.Should().ThrowAsync<RemoteWorkerCapacityUnavailableException>();
        fixture.Handler.CreateCount.Should().Be(1);
        fixture.Handler.ServerExists.Should().BeFalse();
        fixture.Handler.PrimaryIpDeleted.Should().BeTrue();
        File.Exists(fixture.DatabasePath + ".hetzner-worker.json").Should().BeFalse();
    }

    [Fact]
    public async Task CreatingWhenAWorkerExistsReturnsItAndCreatesNothing()
    {
        using var fixture = new Fixture();
        using var lifecycle = fixture.CreateLifecycle();
        await lifecycle.EnsureReadyAsync(fixture.Settings, fixture.DatabasePath);

        var worker = await lifecycle.CreateAsync(fixture.Settings, fixture.DatabasePath, new RemoteWorkerCreationOption("cx53", "fsn1", 0.057m));

        worker.ServerId.Should().Be(42);
        worker.ServerType.Should().Be("cx43");
        fixture.Handler.CreateCount.Should().Be(1);
    }

    [Fact]
    public async Task ReadRecordedReturnsTheStoredWorkerWithoutCallingHetzner()
    {
        using var fixture = new Fixture();
        fixture.Settings.Processing.RemoteSsh.Hetzner.ServerTypes = ["cpx42"];
        using var lifecycle = fixture.CreateLifecycle();
        (await lifecycle.ReadRecordedAsync(fixture.DatabasePath)).Should().BeNull();
        var created = await lifecycle.EnsureReadyAsync(fixture.Settings, fixture.DatabasePath);
        var requestsBefore = fixture.Handler.RequestCount;

        var recorded = await lifecycle.ReadRecordedAsync(fixture.DatabasePath);

        fixture.Handler.RequestCount.Should().Be(requestsBefore);
        recorded.Should().NotBeNull();
        recorded!.ServerId.Should().Be(created.ServerId);
        recorded.ServerType.Should().Be("cpx42");
        recorded.Location.Should().Be("hel1");
        recorded.CreatedUtc.Should().Be(created.CreatedUtc);
        recorded.Status.Should().Be("recorded");
    }

    [Fact]
    public async Task EnsureFallsBackAcrossTypesWhenPlacementFailsInEveryLocation()
    {
        using var fixture = new Fixture();
        fixture.Settings.Processing.RemoteSsh.Hetzner.ServerTypes = ["cx43", "cpx42"];
        fixture.Settings.Processing.RemoteSsh.Hetzner.Locations = ["hel1", "fsn1"];
        fixture.Handler.CreateFailures.Enqueue((HttpStatusCode.PreconditionFailed, "resource_unavailable", "stale"));
        fixture.Handler.CreateFailures.Enqueue((HttpStatusCode.PreconditionFailed, "resource_unavailable", "stale"));
        using var lifecycle = fixture.CreateLifecycle();
        var messages = new List<string>();
        var asked = 0;

        await lifecycle.EnsureReadyAsync(fixture.Settings, fixture.DatabasePath, new SynchronousProgress(messages.Add), confirmCreation: (_, _) =>
        {
            asked++;
            return Task.FromResult(true);
        });

        asked.Should().Be(1, "one approval covers every placement fallback");
        fixture.Handler.CreateServerTypes.Should().Equal("cx43", "cx43", "cpx42");
        fixture.Handler.CreateLocations.Should().Equal("hel1", "fsn1", "hel1");
        messages.Should().Contain(message => message.StartsWith("Hetzner cx43 in fsn1 placement unavailable", StringComparison.Ordinal) && message.EndsWith("falling back to cpx42 in hel1.", StringComparison.Ordinal));
    }

    [Fact]
    public async Task EnsureNeverCreatesATypeTheWorkerWouldRejectOrThatCostsMoreThanTheCap()
    {
        using var fixture = new Fixture();
        // cx23: too few cores and too little memory. cax31: ARM. ccx43: over the cap. cpx62: deprecated.
        fixture.Settings.Processing.RemoteSsh.Hetzner.ServerTypes = ["cx23", "cax31", "ccx43", "cpx62", "nope1", "ccx33"];
        fixture.Settings.Processing.RemoteSsh.Hetzner.MaxHourlyPrice = 0.30m;
        fixture.Handler.Catalogue["cpx62"].Deprecated = true;
        using var lifecycle = fixture.CreateLifecycle();
        var messages = new List<string>();

        await lifecycle.EnsureReadyAsync(fixture.Settings, fixture.DatabasePath, new SynchronousProgress(messages.Add));

        fixture.Handler.CreateServerTypes.Should().Equal("ccx33");
        messages.Should().Contain("Skipping Hetzner cx23: 2 cores, below minimumCpuCount 8; 4 GB memory, below minimumAvailableMemory 14GiB.");
        messages.Should().Contain("Skipping Hetzner cax31: arm architecture, but the worker needs x86.");
        messages.Should().Contain("Skipping Hetzner ccx43: over the EUR 0.30/h cap in hel1 (EUR 0.533/h), fsn1 (EUR 0.533/h), and nbg1 (EUR 0.533/h).");
        messages.Should().Contain("Skipping Hetzner cpx62: deprecated by Hetzner.");
        messages.Should().Contain("Skipping Hetzner nope1: not a Hetzner server type.");
    }

    [Fact]
    public async Task EnsureAppliesThePriceCapPerLocationAndAllowsNoCap()
    {
        using var fixture = new Fixture();
        fixture.Settings.Processing.RemoteSsh.Hetzner.ServerTypes = ["ccx43"];
        fixture.Handler.Catalogue["ccx43"].PriceOverrides["hel1"] = 0.90m;
        fixture.Settings.Processing.RemoteSsh.Hetzner.MaxHourlyPrice = 0.60m;
        using (var capped = fixture.CreateLifecycle()) await capped.EnsureReadyAsync(fixture.Settings, fixture.DatabasePath);
        fixture.Handler.CreateLocations.Should().Equal("fsn1");

        using var uncappedFixture = new Fixture();
        uncappedFixture.Settings.Processing.RemoteSsh.Hetzner.ServerTypes = ["ccx43"];
        uncappedFixture.Handler.Catalogue["ccx43"].PriceOverrides["hel1"] = 0.90m;
        uncappedFixture.Settings.Processing.RemoteSsh.Hetzner.MaxHourlyPrice = null;
        using var uncapped = uncappedFixture.CreateLifecycle();
        await uncapped.EnsureReadyAsync(uncappedFixture.Settings, uncappedFixture.DatabasePath);
        uncappedFixture.Handler.CreateLocations.Should().Equal("hel1");
    }

    [Fact]
    public async Task EnsureNamesEveryTypeAndLocationWhenNoCandidateRemains()
    {
        using var fixture = new Fixture();
        fixture.Settings.Processing.RemoteSsh.Hetzner.ServerTypes = ["cx43", "cax31", "cpx42"];
        fixture.Settings.Processing.RemoteSsh.Hetzner.Locations = ["hel1", "fsn1"];
        fixture.Handler.Catalogue["cx43"].SoldOut();
        fixture.Handler.Catalogue["cpx42"].Locations["hel1"] = (false, false);
        fixture.Handler.CreateFailures.Enqueue((HttpStatusCode.PreconditionFailed, "resource_unavailable", "fsn capacity"));
        using var lifecycle = fixture.CreateLifecycle();

        var action = () => lifecycle.EnsureReadyAsync(fixture.Settings, fixture.DatabasePath);

        var exception = await action.Should().ThrowAsync<RemoteWorkerCapacityUnavailableException>();
        exception.Which.Outcomes.Should().BeEquivalentTo(new[]
        {
            new RemoteWorkerTypeOutcome("cx43", ["hel1", "fsn1"], "unavailable in hel1 and fsn1"),
            new RemoteWorkerTypeOutcome("cax31", [], "arm architecture, but the worker needs x86"),
            new RemoteWorkerTypeOutcome("cpx42", ["hel1", "fsn1"], "unavailable in hel1; placement failed in fsn1 (HTTP 412 PreconditionFailed (resource_unavailable): fsn capacity)")
        }, options => options.WithStrictOrdering());
        exception.Which.Message.Should().StartWith("No Hetzner worker could be created")
            .And.Contain("cx43 in hel1, fsn1: unavailable in hel1 and fsn1")
            .And.Contain("cax31: arm architecture")
            .And.Contain("cpx42 in hel1, fsn1:");
        fixture.Handler.CreateCount.Should().Be(1);
        File.Exists(fixture.DatabasePath + ".hetzner-worker.json").Should().BeFalse();
    }

    [Fact]
    public async Task EnsureReadsEveryPageOfTheServerTypeListing()
    {
        using var fixture = new Fixture();
        fixture.Settings.Processing.RemoteSsh.Hetzner.ServerTypes = ["cax31", "ccx33"];
        fixture.Handler.ServerTypePageSize = 3;
        using var lifecycle = fixture.CreateLifecycle();

        await lifecycle.EnsureReadyAsync(fixture.Settings, fixture.DatabasePath);

        fixture.Handler.ServerTypeRequests.Should().Be(3);
        fixture.Handler.CreateServerTypes.Should().Equal("ccx33");
    }

    [Fact]
    public async Task EnsureReportsHeartbeatWhenBootstrapPhaseDoesNotChange()
    {
        using var fixture = new Fixture();
        for (var attempt = 0; attempt < 5; attempt++)
            fixture.Processes.Results.Enqueue(new ExternalProcessResult(0, "percent=60\nstage=Building FFmpeg\n", string.Empty));
        fixture.Processes.Results.Enqueue(new ExternalProcessResult(0, "percent=100\nstage=Ready\n", string.Empty));
        var clock = new FakeClock(DateTimeOffset.UtcNow);
        using var lifecycle = fixture.CreateLifecycle(
            progressHeartbeatInterval: TimeSpan.FromSeconds(60),
            utcNow: clock.Now,
            delay: (_, _) =>
            {
                clock.Advance(TimeSpan.FromSeconds(15));
                return Task.CompletedTask;
            });
        var messages = new List<string>();

        await lifecycle.EnsureReadyAsync(fixture.Settings, fixture.DatabasePath, new SynchronousProgress(messages.Add));

        messages.Should().Contain("Bootstrapping: 60%, building FFmpeg (1m 00s in this step).");
    }

    [Fact]
    public async Task SshFailuresWhileTheServerBootsAreSilentForThreeMinutesThenReportedOncePerMinute()
    {
        using var fixture = new Fixture();
        // A new server refuses SSH while it boots: 24 checks, 15 s apart, fail for 6 minutes before it answers.
        for (var attempt = 0; attempt < 24; attempt++)
            fixture.Processes.Results.Enqueue(new ExternalProcessResult(255, string.Empty, "ssh: connect to host 203.0.113.42 port 22: Connection refused"));
        var clock = new FakeClock(DateTimeOffset.UtcNow);
        using var lifecycle = fixture.CreateLifecycle(
            utcNow: clock.Now,
            delay: (_, _) =>
            {
                clock.Advance(TimeSpan.FromSeconds(15));
                return Task.CompletedTask;
            });
        var messages = new List<string>();

        await lifecycle.EnsureReadyAsync(fixture.Settings, fixture.DatabasePath, new SynchronousProgress(messages.Add));

        messages.Where(message => message.StartsWith("Still waiting", StringComparison.Ordinal)).Should().Equal(
            "Still waiting for the worker's SSH (3m 00s): ssh: connect to host 203.0.113.42 port 22: Connection refused",
            "Still waiting for the worker's SSH (4m 00s): ssh: connect to host 203.0.113.42 port 22: Connection refused",
            "Still waiting for the worker's SSH (5m 00s): ssh: connect to host 203.0.113.42 port 22: Connection refused");
        messages.Should().NotContain(message => message.Contains("retrying", StringComparison.Ordinal));
        messages.Should().Contain("Worker ready after 6m 00s.");
        messages.Should().NotContain(message => message.Contains("Managed worker bootstrap completed", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AFailedVideooptSshCheckNeverReportsTheWorkerSetUpAsComplete()
    {
        using var fixture = new Fixture();
        // The bootstrap script is done (root probe answers 100%), but the videoopt user cannot connect yet; the next round can.
        fixture.Processes.Results.Enqueue(new ExternalProcessResult(0, "percent=100\nstage=Ready\n", string.Empty));
        fixture.Processes.Results.Enqueue(new ExternalProcessResult(255, string.Empty, "videoopt@203.0.113.42: Permission denied (publickey)."));
        using var lifecycle = fixture.CreateLifecycle();
        var messages = new List<string>();

        await lifecycle.EnsureReadyAsync(fixture.Settings, fixture.DatabasePath, new SynchronousProgress(messages.Add));

        messages.Should().NotContain(message => message.StartsWith("Bootstrapping: 100%", StringComparison.Ordinal));
        messages.Where(message => message.StartsWith("Worker ready", StringComparison.Ordinal)).Should().ContainSingle();
        fixture.Processes.Requests.Should().HaveCount(4);
    }

    [Theory]
    // The ERR trap's marker, with the cloud-init log's tail; die's marker, whose own log line is dropped as a repeat.
    [InlineData(
        "failed=1\nstage=Building FFmpeg\nstatus=2\nline=150\ncommand=make -j8\nlog=libavcodec/x.c:1: error: boom\nlog=make: *** [all] Error 2\n",
        "failed to bootstrap while building FFmpeg: make -j8 exited with code 2 (line 150 of the bootstrap script).\nThe end of its cloud-init log:\n  libavcodec/x.c:1: error: boom\n  make: *** [all] Error 2")]
    [InlineData(
        "failed=1\nstage=Verifying installed codecs\nerror=FFmpeg does not expose the libsvtav1 encoder\nlog=bootstrap error: FFmpeg does not expose the libsvtav1 encoder\n",
        "failed to bootstrap while verifying installed codecs: FFmpeg does not expose the libsvtav1 encoder.")]
    public async Task ABootstrapThatReportsItsFailureEndsTheWaitAtOnceWithTheStageAndCause(string probeAnswer, string expectedEnd)
    {
        using var fixture = new Fixture();
        fixture.Processes.Results.Enqueue(new ExternalProcessResult(0, "percent=60\nstage=Building FFmpeg\n", string.Empty));
        fixture.Processes.Results.Enqueue(new ExternalProcessResult(0, probeAnswer, string.Empty));
        var delays = 0;
        using var lifecycle = fixture.CreateLifecycle(delay: (_, _) =>
        {
            delays++;
            return Task.CompletedTask;
        });

        var act = () => lifecycle.EnsureReadyAsync(fixture.Settings, fixture.DatabasePath);

        var failure = await act.Should().ThrowAsync<RemoteWorkerBootstrapFailedException>();
        failure.Which.Message.ReplaceLineEndings("\n").Should().StartWith("The Hetzner worker ").And.EndWith(expectedEnd);
        // One wait between the progress answer and the failed one, then no more polling.
        delays.Should().Be(1);
        fixture.Processes.Requests.Should().HaveCount(2);
        // The server bills until its fate is decided, so it stays recorded for that.
        (await lifecycle.ReadRecordedAsync(fixture.DatabasePath)).Should().NotBeNull();
    }

    [Fact]
    public async Task ANewWorkerIsToldItWillTakeSeveralMinutesBeforeTheFirstProgressLine()
    {
        using var fixture = new Fixture();
        fixture.Processes.Results.Enqueue(new ExternalProcessResult(0, "percent=35\nstage=Building SVT-AV1\n", string.Empty));
        using var lifecycle = fixture.CreateLifecycle();
        var messages = new List<string>();

        await lifecycle.EnsureReadyAsync(fixture.Settings, fixture.DatabasePath, new SynchronousProgress(messages.Add));

        var announcement = messages.FindIndex(message => message.StartsWith("Bootstrapping the worker: it builds libvmaf, SVT-AV1 and FFmpeg from source and installs ab-av1, which takes several minutes.", StringComparison.Ordinal));
        announcement.Should().BeGreaterThan(-1);
        messages.IndexOf("Bootstrapping: 35%, building SVT-AV1.").Should().BeGreaterThan(announcement);
        // The worker is named, never numbered, and "server" stays out: it is a worker from the first line to the last.
        var name = fixture.Settings.Processing.RemoteSsh.Hetzner.ServerNamePrefix;
        messages[0].Should().Be("No Hetzner worker exists yet.");
        messages.Should().ContainSingle(message => message.StartsWith($"Hetzner accepted cx43 in hel1 for the worker {name}-", StringComparison.Ordinal) && message.EndsWith("; waiting for it to start.", StringComparison.Ordinal));
        messages.Should().ContainSingle(message => message.StartsWith($"Created the Hetzner worker {name}-", StringComparison.Ordinal) && message.EndsWith("; it is bootstrapping.", StringComparison.Ordinal));

        using var again = fixture.CreateLifecycle();
        var reuse = new List<string>();
        await again.EnsureReadyAsync(fixture.Settings, fixture.DatabasePath, new SynchronousProgress(reuse.Add));
        reuse.Should().ContainSingle(message => message.StartsWith($"Reusing the Hetzner worker {name}-", StringComparison.Ordinal) && message.EndsWith("; checking its bootstrap progress.", StringComparison.Ordinal));
        reuse.Should().NotContain(message => message.StartsWith("Bootstrapping the worker", StringComparison.Ordinal));
        messages.Concat(reuse).Should().NotContain(message => message.Contains("server ", StringComparison.OrdinalIgnoreCase) && !message.Contains("server type", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ReadinessProbeTimeoutCancelsTheSshAttemptAndRetries()
    {
        using var fixture = new Fixture();
        fixture.Processes.BlockingAttempts = 1;
        using var lifecycle = fixture.CreateLifecycle(TimeSpan.FromMilliseconds(20));

        await lifecycle.EnsureReadyAsync(fixture.Settings, fixture.DatabasePath);

        fixture.Processes.Requests.Count.Should().Be(3);
        fixture.Processes.CancelledRequests.Should().Be(1);
    }

    [Fact]
    public async Task EnsureRecoversOwnedLabelledServerWhenLocalStateWasLost()
    {
        using var fixture = new Fixture();
        using (var first = fixture.CreateLifecycle()) await first.EnsureReadyAsync(fixture.Settings, fixture.DatabasePath);
        var createCount = fixture.Handler.CreateCount;
        File.Delete(fixture.DatabasePath + ".hetzner-worker.json");

        using var second = fixture.CreateLifecycle();
        var worker = await second.EnsureReadyAsync(fixture.Settings, fixture.DatabasePath);

        worker.ServerId.Should().Be(42);
        fixture.Handler.CreateCount.Should().Be(createCount);
        File.Exists(fixture.DatabasePath + ".hetzner-worker.json").Should().BeTrue();
    }

    [Fact]
    public async Task DeleteRemovesOwnedServerPrimaryIpAndLocalRecoveryState()
    {
        using var fixture = new Fixture { AutoDeletePrimaryIpWithServer = false };
        using var lifecycle = fixture.CreateLifecycle();
        await lifecycle.EnsureReadyAsync(fixture.Settings, fixture.DatabasePath);

        var deleted = await lifecycle.DeleteAsync(fixture.Settings, fixture.DatabasePath);

        deleted.Should().BeTrue();
        fixture.Handler.ServerExists.Should().BeFalse();
        fixture.Handler.PrimaryIpExists.Should().BeFalse();
        fixture.Handler.PrimaryIpDeleted.Should().BeTrue();
        File.Exists(fixture.DatabasePath + ".hetzner-worker.json").Should().BeFalse();
    }

    [Fact]
    public async Task DeleteFindsLabelledOrphanPrimaryIpAfterServerAndStateWereLost()
    {
        using var fixture = new Fixture { AutoDeletePrimaryIpWithServer = false };
        using (var first = fixture.CreateLifecycle()) await first.EnsureReadyAsync(fixture.Settings, fixture.DatabasePath);
        fixture.Handler.DeleteServerOutOfBand();
        File.Delete(fixture.DatabasePath + ".hetzner-worker.json");

        using var second = fixture.CreateLifecycle();
        var deleted = await second.DeleteAsync(fixture.Settings, fixture.DatabasePath);

        deleted.Should().BeTrue();
        fixture.Handler.PrimaryIpDeleted.Should().BeTrue();
    }

    [Fact]
    public async Task RefusesToRecoverServerWithoutExactOwnershipLabels()
    {
        using var fixture = new Fixture();
        fixture.Handler.ReturnWrongOwnership = true;
        using var lifecycle = fixture.CreateLifecycle();

        var action = () => lifecycle.EnsureReadyAsync(fixture.Settings, fixture.DatabasePath);

        await action.Should().ThrowAsync<InvalidOperationException>().WithMessage("*ownership labels*");
        fixture.Handler.CreatePayload.Should().BeNull();
    }

    [Fact]
    public async Task DeleteRefusesARecordedServerWithoutExactOwnershipLabels()
    {
        using var fixture = new Fixture { AutoDeletePrimaryIpWithServer = false };
        using var lifecycle = fixture.CreateLifecycle();
        await lifecycle.EnsureReadyAsync(fixture.Settings, fixture.DatabasePath);
        fixture.Handler.ReturnWrongOwnership = true;

        var action = () => lifecycle.DeleteAsync(fixture.Settings, fixture.DatabasePath);

        await action.Should().ThrowAsync<InvalidOperationException>().WithMessage("*ownership labels*");
        fixture.Handler.DeleteRequests.Should().Be(0, "a server that is not ours is never deleted");
        fixture.Handler.ServerExists.Should().BeTrue();
        fixture.Handler.PrimaryIpExists.Should().BeTrue();
        File.Exists(fixture.DatabasePath + ".hetzner-worker.json").Should().BeTrue();
    }

    [Fact]
    public async Task CheckAnswersAtOnceWhileAnotherHolderHasTheLifecycleLock()
    {
        using var fixture = new Fixture();
        using (var creating = fixture.CreateLifecycle()) await creating.EnsureReadyAsync(fixture.Settings, fixture.DatabasePath);
        // A run that is creating a worker holds this lock through the whole bootstrap; a status read must not wait for it.
        await using var held = await WorkerStateStore.AcquireLockAsync(fixture.DatabasePath, null, CancellationToken.None);
        using var lifecycle = fixture.CreateLifecycle();

        var check = await lifecycle.CheckAsync(fixture.Settings, fixture.DatabasePath).WaitAsync(TimeSpan.FromSeconds(10));

        check.NotCheckedReason.Should().BeNull();
        check.Worker.Should().NotBeNull();
        check.Worker!.ServerId.Should().Be(42);
        check.Worker.Status.Should().Be("running");
    }

    [Fact]
    public async Task CheckPointsTheSettingsAtTheWorkerItFoundSoDoctorCanReachIt()
    {
        using var fixture = new Fixture();
        using (var creating = fixture.CreateLifecycle()) await creating.EnsureReadyAsync(fixture.Settings, fixture.DatabasePath);
        var settings = fixture.Settings;
        (settings.Processing.RemoteSsh.Host, settings.Processing.RemoteSsh.User, settings.Processing.RemoteSsh.KnownHostsFile) = ("", "", "");
        using var lifecycle = fixture.CreateLifecycle();

        var check = await lifecycle.CheckAsync(settings, fixture.DatabasePath);

        check.Worker.Should().NotBeNull();
        settings.Processing.RemoteSsh.Host.Should().Be(check.Worker!.Address);
        settings.Processing.RemoteSsh.User.Should().Be("videoopt");
        settings.Processing.RemoteSsh.KnownHostsFile.Should().Be(fixture.DatabasePath + ".hetzner-42.known_hosts");
    }

    [Fact]
    public async Task DeleteWaitsWithOneLineWhileAnotherCommandHoldsTheLifecycleLock()
    {
        using var fixture = new Fixture();
        var messages = new List<string>();
        var held = await WorkerStateStore.AcquireLockAsync(fixture.DatabasePath, null, CancellationToken.None);
        using var lifecycle = fixture.CreateLifecycle();

        var deleting = lifecycle.DeleteAsync(fixture.Settings, fixture.DatabasePath, new SynchronousProgress(messages.Add));
        await WaitForAsync(() => messages.Count > 0);
        // Long enough for several of the lock's retries: the line is printed once, not on each.
        await Task.Delay(700);

        messages.Should().Equal("Waiting for another video-optimiser command that is using the worker; Ctrl+C stops waiting.");
        deleting.IsCompleted.Should().BeFalse();
        await held.DisposeAsync();
        (await deleting.WaitAsync(TimeSpan.FromSeconds(10))).Should().BeFalse("no worker exists to delete");
        messages.Should().ContainSingle();
    }

    [Fact]
    public async Task EnsureWaitsWithOneLineAndCtrlCStopsTheWait()
    {
        using var fixture = new Fixture();
        var messages = new List<string>();
        await using var held = await WorkerStateStore.AcquireLockAsync(fixture.DatabasePath, null, CancellationToken.None);
        using var lifecycle = fixture.CreateLifecycle();
        using var stop = new CancellationTokenSource();

        var ensuring = lifecycle.EnsureReadyAsync(fixture.Settings, fixture.DatabasePath, new SynchronousProgress(messages.Add), cancellationToken: stop.Token);
        await WaitForAsync(() => messages.Count > 0);
        stop.Cancel();

        await ((Func<Task>)(() => ensuring)).Should().ThrowAsync<OperationCanceledException>();
        messages.Should().Equal("Waiting for another video-optimiser command that is using the worker; Ctrl+C stops waiting.");
        fixture.Handler.CreateCount.Should().Be(0);
    }

    [Fact]
    public async Task DeleteSaysNothingAboutWaitingWhenTheLockIsFree()
    {
        using var fixture = new Fixture();
        using var lifecycle = fixture.CreateLifecycle();
        var messages = new List<string>();

        (await lifecycle.DeleteAsync(fixture.Settings, fixture.DatabasePath, new SynchronousProgress(messages.Add))).Should().BeFalse();

        messages.Should().BeEmpty();
    }

    [Fact]
    public async Task DeleteOfAServerThatIsAlreadyGoneSaysSoInsteadOfDeletingIt()
    {
        using var fixture = new Fixture { AutoDeletePrimaryIpWithServer = false };
        using var lifecycle = fixture.CreateLifecycle();
        var created = await lifecycle.EnsureReadyAsync(fixture.Settings, fixture.DatabasePath);
        fixture.Handler.DeleteServerOutOfBand();
        var messages = new List<string>();

        var deleted = await lifecycle.DeleteAsync(fixture.Settings, fixture.DatabasePath, new SynchronousProgress(messages.Add));

        deleted.Should().BeTrue();
        messages.Should().Equal(
            $"The worker {created.Name} is already gone from Hetzner; its record on this PC is removed.",
            "Deleting its IPv4 address.",
            "Worker deleted; billing for it and its IPv4 address has stopped.");
        fixture.Handler.PrimaryIpDeleted.Should().BeTrue();
        File.Exists(fixture.DatabasePath + ".hetzner-worker.json").Should().BeFalse();
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("The condition never held.");
            await Task.Delay(20);
        }
    }

    /// <summary>Hetzner now names a server's location at the top level; older answers named it under <c>datacenter</c>.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CheckReadsThePriceForTheServersLocation(bool locationUnderDatacenter)
    {
        using var fixture = new Fixture();
        fixture.Handler.LocationUnderDatacenter = locationUnderDatacenter;
        // The other locations keep the catalogue's EUR 0.031, so only the server's own location gives this price.
        fixture.Handler.Catalogue["cx43"].PriceOverrides["hel1"] = 0.0119m;
        using var lifecycle = fixture.CreateLifecycle();
        await lifecycle.EnsureReadyAsync(fixture.Settings, fixture.DatabasePath);

        var check = await lifecycle.CheckAsync(fixture.Settings, fixture.DatabasePath);

        check.HourlyGross.Should().Be(0.0119m);
        check.Worker!.ServerType.Should().Be("cx43");
        check.Worker.Location.Should().Be("hel1");
        check.Worker.CreatedUtc.Should().Be(new DateTimeOffset(2026, 8, 20, 0, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public async Task CheckWithoutAPriceInTheAnswerStillReportsTheServer()
    {
        using var fixture = new Fixture();
        fixture.Handler.OmitServerPrices = true;
        using var lifecycle = fixture.CreateLifecycle();
        await lifecycle.EnsureReadyAsync(fixture.Settings, fixture.DatabasePath);

        var check = await lifecycle.CheckAsync(fixture.Settings, fixture.DatabasePath);

        check.Worker!.ServerId.Should().Be(42);
        check.HourlyGross.Should().BeNull();
        check.NotCheckedReason.Should().BeNull();
    }

    [Fact]
    public async Task CheckSaysAMissingServerWhoseIpv4IsRetainedStillBills()
    {
        using var fixture = new Fixture { AutoDeletePrimaryIpWithServer = false };
        using var lifecycle = fixture.CreateLifecycle();
        await lifecycle.EnsureReadyAsync(fixture.Settings, fixture.DatabasePath);
        fixture.Handler.DeleteServerOutOfBand();

        var check = await lifecycle.CheckAsync(fixture.Settings, fixture.DatabasePath);

        check.Worker!.Status.Should().Be("server-missing-primary-ip-retained");
        check.Worker.Name.Should().Be("video-optimiser-test");
        check.HourlyGross.Should().BeNull();
        check.NotCheckedReason.Should().BeNull();
        File.Exists(fixture.DatabasePath + ".hetzner-worker.json").Should().BeTrue("a status read leaves the record for worker delete");
    }

    [Fact]
    public async Task CheckFindsNoWorkerWhenTheRecordIsStaleAndNothingBills()
    {
        using var fixture = new Fixture();
        using var lifecycle = fixture.CreateLifecycle();
        await lifecycle.EnsureReadyAsync(fixture.Settings, fixture.DatabasePath);
        fixture.Handler.DeleteServerAndIpOutOfBand();

        var check = await lifecycle.CheckAsync(fixture.Settings, fixture.DatabasePath);

        check.Should().Be(new WorkerCheck(null, null, null));
        File.Exists(fixture.DatabasePath + ".hetzner-worker.json").Should().BeTrue("the check deletes nothing");
    }

    [Fact]
    public async Task CheckFindsTheLabelledServerWithoutAStateFileAndWritesNone()
    {
        using var fixture = new Fixture();
        using var lifecycle = fixture.CreateLifecycle();
        await lifecycle.EnsureReadyAsync(fixture.Settings, fixture.DatabasePath);
        File.Delete(fixture.DatabasePath + ".hetzner-worker.json");

        var check = await lifecycle.CheckAsync(fixture.Settings, fixture.DatabasePath);

        check.Worker!.ServerId.Should().Be(42);
        check.Worker.Status.Should().Be("running");
        check.Worker.ServerType.Should().Be("cx43");
        check.Worker.Location.Should().Be("hel1");
        check.HourlyGross.Should().Be(0.031m);
        File.Exists(fixture.DatabasePath + ".hetzner-worker.json").Should().BeFalse();
    }

    [Fact]
    public async Task CheckFindsNoWorkerWhenHetznerHasNoLabelledServerAndNoStateIsRecorded()
    {
        using var fixture = new Fixture();
        using var lifecycle = fixture.CreateLifecycle();

        (await lifecycle.CheckAsync(fixture.Settings, fixture.DatabasePath)).Should().Be(new WorkerCheck(null, null, null));
    }

    [Fact]
    public async Task CheckWithoutATokenShowsTheRecordedWorkerAndWhyHetznerWasNotAsked()
    {
        using var fixture = new Fixture();
        using var lifecycle = fixture.CreateLifecycle();
        await lifecycle.EnsureReadyAsync(fixture.Settings, fixture.DatabasePath);
        fixture.RemoveToken();
        var requestsBefore = fixture.Handler.RequestCount;

        var check = await lifecycle.CheckAsync(fixture.Settings, fixture.DatabasePath);

        check.Worker!.Status.Should().Be("recorded");
        check.Worker.ServerId.Should().Be(42);
        check.HourlyGross.Should().BeNull();
        check.NotCheckedReason.Should().Be("no API token is set");
        fixture.Handler.RequestCount.Should().Be(requestsBefore);
    }

    [Fact]
    public async Task CheckWhenHetznerFailsShowsTheRecordedWorkerAndTheMessage()
    {
        using var fixture = new Fixture();
        using var lifecycle = fixture.CreateLifecycle();
        await lifecycle.EnsureReadyAsync(fixture.Settings, fixture.DatabasePath);
        fixture.Handler.Failure = new HttpRequestException("No such host is known.");

        var check = await lifecycle.CheckAsync(fixture.Settings, fixture.DatabasePath);

        check.Worker!.Status.Should().Be("recorded");
        check.HourlyGross.Should().BeNull();
        check.NotCheckedReason.Should().Be("No such host is known.");
    }

    [Fact]
    public async Task CheckGivesUpOnAHetznerThatDoesNotAnswerWithinItsTimeout()
    {
        using var fixture = new Fixture();
        using var lifecycle = fixture.CreateLifecycle(checkTimeout: TimeSpan.FromMilliseconds(50));
        await lifecycle.EnsureReadyAsync(fixture.Settings, fixture.DatabasePath);
        fixture.Handler.Hang = true;

        var check = await lifecycle.CheckAsync(fixture.Settings, fixture.DatabasePath).WaitAsync(TimeSpan.FromSeconds(10));

        check.Worker!.Status.Should().Be("recorded");
        check.NotCheckedReason.Should().Be("Hetzner didn't answer within 15 seconds");
    }

    /// <summary>
    /// VOPT-142: HttpClient's own timeout is a <see cref="TimeoutException"/>, which every caller reads as a failure; read as
    /// an <see cref="OperationCanceledException"/>, the TUI and the CLI took it for the user stopping. A caller that did stop
    /// still gets its cancellation.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AHetznerThatDoesNotAnswerTimesOutUnlessTheCallerStopped(bool callerStopped)
    {
        using var fixture = new Fixture();
        fixture.Handler.Hang = true;
        using var lifecycle = fixture.CreateLifecycle(httpTimeout: callerStopped ? Timeout.InfiniteTimeSpan : TimeSpan.FromMilliseconds(50));
        using var stop = new CancellationTokenSource();
        if (callerStopped) await stop.CancelAsync();

        var action = () => lifecycle.ListCreationOptionsAsync(fixture.Settings, stop.Token);

        if (callerStopped)
            await action.Should().ThrowAsync<OperationCanceledException>();
        else
            (await action.Should().ThrowExactlyAsync<TimeoutException>()).Which.Message.Should().Be("Hetzner didn't answer GET server_types within 0.05 seconds.");
    }

    /// <summary>Should HttpClient's own timeout ever beat the check's, the status read still answers with a reason.</summary>
    [Fact]
    public async Task CheckTurnsAnHttpTimeoutIntoAReason()
    {
        using var fixture = new Fixture();
        using var lifecycle = fixture.CreateLifecycle(httpTimeout: TimeSpan.FromMilliseconds(50));
        await lifecycle.EnsureReadyAsync(fixture.Settings, fixture.DatabasePath);
        fixture.Handler.Hang = true;

        var check = await lifecycle.CheckAsync(fixture.Settings, fixture.DatabasePath).WaitAsync(TimeSpan.FromSeconds(10));

        check.Worker!.Status.Should().Be("recorded");
        check.NotCheckedReason.Should().StartWith("Hetzner didn't answer GET servers").And.EndWith("within 0.05 seconds.");
    }

    [Fact]
    public async Task CheckWithTwoLabelledServersSaysSoInsteadOfChoosing()
    {
        using var fixture = new Fixture();
        using var lifecycle = fixture.CreateLifecycle();
        await lifecycle.EnsureReadyAsync(fixture.Settings, fixture.DatabasePath);
        File.Delete(fixture.DatabasePath + ".hetzner-worker.json");
        fixture.Handler.ListTwoServers = true;

        var check = await lifecycle.CheckAsync(fixture.Settings, fixture.DatabasePath);

        check.Worker.Should().BeNull();
        check.NotCheckedReason.Should().StartWith("More than one Hetzner worker has this controller label");
    }

    [Fact]
    public async Task CheckHonoursTheCallersCancellation()
    {
        using var fixture = new Fixture();
        using var lifecycle = fixture.CreateLifecycle();
        await lifecycle.EnsureReadyAsync(fixture.Settings, fixture.DatabasePath);
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        var action = () => lifecycle.CheckAsync(fixture.Settings, fixture.DatabasePath, cancelled.Token);

        await action.Should().ThrowAsync<OperationCanceledException>();
    }

    private static readonly DateTimeOffset At = new(2026, 10, 17, 9, 0, 0, TimeSpan.Zero);

    /// <summary>The <c>created</c> the fake Hetzner gives every server.</summary>
    private static readonly DateTimeOffset HetznerCreated = new(2026, 8, 20, 0, 0, 0, TimeSpan.Zero);

    /// <summary>The server bills once it exists, so a pricing read that fails or lists no IPv4 there never fails the create: the bill takes the server's price alone.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ACreateWhoseIpv4PriceIsUnknownStillCreatesAndBillsTheServerPrice(bool pricingFails)
    {
        using var fixture = new Fixture();
        if (pricingFails) fixture.Handler.PricingFails = true;
        else fixture.Handler.Ipv4HourlyGross.Clear();
        using var lifecycle = fixture.CreateLifecycle(utcNow: new FakeClock(At).Now);

        var worker = await lifecycle.EnsureReadyAsync(fixture.Settings, fixture.DatabasePath);

        worker.ServerId.Should().Be(42);
        (await BillAsync(fixture)).HourlyGross.Should().Be(0.031m);
    }

    /// <summary>A server whose provisioning failed existed, so Hetzner bills its hour: its bill ends when the cleanup deleted it.</summary>
    [Fact]
    public async Task AFailedCreateEndsTheBillOfTheServerItCleanedUp()
    {
        using var fixture = new Fixture { AutoDeletePrimaryIpWithServer = false };
        fixture.Handler.ActionFailures.Enqueue(("resource_unavailable", "error during placement"));
        using var lifecycle = fixture.CreateLifecycle(utcNow: new FakeClock(At).Now);

        var action = () => lifecycle.CreateAsync(fixture.Settings, fixture.DatabasePath, new RemoteWorkerCreationOption("cx43", "hel1", 0.031m));

        await action.Should().ThrowAsync<RemoteWorkerCapacityUnavailableException>();
        (await BillAsync(fixture)).Should().Match<BillRow>(bill => bill.DeletedUtc == At && !bill.EndEstimated);
    }

    /// <summary>
    /// VOPT-139: a Ctrl+C while the create request is out must not cut off the answer naming the server Hetzner made. The stop
    /// lands once the server is recorded, so the run's end can decide its fate. The token is already cancelled when the state
    /// file is written, so this also catches that write going back onto it.
    /// </summary>
    [Fact]
    public async Task AStopDuringTheCreateRequestStopsOnlyOnceTheServerIsRecorded()
    {
        using var fixture = new Fixture();
        fixture.Handler.CreateGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var lifecycle = fixture.CreateLifecycle(utcNow: new FakeClock(At).Now);
        using var stop = new CancellationTokenSource();

        var ensuring = lifecycle.EnsureReadyAsync(fixture.Settings, fixture.DatabasePath, cancellationToken: stop.Token);
        await fixture.Handler.CreateArrived.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await stop.CancelAsync();
        fixture.Handler.CreateGate.SetResult();

        await ((Func<Task>)(() => ensuring)).Should().ThrowAsync<OperationCanceledException>();
        (await lifecycle.ReadRecordedAsync(fixture.DatabasePath))!.ServerId.Should().Be(42);
        (await BillAsync(fixture)).ServerId.Should().Be(42);
        fixture.Processes.Requests.Should().BeEmpty("the stop lands before the worker's setup starts");
    }

    /// <summary>
    /// VOPT-139: a create whose answer was lost may still have made the server. Found by its label, it is recorded as a later
    /// resolve would record it, and the create's own failure still ends the call. Found absent, nothing is recorded. When
    /// Hetzner can't be asked, nothing records the server, so the message says how to look for it and delete it.
    /// </summary>
    /// <remarks>The timed-out row is HttpClient's own timeout firing on the create (VOPT-142), not a stand-in exception.</remarks>
    [Theory]
    [InlineData(true, false, false)]
    [InlineData(false, false, false)]
    [InlineData(true, true, false)]
    [InlineData(true, false, true)]
    public async Task ACreateWhoseAnswerIsLostRecordsTheServerItFindsByLabel(bool serverMade, bool lookupFails, bool timedOut)
    {
        using var fixture = new Fixture();
        var lost = new HttpRequestException("The response ended prematurely.");
        if (timedOut) fixture.Handler.HangCreate = true;
        else fixture.Handler.LostCreateAnswer = lost;
        fixture.Handler.LostCreateMadeServer = serverMade;
        fixture.Handler.ListingFailsAfterCreate = lookupFails;
        using var lifecycle = fixture.CreateLifecycle(utcNow: new FakeClock(At).Now, httpTimeout: timedOut ? TimeSpan.FromMilliseconds(200) : null);

        var action = () => lifecycle.EnsureReadyAsync(fixture.Settings, fixture.DatabasePath);

        var thrown = (await action.Should().ThrowAsync<Exception>()).Which;
        if (lookupFails)
            thrown.Should().BeOfType<RemoteWorkerCreationUnconfirmedException>().Which.Message.Should().Contain("worker status").And.Contain("worker delete");
        else if (timedOut)
            thrown.Should().BeOfType<TimeoutException>().Which.Message.Should().Be("Hetzner didn't answer POST servers within 0.2 seconds.");
        else
            thrown.Should().BeSameAs(lost);
        var recorded = serverMade && !lookupFails;
        File.Exists(fixture.DatabasePath + ".hetzner-worker.json").Should().Be(recorded);
        if (recorded)
        {
            (await lifecycle.ReadRecordedAsync(fixture.DatabasePath))!.ServerId.Should().Be(42);
            (await BillAsync(fixture)).ServerId.Should().Be(42);
        }
        fixture.Handler.CreateCount.Should().Be(1, "a lost answer is never retried as another create");
    }

    /// <summary>
    /// <c>worker delete</c> ends the bill when Hetzner confirms the deletion. A worker already deleted in the Hetzner console
    /// ends at the last time it was seen up, marked as an estimate, since when it went is unknown.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DeletingAWorkerEndsItsBill(bool alreadyGone)
    {
        using var fixture = new Fixture();
        var clock = new FakeClock(At);
        using var lifecycle = fixture.CreateLifecycle(utcNow: clock.Now);
        await lifecycle.EnsureReadyAsync(fixture.Settings, fixture.DatabasePath);
        clock.Advance(TimeSpan.FromHours(2));
        if (alreadyGone) fixture.Handler.DeleteServerOutOfBand();

        await lifecycle.DeleteAsync(fixture.Settings, fixture.DatabasePath);

        var bill = await BillAsync(fixture);
        (bill.DeletedUtc, bill.EndEstimated).Should().Be(alreadyGone ? (At, true) : (At.AddHours(2), false));
    }

    /// <summary>
    /// A create starts the worker's bill at what Hetzner charges: the server type in its location plus the Primary IPv4 there,
    /// priced apart. Reusing the live worker moves the bill's last-seen time; one with no bill (created before bills were
    /// kept) gets one from what Hetzner says now, with its own creation time and the same summed price.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReusingALiveWorkerMovesItsLastSeenTimeAndBackfillsAMissingBill(bool billMissing)
    {
        using var fixture = new Fixture();
        var clock = new FakeClock(At);
        using var lifecycle = fixture.CreateLifecycle(utcNow: clock.Now);
        await lifecycle.EnsureReadyAsync(fixture.Settings, fixture.DatabasePath);
        if (billMissing) await RecordRows.ExecuteAsync(fixture.DatabasePath, "DELETE FROM workers;");
        clock.Advance(TimeSpan.FromMinutes(30));

        await lifecycle.EnsureReadyAsync(fixture.Settings, fixture.DatabasePath);

        (await BillAsync(fixture)).Should().Be(new BillRow(42, "cx43", fixture.Handler.CreateLocations[^1], 0.032m, HetznerCreated, null, false, At.AddMinutes(30)));
    }

    private sealed record BillRow(long ServerId, string ServerType, string Location, decimal? HourlyGross, DateTimeOffset CreatedUtc, DateTimeOffset? DeletedUtc, bool EndEstimated, DateTimeOffset LastSeenUtc);

    private static async Task<BillRow> BillAsync(Fixture fixture)
    {
        var row = (await RecordRows.QueryAsync(fixture.DatabasePath, "SELECT server_id, server_type, location, hourly_gross, created_utc, deleted_utc, end_estimated, last_seen_utc FROM workers;")).Should().ContainSingle().Subject;
        static DateTimeOffset Time(object? value) => DateTimeOffset.Parse((string)value!, System.Globalization.CultureInfo.InvariantCulture);
        return new BillRow(
            (long)row[0]!, (string)row[1]!, (string)row[2]!,
            row[3] is string price ? decimal.Parse(price, System.Globalization.CultureInfo.InvariantCulture) : null,
            Time(row[4]), row[5] is null ? null : Time(row[5]), (long)row[6]! == 1, Time(row[7]));
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "video-optimiser-tests", Guid.NewGuid().ToString("N"));
        private readonly string _tokenVariable = "VIDEO_OPTIMISER_TEST_TOKEN_" + Guid.NewGuid().ToString("N");

        public Fixture()
        {
            Directory.CreateDirectory(_directory);
            DatabasePath = Path.Combine(_directory, "jobs.db");
            var bootstrap = Path.Combine(_directory, "bootstrap.sh");
            File.WriteAllText(bootstrap, "#!/usr/bin/env bash\ntouch /opt/video-optimiser/.bootstrap-complete\n");
            var identity = Path.Combine(_directory, "worker-key");
            File.WriteAllText(identity, "test");
            Settings = new AppSettings
            {
                Processing = new ProcessingSettings
                {
                    Mode = ProcessingModes.RemoteSsh,
                    RemoteSsh = new RemoteSshSettings
                    {
                        Lifecycle = RemoteLifecycleModes.Hetzner,
                        IdentityFile = identity,
                        Hetzner = new HetznerSettings
                        {
                            ApiTokenEnvironmentVariable = _tokenVariable,
                            SshKeyName = "video-optimiser-hetzner-cx43",
                            BootstrapScriptPath = bootstrap,
                            ServerTypes = ["cx43"]
                        }
                    }
                }
            };
            Environment.SetEnvironmentVariable(_tokenVariable, "secret-test-token");
        }

        public string DatabasePath { get; }
        public AppSettings Settings { get; }
        public FakeHetznerHandler Handler { get; } = new();
        public RecordingProcessRunner Processes { get; } = new();
        public bool AutoDeletePrimaryIpWithServer { set => Handler.AutoDeletePrimaryIpWithServer = value; }

        public void RemoveToken() => Environment.SetEnvironmentVariable(_tokenVariable, null);

        public void UseDotEnvToken()
        {
            Environment.SetEnvironmentVariable(_tokenVariable, null);
            var path = Path.Combine(_directory, ".env");
            File.WriteAllText(path, $"{_tokenVariable}=secret-test-token\n");
            Settings.Processing.RemoteSsh.Hetzner.ApiTokenFile = path;
        }

        public HetznerRemoteWorkerLifecycle CreateLifecycle(
            TimeSpan? readinessProbeTimeout = null,
            TimeSpan? progressHeartbeatInterval = null,
            Func<DateTimeOffset>? utcNow = null,
            Func<TimeSpan, CancellationToken, Task>? delay = null,
            TimeSpan? checkTimeout = null,
            TimeSpan? httpTimeout = null) => new(
            Processes,
            new SqliteWorkerBillStore(new SqliteDatabaseInitializer()),
            new CommandHints(null),
            new HttpClient(Handler) { BaseAddress = new Uri("https://api.test/v1/"), Timeout = httpTimeout ?? TimeSpan.FromSeconds(100) },
            delay ?? ((_, _) => Task.CompletedTask),
            ownsHttpClient: true,
            readinessProbeTimeout: readinessProbeTimeout,
            progressHeartbeatInterval: progressHeartbeatInterval,
            utcNow: utcNow,
            checkTimeout: checkTimeout);

        public void Dispose()
        {
            Environment.SetEnvironmentVariable(_tokenVariable, null);
            Directory.Delete(_directory, recursive: true);
        }
    }

    private sealed class RecordingProcessRunner : IExternalProcessRunner
    {
        public List<ExternalProcessRequest> Requests { get; } = [];
        public Queue<ExternalProcessResult> Results { get; } = new();
        public int BlockingAttempts { get; set; }
        public int CancelledRequests { get; private set; }

        public Task<ExternalProcessResult> RunAsync(ExternalProcessRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            if (BlockingAttempts > 0)
            {
                BlockingAttempts--;
                var completion = new TaskCompletionSource<ExternalProcessResult>(TaskCreationOptions.RunContinuationsAsynchronously);
                cancellationToken.Register(() =>
                {
                    CancelledRequests++;
                    completion.TrySetCanceled(cancellationToken);
                });
                return completion.Task;
            }

            if (Results.TryDequeue(out var result)) return Task.FromResult(result);
            return Task.FromResult(new ExternalProcessResult(0, "percent=100\nstage=Ready\n", string.Empty));
        }
    }

    private sealed class SynchronousProgress(Action<string> callback) : IProgress<string>
    {
        public void Report(string value) => callback(value);
    }

    private sealed class FakeClock(DateTimeOffset initial)
    {
        private DateTimeOffset _now = initial;

        public DateTimeOffset Now() => _now;

        public void Advance(TimeSpan amount) => _now += amount;
    }

    private sealed class FakeServerType(string name, string architecture, int cores, decimal memory, decimal hourlyGross)
    {
        public bool Deprecated { get; set; }
        public Dictionary<string, (bool Available, bool Recommended)> Locations { get; } = new(StringComparer.OrdinalIgnoreCase)
        {
            ["hel1"] = (true, false),
            ["fsn1"] = (true, false),
            ["nbg1"] = (true, false)
        };
        public Dictionary<string, decimal> PriceOverrides { get; } = new(StringComparer.OrdinalIgnoreCase);

        public void SoldOut()
        {
            foreach (var location in Locations.Keys.ToArray()) Locations[location] = (false, false);
        }

        public string PricesJson() => string.Join(",", Locations.Keys.Select(location =>
        {
            var gross = PriceOverrides.GetValueOrDefault(location, hourlyGross).ToString("0.0000000000000000", System.Globalization.CultureInfo.InvariantCulture);
            return $"{{\"location\":\"{location}\",\"price_hourly\":{{\"gross\":\"{gross}\",\"net\":\"0\"}}}}";
        }));

        public string ToJson()
        {
            static string Bool(bool value) => value ? "true" : "false";
            var locations = string.Join(",", Locations.Select(item => $"{{\"name\":\"{item.Key}\",\"available\":{Bool(item.Value.Available)},\"recommended\":{Bool(item.Value.Recommended)},\"deprecation\":null}}"));
            var prices = PricesJson();
            return $"{{\"name\":\"{name}\",\"architecture\":\"{architecture}\",\"cores\":{cores},\"memory\":{memory.ToString(System.Globalization.CultureInfo.InvariantCulture)},\"deprecated\":{Bool(Deprecated)},\"deprecation\":null,\"locations\":[{locations}],\"prices\":[{prices}]}}";
        }
    }

    private sealed class FakeHetznerHandler : HttpMessageHandler
    {
        public bool ServerExists { get; private set; }
        public bool PrimaryIpExists { get; private set; }
        public bool PrimaryIpDeleted { get; private set; }
        public bool AutoDeleteUpdated { get; private set; }
        public bool AutoDeletePrimaryIpWithServer { get; set; } = true;
        public bool ReturnWrongOwnership { get; set; }
        public string? ServerTypePreflightResponse { get; set; }
        public Queue<(string Code, string Message)> ActionFailures { get; } = new();
        public bool SecondCreateObservedCleanResources { get; private set; }
        private (string Code, string Message)? _pendingActionFailure;
        public JsonDocument? CreatePayload { get; set; }
        public int CreateCount { get; private set; }
        // Mirrors the live catalogue's shape on 2026-10-12: gross EUR per hour, memory in GB (GiB).
        public Dictionary<string, FakeServerType> Catalogue { get; } = new(StringComparer.OrdinalIgnoreCase)
        {
            ["cx23"] = new("cx23", "x86", 2, 4, 0.006m),
            ["cx43"] = new("cx43", "x86", 8, 16, 0.031m),
            ["cx53"] = new("cx53", "x86", 16, 32, 0.057m),
            ["cpx42"] = new("cpx42", "x86", 8, 16, 0.134m),
            ["cpx62"] = new("cpx62", "x86", 16, 32, 0.250m),
            ["ccx33"] = new("ccx33", "x86", 8, 32, 0.266m),
            ["ccx43"] = new("ccx43", "x86", 16, 64, 0.533m),
            ["cax31"] = new("cax31", "arm", 8, 16, 0.019m)
        };
        public Dictionary<string, (bool Available, bool Recommended)> LocationAvailability => Catalogue["cx43"].Locations;
        public int? ServerTypePageSize { get; set; }
        public int ServerTypeRequests { get; private set; }
        public Queue<(HttpStatusCode Status, string Code, string Message)> CreateFailures { get; } = new();
        /// <summary>A raw body for a 400 answer to the create request, for error bodies <see cref="Error"/> cannot write.</summary>
        public string? CreateErrorBody { get; set; }
        public List<string> CreateLocations { get; } = [];
        public List<string> CreateServerTypes { get; } = [];

        public void DeleteServerOutOfBand() => ServerExists = false;

        public void DeleteServerAndIpOutOfBand() => (ServerExists, PrimaryIpExists) = (false, false);

        /// <summary>Thrown for every request, as a network failure would be.</summary>
        public Exception? Failure { get; set; }

        /// <summary>Never answers, until the request is cancelled.</summary>
        public bool Hang { get; set; }

        /// <summary>Answers the server listing with two servers carrying this controller's labels.</summary>
        public bool ListTwoServers { get; set; }

        /// <summary>Leaves the <c>prices</c> array out of a server's <c>server_type</c>, as an older API answer might.</summary>
        public bool OmitServerPrices { get; set; }

        /// <summary>Names a server's location under <c>datacenter</c>, as Hetzner did before it moved it to the top level.</summary>
        public bool LocationUnderDatacenter { get; set; }

        public int RequestCount { get; private set; }
        public int DeleteRequests { get; private set; }

        /// <summary>The Primary IPv4's hourly gross per location that <c>GET /pricing</c> lists; a location left out has no IPv4 price.</summary>
        public Dictionary<string, decimal> Ipv4HourlyGross { get; } = new(StringComparer.OrdinalIgnoreCase) { ["hel1"] = 0.001m, ["fsn1"] = 0.001m, ["nbg1"] = 0.001m };

        /// <summary>Answers <c>GET /pricing</c> with a server error.</summary>
        public bool PricingFails { get; set; }

        /// <summary>
        /// When set, the create request is held until it completes or the request's own token is cancelled; <see cref="CreateArrived"/>
        /// completes once the request is in.
        /// </summary>
        public TaskCompletionSource? CreateGate { get; set; }

        public TaskCompletionSource CreateArrived { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>
        /// The create request throws this instead of answering, as a timeout or a dropped connection would: after Hetzner made the
        /// server when <see cref="LostCreateMadeServer"/>, before it got the request otherwise.
        /// </summary>
        public Exception? LostCreateAnswer { get; set; }

        public bool LostCreateMadeServer { get; set; }

        /// <summary>The create request never answers, until HttpClient's own timeout cancels it; it honours <see cref="LostCreateMadeServer"/>.</summary>
        public bool HangCreate { get; set; }

        /// <summary>The server listing throws, as a network failure would, once a create was requested.</summary>
        public bool ListingFailsAfterCreate { get; set; }

        /// <summary>The live answer's shape (Hetzner's API reference, 2026-10-17), with an IPv6 entry first so the type is checked.</summary>
        private string PricingJson()
        {
            static string Price(string location, decimal gross) =>
                $"{{\"location\":\"{location}\",\"price_hourly\":{{\"net\":\"0.0000\",\"gross\":\"{gross.ToString(System.Globalization.CultureInfo.InvariantCulture)}\"}},\"price_monthly\":{{\"net\":\"0.0000\",\"gross\":\"0.0000\"}}}}";
            var ipv4 = string.Join(",", Ipv4HourlyGross.Select(item => Price(item.Key, item.Value)));
            return $"{{\"pricing\":{{\"currency\":\"EUR\",\"vat_rate\":\"19.00\",\"primary_ips\":[{{\"type\":\"ipv6\",\"prices\":[{Price("hel1", 0.5m)}]}},{{\"type\":\"ipv4\",\"prices\":[{ipv4}]}}]}}}}";
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            if (Failure is not null) throw Failure;
            if (Hang) await Task.Delay(Timeout.Infinite, cancellationToken);
            if (request.Method == HttpMethod.Delete) DeleteRequests++;
            request.Headers.Authorization!.Scheme.Should().Be("Bearer");
            request.Headers.Authorization.Parameter.Should().Be("secret-test-token");
            var path = request.RequestUri!.PathAndQuery;
            if (request.Method == HttpMethod.Get && path.StartsWith("/v1/server_types?", StringComparison.Ordinal))
            {
                ServerTypeRequests++;
                if (ServerTypePreflightResponse is not null) return Json(ServerTypePreflightResponse);
                var page = int.Parse(System.Web.HttpUtility.ParseQueryString(request.RequestUri.Query)["page"] ?? "1", System.Globalization.CultureInfo.InvariantCulture);
                var size = ServerTypePageSize ?? Catalogue.Count;
                var entries = Catalogue.Values.Skip((page - 1) * size).Take(size).Select(type => type.ToJson());
                var next = page * size < Catalogue.Count ? (page + 1).ToString(System.Globalization.CultureInfo.InvariantCulture) : "null";
                return Json($"{{\"server_types\":[{string.Join(",", entries)}],\"meta\":{{\"pagination\":{{\"page\":{page},\"next_page\":{next}}}}}}}");
            }
            if (request.Method == HttpMethod.Get && path.StartsWith("/v1/servers?", StringComparison.Ordinal))
            {
                if (ListingFailsAfterCreate && CreateCount > 0) throw new HttpRequestException("No such host is known.");
                var servers = ReturnWrongOwnership ? $"[{ServerJson(wrongOwnership: true)}]"
                    : ListTwoServers ? $"[{ServerJson()},{ServerJson()}]"
                    : ServerExists ? $"[{ServerJson()}]" : "[]";
                return Json($"{{\"servers\":{servers}}}");
            }
            if (request.Method == HttpMethod.Get && path == "/v1/pricing")
                return PricingFails ? Error(HttpStatusCode.InternalServerError, "server_error", "pricing is down") : Json(PricingJson());
            if (request.Method == HttpMethod.Get && path.StartsWith("/v1/primary_ips?", StringComparison.Ordinal))
                return Json(PrimaryIpExists ? "{\"primary_ips\":[{\"id\":99}]}" : "{\"primary_ips\":[]}");
            if (request.Method == HttpMethod.Post && path == "/v1/servers")
            {
                CreatePayload = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
                if (CreateCount == 1) SecondCreateObservedCleanResources = !ServerExists && !PrimaryIpExists;
                CreateCount++;
                CreateLocations.Add(CreatePayload.RootElement.GetProperty("location").GetString()!);
                CreateServerTypes.Add(CreatePayload.RootElement.GetProperty("server_type").GetString()!);
                CreateArrived.TrySetResult();
                if (CreateGate is not null) await CreateGate.Task.WaitAsync(cancellationToken);
                if (LostCreateAnswer is not null || HangCreate)
                {
                    if (LostCreateMadeServer) (ServerExists, PrimaryIpExists) = (true, true);
                    if (HangCreate) await Task.Delay(Timeout.Infinite, cancellationToken);
                    throw LostCreateAnswer!;
                }
                if (CreateErrorBody is not null)
                    return new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent(CreateErrorBody, Encoding.UTF8, "application/json") };
                if (CreateFailures.TryDequeue(out var failure))
                    return Error(failure.Status, failure.Code, failure.Message);
                ServerExists = true;
                PrimaryIpExists = true;
                if (ActionFailures.TryDequeue(out var actionFailure))
                {
                    _pendingActionFailure = actionFailure;
                    return Json($"{{\"server\":{ServerJson()},\"action\":{{\"id\":12}}}}");
                }
                return Json($"{{\"server\":{ServerJson()},\"action\":{{\"id\":10}}}}");
            }
            if (request.Method == HttpMethod.Put && path == "/v1/primary_ips/99")
            {
                AutoDeleteUpdated = true;
                return Json("{\"primary_ip\":{\"id\":99}}");
            }
            if (request.Method == HttpMethod.Get && path == "/v1/actions/12" && _pendingActionFailure is { } pendingFailure)
                return Json($"{{\"action\":{{\"status\":\"error\",\"error\":{{\"code\":\"{pendingFailure.Code}\",\"message\":\"{pendingFailure.Message}\"}}}}}}");
            if (request.Method == HttpMethod.Get && path is "/v1/actions/10" or "/v1/actions/11")
                return Json("{\"action\":{\"status\":\"success\"}}");
            if (request.Method == HttpMethod.Get && path == "/v1/servers/42")
                return ServerExists ? Json($"{{\"server\":{ServerJson(wrongOwnership: ReturnWrongOwnership)}}}") : new HttpResponseMessage(HttpStatusCode.NotFound);
            if (request.Method == HttpMethod.Delete && path == "/v1/servers/42")
            {
                ServerExists = false;
                if (AutoDeletePrimaryIpWithServer) PrimaryIpExists = false;
                return Json("{\"action\":{\"id\":11}}");
            }
            if (request.Method == HttpMethod.Get && path == "/v1/primary_ips/99")
                return PrimaryIpExists ? Json("{\"primary_ip\":{\"id\":99}}") : new HttpResponseMessage(HttpStatusCode.NotFound);
            if (request.Method == HttpMethod.Delete && path == "/v1/primary_ips/99")
            {
                PrimaryIpExists = false;
                PrimaryIpDeleted = true;
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }
            throw new InvalidOperationException($"Unexpected request: {request.Method} {path}");
        }

        private string ServerJson(bool wrongOwnership = false)
        {
            var labels = wrongOwnership
                ? "\"video-optimiser-managed\":\"true\",\"video-optimiser-controller\":\"someone-else\""
                : CreatePayload?.RootElement.GetProperty("labels").EnumerateObject().Select(item => $"\"{item.Name}\":\"{item.Value.GetString()}\"").Aggregate((left, right) => left + "," + right)
                  ?? "\"video-optimiser-managed\":\"true\",\"video-optimiser-controller\":\"placeholder\"";
            var placement = CreatePayload is null
                ? string.Empty
                : $",\"server_type\":{{\"name\":\"{CreateServerTypes[^1]}\"{(OmitServerPrices ? string.Empty : $",\"prices\":[{Catalogue[CreateServerTypes[^1]].PricesJson()}]")}}}," + (LocationUnderDatacenter
                    ? $"\"datacenter\":{{\"location\":{{\"name\":\"{CreateLocations[^1]}\"}}}}"
                    : $"\"location\":{{\"name\":\"{CreateLocations[^1]}\"}}");
            return $"{{\"id\":42,\"name\":\"video-optimiser-test\",\"status\":\"running\",\"created\":\"2026-08-20T00:00:00Z\",\"labels\":{{{labels}}},\"public_net\":{{\"ipv4\":{{\"id\":99,\"ip\":\"203.0.113.42\"}}}}{placement}}}";
        }

        private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

        private static HttpResponseMessage Error(HttpStatusCode status, string code, string message) => new(status)
        {
            Content = new StringContent($"{{\"error\":{{\"code\":\"{code}\",\"message\":\"{message}\"}}}}", Encoding.UTF8, "application/json")
        };
    }
}
