namespace Spatial.PluginSdk.Transformations;

/// <summary>
/// The reference ellipsoid of a CRS as reported by
/// the CRS description service (ADR-0027/ADR-0033), when the provider knows one:
/// name, semi-major and semi-minor axes and their unit. Carries only
/// framework types (ADR-0005).
/// </summary>
public sealed record CrsEllipsoid(string Name, double SemiMajorAxis, double SemiMinorAxis, string UnitName)
{
    /// <inheritdoc />
    public override string ToString() => $"{Name} (a {SemiMajorAxis}, b {SemiMinorAxis} {UnitName})";
}
