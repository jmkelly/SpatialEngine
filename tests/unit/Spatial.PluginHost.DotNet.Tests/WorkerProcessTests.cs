using Spatial.Plugin.Fixtures;
using Spatial.PluginHost.DotNet.Manifest;
using Spatial.PluginHost.DotNet.Protocol;
using Spatial.PluginSdk.Capabilities;

namespace Spatial.PluginHost.DotNet.Tests;

/// <summary>
/// Phase 5 separate-process workers (plan §16, Epic F): the worker host is a
/// real child process — this suite drives it over the wire protocol and
/// verifies the handshake, health, invocation, progress, cancellation,
/// deadline/timeout, crash and drain behaviours end to end, plus the manifest
/// compatibility gate that refuses to activate a diverged package.
/// </summary>
public sealed class WorkerProcessTests : IAsyncDisposable
{
    /// <summary>
    /// Upper bound for worker handshakes, responses and clean exits. Generous on
    /// purpose: the whole solution runs in parallel and several of these tests
    /// spawn child processes at once, so a 5s response budget raced worker
    /// startup under load. The timeout is a hang guard, not an assertion.
    /// </summary>
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(30);

    private readonly List<WorkerRig> _rigs = [];

    public async ValueTask DisposeAsync()
    {
        foreach (var rig in _rigs)
        {
            await rig.DisposeAsync();
        }
    }

    [Fact]
    public async Task Hello_reports_the_validated_package()
    {
        await using var rig = Start(FixtureManifest.V1());

        var hello = await rig.Hello.WaitAsync(TestTimeout);
        Assert.Equal("fixture@1", hello.Id);
        Assert.Equal("dotnet", hello.Runtime);
        Assert.Equal(FixtureManifest.AssemblyName, hello.Assembly);
        Assert.Equal(FixtureManifest.V1ProviderType, hello.AssemblyType);
        Assert.Equal(6, hello.Capabilities.Count);
        Assert.Contains("spatial.fixture.crash@1", hello.Capabilities);
    }

    [Fact]
    public async Task Health_ping_round_trips()
    {
        await using var rig = Start(FixtureManifest.V1());
        await rig.Hello.WaitAsync(TestTimeout);

        var response = await rig.Channel.RequestAsync(
            WorkerProtocol.Ping,
            new System.Text.Json.Nodes.JsonObject { ["nonce"] = "n1" },
            WorkerProtocol.Pong,
            TestTimeout);
        Assert.Equal("n1", response!["nonce"]!.GetValue<string>());
    }

    [Fact]
    public async Task Peek_invocation_returns_the_provider_id()
    {
        await using var rig = Start(FixtureManifest.V1());
        await rig.Hello.WaitAsync(TestTimeout);

        var outcome = await rig.InvokeAsync(FixtureProviderV1.PeekCapability, timeout: TestTimeout);
        Assert.Equal(WorkerOutcomeKind.Success, outcome.Kind);
        Assert.Equal("fixture@1", outcome.Value);
    }

    [Fact]
    public async Task Sleep_reports_progress_and_succeeds()
    {
        await using var rig = Start(FixtureManifest.V1());
        await rig.Hello.WaitAsync(TestTimeout);

        var outcome = await rig.InvokeAsync(
            FixtureProviderV1.SleepCapability, new Dictionary<string, object?> { ["milliseconds"] = 80L }, timeout: TestTimeout);

        Assert.Equal(WorkerOutcomeKind.Success, outcome.Kind);
        Assert.Equal(80L, outcome.Value);
        Assert.True(rig.Progress.Count > 0, "a long-running invoke must report progress");
        Assert.All(rig.Progress, sample => Assert.NotNull(sample.Fraction));
    }

    [Fact]
    public async Task Cancel_mid_invoke_yields_cancelled()
    {
        await using var rig = Start(FixtureManifest.V1());
        await rig.Hello.WaitAsync(TestTimeout);

        var invokeId = Guid.NewGuid().ToString("N");
        var payload = WorkerPayload.Invoke(
            FixtureProviderV1.SleepCapability.ToString(),
            new Dictionary<string, object?> { ["milliseconds"] = 5000L },
            [],
            null);
        var pending = rig.Channel.RequestAsync(
            WorkerProtocol.Invoke, payload, WorkerProtocol.Result, timeout: null, id: invokeId);

        // Wait until the worker reports progress, then cancel the invocation.
        var deadline = DateTimeOffset.UtcNow + TestTimeout;
        while (rig.Progress.Count == 0 && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(20);
        }

        Assert.NotEmpty(rig.Progress);
        await rig.Channel.SendAsync(WorkerProtocol.Cancel, null, invokeId);

        var outcome = WorkerPayload.ReadOutcome(await pending.AsTask().WaitAsync(TestTimeout));
        Assert.Equal(WorkerOutcomeKind.Failure, outcome.Kind);
        Assert.Equal(CapabilityErrorKind.Cancelled, outcome.Error!.Kind);
    }

    [Fact]
    public async Task Deadline_turns_a_far_running_invoke_into_a_timeout()
    {
        await using var rig = Start(FixtureManifest.V1());
        await rig.Hello.WaitAsync(TestTimeout);

        var outcome = await rig.InvokeAsync(
            FixtureProviderV1.TimeoutCapability,
            new Dictionary<string, object?> { ["milliseconds"] = 60000L },
            deadline: DateTimeOffset.UtcNow + TimeSpan.FromMilliseconds(350),
            timeout: TestTimeout);

        Assert.Equal(WorkerOutcomeKind.Failure, outcome.Kind);
        Assert.Equal(CapabilityErrorKind.DeadlineExceeded, outcome.Error!.Kind);
        Assert.Equal("deadline.exceeded", outcome.Error.Code);
    }

    [Fact]
    public async Task Crash_kills_the_process_and_disconnects_the_invocation()
    {
        await using var rig = Start(FixtureManifest.V1());
        await rig.Hello.WaitAsync(TestTimeout);

        var exception = await Assert.ThrowsAsync<WorkerDisconnectedException>(() =>
            rig.InvokeAsync(FixtureProviderV1.CrashCapability, timeout: TestTimeout));
        Assert.Contains("disconnected", exception.Message);
        await rig.WaitForExitAsync(TestTimeout);
        Assert.NotEqual(0, await rig.ExitCode);
        Assert.False(rig.Channel.IsConnected);
    }

    [Fact]
    public async Task Close_drains_inflight_work_and_exits_zero()
    {
        await using var rig = Start(FixtureManifest.V1());
        await rig.Hello.WaitAsync(TestTimeout);

        var pending = rig.InvokeAsync(
            FixtureProviderV1.SleepCapability, new Dictionary<string, object?> { ["milliseconds"] = 120L }, timeout: null);
        await rig.Channel.SendAsync(WorkerProtocol.Close, new System.Text.Json.Nodes.JsonObject { ["reason"] = "draining" });

        var outcome = await pending.WaitAsync(TestTimeout);
        Assert.Equal(WorkerOutcomeKind.Success, outcome.Kind);
        Assert.Equal(0, await rig.Closed.WaitAsync(TestTimeout));
        await rig.WaitForExitAsync(TestTimeout);
        Assert.Equal(0, await rig.ExitCode);
    }

    [Fact]
    public async Task Malformed_invoke_is_a_contract_violation()
    {
        await using var rig = Start(FixtureManifest.V1());
        await rig.Hello.WaitAsync(TestTimeout);

        var payload = new System.Text.Json.Nodes.JsonObject { ["arguments"] = new System.Text.Json.Nodes.JsonObject() };
        var response = await rig.Channel.RequestAsync(WorkerProtocol.Invoke, payload, WorkerProtocol.Result, TestTimeout);
        var outcome = WorkerPayload.ReadOutcome(response);

        Assert.Equal(WorkerOutcomeKind.Failure, outcome.Kind);
        Assert.Equal(CapabilityErrorKind.ContractViolation, outcome.Error!.Kind);
        Assert.Contains("capability", outcome.Error.Message);

        // Drain cleanly: a killed worker's coverlet results are never flushed.
        await rig.Channel.SendAsync(WorkerProtocol.Close, new System.Text.Json.Nodes.JsonObject { ["reason"] = "test done" });
        Assert.Equal(0, await rig.Closed.WaitAsync(TestTimeout));
        await rig.WaitForExitAsync(TestTimeout);
        Assert.Equal(0, await rig.ExitCode);
    }

    [Fact]
    public async Task Untagged_object_arguments_are_rejected_with_the_inline_protocol_hint()
    {
        await using var rig = Start(FixtureManifest.V1());
        await rig.Hello.WaitAsync(TestTimeout);

        // Send the wire payload directly (bypassing the sender's codec) so the
        // *worker* decodes an untagged object and must reject it.
        var payload = new System.Text.Json.Nodes.JsonObject
        {
            ["capability"] = FixtureProviderV1.PeekCapability.ToString(),
            ["permissions"] = new System.Text.Json.Nodes.JsonArray(),
            ["arguments"] = new System.Text.Json.Nodes.JsonObject { ["unexpected"] = new System.Text.Json.Nodes.JsonObject { ["x"] = 1 } },
        };
        var response = await rig.Channel.RequestAsync(WorkerProtocol.Invoke, payload, WorkerProtocol.Result, TestTimeout);
        var outcome = WorkerPayload.ReadOutcome(response);

        Assert.Equal(WorkerOutcomeKind.Failure, outcome.Kind);
        Assert.Equal(CapabilityErrorKind.ContractViolation, outcome.Error!.Kind);
        Assert.Contains("argument 'unexpected'", outcome.Error.Message);
    }

    [Fact]
    public async Task An_invoke_without_an_id_is_a_protocol_error()
    {
        await using var rig = Start(FixtureManifest.V1());
        await rig.Hello.WaitAsync(TestTimeout);

        var payload = WorkerPayload.Invoke(
            FixtureProviderV1.PeekCapability.ToString(),
            new Dictionary<string, object?>(),
            [],
            null);
        await rig.Channel.SendAsync(WorkerProtocol.Invoke, payload);

        var message = await rig.ProtocolError.WaitAsync(TestTimeout);
        Assert.Contains("must carry an id", message);

        // Drain cleanly: a killed worker's coverlet results are never flushed.
        await rig.Channel.SendAsync(WorkerProtocol.Close, new System.Text.Json.Nodes.JsonObject { ["reason"] = "test done" });
        Assert.Equal(0, await rig.Closed.WaitAsync(TestTimeout));
        await rig.WaitForExitAsync(TestTimeout);
        Assert.Equal(0, await rig.ExitCode);
    }

    [Fact]
    public async Task An_unknown_capability_is_rejected_by_the_provider()
    {
        await using var rig = Start(FixtureManifest.V1());
        await rig.Hello.WaitAsync(TestTimeout);

        var outcome = await rig.InvokeAsync(
            CapabilityId.Parse("spatial.fixture.nobody@1"), timeout: TestTimeout);

        Assert.Equal(WorkerOutcomeKind.Failure, outcome.Kind);
        Assert.Equal(CapabilityErrorKind.ContractViolation, outcome.Error!.Kind);
        Assert.Contains("does not serve", outcome.Error.Message);
    }

    [Fact]
    public async Task An_incompatible_manifest_refuses_to_start()
    {
        // Declares fixture@2 but points the loader at the v1 provider type — the
        // worker host must fail the activation compatibility check before hello.
        var wrong = FixtureManifest.V2() with
        {
            AssemblyType = FixtureManifest.V1ProviderType,
        };
        await using var rig = Start(wrong);

        await rig.WaitForExitAsync(TestTimeout);
        Assert.NotEqual(0, await rig.ExitCode);
        await rig.StderrDrained.WaitAsync(TestTimeout);
        Assert.Contains("not compatible", rig.StandardError);
    }

    private WorkerRig Start(PluginManifest manifest)
    {
        var rig = WorkerRig.Start(manifest);
        _rigs.Add(rig);
        return rig;
    }
}
