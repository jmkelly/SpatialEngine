namespace Spatial.PluginSdk.Resources;

/// <summary>
/// The lifecycle state of a runtime-owned resource (plan §8 "ownership,
/// leases and disposal"): open when it can be leased, leased while at least
/// one active lease exists, closed once it has been disposed. The runtime's
/// resource registry tracks the state behind the opaque handle.
/// </summary>
public enum ResourceState
{
    Open = 0,
    Leased = 1,
    Closed = 2,
}
