using Spatial.Contracts;
using Spatial.Contracts.Providers;
using Spatial.Core.Geometry;
using Spatial.Esri.Codec;

namespace Spatial.Adapter.GeoServices;

/// <summary>
/// One planned MapServer export (spec §4.0.4, ADR-0048): the framed
/// viewport, the composed style, the render sources and the negotiated
/// image format, all resolved from the request before anything is drawn.
/// Split out of <see cref="MapExportEndpoints"/> so the route handler
/// decides <em>how</em> the export is served (image bytes or the metadata
/// envelope) and <see cref="MapExportPlanner"/> decides <em>what</em> is
/// exported.
/// </summary>
internal sealed record MapExportPlan(
    RasterViewport Viewport,
    string Style,
    IReadOnlyList<MapLayerSource> Sources,
    RasterFormat Format,
    int Width,
    int Height,
    CoordinateReference? ImageCrs);
