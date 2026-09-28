using System.Globalization;
using System.Text;
using System.Text.Json;
using Spatial.Contracts;
using Spatial.Contracts.Providers;
using Spatial.Core.Features;
using Spatial.Core.Features.Query;
using Spatial.Core.Geometry;
using Spatial.Querying;

namespace Spatial.Stores.ArcGisRest;

/// <summary>
/// The ArcGIS REST consuming provider (ADR-0035): an in-process
/// <see cref="IDataCatalogue"/>, <see cref="IFeatureStore"/> and
/// <see cref="IFeatureAggregateStore"/> over one
/// configured remote FeatureServer/MapServer. Layers become datasets; Esri
/// JSON geometries and features are converted to core values by
/// <see cref="ArcGisRestMapper"/>. Reads are paginated and cancellable; the
/// remote <c>where</c> is rendered from the engine's closed filter grammar,
/// never a caller string. Writes and dataset creation are unsupported (a
/// typed <c>invalid.arguments</c>).
/// </summary>
public sealed class ArcGisRestStore : IDataCatalogue, IFeatureStore, IFeatureAggregateStore
{
    private readonly HttpClient _http;
    private readonly string _baseUrl;
    private readonly string? _token;

    /// <summary>Creates a store over <paramref name="baseUrl"/> using the ambient <see cref="HttpClient"/>.</summary>
    public ArcGisRestStore(HttpClient http, ArcGisRestServiceOptions options, string? token = null)
        : this(http, Validate(options), token)
    {
    }

    internal ArcGisRestStore(HttpClient http, string baseUrl, string? token)
    {
        _http = http;
        _baseUrl = baseUrl;
        _token = string.IsNullOrWhiteSpace(token) ? null : token;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<DatasetSummary>> ListAsync(string? pattern = null, CancellationToken cancellationToken = default)
    {
        using var document = await GetJsonAsync(_baseUrl, [], cancellationToken);
        var summaries = new List<DatasetSummary>();
        foreach (var layer in ArcGisRestMapper.Layers(document.RootElement))
        {
            var layerId = layer.GetProperty("id").GetInt32();
            if (!ArcGisRestMapper.Like(pattern, ArcGisRestMapper.LayerName(layer, layerId)))
            {
                continue;
            }

            using var metadata = await GetJsonAsync(LayerUrl(layerId), [], cancellationToken);
            summaries.Add(ArcGisRestMapper.Summary(layerId, metadata.RootElement));
        }

        return summaries;
    }

    /// <inheritdoc />
    public async Task<DatasetDescription> DescribeAsync(string dataset, CancellationToken cancellationToken = default)
    {
        var layerId = ArcGisRestMapper.ParseLayerId(dataset);
        using var metadata = await GetJsonAsync(LayerUrl(layerId), [], cancellationToken);
        return ArcGisRestMapper.Describe(layerId, metadata.RootElement);
    }

    /// <inheritdoc />
    public Task<string> CreateAsync(string dataset, FeatureBatch sample, int srid, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sample);
        throw SpatialException.BadArguments("An ArcGIS REST service is read-only; dataset creation is not supported.");
    }

    /// <inheritdoc />
    public Task<int> WriteAsync(string dataset, FeatureBatch batch, string? transaction = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(batch);
        throw SpatialException.BadArguments("An ArcGIS REST service is read-only; writes are not supported.");
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<FeatureBatch>> ScanAsync(
        string dataset, CancellationToken cancellationToken = default)
    {
        var (schema, features) = await FetchAsync(dataset, FeatureQuery.All, cancellationToken).ConfigureAwait(false);
        return [new FeatureBatch(schema, features)];
    }

    /// <summary>
    /// The plan read and the reduction faces over the reference executor: this
    /// provider pages the remote service for itself, so the identity
    /// restriction, the attribute predicate, the bounding-box pre-filter, the
    /// projection, the ordering, the paging, the count, the distinct set and
    /// the grouped aggregate are all applied to the rows the remote returned,
    /// in the engine's terms (ADR-0074 §4). The plan's predicate is rendered
    /// into the remote <c>where</c> so the remote does the restricting, and
    /// the reference evaluator then finishes the plan over what came back, so
    /// the answer is the reference's answer and not the remote dialect's. It
    /// is correct because the reference judges it, not because the remote
    /// service could express it.
    /// </summary>
    public async Task<FeatureQueryPage> QueryAsync(
        string dataset, FeatureQuery query, CancellationToken cancellationToken = default)
    {
        var (schema, features) = await FetchAsync(dataset, query, cancellationToken).ConfigureAwait(false);
        return FeaturePlanExecutor.Execute(schema, features, query, cancellationToken);
    }

    /// <inheritdoc cref="QueryAsync"/>
    public Task<int> CountAsync(string dataset, FeatureQuery query, CancellationToken cancellationToken = default) =>
        FeaturePlanFallback.CountAsync(this, dataset, query, cancellationToken);

    /// <inheritdoc cref="QueryAsync"/>
    public Task<DistinctPage> DistinctAsync(
        string dataset, FeatureQuery query, DistinctQuery distinct, CancellationToken cancellationToken = default) =>
        FeaturePlanFallback.DistinctAsync(this, dataset, query, distinct, cancellationToken);

    /// <inheritdoc cref="QueryAsync"/>
    public Task<AggregatePage> AggregateAsync(
        string dataset, FeatureQuery query, AggregateQuery aggregate, CancellationToken cancellationToken = default) =>
        FeaturePlanFallback.AggregateAsync(this, dataset, query, aggregate, cancellationToken);

    /// <summary>
    /// Every feature of a remote layer, with the plan's pushable restriction
    /// applied remotely: the attribute predicate is rendered into the service's
    /// own <c>where</c> from the core-typed tree, and the bounding box becomes
    /// the service's envelope parameter. Only the members a remote read can
    /// express cross the wire; the rest is the caller's plan.
    /// </summary>
    private async Task<(FeatureSchema Schema, List<Feature> Features)> FetchAsync(
        string dataset, FeatureQuery query, CancellationToken cancellationToken)
    {
        var layerId = ArcGisRestMapper.ParseLayerId(dataset);
        using var metadata = await GetJsonAsync(LayerUrl(layerId), [], cancellationToken).ConfigureAwait(false);
        var description = ArcGisRestMapper.Describe(layerId, metadata.RootElement);
        if (query.Ids is { Count: > 0 })
        {
            // Rejected by name rather than ignored: restricting by identity
            // here would read the whole layer and drop rows afterwards, and
            // the remote service has an identity request of its own.
            throw SpatialException.BadArguments(
                "The ArcGIS REST store does not support the identity restriction of a feature query plan; query the remote layer's own objectIds instead.");
        }

        var where = ArcGisRestMapper.RenderWhere(query.Where);
        var features = await FetchAllAsync(
            description, query.BoundingBox, where, ArcGisRestMapper.PageSize(metadata.RootElement), cancellationToken)
            .ConfigureAwait(false);
        return ((FeatureSchema)description.Schema, features);
    }

    private async Task<List<Feature>> FetchAllAsync(
        DatasetDescription description, BoundingBox? bbox, string? where, int pageSize, CancellationToken cancellationToken)
    {
        var layerId = ArcGisRestMapper.ParseLayerId(description.Id);
        var features = new List<Feature>();
        var offset = 0;
        while (true)
        {
            using var page = await GetJsonAsync(QueryUrl(layerId, description, bbox, where, offset, pageSize), [], cancellationToken);
            var pageFeatures = ArcGisRestMapper.ReadFeatures(page.RootElement, description);
            features.AddRange(pageFeatures);
            offset += pageFeatures.Count;
            if (!ArcGisRestMapper.Exceeded(page.RootElement) || pageFeatures.Count == 0)
            {
                break;
            }
        }

        return features;
    }

    private async Task<JsonDocument> GetJsonAsync(string url, IReadOnlyList<KeyValuePair<string, string>> parameters, CancellationToken cancellationToken)
    {
        var requestUrl = BuildUrl(url, parameters);
        HttpResponseMessage response;
        try
        {
            response = await _http.GetAsync(requestUrl, cancellationToken);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            throw SpatialException.Unavailable($"The ArcGIS REST service at {SafeUrl(url)} could not be reached.", exception);
        }

        using (response)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            var document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.TryGetProperty("error", out var error))
            {
                var mapped = ArcGisRestMapper.MapError(error);
                document.Dispose();
                throw mapped;
            }

            if (!response.IsSuccessStatusCode)
            {
                document.Dispose();
                throw SpatialException.Unavailable($"The ArcGIS REST service at {SafeUrl(url)} returned HTTP {(int)response.StatusCode}.");
            }

            return document;
        }
    }

    private string BuildUrl(string url, IReadOnlyList<KeyValuePair<string, string>> parameters)
    {
        var builder = new StringBuilder(url);
        var separator = url.Contains('?') ? '&' : '?';
        foreach (var (key, value) in parameters)
        {
            builder.Append(separator).Append(Uri.EscapeDataString(key)).Append('=').Append(Uri.EscapeDataString(value));
            separator = '&';
        }

        if (_token is not null)
        {
            builder.Append(separator).Append("token=").Append(Uri.EscapeDataString(_token));
        }

        return builder.ToString();
    }

    private string QueryUrl(int layerId, DatasetDescription description, BoundingBox? bbox, string? where, int offset, int pageSize)
    {
        var parameters = QueryParameters(description, bbox, where, offset, pageSize);
        return $"{LayerUrl(layerId)}/query" + QueryString(parameters);
    }

    private static List<KeyValuePair<string, string>> QueryParameters(
        DatasetDescription description, BoundingBox? bbox, string? where, int offset, int pageSize)
    {
        var parameters = new List<KeyValuePair<string, string>>
        {
            new("f", "json"),
            new("outFields", "*"),
            new("returnGeometry", "true"),
            new("resultOffset", offset.ToString(CultureInfo.InvariantCulture)),
            new("resultRecordCount", pageSize.ToString(CultureInfo.InvariantCulture)),
            new("orderByFields", OrderByField(description)),
        };
        if (description.Srid > 0)
        {
            parameters.Add(new("outSR", ArcGisRestMapper.EnvelopeSpatialReference(description.Srid)));
        }

        AddOrdinateSelection(parameters, description.GeometryLayout);

        if (where is not null)
        {
            parameters.Add(new("where", where));
        }

        if (bbox is { } box)
        {
            AddEnvelope(parameters, box, description.Srid);
        }

        return parameters;
    }

    /// <summary>
    /// The stable paging key (T-027): the remote object-id field, so pages
    /// that a remote does not order deterministically cannot overlap or drop
    /// rows across resultOffset windows.
    /// </summary>
    private static string OrderByField(DatasetDescription description) =>
        description.IdColumns is { Count: > 0 } ids ? ids[0] : "OBJECTID";

    /// <summary>
    /// A Feature Server only returns the extra ordinates a query asks for
    /// (spec §9.1.4 <c>returnZ</c>/<c>returnM</c>), so a layer that declares
    /// them has to be asked for them: the description advertises
    /// <c>hasZ</c>/<c>hasM</c> (ADR-0084), and that claim has to be backed by
    /// the read path (ADR-0091). A two-dimensional layer asks for neither, so
    /// the remote keeps its own default.
    /// </summary>
    private static void AddOrdinateSelection(List<KeyValuePair<string, string>> parameters, CoordinateLayout layout)
    {
        if (layout.HasZ())
        {
            parameters.Add(new("returnZ", "true"));
        }

        if (layout.HasM())
        {
            parameters.Add(new("returnM", "true"));
        }
    }

    private static void AddEnvelope(List<KeyValuePair<string, string>> parameters, BoundingBox box, int srid)
    {
        parameters.Add(new("geometry", FormattableString.Invariant($"{{\"xmin\":{box.MinX},\"ymin\":{box.MinY},\"xmax\":{box.MaxX},\"ymax\":{box.MaxY}}}")));
        parameters.Add(new("geometryType", "esriGeometryEnvelope"));
        parameters.Add(new("spatialRel", "esriSpatialRelEnvelopeIntersects"));
        if (srid > 0)
        {
            parameters.Add(new("inSR", ArcGisRestMapper.EnvelopeSpatialReference(srid)));
        }
    }

    private string LayerUrl(int layerId) => $"{_baseUrl}/{layerId.ToString(CultureInfo.InvariantCulture)}";

    private static string QueryString(IReadOnlyList<KeyValuePair<string, string>> parameters)
    {
        var builder = new StringBuilder();
        var separator = '?';
        foreach (var (key, value) in parameters)
        {
            builder.Append(separator).Append(Uri.EscapeDataString(key)).Append('=').Append(Uri.EscapeDataString(value));
            separator = '&';
        }

        return builder.ToString();
    }

    private static string SafeUrl(string url) => url.Contains('?') ? url[..url.IndexOf('?')] : url;

    private static string Validate(ArcGisRestServiceOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(options.Url)
            || !Uri.TryCreate(options.Url, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new InvalidOperationException($"ArcGisRest service '{options.Name}' needs an absolute http(s) URL.");
        }

        return options.Url.TrimEnd('/');
    }
}
