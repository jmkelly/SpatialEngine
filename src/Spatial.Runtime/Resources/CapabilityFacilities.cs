using Spatial.PluginSdk.Capabilities;
using Spatial.PluginSdk.Resources;
using Spatial.PluginSdk.Streams;
using Spatial.Runtime.Jobs;
using Spatial.Runtime.Streams;

namespace Spatial.Runtime.Resources;

/// <summary>
/// The runtime's <see cref="ICapabilityFacilities"/> for one invocation:
/// mints resources and bounded streams owned by the serving provider and
/// registered in the shared <see cref="ResourceRegistry"/>. The runtime
/// attaches one instance to every invocation it routes (inline or as a job);
/// when the invocation runs inside a job, created stream handles are also
/// published on the job so clients can read them while it runs.
/// </summary>
internal sealed class CapabilityFacilities : ICapabilityFacilities
{
    private readonly RuntimeResourceFactory _resources;
    private readonly RuntimeStreamFactory _streams;

    public static ICapabilityFacilities Create(ResourceRegistry registry, ProviderId owner) =>
        Create(registry, owner, null);

    public static ICapabilityFacilities Create(ResourceRegistry registry, ProviderId owner, CapabilityJob? job) =>
        new CapabilityFacilities(registry, owner, job);

    private CapabilityFacilities(ResourceRegistry registry, ProviderId owner, CapabilityJob? job)
    {
        _resources = new RuntimeResourceFactory(registry, owner);
        _streams = new RuntimeStreamFactory(registry, owner, job);
    }

    public IResourceFactory Resources => _resources;

    public IStreamFactory Streams => _streams;

    private sealed class RuntimeResourceFactory(ResourceRegistry registry, ProviderId owner) : IResourceFactory
    {
        public ResourceHandle Create(ResourceKind kind) => registry.Create(owner, kind);
    }

    private sealed class RuntimeStreamFactory(ResourceRegistry registry, ProviderId owner, CapabilityJob? job) : IStreamFactory
    {
        public StreamChannel Create(ResourceKind kind, int capacity)
        {
            var stream = new BoundedStream(capacity);
            var handle = registry.Create(owner, kind, stream);
            job?.PublishResource(handle);
            return new StreamChannel(handle, stream);
        }
    }
}
