using Spatial.PluginHost.DotNet.Protocol;
using Spatial.PluginSdk.Capabilities;
using Spatial.PluginSdk.Resources;
using Spatial.PluginSdk.Streams;

namespace Spatial.PluginHost.DotNet.WorkerHost;

/// <summary>
/// The worker-side invocation facilities (plan §8, ADR-0022/0023): every
/// resource and stream a worker capability needs is created by the *host*
/// through the wire protocol — the worker asks (<c>facility.mint</c>,
/// <c>facility.stream.*</c>) and the supervisor's resource registry answers,
/// so opaque handles stay runtime-owned and the bounded buffer lives
/// host-side (backpressure across the process boundary is real: a write
/// against a full buffer blocks until the host's consumer reads).
/// </summary>
internal sealed class WireFacilities(WorkerChannel channel) : ICapabilityFacilities
{
    private static readonly TimeSpan FacilityTimeout = TimeSpan.FromSeconds(10);

    public IResourceFactory Resources => new WireResourceFactory(channel);

    public IStreamFactory Streams => new WireStreamFactory(channel);

    private sealed class WireResourceFactory(WorkerChannel channel) : IResourceFactory
    {
        public ResourceHandle Create(ResourceKind kind)
        {
            var response = channel.RequestAsync(
                WorkerProtocol.FacilityMint,
                WorkerPayload.FacilityMint(kind.Name),
                WorkerProtocol.FacilityResult,
                FacilityTimeout).AsTask().GetAwaiter().GetResult();
            return WorkerPayload.ReadFacilityHandle(response);
        }
    }

    private sealed class WireStreamFactory(WorkerChannel channel) : IStreamFactory
    {
        public StreamChannel Create(ResourceKind kind, int capacity)
        {
            var response = channel.RequestAsync(
                WorkerProtocol.FacilityStreamCreate,
                WorkerPayload.FacilityStreamCreate(kind.Name, capacity),
                WorkerProtocol.FacilityResult,
                FacilityTimeout).AsTask().GetAwaiter().GetResult();
            var handle = WorkerPayload.ReadFacilityHandle(response);
            var capacityHint = ReadCapacity(response);
            return new StreamChannel(handle, new WireStreamWriter(channel, handle.Id.Value, capacityHint));
        }

        private static int ReadCapacity(System.Text.Json.Nodes.JsonNode? response) =>
            response?["capacity"]?.GetValue<int>() ?? 0;
    }

    private sealed class WireStreamWriter(WorkerChannel channel, Guid token, int capacity) : IStreamWriter
    {
        public int Capacity => capacity;

        public async ValueTask WriteAsync(object? item, CancellationToken cancellationToken = default)
        {
            var response = await channel.RequestAsync(
                WorkerProtocol.FacilityStreamWrite,
                WorkerPayload.FacilityStreamWrite(token, [item]),
                WorkerProtocol.FacilityResult,
                timeout: null,
                cancellationToken: cancellationToken);
            WorkerPayload.EnsureFacilityOk(response);
        }

        /// <summary>
        /// The wire probe cannot know the host's buffer state; a write is
        /// accepted optimistically and the real backpressure applies on the
        /// next <see cref="WriteAsync"/>.
        /// </summary>
        public bool TryWrite(object? item)
        {
            if (!channel.IsConnected)
            {
                return false;
            }

            _ = channel.SendAsync(
                WorkerProtocol.FacilityStreamWrite,
                WorkerPayload.FacilityStreamWrite(token, [item]),
                Guid.NewGuid().ToString("N")).AsTask();
            return true;
        }

        public void Complete(ICapabilityError? failure = null)
        {
            if (channel.IsConnected)
            {
                _ = channel.SendAsync(
                    WorkerProtocol.FacilityStreamComplete,
                    WorkerPayload.FacilityStreamComplete(token, failure),
                    Guid.NewGuid().ToString("N")).AsTask();
            }
        }
    }
}
