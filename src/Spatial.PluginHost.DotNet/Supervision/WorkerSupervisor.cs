using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Spatial.PluginHost.DotNet.Manifest;
using Spatial.PluginHost.DotNet.Protocol;
using Spatial.PluginSdk.Capabilities;
using Spatial.Runtime.Capabilities;

namespace Spatial.PluginHost.DotNet.Supervision;

/// <summary>
/// The process supervisor (plan §16 Phase 5, Epic F): discovers and validates
/// immutable plugin packages, activates them as separate worker processes
/// over the wire protocol (ADR-0025), health-checks with <c>ping</c>,
/// restarts crashed workers with backoff, supports side-by-side versions and
/// active-preference routing, drains a version without stopping the host
/// (the <see cref="Spatial.Runtime.Resources.ResourceRegistry"/>'s
/// DisposeOwner is the draining call site) and can roll new work back to a
/// previous version. The supervisor is a thin orchestrator — the mechanics
/// live in the worker process, facility server, relay and provider types.
/// </summary>
public sealed class WorkerSupervisor : IAsyncDisposable
{
    private readonly CapabilityRuntime _runtime;
    private readonly WorkerSupervisorOptions _options;
    private readonly WorkerFacilityServer _facilities;
    private readonly List<WorkerInstance> _workers = [];
    private readonly List<SupervisorEvent> _events = [];
    private readonly ConcurrentDictionary<Guid, TaskCompletionSource<HelloDocument>> _handshakes = new();
    private readonly ConcurrentDictionary<Guid, TaskCompletionSource> _drains = new();
    private readonly ConcurrentDictionary<Guid, byte> _restarting = new();
    private readonly WorkerHealthMonitor _health;
    private readonly object _eventsGate = new();
    private int _disposed;

    public WorkerSupervisor(CapabilityRuntime runtime, WorkerSupervisorOptions options)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _facilities = new WorkerFacilityServer(runtime.Resources);
        _health = new WorkerHealthMonitor(
            options.Health,
            canCheck: instance => instance.Channel is not null && WorkerStateSupport.CanServe(instance.State),
            probe: ProbeHealthAsync,
            restartUnhealthy: RestartUnhealthyAsync);
    }

    /// <summary>The worker host executable next to this assembly (the default for new supervisors).</summary>
    public static string DefaultExecutable
    {
        get
        {
            var directory = Path.GetDirectoryName(typeof(WorkerSupervisor).Assembly.Location)!;
            return Path.Combine(
                directory,
                OperatingSystem.IsWindows() ? "Spatial.PluginHost.DotNet.exe" : "Spatial.PluginHost.DotNet");
        }
    }

    public IReadOnlyList<WorkerInstance> Workers => _workers.ToArray();

    public IReadOnlyList<SupervisorEvent> Events
    {
        get
        {
            lock (_eventsGate)
            {
                return _events.ToArray();
            }
        }
    }

    /// <summary>Discovers the valid packages under <paramref name="packagesRoot"/> (empty list when none).</summary>
    public IReadOnlyList<PluginPackage> Discover(string packagesRoot)
    {
        var packages = PluginDiscovery.Discover(packagesRoot);
        foreach (var package in packages)
        {
            Record(SupervisorEventKind.Discovered, Guid.Empty, $"discovered {package.Manifest.Id} at {package.Path}");
        }

        return packages;
    }

    /// <summary>
    /// Validates the package at <paramref name="packagePath"/> and activates it
    /// as an isolated worker: spawn, handshake, health, registry registration
    /// and <see cref="WorkerState.Active"/>. Throws
    /// <see cref="WorkerActivationException"/> when the package is invalid,
    /// already registered, or never became healthy inside the startup timeout.
    /// </summary>
    public async Task<WorkerInstance> ActivateAsync(string packagePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packagePath);
        if (!PluginDiscovery.TryLoadPackage(packagePath, out var package, out var problems))
        {
            throw new WorkerActivationException(
                $"cannot activate '{packagePath}': {string.Join("; ", problems)}");
        }

        var instance = new WorkerInstance(package!);
        instance.Transition(WorkerState.Validated, "manifest validated");
        Record(SupervisorEventKind.Validated, instance.Id, $"validated {instance.ProviderId}");
        _workers.Add(instance);

        if (_runtime.Registry.GetProvider(instance.ProviderId) is not null)
        {
            throw new WorkerActivationException(
                $"provider {instance.ProviderId} is already registered; drain or unregister it before activating another");
        }

        await ActivateProcessAsync(instance);
        return instance;
    }

    /// <summary>
    /// Routes new work to <paramref name="providerId"/> for every capability it
    /// serves (plan §10.4 "route new work to the new version"): one active
    /// preference per capability, soft and reversible.
    /// </summary>
    public void RouteNewWorkTo(ProviderId providerId)
    {
        var instance = FindByProviderId(providerId)
            ?? throw new WorkerActivationException($"no worker provides {providerId}; activate it first");
        foreach (var capability in instance.Package.Manifest.Capabilities ?? [])
        {
            _runtime.SetActivePreference(CapabilityId.Parse(capability.Id), providerId);
        }

        Record(SupervisorEventKind.Diagnostic, instance.Id, $"new work routes to {providerId}");
    }

    /// <summary>
    /// The rollback path of plan §10.4: reactivates the previous version package
    /// (when it is not already serving) and routes new work back to it.
    /// </summary>
    public async Task<WorkerInstance> RollbackAsync(string previousPackagePath)
    {
        var existing = FindByPath(previousPackagePath);
        var worker = existing is { } instance && WorkerStateSupport.CanServe(instance.State)
            ? instance
            : existing is null
                ? await ActivateAsync(previousPackagePath)
                : await ReactivateAsync(existing);
        RouteNewWorkTo(worker.ProviderId);
        Record(SupervisorEventKind.RolledBack, worker.Id, $"rolled new work back to {worker.ProviderId}");
        return worker;
    }

    /// <summary>
    /// Drains one worker (plan §10.4): stops routing new work (health →
    /// unhealthy), waits for in-flight invocations, reclaims the worker's
    /// resources through the resource registry — the <c>DisposeOwner</c>
    /// draining call site — asks the worker to close gracefully and stops the
    /// process. The host keeps running; a drained version can be replaced or
    /// re-activated any time.
    /// </summary>
    public async Task DrainAsync(Guid workerId)
    {
        var instance = Find(workerId);
        if (instance.Channel is null || WorkerStateSupport.IsTerminal(instance.State))
        {
            return;
        }

        instance.Transition(WorkerState.Draining, $"draining {instance.ProviderId}");
        Record(SupervisorEventKind.Draining, instance.Id, $"draining {instance.ProviderId}");
        _runtime.Registry.SetHealth(instance.ProviderId, ProviderHealth.Unhealthy);

        await WaitForInFlightAsync(instance);
        await _runtime.Resources.DisposeOwnerAsync(instance.ProviderId);
        _facilities.Reset();

        var channel = instance.Channel;
        if (channel.IsConnected)
        {
            var drain = _drains.GetOrAdd(instance.Id, _ => new TaskCompletionSource());
            await channel.SendAsync(WorkerProtocol.Close, new JsonObject { ["reason"] = "draining" });
            await drain.Task.WaitAsync(_options.DrainTimeout);
        }

        if (instance.Process is not null)
        {
            await instance.Process.StopAsync(_options.DrainTimeout);
        }

        _runtime.Registry.Unregister(instance.ProviderId);
        instance.Transition(WorkerState.Stopped, $"stopped {instance.ProviderId}");
        Record(SupervisorEventKind.Stopped, instance.Id, $"stopped {instance.ProviderId}");
    }

    /// <summary>Probes one worker; repeated failures trip the threshold and restart it.</summary>
    public Task<bool> HealthCheckOnceAsync(Guid workerId) => _health.CheckAsync(Find(workerId));

    /// <summary>Runs periodic health checks for every serving worker until cancellation.</summary>
    public Task RunHealthChecksAsync(CancellationToken cancellationToken) =>
        _health.RunAsync(() => _workers.ToArray(), cancellationToken);

    /// <summary>
    /// Restarts a crashed or unhealthy worker with backoff: a fresh process
    /// and handshake restore it to <see cref="WorkerState.Active"/>. Exhausting
    /// the restart policy marks the worker <see cref="WorkerState.Failed"/>.
    /// </summary>
    public async Task RestartAsync(Guid workerId)
    {
        var instance = Find(workerId);
        if (!_restarting.TryAdd(workerId, 0)
            || WorkerStateSupport.IsTerminal(instance.State)
            || instance.State == WorkerState.Draining)
        {
            return;
        }

        try
        {
            if (!_options.RestartPolicy.ShouldRestart(instance.RestartCount))
            {
                instance.Fail($"the restart policy was exhausted after {instance.RestartCount} attempts");
                _runtime.Registry.SetHealth(instance.ProviderId, ProviderHealth.Unhealthy);
                Record(SupervisorEventKind.Failed, instance.Id, instance.LastError!);
                return;
            }

            instance.CountRestart();
            Record(SupervisorEventKind.Restarting, instance.Id,
                $"restarting {instance.ProviderId} (attempt {instance.RestartCount})");
            await Task.Delay(RestartPolicy.BackoffFor(instance.RestartCount));
            await ActivateProcessAsync(instance);
        }
        finally
        {
            _restarting.TryRemove(workerId, out _);
        }
    }

    /// <summary>Drains every worker (host shutdown path).</summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        foreach (var instance in _workers.ToArray())
        {
            await DrainAsync(instance.Id);
        }
    }

    /// <summary>Spawns (or respawns) a worker's process, runs the handshake and promotes it to Active.</summary>
    private async Task ActivateProcessAsync(WorkerInstance instance)
    {
        instance.Transition(WorkerState.Starting, $"starting {instance.ProviderId}");
        Record(SupervisorEventKind.Started, instance.Id, $"starting {instance.ProviderId}");

        var handshake = new TaskCompletionSource<HelloDocument>(TaskCreationOptions.RunContinuationsAsynchronously);
        _handshakes[instance.Id] = handshake;
        var process = WorkerProcess.Start(
            _options.WorkerExecutable, instance.Package.Path, $"worker:{instance.ProviderId}", HandlerFor(instance),
            _options.WorkerEnvironment);
        instance.Channel = process.Channel;
        instance.Process = process;
        instance.MarkProcess(process.ProcessId);
        _ = WatchDisconnectAsync(instance);

        HelloDocument? hello = null;
        try
        {
            hello = await handshake.Task.WaitAsync(_options.StartupTimeout);
        }
        catch (TimeoutException)
        {
        }

        var failureMessage = hello is null
            ? $"the worker {instance.ProviderId} did not handshake within {_options.StartupTimeout}; "
                + $"stderr: {process.StandardError}"
            : instance.ValidateHandshake(hello);
        if (failureMessage is not null)
        {
            instance.Fail(failureMessage);
            Record(SupervisorEventKind.Failed, instance.Id, failureMessage);
            await process.StopAsync(TimeSpan.FromSeconds(2));
            return;
        }

        instance.Provider ??= new WorkerProvider(instance);
        instance.Relay ??= new WorkerInvocationRelay();
        if (_runtime.Registry.GetProvider(instance.ProviderId) is null)
        {
            _runtime.Registry.Register(instance.Provider);
        }
        else
        {
            _runtime.Registry.SetHealth(instance.ProviderId, ProviderHealth.Healthy);
        }

        instance.Transition(WorkerState.Healthy, $"the worker {instance.ProviderId} is healthy");
        instance.Transition(WorkerState.Active, $"the worker {instance.ProviderId} is active");
        Record(SupervisorEventKind.Activated, instance.Id,
            $"activated {instance.ProviderId} (pid {process.ProcessId})");
    }

    private async Task WatchDisconnectAsync(WorkerInstance instance)
    {
        await instance.Channel!.Disconnected;
        if (Volatile.Read(ref _disposed) != 0
            || WorkerStateSupport.IsTerminal(instance.State)
            || instance.State == WorkerState.Draining)
        {
            return;
        }

        var stderr = instance.Process is null ? string.Empty : instance.Process.StandardError;
        instance.LastError = $"the worker {instance.ProviderId} disconnected; stderr: {stderr}";
        instance.Transition(WorkerState.Starting, $"restarting {instance.ProviderId} after a crash");
        Record(SupervisorEventKind.Crashed, instance.Id,
            $"worker {instance.ProviderId} disconnected; restarting");
        await RestartAsync(instance.Id);
    }

    private async Task WaitForInFlightAsync(WorkerInstance instance)
    {
        var deadline = DateTimeOffset.UtcNow + _options.DrainTimeout;
        var relay = instance.Relay;
        while (relay is not null && relay.InFlightCount > 0 && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(25);
        }
    }

    private Task<bool> ProbeHealthAsync(WorkerInstance instance) =>
        WorkerHealth.ProbeAsync(
            instance.Channel!, TimeSpan.FromMilliseconds(_options.Health.PingTimeoutMilliseconds));

    /// <summary>The health monitor's escalation hook: mark the worker unhealthy, record the crash and restart it.</summary>
    internal async Task RestartUnhealthyAsync(WorkerInstance instance, int failures)
    {
        _runtime.Registry.SetHealth(instance.ProviderId, ProviderHealth.Unhealthy);
        Record(SupervisorEventKind.Crashed, instance.Id, $"health check failed {failures} times; restarting");
        await RestartAsync(instance.Id);
    }

    private Task<WorkerInstance> ReactivateAsync(WorkerInstance instance) =>
        Serving(instance) ? Task.FromResult(instance) : ReactivateProcessAsync(instance);

    private static bool Serving(WorkerInstance instance) =>
        instance.Channel is not null && WorkerStateSupport.CanServe(instance.State);

    private async Task<WorkerInstance> ReactivateProcessAsync(WorkerInstance instance)
    {
        await ActivateProcessAsync(instance);
        return instance;
    }

    private Func<WorkerEnvelope, ValueTask> HandlerFor(WorkerInstance instance) => envelope =>
        envelope.Type switch
        {
            WorkerProtocol.Hello => CompleteHandshake(instance, envelope),
            WorkerProtocol.Progress => instance.Relay is { } relay
                ? relay.TryRelayProgress(envelope.Id, envelope.Payload)
                : ValueTask.CompletedTask,
            WorkerProtocol.Closed => CompleteDrain(instance),
            WorkerProtocol.Error => instance.RecordPayloadError(envelope),
            WorkerProtocol.FacilityMint or WorkerProtocol.FacilityStreamCreate
                or WorkerProtocol.FacilityStreamWrite or WorkerProtocol.FacilityStreamComplete
                => HandleFacilityAsync(instance, envelope),
            _ => ValueTask.CompletedTask,
        };

    private ValueTask HandleFacilityAsync(WorkerInstance instance, WorkerEnvelope envelope)
    {
        // Process facility requests off the read loop: a stream write can block
        // on cross-process backpressure, which must never stall the loop that
        // answers the worker's next request. The facility server gates each
        // worker's requests (single-writer streams).
        _ = ProcessFacilityAsync(instance, envelope);
        return ValueTask.CompletedTask;
    }

    private async Task ProcessFacilityAsync(WorkerInstance instance, WorkerEnvelope envelope)
    {
        await _facilities.ProcessAsync(
            instance.Channel!, envelope, instance.ProviderId, instance.Id,
            (workerId, message) => Record(SupervisorEventKind.Diagnostic, workerId,
                $"facility request failed: {message}"));
    }

    private ValueTask CompleteHandshake(WorkerInstance instance, WorkerEnvelope envelope)
    {
        if (HandshakeSource(instance) is { } handshake)
        {
            handshake.TrySetResult(WorkerPayload.ReadHello(envelope.Payload));
        }

        return ValueTask.CompletedTask;
    }

    private async ValueTask CompleteDrain(WorkerInstance instance)
    {
        if (_drains.TryGetValue(instance.Id, out var drain))
        {
            drain.TrySetResult();
        }

        await ValueTask.CompletedTask;
    }

    private void Record(SupervisorEventKind kind, Guid workerId, string message)
    {
        lock (_eventsGate)
        {
            _events.Add(new SupervisorEvent(kind, DateTimeOffset.UtcNow, workerId, message));
        }
    }

    private WorkerInstance Find(Guid workerId) =>
        _workers.FirstOrDefault(instance => instance.Id == workerId)
        ?? throw new KeyNotFoundException($"no supervised worker with id {workerId}");

    private WorkerInstance? FindByProviderId(ProviderId providerId) =>
        _workers.FirstOrDefault(instance => instance.ProviderId == providerId);

    private WorkerInstance? FindByPath(string packagePath) =>
        _workers.FirstOrDefault(instance => instance.Package.Path == packagePath);

    private TaskCompletionSource<HelloDocument>? HandshakeSource(WorkerInstance instance) =>
        _handshakes.GetValueOrDefault(instance.Id);
}
