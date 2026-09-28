namespace Spatial.Transformations.ProjNet.Grids;

/// <summary>
/// The on-disk grid formats the registry reads. A grid's format is published
/// with the operation it backs, because a client reading a
/// <c>findTransformations</c> listing is told which standard the shift came
/// from and not merely that it came from a file.
/// </summary>
internal enum GridFormat
{
    /// <summary>
    /// NTv2 (the <c>.gsb</c> bundle): a header per sub-grid followed by the
    /// shifts in seconds of arc, with the accuracy in metres.
    /// </summary>
    Ntv2,
}

/// <summary>How a <see cref="GridFormat"/> is named in a published operation.</summary>
internal static class GridFormats
{
    /// <summary>
    /// The standard's own name. The enum member is the C# spelling, which is
    /// not what a client reading a <c>findTransformations</c> listing wants to
    /// see beside a grid it has never heard of.
    /// </summary>
    public static string Standard(GridFormat format) => format switch
    {
        GridFormat.Ntv2 => "NTv2",
        _ => throw new ArgumentOutOfRangeException(nameof(format), format, "No published name for this grid format."),
    };
}
