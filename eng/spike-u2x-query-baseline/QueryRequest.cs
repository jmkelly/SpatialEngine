using Spatial.Contracts;
using Spatial.Core.Features.Query;
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
    /// The pushdown plan's attribute term: the store read is a
    /// <see cref="FeatureQuery"/> plan (ADR-0074), so the store takes the
    /// compiled predicate rather than a filter string.
    /// </summary>
    internal Predicate? StoreFilter => ParsedWhere();

    /// <summary>The query envelope, as the matcher's envelope test sees it.</summary>
    internal Spatial.Core.Geometry.Envelope Envelope =>
        new(Bbox.MinX, Bbox.MinY, Bbox.MaxX, Bbox.MaxY);

    /// <summary>The parsed where clause, or a failure — the same parse the adapter does.</summary>
    internal Predicate? ParsedWhere() =>
        EsriWhere.TryParse(Where, out var where, out var error) && where is { } parsed
            ? parsed.Predicate
            : throw new InvalidOperationException($"the spike's own where clause does not parse: {error}");

    /// <summary>
    /// The plan the store read is asked for: the bbox pre-filter and the
    /// attribute predicate, plus the ordering and the page cap when
    /// <paramref name="paged"/> asks for the shape a served request wants
    /// (ADR-0074 §5, ADR-0116). Without the cap it is the whole match set,
    /// which is what path B measures.
    /// </summary>
    internal FeatureQuery Plan(bool paged, string variant)
    {
        var order = new[] { new OrderTerm(OrderByField, OrderDescending ? SortDirection.Descending : SortDirection.Ascending) };
        return new FeatureQuery(
            Where: StoreFilter,
            BoundingBox: Bbox,
            Order: paged ? order : null,
            Limit: paged && !IsCountOnly(variant) && !IsStatistics(variant) ? Math.Min(ResultRecordCount, 1000) : null);
    }

    /// <summary>Which result shape a variant name asks for.</summary>
    internal static bool IsCountOnly(string variant) => variant == "countOnly";

    internal static bool IsStatistics(string variant) => variant == "statistics";
}
