using System.Globalization;
using System.Text;
using System.Text.Json;
using Spatial.Core.Features;
using Spatial.PluginSdk;
using Spatial.PluginSdk.Providers;

namespace Spatial.Provider.ArcGisRest;

/// <summary>
/// The ArcGIS REST consuming provider (ADR-0035): an in-process
/// <see cref="IDataCatalogue"/> and <see cref="IFeatureStore"/> over one
/// configured remote FeatureServer/MapServer. Layers become datasets; Esri
/// JSON geometries and features are converted to core values by
/// <see cref="ArcGisRestMapper"/>. Reads are paginated and cancellable; the
/// remote <c>where</c> is rendered from the engine's closed filter grammar,
/// never a caller string. Writes and dataset creation are unsupported (a
/// typed <c>invalid.arguments</c>).
/// </summary>
public sealed class ArcGisRestStore : IDataCatalogue, IFeatureStore
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
    public Task<IReadOnlyList<FeatureBatch>> ScanAsync(string dataset, CancellationToken cancellationToken = default) =>
        QueryAsync(dataset, bbox: null, filter: null, cancellationToken);

    /// <inheritdoc />
    public async Task<IReadOnlyList<FeatureBatch>> QueryAsync(
        string dataset, BoundingBox? bbox = null, string? filter = null, CancellationToken cancellationToken = default)
    {
        var layerId = ArcGisRestMapper.ParseLayerId(dataset);
        using var metadata = await GetJsonAsync(LayerUrl(layerId), [], cancellationToken);
        var description = ArcGisRestMapper.Describe(layerId, metadata.RootElement);
        var where = ArcGisRestMapper.RenderWhere(filter);
        var features = await FetchAllAsync(description, bbox, where, ArcGisRestMapper.PageSize(metadata.RootElement), cancellationToken);
        return [new FeatureBatch(description.Schema, features)];
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
        };
        if (description.Srid > 0)
        {
            parameters.Add(new("outSR", ArcGisRestMapper.EnvelopeSpatialReference(description.Srid)));
        }

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
