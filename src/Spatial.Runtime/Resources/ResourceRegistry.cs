using System.Diagnostics.CodeAnalysis;
using Spatial.PluginSdk.Capabilities;
using Spatial.PluginSdk.Resources;
using Spatial.PluginSdk.Streams;
using Spatial.Runtime.Streams;

namespace Spatial.Runtime.Resources;

/// <summary>
/// The runtime's resource tracker (plan §8 "opaque handles with ownership,
/// leases and disposal"): mints handles for providers, tracks open/leased/
/// closed state, issues and honours leases, closes resources on disposal and
/// reclaims — and reports — resources a client leaked (one that was never
/// closed when its owning provider was disposed). Thread-safe for concurrent
/// providers and consumers.
/// </summary>
public sealed class ResourceRegistry
{
    private static readonly TimeSpan DefaultLeaseDuration = TimeSpan.FromSeconds(30);

    private readonly object _gate = new();
    private readonly Dictionary<ResourceId, ResourceRecord> _records = new();
    private readonly Func<DateTimeOffset> _clock;

    /// <summary>Creates a registry using UTC now as its clock (injectable for deterministic lease tests).</summary>
    public ResourceRegistry(Func<DateTimeOffset>? clock = null)
    {
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>Number of resources not yet closed — the current leak level.</summary>
    public int OpenCount
    {
        get
        {
            lock (_gate)
            {
                return _records.Values.Count(record => record.State != ResourceState.Closed);
            }
        }
    }

    /// <summary>
    /// Mints and registers an open resource owned by <paramref name="owner"/>.
    /// Providers create resources through the invocation facilities.
    /// </summary>
    public ResourceHandle Create(ProviderId owner, ResourceKind kind)
    {
        lock (_gate)
        {
            var handle = NewHandle(owner, kind);
            _records.Add(handle.Id, new ResourceRecord(handle));
            return handle;
        }
    }

    /// <summary>Mints and registers an open resource carrying a runtime-held payload (streams).</summary>
    internal ResourceHandle Create(ProviderId owner, ResourceKind kind, object payload)
    {
        lock (_gate)
        {
            var handle = NewHandle(owner, kind);
            _records.Add(handle.Id, new ResourceRecord(handle) { Payload = payload });
            return handle;
        }
    }

    /// <summary>
    /// Acquires a lease on the resource: null-ish false when the resource is
    /// unknown or closed; otherwise a new lease valid from now for
    /// <paramref name="duration"/> (default 30 s) and the resource becomes
    /// <see cref="ResourceState.Leased"/>.
    /// </summary>
    public bool TryAcquireLease(ResourceHandle handle, TimeSpan? duration, [NotNullWhen(true)] out ResourceLease? lease)
    {
        lock (_gate)
        {
            lease = null;
            if (handle is null || !_records.TryGetValue(handle.Id, out var record) || record.State == ResourceState.Closed)
            {
                return false;
            }

            PruneExpired(record);
            var grant = duration ?? DefaultLeaseDuration;
            if (grant <= TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(duration), grant, "A lease duration must be positive.");
            }

            var now = _clock();
            lease = new ResourceLease(handle.Id, now + grant, grant);
            record.Leases.Add(lease);
            record.State = ResourceState.Leased;
            return true;
        }
    }

    /// <summary>
    /// Replaces an active lease with a fresh one (same resource, extended
    /// from now). False when the resource is unknown or closed, or the lease
    /// is no longer held.
    /// </summary>
    public bool TryRenewLease(ResourceLease lease, TimeSpan? duration, [NotNullWhen(true)] out ResourceLease? renewed)
    {
        lock (_gate)
        {
            renewed = null;
            if (lease is null || !_records.TryGetValue(lease.ResourceId, out var record) || record.State == ResourceState.Closed)
            {
                return false;
            }

            var grant = duration ?? lease.Duration;
            if (grant <= TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(duration), grant, "A lease duration must be positive.");
            }

            PruneExpired(record);
            if (!record.Leases.Remove(lease))
            {
                return false;
            }

            var now = _clock();
            renewed = new ResourceLease(lease.ResourceId, now + grant, grant);
            record.Leases.Add(renewed);
            record.State = ResourceState.Leased;
            return true;
        }
    }

    /// <summary>Releases a lease; the resource returns to <see cref="ResourceState.Open"/> when the last one goes.</summary>
    public bool ReleaseLease(ResourceLease lease)
    {
        lock (_gate)
        {
            if (lease is null || !_records.TryGetValue(lease.ResourceId, out var record))
            {
                return false;
            }

            var removed = record.Leases.Remove(lease);
            PruneExpired(record);
            if (record.State != ResourceState.Closed && record.Leases.Count == 0)
            {
                record.State = ResourceState.Open;
            }

            return removed;
        }
    }

    /// <summary>The current state of the resource; <see cref="ResourceState.Closed"/> for unknown handles.</summary>
    public ResourceState GetState(ResourceHandle handle)
    {
        lock (_gate)
        {
            return _records.TryGetValue(handle.Id, out var record) ? record.State : ResourceState.Closed;
        }
    }

    /// <summary>The read-only view of a registered resource (identity, owner, state).</summary>
    public bool TryGetResource(ResourceHandle handle, [NotNullWhen(true)] out ICapabilityResource? resource)
    {
        lock (_gate)
        {
            if (_records.TryGetValue(handle.Id, out var record))
            {
                resource = record.View();
                return true;
            }

            resource = null;
            return false;
        }
    }

    /// <summary>
    /// Opens the consumer side of a stream resource. Requires a registered,
    /// non-closed resource, an active (unexpired) lease and a stream payload.
    /// </summary>
    public bool TryOpenStream(ResourceHandle handle, ResourceLease? lease, [NotNullWhen(true)] out ICapabilityStream? stream)
    {
        lock (_gate)
        {
            stream = null;
            if (lease is null || !_records.TryGetValue(handle.Id, out var record)
                || record.State == ResourceState.Closed || record.Payload is not BoundedStream bounded)
            {
                return false;
            }

            if (!lease.IsActive(_clock()))
            {
                return false;
            }

            stream = bounded;
            return true;
        }
    }

    /// <summary>Whether the handle refers to a registered, stream-backed resource (streaming contract enforcement).</summary>
    public bool IsStream(ResourceHandle handle)
    {
        lock (_gate)
        {
            return _records.TryGetValue(handle.Id, out var record) && record.Payload is BoundedStream;
        }
    }

    /// <summary>Disposes a resource: closed state, leases dropped and the payload (stream) disposed.</summary>
    public async Task CloseAsync(ResourceHandle handle)
    {
        object? payload;
        lock (_gate)
        {
            if (!_records.TryGetValue(handle.Id, out var record))
            {
                return;
            }

            payload = record.Payload;
            record.Leases.Clear();
            record.Payload = null;
            record.State = ResourceState.Closed;
        }

        await DisposePayloadAsync(payload);
    }

    /// <summary>
    /// Reclaims every open resource owned by <paramref name="owner"/> (owner
    /// teardown or plugin replacement): closes them and disposes their
    /// payloads. Returns how many were still open — the leak count, the
    /// resources a client never released or closed.
    /// </summary>
    public async Task<int> DisposeOwnerAsync(ProviderId owner)
    {
        var leaked = 0;
        var payloads = new List<object?>();
        lock (_gate)
        {
            foreach (var record in _records.Values.Where(record => record.Handle.Owner == owner).ToArray())
            {
                if (record.State == ResourceState.Closed)
                {
                    continue;
                }

                leaked++;
                record.Leases.Clear();
                payloads.Add(record.Payload);
                record.Payload = null;
                record.State = ResourceState.Closed;
            }
        }

        foreach (var payload in payloads)
        {
            await DisposePayloadAsync(payload);
        }

        return leaked;
    }

    private ResourceHandle NewHandle(ProviderId owner, ResourceKind kind) =>
        new(ResourceId.Create(), kind, owner, _clock());

    private void PruneExpired(ResourceRecord record)
    {
        var now = _clock();
        record.Leases.RemoveAll(lease => !lease.IsActive(now));
    }

    private static async ValueTask DisposePayloadAsync(object? payload)
    {
        if (payload is BoundedStream stream)
        {
            await stream.DisposeAsync();
        }
    }

    private sealed class ResourceRecord(ResourceHandle handle)
    {
        public ResourceHandle Handle { get; } = handle;

        public ResourceState State { get; set; } = ResourceState.Open;

        public List<ResourceLease> Leases { get; } = [];

        public object? Payload { get; set; }

        public ICapabilityResource View() => new ResourceView(Handle.Id, Handle.Kind, Handle.Owner, State, Handle.CreatedAt);

        private sealed record ResourceView(
            ResourceId Id,
            ResourceKind Kind,
            ProviderId Owner,
            ResourceState State,
            DateTimeOffset CreatedAt) : ICapabilityResource;
    }
}
