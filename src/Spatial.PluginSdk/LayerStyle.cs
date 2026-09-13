namespace Spatial.PluginSdk;

/// <summary>
/// The optional render style of a published layer (ADR-0047). It is the
/// renderer's supported subset in core-typed form — a colour, opacity, a
/// stroke width and a point radius — carried as service data so a published
/// map service is self-describing. Adapters lower it to their own shape
/// (GeoServices <c>drawingInfo</c>, the render pipeline's draw plan); it is
/// deliberately not a MapLibre or Esri type (ADR-0005/0033).
/// </summary>
public sealed record LayerStyle(
    string Color,
    double Opacity = 1.0,
    double LineWidth = 2.0,
    double Radius = 5.0,
    bool Visible = true)
{
    /// <summary>A neutral style used when a layer declares none.</summary>
    public static LayerStyle Default { get; } = new("#4fc3f7");

    public override string ToString() => $"{Color} @{Opacity:0.##}, lw {LineWidth:0.##}, r {Radius:0.##}";
}
