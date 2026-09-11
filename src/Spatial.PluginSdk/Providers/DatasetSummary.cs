namespace Spatial.PluginSdk.Providers;

/// <summary>
/// The catalogue entry of one spatial dataset: the stable identifier
/// (<c>schema.table</c>), the geometry column and its SRID, and a Postgres
/// row-count estimate. Served as JSON by <c>GET /api/catalogue</c> (ADR-0033).
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
