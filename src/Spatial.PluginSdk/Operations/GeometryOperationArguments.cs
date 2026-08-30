namespace Spatial.PluginSdk.Operations;

/// <summary>
/// The stable argument names shared by the spatial geometry operation
/// contracts (<see cref="BufferContract"/>, <see cref="IntersectionContract"/>,
/// <see cref="ValidateContract"/> and <see cref="SimplifyContract"/>). Names
/// are part of the wire contract: providers read arguments by these names and
/// the conformance suite builds invocations from them, so they live in one
/// place.
/// </summary>
public static class GeometryOperationArguments
{
    /// <summary>The single input geometry (buffer, validate, simplify).</summary>
    public const string Geometry = "geometry";

    /// <summary>The left input geometry of a pairwise operation.</summary>
    public const string Left = "left";

    /// <summary>The right input geometry of a pairwise operation.</summary>
    public const string Right = "right";

    /// <summary>The buffer distance (negative distances erode).</summary>
    public const string Distance = "distance";

    /// <summary>Quadrant segments for a buffer (default 8).</summary>
    public const string QuadrantSegments = "quadrantSegments";

    /// <summary>The simplification tolerance.</summary>
    public const string Tolerance = "tolerance";
}
