using Spatial.Core.Features;
using Spatial.Core.Geometry;

namespace Spatial.Contracts.Providers;

/// <summary>
/// The full schema description of one spatial dataset (ADR-0028): identity,
/// the geometry
/// column with its SRID and geometry type, a row-count estimate, the columns
/// that form the feature identity, and the feature schema (fields in column
/// order — geometry fields included as <see cref="AttributeKind.Geometry"/>).
/// Served as JSON by <c>GET /api/datasets/{id}</c> (ADR-0033). The schema fields use
/// only core field vocabulary (<see cref="FieldDefinition"/>), so clients
/// never see provider-specific types.
/// </summary>
/// <param name="Schema">
/// The concrete <see cref="FeatureSchema"/>, never <see cref="IFeatureSchema"/>:
/// System.Text.Json cannot deserialize an interface-typed property, so the
/// interface-typed version broke the catalogue round trip with
/// <c>NotSupportedException</c> at every read. An interface may be accepted
/// where a value is never deserialized, not here. Guarded by
/// <c>Spatial.Architecture.Tests.SchemaBindingGuardTests</c>.
/// </param>
/// <param name="GeometryLayout">
/// The ordinates the store <em>declares</em> for the geometry column
/// (ADR-0084). A store that can prove the column carries Z or M reports it;
/// a store that cannot prove it reports <see cref="CoordinateLayout.Xy"/>.
/// The default is therefore the honest answer, not an omission: a caller can
/// trust a true here, and the GeoServices layer metadata advertises
/// <c>hasZ</c>/<c>hasM</c> exactly to the extent it is told (ADR-0081).
/// </param>
public sealed record DatasetDescription(
    string Id,
    string SchemaName,
    string Table,
    string GeometryColumn,
    int Srid,
    string GeometryType,
    long EstimatedRowCount,
    IReadOnlyList<string> IdColumns,
    FeatureSchema Schema,
    CoordinateLayout GeometryLayout = CoordinateLayout.Xy)
{
    public override string ToString() => $"{Id}: {Schema}";
}
