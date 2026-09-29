using Spatial.Contracts.Http;
using Spatial.Esri.Codec;

namespace Spatial.Adapter.GeoServices;

/// <summary>
/// Paging a query's matched features: the page start (opaque
/// <c>resultPaginationToken</c> or <c>resultOffset</c>), the effective page
/// cap and the <c>exceededTransferLimit</c> flag. Split out of
/// <see cref="FeatureQueryEngine"/> so the query facade keeps only
/// orchestration and the paging fan-out lives with the code that uses it
/// (ADR-0040).
///
/// <para>
/// This is the paging of the <em>match</em> path: the surface evaluated the
/// match itself, so the position it pages is a position in the match set it
/// holds, and <see cref="ResultPagination"/> mints the token for it (ADR-0116
/// §3). A request the store answers carries the store's own continuation
/// instead, and never comes through here — the two token vocabularies never mix
/// on one request, which is why a token minted on one path is a typed
/// invalid-argument failure on the other rather than a page of the wrong
/// question.
/// </para>
/// </summary>
internal static class FeaturePaging
{
    /// <summary>
    /// The page start: the opaque <c>resultPaginationToken</c> cursor when
    /// the client continues a token workflow, else <c>resultOffset</c>. Parse
    /// already rejects the combination, so the token simply wins by presence.
    /// </summary>
    internal static int ResolveOffset(EsriFeatureQuery query) =>
        query.ResultPaginationToken is { } token ? ResultPagination.Decode(token) : query.ResultOffset ?? 0;

    /// <summary>
    /// The effective page cap: <c>maxRecordCount × maxRecordCountFactor</c>
    /// (T-021). <c>returnExceededLimitFeatures</c> is accepted so the REST JS
    /// <c>queryAllFeatures</c> loop runs unmodified; the
    /// <c>exceededTransferLimit</c> flag stays correct either way.
    /// </summary>
    internal static int EffectivePageSize(EsriFeatureQuery query)
    {
        var cap = EsriLayerModel.MaxRecordCount * (query.MaxRecordCountFactor ?? 1);
        return Math.Min(query.ResultRecordCount ?? cap, cap);
    }

    /// <summary>One page of matched features, with the flag and cursor the response reports.</summary>
    internal static PageResult Page(List<MatchedFeature> matches, EsriFeatureQuery query)
    {
        var offset = Math.Min(ResolveOffset(query), matches.Count);
        var count = EffectivePageSize(query);
        var items = matches.Skip(offset).Take(count).ToArray();
        var exceeded = offset + items.Length < matches.Count;
        return new PageResult(items, exceeded, exceeded ? ResultPagination.Encode(offset + items.Length) : null);
    }

    internal sealed record PageResult(IReadOnlyList<MatchedFeature> Items, bool Exceeded, string? NextToken);
}
