namespace Spatial.Cli;

/// <summary>
/// The geometry family a layer draws, used to choose which MapLibre style
/// layers the compact draw recipe lowers to (ADR-0052). <c>mixed</c> draws
/// fill, line and circle so an unstyled or heterogeneous layer stays
/// visible.
/// </summary>
public enum GeometryFamily
{
    /// <summary>Polygon fill (plus outline).</summary>
    Polygon,

    /// <summary>Line stroke.</summary>
    Line,

    /// <summary>Point circle.</summary>
    Point,

    /// <summary>All three: fill, line and circle.</summary>
    Mixed,
}

/// <summary>Parses the <c>--geometry</c> value.</summary>
public static class GeometryFamilies
{
    /// <summary>Parses a family name, failing with a usage error on an unknown value.</summary>
    public static GeometryFamily Parse(string? value) => (value ?? "mixed").ToLowerInvariant() switch
    {
        "polygon" or "fill" => GeometryFamily.Polygon,
        "line" => GeometryFamily.Line,
        "point" => GeometryFamily.Point,
        "mixed" => GeometryFamily.Mixed,
        _ => throw new CliUsageException($"Unknown geometry '{value}'. Use point, line, polygon or mixed."),
    };

    /// <summary>The wire name of a family.</summary>
    public static string Name(GeometryFamily family) => family.ToString().ToLowerInvariant();
}
