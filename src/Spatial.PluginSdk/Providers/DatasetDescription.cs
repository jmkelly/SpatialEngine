using Spatial.Core.Features;

namespace Spatial.PluginSdk.Providers;

/// <summary>
/// The full schema description of one spatial dataset (plan §11 "schema
/// description", §13.2 "inspect schemas and CRS"): identity, the geometry
/// column with its SRID and geometry type, a row-count estimate, the columns
/// that form the feature identity, and the feature schema (fields in column
/// order — geometry fields included as <see cref="AttributeKind.Geometry"/>).
/// The wire form is the <c>dataset.description</c> JSON document produced by
/// <see cref="DatasetMetadataJson.WriteDescription"/> — the single item of the
/// <c>spatial.dataset.describe@1</c> stream (ADR-0028). The schema fields use
/// only core field vocabulary (<see cref="FieldDefinition"/>), so clients
/// never see provider-specific types. The schema binds <see cref="IFeatureSchema"/>
/// (the model's contract face, ADR-0029) so contract code never depends on the
/// concrete implementation.
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
    IFeatureSchema Schema)
{
    public override string ToString() => $"{Id}: {Schema}";
}
