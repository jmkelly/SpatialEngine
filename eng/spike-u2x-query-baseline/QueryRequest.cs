using Spatial.Contracts;
using Spatial.Esri.Codec;

namespace Spatial.Spike.QueryBaseline;

/// <summary>
/// The request the spike measures: a FeatureServer query on the world-cities
/// layer with a bbox, a where clause and a small <c>resultRecordCount</c>, in
/// the three result shapes the bead names (paged features, count only, and a
/// grouped <c>outStatistics</c>), against two bboxes — a selective viewport
/// and the whole world, so the constant scan cost and the variable match
/// count are visible separately.
/// </summary>
internal sealed record QueryRequest(
    string Scenario,
    BoundingBox Bbox,
    string Where,
    int ResultRecordCount,
    string OrderByField,
    bool OrderDescending,
    string? GroupByField)
{
    internal const string PopulationField = "population";
    internal const string CountryField = "country";
    internal const string StatisticsType = "avg";

    /// <summary>The three result shapes, applied to the same matched set.</summary>
    internal static IReadOnlyList<string> Variants { get; } = ["page25", "countOnly", "statistics"];

    internal static IReadOnlyList<QueryRequest> Scenarios { get; } =
    [
        new("europe", new BoundingBox(-10, 36, 5, 55), $"{PopulationField} > 100000", 25, PopulationField, true, CountryField),
        new("global", new BoundingBox(-180, -90, 180, 90), $"{PopulationField} > 100000", 25, PopulationField, true, CountryField),
    ];

    /// <summary>
    /// The equivalent pushdown filter for the store: the same predicate in the
    /// store's own attribute-filter grammar (ADR-0028), which is the string
    /// <c>IFeatureStore.QueryAsync</c> takes today.
    /// </summary>
    internal string StoreFilter => Where;

    /// <summary>The query envelope, as the matcher's envelope test sees it.</summary>
    internal Spatial.Core.Geometry.Envelope Envelope =>
        new(Bbox.MinX, Bbox.MinY, Bbox.MaxX, Bbox.MaxY);

    /// <summary>The parsed where clause, or a failure — the same parse the adapter does.</summary>
    internal EsriFilterClause ParsedWhere() =>
        EsriFilterClause.TryParse(Where, out var clause, out var error) && clause is { } parsed
            ? parsed
            : throw new InvalidOperationException($"the spike's own where clause does not parse: {error}");

    /// <summary>Which result shape a variant name asks for.</summary>
    internal static bool IsCountOnly(string variant) => variant == "countOnly";

    internal static bool IsStatistics(string variant) => variant == "statistics";
}
