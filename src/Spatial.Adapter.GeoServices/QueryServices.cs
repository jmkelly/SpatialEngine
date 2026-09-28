using Spatial.Contracts;
using Spatial.Contracts.Transformations;

namespace Spatial.Adapter.GeoServices;

/// <summary>
/// The engine services one Feature Service query path resolves from DI
/// (ADR-0033/0036): the set, relation, measurement, CRS-directory and
/// transformation faces. Bundling them keeps the query, catalog and
/// feature-resource signatures to what each operation actually varies on
/// — the <c>distance</c> band needs the buffer and the CRS family, the
/// <c>returnCentroid</c> output needs the measurement verb — and makes the
/// fan-out explicit at every entry point.
/// </summary>
/// <param name="Operations">Buffer/intersection/validate/simplify.</param>
/// <param name="Relations">The DE-9IM relation verb behind <c>spatialRel</c>.</param>
/// <param name="Measures">Area/length/distance/label point/centroid.</param>
/// <param name="Catalogue">The CRS description service, which is what tells a
/// projected layer (metres) from a geographic one (degrees) when a
/// <c>units</c> code has to be resolved.</param>
/// <param name="Transforms">The coordinate transform service behind <c>inSR</c>/<c>outSR</c>.</param>
internal sealed record QueryServices(
    IGeometryOperations Operations,
    IGeometryRelations Relations,
    IGeometryMeasures Measures,
    ICrsDirectory Catalogue,
    ICoordinateTransforms Transforms);
