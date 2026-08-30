namespace Spatial.PluginSdk.Capabilities;

/// <summary>
/// A named, described example invocation that a conforming provider must
/// serve the same way (plan §9 "conformance examples"). The values are core
/// types; conformance suites (Phase 6) run these examples against every
/// provider of a capability.
/// </summary>
public sealed record ConformanceExample(
    string Name,
    string Description,
    IReadOnlyDictionary<string, object?>? Arguments = null);
