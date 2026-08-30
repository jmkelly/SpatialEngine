namespace Spatial.PluginSdk.Transformations;

/// <summary>
/// The stable argument names of the transformation contracts
/// (<see cref="CrsDescribeContract"/> and <see cref="TransformContract"/>,
/// ADR-0027). Names are part of the wire contract: providers read arguments
/// by these names and the conformance suite builds invocations from them, so
/// they live in one place.
/// </summary>
public static class TransformationArguments
{
    /// <summary>The CRS identity string of a describe invocation (<c>EPSG:4326</c>).</summary>
    public const string Crs = "crs";

    /// <summary>The single input geometry of a transform invocation.</summary>
    public const string Geometry = "geometry";

    /// <summary>The source CRS identity; defaults to the geometry's own CRS when omitted.</summary>
    public const string Source = "source";

    /// <summary>The required target CRS identity.</summary>
    public const string Target = "target";
}
