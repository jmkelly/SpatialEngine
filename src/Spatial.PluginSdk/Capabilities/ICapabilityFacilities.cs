using Spatial.PluginSdk.Resources;
using Spatial.PluginSdk.Streams;

namespace Spatial.PluginSdk.Capabilities;

/// <summary>
/// The runtime-backed surfaces a provider uses while serving an invocation
/// (Phase 4): <see cref="Resources"/> to mint runtime-owned resource handles
/// and <see cref="Streams"/> to create bounded, backpressured streams. The
/// runtime attaches facilities to every invocation it routes (inline or as a
/// job); providers see them through <see cref="IInvocationContext.Facilities"/>.
/// </summary>
public interface ICapabilityFacilities
{
    /// <summary>Mints runtime-owned resources (opaque handles with ownership, leases and disposal).</summary>
    IResourceFactory Resources { get; }

    /// <summary>Creates bounded, backpressured streams for streaming capabilities.</summary>
    IStreamFactory Streams { get; }
}
