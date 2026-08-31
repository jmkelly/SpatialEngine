using Spatial.PluginSdk.Capabilities;
using Spatial.PluginSdk.Resources;
using Spatial.PluginSdk.Streams;
using Spatial.Runtime.Capabilities;
using Spatial.Runtime.Resources;

namespace Spatial.Provider.Demo.Tests;

/// <summary>
/// The in-process demo host: a capability runtime with the <c>demo@1</c>
/// provider registered and the shared resource registry reachable, so tests
/// can invoke catalogue/scan/query and then read the minted streams like any
/// client (lease + open + drain) without a database or a worker process.
/// </summary>
internal sealed class DemoTestHost
{
    private DemoTestHost(CapabilityRegistry registry, CapabilityRuntime runtime)
    {
        Registry = registry;
        Runtime = runtime;
    }

    public CapabilityRegistry Registry { get; }

    public CapabilityRuntime Runtime { get; }

    /// <summary>The runtime's resource tracker (opaque handles, leases, streams).</summary>
    public ResourceRegistry Resources => Runtime.Resources;

    public static DemoTestHost Create()
    {
        var registry = new CapabilityRegistry();
        registry.Register(new DemoProvider());
        return new DemoTestHost(registry, new CapabilityRuntime(registry));
    }

    /// <summary>Invokes one capability with the data-provider read permission granted.</summary>
    public Task<CapabilityOutcome> InvokeAsync(
        CapabilityId capability,
        IReadOnlyDictionary<string, object?>? arguments = null,
        CancellationToken cancellationToken = default) =>
        Runtime.InvokeAsync(CapabilityInvocation.Create(capability, arguments ?? new Dictionary<string, object?>()) with
        {
            CancellationToken = cancellationToken,
            GrantedPermissions = new HashSet<Permission> { Permission.Parse("spatial.feature.read") },
        });

    /// <summary>Reads a minted stream handle to its end as decoded items.</summary>
    public static async Task<IReadOnlyList<object>> DrainAsync(ResourceHandle handle, ResourceRegistry resources)
    {
        Assert.True(resources.TryAcquireLease(handle, TimeSpan.FromSeconds(10), out var lease));
        Assert.True(resources.TryOpenStream(handle, lease, out var stream));
        var items = new List<object>();
        while (stream.TryReadBatch(32, out var batch))
        {
            foreach (var item in batch)
            {
                items.Add(item!);
            }
        }

        await stream.WaitForCompletionAsync();
        return items;
    }

    /// <summary>Reads a minted stream handle holding canonical batch byte arrays.</summary>
    public static async Task<IReadOnlyList<byte[]>> DrainBytesAsync(ResourceHandle handle, ResourceRegistry resources)
    {
        var items = await DrainAsync(handle, resources);
        return items.Cast<byte[]>().ToArray();
    }
}
