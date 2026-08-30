namespace Spatial.Runtime.Capabilities;

/// <summary>
/// Which deterministic resolution step selected the provider (plan §9):
/// explicit caller pin, compatible resource-local provider, configured
/// preferred provider, or the first healthy provider by stable id. Recorded
/// in <see cref="InvocationProvenance"/> for diagnostics.
/// </summary>
public enum ResolutionStep
{
    Explicit = 0,
    ResourceLocal = 1,
    ConfiguredPreferred = 2,
    FirstHealthy = 3,
}
