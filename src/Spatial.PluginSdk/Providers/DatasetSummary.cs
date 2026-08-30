namespace Spatial.PluginSdk.Providers;

/// <summary>
/// The catalogue entry of one spatial dataset (plan §13.2 "browse datasets"):
/// the stable identifier (<c>schema.table</c>), the geometry column and its
/// SRID, and a Postgres row-count estimate. The wire form is the
/// <c>catalogue.metadata</c> JSON document produced by
/// <see cref="DatasetMetadataJson.WriteSummary"/> — one item per dataset on
/// the <c>spatial.catalogue.list@1</c> stream (ADR-0028).
/// </summary>
public sealed record DatasetSummary(
    string Id,
    string Schema,
    string Table,
    string GeometryColumn,
    int Srid,
    long EstimatedRowCount)
{
    public override string ToString() => $"{Id}: {GeometryColumn} (SRID {Srid}, {EstimatedRowCount} rows est.)";
}
