namespace Spatial.PluginSdk.Transformations;

/// <summary>
/// One axis of a coordinate reference system as reported by
/// <c>spatial.crs.describe@1</c> (ADR-0027): its name, orientation and unit.
/// Only core/framework types cross the contract surface (ADR-0005); the
/// concrete axis vocabulary comes from the provider's CRS catalogue.
/// </summary>
public sealed record CrsAxis(string Name, AxisOrientation Orientation, string UnitName)
{
    /// <inheritdoc />
    public override string ToString() => $"{Name} ({Orientation}, {UnitName})";
}
