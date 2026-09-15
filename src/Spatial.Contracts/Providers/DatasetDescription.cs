using Spatial.Core.Features;

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
public sealed record DatasetDescription(
    string Id,
    string SchemaName,
    string Table,
    string GeometryColumn,
    int Srid,
    string GeometryType,
    long EstimatedRowCount,
    IReadOnlyList<string> IdColumns,
    FeatureSchema Schema)
{
    public override string ToString() => $"{Id}: {Schema}";
}
