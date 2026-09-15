namespace Spatial.Contracts.Transformations;

/// <summary>
/// The orientation of one axis of a coordinate reference system, as reported
/// by the CRS description service (ADR-0027/ADR-0033). A CRS's native
/// axis order and orientations are part of its description; the engine's
/// geometry convention is always x-first — x is the first axis of the CRS
/// (longitude for geographic, easting for projected) and y the second —
/// regardless of what the CRS's official definition declares.
/// </summary>
public enum AxisOrientation
{
    /// <summary>Values increase towards east (x), e.g. longitude.</summary>
    East,

    /// <summary>Values increase towards north (y), e.g. latitude.</summary>
    North,

    /// <summary>Values increase towards west.</summary>
    West,

    /// <summary>Values increase towards south.</summary>
    South,

    /// <summary>Values increase upwards (vertical axes).</summary>
    Up,

    /// <summary>Values increase downwards (vertical axes).</summary>
    Down,

    /// <summary>An orientation the provider could not classify.</summary>
    Other,
}
