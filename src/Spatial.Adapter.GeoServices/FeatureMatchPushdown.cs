using Spatial.Contracts;
using Spatial.Contracts.Providers;
using Spatial.Core.Features;
using Spatial.Core.Features.Query;
using Spatial.Core.Geometry;
using Spatial.Esri.Codec;

namespace Spatial.Adapter.GeoServices;

/// <summary>
/// The served feature-match envelope compiled onto the store's query surface
/// (ADR-0110): <c>objectIds</c> becomes the plan's identity restriction, the
/// Esri <c>where</c> grammar and the <c>time</c> extent its attribute
/// predicate, and the query geometry's envelope the bounding-box pre-filter —
/// so the match loop is handed the candidate rows instead of reading the whole
/// table to reject them one at a time (SpatialEngine-u2x.11).
///
/// <para>
/// The compilation is decided <em>before</em> the store is asked, and it is
/// all-or-nothing: a plan is compiled only when the whole envelope is
/// expressible, and a request that is not expressible never reaches a store at
/// all. There is no "try it, then scan" — a fallback after a pushdown attempt
/// is the easiest way to break principle 15, because the answer would depend
/// on what the store happened to do with the plan.
/// </para>
///
/// <para>
/// Two rules decide whether a layer can push anything at all, and both are
/// about identity rather than about dialect support. A layer whose
/// <c>OBJECTID</c> is the scan ordinal (ADR-0037) cannot: a read that returned
/// only the matching rows would renumber that key, so the same feature would
/// come back with an object id that depends on the query (ADR-0097). And a
/// <c>uniqueIds</c> request cannot be answered by any restricted read, because
/// the refusal it earns on a layer with no string-or-guid identity field is
/// raised per feature — a narrower read could return no feature to refuse on,
/// and a refused request would become an empty answer.
/// </para>
///
/// <para>
/// What is pushed is a <em>pre-filter</em>, never the answer: it selects a
/// superset of the matching features and the in-memory matcher still decides
/// each of them (ADR-0074 §4). That is what makes this a refactor rather than
/// a behaviour change, and it is why the topology relations can ride along on
/// the same box: every one of them implies an intersection, so the envelope
/// pre-filter admits everything the relation can accept and the relation
/// itself is still evaluated per feature.
/// </para>
/// </summary>
internal static class FeatureMatchPushdown
{
    /// <summary>
    /// The plan the match envelope becomes, or <c>null</c> when this request
    /// is not expressible and the caller reads the whole dataset.
    /// </summary>
    public static FeatureQuery? Compile(FeatureSpatialMatcher.QuerySpec spec)
    {
        if (!Expressible(spec))
        {
            return null;
        }

        var clause = Clause(spec);
        var box = Box(spec.QueryGeometry);
        if (clause is null && box is null && spec.Query.ObjectIds is null)
        {
            // Nothing to restrict: the scan is the plan, and asking for it as
            // a query would be a round trip that reads every row anyway.
            return null;
        }

        return new FeatureQuery(
            Ids: Identities(spec),
            Where: clause,
            BoundingBox: box);
    }

    /// <summary>
    /// Whether the whole envelope is expressible on this layer. Each clause is
    /// a refusal by name, decided from the request and the layer's identity
    /// model alone — never from what a store turns out to support.
    /// </summary>
    private static bool Expressible(FeatureSpatialMatcher.QuerySpec spec)
    {
        // The identity rule (ADR-0097): a store that returned only the
        // matching rows would renumber a scan-ordinal OBJECTID.
        if (!spec.Scheme.IsIdentity || spec.Dataset.IdColumns is not [var _])
        {
            return false;
        }

        // A unique-id request is refused by name per feature, so it is never
        // asked of a restricted read (see the class note).
        if (spec.Query.UniqueIds is not null)
        {
            return false;
        }

        // An unsupported spatialRel is refused by name per feature too, and a
        // restricted read that matched nothing would answer instead.
        return Supported(spec.Query.SpatialRel);
    }

    private static bool Supported(string spatialRel) =>
        spatialRel is EsriFeatureQuery.EnvelopeIntersects
            or EsriFeatureQuery.Contains
            or EsriFeatureQuery.Within
            or EsriFeatureQuery.Touches
            or EsriFeatureQuery.Overlaps
            or EsriFeatureQuery.Crosses
            or EsriFeatureQuery.Intersects;

    /// <summary>
    /// The object ids as the plan's identity restriction. The values are the
    /// layer's own identity column, so the store reads the rows by the same
    /// key it numbers them by; an empty list is a request that matches nothing
    /// and every store answers it as such.
    /// </summary>
    private static FeatureId[]? Identities(FeatureSpatialMatcher.QuerySpec spec) =>
        spec.Query.ObjectIds?.Select(EsriObjectIdScheme.ToFeatureId).ToArray();

    /// <summary>
    /// The attribute predicate: the Esri <c>where</c> clause (ADR-0097) and
    /// the <c>time</c> extent, as one conjunction.
    /// </summary>
    private static Predicate? Clause(FeatureSpatialMatcher.QuerySpec spec)
    {
        var where = EsriWhereResolver.Pushdown(spec.Query.Where, spec.Scheme, spec.Dataset);
        var time = Time(spec.Dataset, spec.Query.Time);
        return (where, time) switch
        {
            (null, null) => null,
            (not null, null) => where,
            (null, not null) => time,
            _ => new Predicate.Every([where!, time!]),
        };
    }

    /// <summary>
    /// The <c>time</c> extent as a predicate over the layer's date fields.
    ///
    /// <para>
    /// A layer with a temporal designation (ADR-0175) is matched by the feature
    /// extent rule instead — the designated start and end bounds straddling the
    /// window — so its pushed clause is that rule's overlaps shape over the
    /// designated pair: the start is not after the window's end, or the end is
    /// not before its start. Overlaps is the superset of all three relations
    /// (a feature that covers the window, or is covered by it, intersects it),
    /// so one clause stays a superset whichever relation the matcher applies,
    /// and a row whose extent straddles the window — no instant of it inside,
    /// which the undesignated clause would drop — is still read.
    /// </para>
    /// </summary>
    private static Predicate? Time(DatasetDescription dataset, EsriTimeExtent? time)
    {
        if (time is not { } extent)
        {
            return null;
        }

        return dataset.TimeFields is { IsEmpty: false } fields
            ? DesignatedTime(dataset, fields, extent)
            : AnyDateTime(dataset, extent);
    }

    /// <summary>
    /// The designated pair's overlaps clause. A designation naming a field the
    /// schema does not have contributes no clause for that bound, which is the
    /// same infinite bound the extent reads.
    /// </summary>
    private static Predicate? DesignatedTime(DatasetDescription dataset, TemporalExtentFields fields, EsriTimeExtent extent)
    {
        var terms = new List<Predicate>();
        AddStartTerm(dataset, fields, extent, terms);
        AddEndTerm(dataset, fields, extent, terms);

        return terms.Count switch
        {
            0 => null,
            1 => terms[0],
            _ => new Predicate.Every(terms),
        };
    }

    private static void AddStartTerm(DatasetDescription dataset, TemporalExtentFields fields, EsriTimeExtent extent, List<Predicate> terms)
    {
        if (fields.StartField is { } start && extent.EndMs is { } end && Declared(dataset, start))
        {
            terms.Add(AtOrBefore(start, end));
        }
    }

    private static void AddEndTerm(DatasetDescription dataset, TemporalExtentFields fields, EsriTimeExtent extent, List<Predicate> terms)
    {
        if (fields.EndField is { } finish && extent.StartMs is { } from && Declared(dataset, finish))
        {
            terms.Add(AtOrAfter(finish, from));
        }
    }

    /// <summary>
    /// One designated field against one window bound: either the field is
    /// null — which is the infinite bound the extent reads, and admits every
    /// row that has no date at all — or it is on the side of the bound the
    /// overlaps clause needs.
    /// </summary>
    private static Predicate.Some Bound(string field, ComparisonOperator comparison, long bound) => new(
    [
        new Predicate.IsNull(new FieldRef(field), Negated: false),
        new Predicate.Compare(new FieldRef(field), comparison, Literal.FromMilliseconds(bound)),
    ]);

    /// <summary>The designated start is not after the window's end.</summary>
    private static Predicate.Some AtOrBefore(string field, long boundMs) =>
        Bound(field, ComparisonOperator.LessOrEqual, boundMs);

    /// <summary>The designated end is not before the window's start.</summary>
    private static Predicate.Some AtOrAfter(string field, long boundMs) =>
        Bound(field, ComparisonOperator.GreaterOrEqual, boundMs);

    private static bool Declared(DatasetDescription dataset, string field) => dataset.Schema.IndexOf(field) >= 0;

    /// <summary>
    /// The undesignated layer's rule: a feature matches when <em>any</em> of
    /// its date values falls inside the inclusive bounds, and a feature with no
    /// date value matches unconditionally (ArcGIS Server ignores <c>time</c>
    /// on a layer with no time-aware field). So the pushed predicate keeps
    /// every feature the rule can accept: for each date field, either the field
    /// is null — the feature may be one that matches for having no dates at
    /// all — or the field is inside the extent. A feature with dates outside
    /// the extent is the only one dropped, and the rule rejects it too.
    /// </summary>
    private static Predicate? AnyDateTime(DatasetDescription dataset, EsriTimeExtent extent)
    {

        var dates = dataset.Schema.Fields
            .Where(field => field.Kind == AttributeKind.DateTimeOffset)
            .Select(field => (Predicate)new Predicate.Some(
            [
                new Predicate.IsNull(new FieldRef(field.Name), Negated: false),
                Within(field.Name, extent),
            ]))
            .ToArray();

        return dates.Length switch
        {
            // A layer with no date field matches every feature whatever the
            // extent says, so there is nothing to restrict.
            0 => null,
            1 => dates[0],
            _ => new Predicate.Some(dates),
        };
    }

    /// <summary>One date field against the inclusive bounds, where a null bound is infinite.</summary>
    private static Predicate.Every Within(string field, EsriTimeExtent extent) => new Predicate.Every(
    [
        extent.StartMs is { } start
            ? new Predicate.Compare(new FieldRef(field), ComparisonOperator.GreaterOrEqual, Literal.FromMilliseconds(start))
            : Predicate.All,
        extent.EndMs is { } end
            ? new Predicate.Compare(new FieldRef(field), ComparisonOperator.LessOrEqual, Literal.FromMilliseconds(end))
            : Predicate.All,
    ]);

    /// <summary>
    /// The query geometry's envelope, in the layer's own CRS — the geometry
    /// the match loop tests against is already the transformed one
    /// (ADR-0020's canonical interchange and the pre-transform the query path
    /// does), so the box the store is asked for and the box the matcher
    /// computes are the same four numbers.
    /// </summary>
    private static BoundingBox? Box(IGeometry? queryGeometry) =>
        queryGeometry?.Envelope is { } envelope
            ? new BoundingBox(envelope.MinX, envelope.MinY, envelope.MaxX, envelope.MaxY)
            : null;
}
