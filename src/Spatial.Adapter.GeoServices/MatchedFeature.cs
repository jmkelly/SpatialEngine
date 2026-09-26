using Spatial.Core.Features;

namespace Spatial.Adapter.GeoServices;

/// <summary>
/// One feature that survived a query's spatial and attribute filters, paired
/// with the Esri <c>OBJECTID</c> it resolved to (ADR-0037). Shared by the
/// query, feature, service-query, statistics, response-writing and
/// raster-catalog paths, so it is its own type rather than a nested member of
/// one of them.
/// </summary>
internal sealed record MatchedFeature(long ObjectId, Feature Feature);
