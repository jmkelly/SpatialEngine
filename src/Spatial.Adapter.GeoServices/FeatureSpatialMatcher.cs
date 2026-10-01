using Spatial.Contracts;
using Spatial.Contracts.Providers;
using Spatial.Core.Features;
using Spatial.Core.Geometry;
using Spatial.Esri.Codec;

namespace Spatial.Adapter.GeoServices;

/// <summary>
/// The feature-match predicate (spec §9.1.4): id, unique-id, where-clause,
/// time-window and spatial matching plus the envelope-prefiltered
/// topological verbs. Split out of <see cref="FeatureQueryEngine"/> so the
/// query facade keeps only orchestration and the match fan-out (filter,
/// time, geometry verbs) lives with the code that uses it (ADR-0040).
/// Shared by the Feature Service match loop and the Image Service catalog
/// query, so both agree on what "matches" means.
/// </summary>
internal static class FeatureSpatialMatcher
{
    /// <summary>
    /// The features that match the query. The match envelope is compiled onto
    /// the store's query surface (ADR-0110), so the store is asked for the
    /// candidate rows — the identities, the attribute clause, the time extent
    /// and the query geometry's envelope — rather than read whole and filtered
    /// here feature by feature.
    ///
    /// <para>
    /// What the store returns is a <em>pre-filter</em>, never the answer:
    /// every row it hands back is still matched here, by the same
    /// <see cref="Matches"/>, so the pushed read is verified by the matcher
    /// rather than trusted in place of it. An envelope that cannot be compiled
    /// for this layer takes <see cref="ScanAndMatchAsync"/>, the whole-dataset
    /// read that was the only path before — never a scan after a pushdown
    /// attempt (principle 15).
    /// </para>
    /// </summary>
    internal static async Task<List<MatchedFeature>> MatchAsync(QuerySpec spec, CancellationToken cancellationToken)
    {
        if (FeatureMatchPushdown.Compile(spec) is not { } plan)
        {
            return await ScanAndMatchAsync(spec, cancellationToken).ConfigureAwait(false);
        }

        var page = await spec.Store.QueryAsync(spec.Dataset.Id, plan, cancellationToken).ConfigureAwait(false);
        return MatchRows(spec, page.Batches, wherePushedDown: true, cancellationToken);
    }

    /// <summary>
    /// The whole-dataset match: every feature of the layer, read and matched in
    /// the facade. This is the path a request whose envelope cannot be compiled
    /// for its layer takes (ADR-0097's identity rule, a <c>uniqueIds</c>
    /// request, an unsupported <c>spatialRel</c>), and it is what the pushed
    /// path is measured against.
    /// </summary>
    internal static async Task<List<MatchedFeature>> ScanAndMatchAsync(QuerySpec spec, CancellationToken cancellationToken) =>
        MatchRows(
            spec,
            await spec.Store.ScanAsync(spec.Dataset.Id, cancellationToken).ConfigureAwait(false),
            wherePushedDown: false,
            cancellationToken);

    /// <summary>
    /// The per-feature match over the rows a read returned, numbering each row
    /// with the layer's own identity scheme. <paramref name="wherePushedDown"/>
    /// says the attribute clause reached the store as the plan's predicate, so
    /// the facade does not test it again here — and must not, or a store that
    /// answered a different row set would look right (ADR-0097).
    /// </summary>
    private static List<MatchedFeature> MatchRows(
        QuerySpec spec,
        IReadOnlyList<FeatureBatch> batches,
        bool wherePushedDown,
        CancellationToken cancellationToken)
    {
        var matches = new List<MatchedFeature>();
        long ordinal = 0;
        foreach (var feature in batches.SelectMany(batch => batch.Features))
        {
            ordinal++;
            if (!spec.Scheme.TryResolve(feature, ordinal, out var objectId))
            {
                throw GeoServicesErrors.ServerError(
                    $"The identity column of layer '{spec.Dataset.Id}' is not an integer.");
            }

            var uniqueId = EsriUniqueIdScheme.ResolveFor(spec.Query, spec.Dataset, feature);
            var candidate = new MatchCandidate(
                spec.Query, feature, objectId, spec.QueryGeometry, spec.Services.Relations, uniqueId, wherePushedDown, spec.Dataset.TimeFields);
            if (Matches(candidate, cancellationToken))
            {
                matches.Add(new MatchedFeature(objectId, feature));
            }
        }

        return matches;
    }

    internal static bool Matches(MatchCandidate match, CancellationToken cancellationToken)
    {
        if (!MatchesIds(match))
        {
            return false;
        }

        if (!MatchesUniqueIds(match))
        {
            return false;
        }

        if (!MatchesWhere(match))
        {
            return false;
        }

        if (!MatchesTimeWindow(match))
        {
            return false;
        }

        return MatchesSpatial(match, cancellationToken);
    }

    private static bool MatchesIds(MatchCandidate match) =>
        match.Query.ObjectIds is not { } ids || ids.Contains(match.ObjectId);

    // T-036/T-058: the string-ID filter (spec §9.1.4, 11.5+). A null unique id
    // never equals a requested id; layers without a string-or-guid unique-id
    // model are rejected when the match loops resolve the id.
    private static bool MatchesUniqueIds(MatchCandidate match) =>
        match.Query.UniqueIds is not { } wanted
        || (match.UniqueId is not null && wanted.Contains(match.UniqueId, StringComparer.Ordinal));

    /// <summary>
    /// The attribute clause. A store pushdown has already applied it; this is
    /// the residual case — a clause naming the synthetic <c>OBJECTID</c> of a
    /// layer with no integer identity column, which no store can read.
    /// </summary>
    private static bool MatchesWhere(MatchCandidate match) =>
        match.WherePushedDown
        || EsriPredicateEvaluator.Matches(
            match.Query.Where?.Predicate,
            match.Feature,
            new EsriFieldOverlay(EsriLayerModel.ObjectIdField, AttributeValue.FromInt64(match.ObjectId)));

    private static bool MatchesTimeWindow(MatchCandidate match) =>
        match.Query.Time is not { } time || MatchesTime(match.Feature, time, match.TimeFields);

    private static bool MatchesSpatial(MatchCandidate match, CancellationToken cancellationToken) =>
        match.QueryGeometry is null
        || SpatialMatch(match.Feature, match.QueryGeometry, match.Query.SpatialRel, match.Relations, cancellationToken);

    /// <summary>
    /// Applies the <c>time</c> extent to one feature.
    ///
    /// <para>
    /// A layer carrying a <see cref="TemporalExtentFields"/> designation is
    /// matched against its feature temporal extent (ADR-0175): the interval
    /// between the two fields the layer designates, inclusive bounds, a null
    /// bound infinite — so a row whose extent straddles the window matches
    /// although no single instant of it falls inside. A layer with no
    /// designation keeps the rule it has always had: any date value inside the
    /// (inclusive) bounds, where a <c>null</c> bound is infinite, and a feature
    /// with no date values matching unconditionally — ArcGIS Server ignores
    /// <c>time</c> on layers without time-aware (date) fields. Shared with the
    /// MapServer identify path (T-059), which filters dated hits with the same
    /// rule.
    /// </para>
    /// </summary>
    internal static bool MatchesTime(Feature feature, EsriTimeExtent time, TemporalExtentFields? fields = null)
    {
        var window = TemporalExtent.FromMilliseconds(time.StartMs, time.EndMs);
        return fields is null
            ? TemporalExtent.MatchesAnyDate(feature, window)
            : TemporalExtent.From(feature, fields).Overlaps(window);
    }

    private static bool SpatialMatch(
        Feature feature,
        IGeometry queryGeometry,
        string spatialRel,
        IGeometryRelations relations,
        CancellationToken cancellationToken)
    {
        if (GeometryPair.Of(FeatureGeometry.Find(feature), queryGeometry) is not { } pair)
        {
            return false;
        }

        if (string.Equals(spatialRel, EsriFeatureQuery.EnvelopeIntersects, StringComparison.Ordinal))
        {
            return pair.FeatureEnvelope.Intersects(pair.QueryEnvelope);
        }

        return MatchTopology(pair, spatialRel, relations, cancellationToken);
    }

    /// <summary>The relations approximated with envelope-prefiltered topological verbs.</summary>
    private static bool MatchTopology(
        GeometryPair pair, string spatialRel, IGeometryRelations relations, CancellationToken cancellationToken) =>
        spatialRel switch
        {
            var rel when string.Equals(rel, EsriFeatureQuery.Contains, StringComparison.Ordinal) =>
                SpatialRelationPredicates.Contains(pair, relations, cancellationToken),
            var rel when string.Equals(rel, EsriFeatureQuery.Within, StringComparison.Ordinal) =>
                SpatialRelationPredicates.Within(pair, relations, cancellationToken),
            var rel when string.Equals(rel, EsriFeatureQuery.Touches, StringComparison.Ordinal) =>
                SpatialRelationPredicates.Touches(pair, relations, cancellationToken),
            var rel when string.Equals(rel, EsriFeatureQuery.Overlaps, StringComparison.Ordinal) =>
                SpatialRelationPredicates.Overlaps(pair, relations, cancellationToken),
            var rel when string.Equals(rel, EsriFeatureQuery.Crosses, StringComparison.Ordinal) =>
                SpatialRelationPredicates.Crosses(pair, relations, cancellationToken),
            var rel when string.Equals(rel, EsriFeatureQuery.Intersects, StringComparison.Ordinal) =>
                SpatialRelationPredicates.Intersects(pair, relations, cancellationToken),
            _ => throw GeoServicesErrors.Invalid($"spatialRel '{spatialRel}' is not supported."),
        };

    /// <summary>
    /// One feature-match invocation: which layer, store and parsed query to
    /// match, with the pre-transformed query geometry, the object-id scheme
    /// the scan ordinals resolve through and the geometry verb faces the
    /// spatial predicates need. Threading one value instead of eight
    /// parameters keeps the match loop readable (metrics
    /// long-parameter-list).
    /// </summary>
    internal sealed record QuerySpec(
        DatasetDescription Dataset,
        IFeatureStore Store,
        EsriFeatureQuery Query,
        IGeometry? QueryGeometry,
        QueryServices Services,
        EsriObjectIdScheme Scheme);

    /// <summary>
    /// One per-feature match candidate: the parsed query, the feature and
    /// its resolved <c>OBJECTID</c>, the pre-transformed query geometry, the
    /// relation verb the spatial predicates need and the layer's temporal
    /// designation. Shared by the Feature
    /// Service match loop, the Image Service catalog query and the
    /// relationship traversal, so all three agree on what "matches" means.
    /// The verbs are reached from the query's own geometry, so a traversal
    /// resolves the relation face it holds rather than the whole
    /// <see cref="QueryServices"/> bundle the query path carries.
    /// </summary>
    internal sealed record MatchCandidate(
        EsriFeatureQuery Query,
        Feature Feature,
        long ObjectId,
        IGeometry? QueryGeometry,
        IGeometryRelations Relations,
        string? UniqueId = null,
        bool WherePushedDown = false,
        TemporalExtentFields? TimeFields = null);
}
