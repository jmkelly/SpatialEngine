using Spatial.Contracts;
using Spatial.Contracts.Providers;
using Spatial.Core.Features;
using Spatial.Core.Features.Query;

namespace Spatial.Spike.QueryBaseline;

/// <summary>
/// The four measured paths for one request, over one store. A and B are store
/// calls plus the in-adapter mirror; C is the per-feature read face; D is the
/// emulated ceiling (see <see cref="EmulatedPushdown"/>) — it is labelled as an
/// emulation in the report so it is never read as a shipped path.
/// </summary>
internal sealed class QueryRunner
{
    /// <summary>The dataset the spike loads the world-cities snapshot into.</summary>
    internal const string Dataset = "public.spike_world_cities";

    private readonly IFeatureStore _store;
    private readonly IFeatureLookup? _lookup;
    private readonly LayerModel _model;
    private readonly IReadOnlyList<Feature> _resident;

    internal QueryRunner(IFeatureStore store, IFeatureLookup? lookup, LayerModel model, IReadOnlyList<Feature> resident)
    {
        _store = store;
        _lookup = lookup;
        _model = model;
        _resident = resident;
    }

    /// <summary>Path A — the production path: <c>ScanAsync</c> then in-adapter filtering.</summary>
    internal async Task<Outcome> ScanAsync(
        QueryRequest request,
        string variant,
        Predicate? where,
        CancellationToken cancellationToken)
    {
        var batches = await _store.ScanAsync(Dataset, cancellationToken);
        var matched = AdapterMirror.Match(batches, _model, where, request.Envelope, cancellationToken);
        return Shape(request, variant, matched, Materialised(batches), cancellationToken);
    }

    /// <summary>
    /// Path B — the same request with the predicate pushed into the store. The
    /// store applies bbox and where, but its readers have no row cap or
    /// cursor, so every <em>matching</em> row still arrives as a batch page.
    /// The adapter pipeline is left exactly as it is, so the delta against A
    /// is the pushdown and nothing else.
    /// </summary>
    internal async Task<Outcome> PushdownAsync(
        QueryRequest request,
        string variant,
        Predicate? where,
        Predicate? storeFilter,
        bool paged,
        CancellationToken cancellationToken)
    {
        var page = await _store.QueryAsync(Dataset, request.Plan(paged, variant), cancellationToken);
        var matched = AdapterMirror.Match(page.Batches, _model, where, request.Envelope, cancellationToken);
        return Shape(request, variant, matched, Materialised(page.Batches), cancellationToken);
    }

    /// <summary>
    /// Whether this store accepts an attribute filter in
    /// <c>QueryAsync</c>. The in-process stores (memory, demo) answer no and
    /// push the bbox only; PostGIS answers yes (ADR-0028). Probed once, so
    /// the report can say which pushdown each number is for.
    /// </summary>
    internal async Task<bool> SupportsAttributePushdownAsync(QueryRequest request, CancellationToken cancellationToken)
    {
        try
        {
            await _store.QueryAsync(
                Dataset,
                new FeatureQuery(Where: request.StoreFilter, BoundingBox: request.Bbox),
                cancellationToken);
            return true;
        }
        catch (SpatialException failure) when (failure.Code == SpatialException.InvalidArguments)
        {
            return false;
        }
    }

    /// <summary>
    /// Whether the per-feature read face can be measured at all on this layer:
    /// the store has to implement it, and the dataset has to declare an
    /// identity column — a keyless dataset is refused with
    /// <c>invalid.arguments</c> by name (ADR-0140), not answered empty, so
    /// path C is reported as unavailable rather than failed.
    /// </summary>
    internal string? IdentityUnavailable
    {
        get
        {
            if (_lookup is null)
            {
                return "the store under test does not implement IFeatureLookup.";
            }

            return _model.IdentityIndex < 0
                ? $"dataset '{Dataset}' declares no integer identity column, so the per-feature read face refuses it (ADR-0140)."
                : null;
        }
    }

    /// <summary>Path C — the per-feature read: resolve a page of ids by identity.</summary>
    internal async Task<Outcome> IdentityAsync(QueryRequest request, CancellationToken cancellationToken)
    {
        if (_lookup is null)
        {
            throw new InvalidOperationException($"the {request.Scenario} store under test does not implement IFeatureLookup.");
        }

        var features = await _lookup.GetAsync(Dataset, PageIdentity(request), cancellationToken);
        return new Outcome(features.Count, features.Count);
    }

    /// <summary>Path D — the emulated ceiling: predicate, ordering and page all applied store-side.</summary>
    internal Task<Outcome> EmulatedPushdownAsync(
        QueryRequest request,
        string variant,
        Predicate? where,
        CancellationToken cancellationToken) =>
        Task.FromResult(EmulatedPushdown.Run(request, variant, _resident, _model, where, cancellationToken));

    /// <summary>
    /// The in-adapter result shape: ordering first (as
    /// <c>FeatureQueryEngine.Project</c> does), then count, statistics or page.
    /// </summary>
    private Outcome Shape(
        QueryRequest request,
        string variant,
        IReadOnlyList<Row> rows,
        int materialised,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (QueryRequest.IsCountOnly(variant))
        {
            return new Outcome(materialised, AdapterMirror.Order([.. rows], _model, request.OrderByField, request.OrderDescending).Count);
        }

        if (QueryRequest.IsStatistics(variant))
        {
            var groups = AdapterMirror.Statistics([.. rows], _model, _model.CountryIndex, _model.PopulationIndex);
            return new Outcome(materialised, groups.Count);
        }

        var ordered = AdapterMirror.Order([.. rows], _model, request.OrderByField, request.OrderDescending);
        return new Outcome(materialised, AdapterMirror.Page(ordered, 0, request.ResultRecordCount).Count);
    }

    /// <summary>The rows a batch list materialised.</summary>
    private static int Materialised(IReadOnlyList<FeatureBatch> batches)
    {
        var total = 0;
        foreach (var batch in batches)
        {
            total += batch.Count;
        }

        return total;
    }

    /// <summary>
    /// The 25 identities a client would already hold for the page it is
    /// rendering — resolved once, outside the measured call, because the
    /// per-feature read path starts from ids it has.
    /// </summary>
    private IReadOnlyList<FeatureId> PageIdentity(QueryRequest request) =>
        [.. EmulatedPushdown.PageFeatures(request, _resident, _model, request.ParsedWhere())
            .Take(request.ResultRecordCount)
            .Select(feature => feature.Id)];
}

/// <summary>
/// Path D: the ceiling the store-query contract (<c>SpatialEngine-u2x.9</c>)
/// would reach, emulated over the rows the store holds. The predicate,
/// ordering and <c>offset</c>/<c>limit</c> are applied in one pass and only
/// the rows the response needs are materialised — no <c>FeatureBatch</c> is
/// built for rows a page, a count or a grouped aggregate never returns.
///
/// This is an emulation, not a store call: <c>IFeatureStore</c> has no
/// ordering, limit, projection or aggregation face today, so nothing in the
/// product can do this. The number is therefore two-sided: it shows how much
/// <em>materialisation and allocation</em> a store-side pushdown would save,
/// while still paying the in-process predicate scan that a real database
/// would have done with an index.
/// </summary>
internal static class EmulatedPushdown
{
    internal static Outcome Run(
        QueryRequest request,
        string variant,
        IReadOnlyList<Feature> resident,
        LayerModel model,
        Predicate? where,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (QueryRequest.IsCountOnly(variant))
        {
            // A count materialises nothing: no row is built at all.
            return new Outcome(0, Matches(request, resident, model, where).Count());
        }

        if (QueryRequest.IsStatistics(variant))
        {
            var groups = Groups(request, resident, model, where);
            return new Outcome(groups.Count, groups.Count);
        }

        var page = PageFeatures(request, resident, model, where);
        return new Outcome(page.Count, page.Count);
    }

    /// <summary>The paged rows, in the requested order, without building a batch per row.</summary>
    internal static List<Feature> PageFeatures(
        QueryRequest request,
        IReadOnlyList<Feature> resident,
        LayerModel model,
        Predicate? where) =>
        [.. Order([.. Matches(request, resident, model, where)], model, request).Take(PageCap(request))];

    private static int PageCap(QueryRequest request) => Math.Min(request.ResultRecordCount, 1000);

    private static HashSet<string> Groups(
        QueryRequest request,
        IReadOnlyList<Feature> resident,
        LayerModel model,
        Predicate? where)
    {
        var groups = new HashSet<string>(StringComparer.Ordinal);
        foreach (var feature in Matches(request, resident, model, where))
        {
            groups.Add(feature[model.CountryIndex].StringValue);
        }

        return groups;
    }

    private static IEnumerable<Feature> Matches(
        QueryRequest request,
        IReadOnlyList<Feature> resident,
        LayerModel model,
        Predicate? where)
    {
        var envelope = request.Envelope;
        long ordinal = 0;
        foreach (var feature in resident)
        {
            ordinal++;
            if (AdapterMirror.MatchesWhere(feature, where) && Intersects(feature, model, envelope))
            {
                yield return feature;
            }
        }
    }

    private static List<Feature> Order(List<Feature> matched, LayerModel model, QueryRequest request)
    {
        var index = model.Schema.IndexOf(request.OrderByField);
        var ordered = request.OrderDescending
            ? matched.OrderByDescending(feature => feature[index], AttributeOrder.Instance)
            : matched.OrderBy(feature => feature[index], AttributeOrder.Instance);
        return [.. ordered];
    }

    private static bool Intersects(Feature feature, LayerModel model, Spatial.Core.Geometry.Envelope envelope) =>
        model.GeometryIndex >= 0
        && feature[model.GeometryIndex].Kind == AttributeKind.Geometry
        && feature[model.GeometryIndex].GeometryValue.Envelope is { } value
        && value.Intersects(envelope);
}
