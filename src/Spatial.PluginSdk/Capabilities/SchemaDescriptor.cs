using Spatial.Core.Features;

namespace Spatial.PluginSdk.Capabilities;

/// <summary>
/// The declared shape of one side of a capability contract: input or output.
/// The name identifies the interchange shape (for example
/// <c>feature.batch</c>); an optional exact
/// <see cref="Core.Features.FeatureSchema"/> pins the attribute columns when
/// the shape is a feature batch. Providers validate actual arguments against
/// their declared schemas at invocation time.
/// </summary>
public sealed record SchemaDescriptor(string Name, string? Description = null, FeatureSchema? FeatureSchema = null);