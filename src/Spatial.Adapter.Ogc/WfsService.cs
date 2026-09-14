using System.Globalization;
using Microsoft.AspNetCore.Http;
using Spatial.Core.Features;
using Spatial.Core.Geometry;
using Spatial.PluginSdk;
using Spatial.PluginSdk.Providers;

namespace Spatial.Adapter.Ogc;

/// <summary>
/// The OGC Web Feature Service 2.0.0 projection (ADR-0053 §3):
/// GetCapabilities, DescribeFeatureType and GetFeature over a map's feature
/// layers. GetFeature returns GeoJSON (<c>application/geo+json</c>); GML is an
/// explicit non-goal and is rejected with a typed <c>InvalidParameterValue</c>
/// ServiceException, with the capabilities document advertising only the
/// served output formats. Queries go through <see cref="IFeatureStore.QueryAsync"/>.
/// Paging is <c>startIndex</c>/<c>count</c> over a stable order (the requested
/// <c>sortBy</c>, else feature-id order within the requested layer order), so
/// a client can page through the whole result and terminate; the GeoJSON
/// envelope carries <c>numberMatched</c>, <c>numberReturned</c> and, while
/// features remain, <c>next</c>. A requested <c>srsName</c> reprojects the
/// response geometries. Unimplemented selectors (FES <c>filter</c>, CQL,
/// resource id), property projection (<c>propertyName</c>/<c>aliases</c>),
/// xlink resolution (<c>resolve</c>/<c>resolveDepth</c>/<c>resolveTimeout</c>)
/// and the value/stored-query and transactional operations are
/// rejected by name rather than silently ignored.
/// </summary>
internal static class WfsService
{
    public static async Task<IResult> HandleAsync(
        string name, OgcParameters parameters, OgcRequestServices services, OgcOptions options, HttpContext context, CancellationToken cancellationToken)
    {
        parameters.RequiredService("WFS");
        var map = await services.ResolveMapAsync(name, MapService.Wfs, "WFS", cancellationToken);
        var request = parameters.RequiredRequest();
        return request.ToUpperInvariant() switch
        {
            "GETCAPABILITIES" => await CapabilitiesAsync(map, services, options, context, cancellationToken),
            "DESCRIBEFEATURETYPE" => await DescribeAsync(map, parameters, services, cancellationToken),
            "GETFEATURE" => await GetFeatureAsync(map, parameters, services, options, context, cancellationToken),
            "GETPROPERTYVALUE" => throw OgcServiceException.NotSupported(
                "The WFS operation 'GetPropertyValue' is not supported; request GetFeature instead."),
            "LISTSTOREDQUERIES" => throw OgcServiceException.NotSupported(
                "The WFS operation 'ListStoredQueries' is not supported; this service exposes no stored queries."),
            "DESCRIBESTOREDQUERIES" => throw OgcServiceException.NotSupported(
                "The WFS operation 'DescribeStoredQueries' is not supported; this service exposes no stored queries."),
            "CREATESTOREDQUERY" => throw OgcServiceException.NotSupported(
                "The WFS operation 'CreateStoredQuery' is not supported; this service exposes no stored queries."),
            "DROPSTOREDQUERY" => throw OgcServiceException.NotSupported(
                "The WFS operation 'DropStoredQuery' is not supported; this service exposes no stored queries."),
            "TRANSACTION" => throw OgcServiceException.NotSupported(
                "The WFS operation 'Transaction' is not supported; the write path is the gated Esri edit verbs plus neutral ingest."),
            "LOCKFEATURE" => throw OgcServiceException.NotSupported(
                "The WFS operation 'LockFeature' is not supported; the write path is the gated Esri edit verbs plus neutral ingest."),
            "GETFEATUREWITHLOCK" => throw OgcServiceException.NotSupported(
                "The WFS operation 'GetFeatureWithLock' is not supported; the write path is the gated Esri edit verbs plus neutral ingest."),
            _ => throw OgcServiceException.NotSupported($"The WFS operation '{request}' is not supported."),
        };
    }

    private static async Task<IResult> CapabilitiesAsync(
        Map map, OgcRequestServices services, OgcOptions options, HttpContext context, CancellationToken cancellationToken)
    {
        var layers = new List<OgcLayer>();
        foreach (var layer in OgcLayers.FeatureLayers(map))
        {
            layers.Add(await services.LoadAsync(map, layer, cancellationToken));
        }

        var baseUrl = $"{context.Request.Scheme}://{context.Request.Host}{options.Root}/{map.Name}/wfs";
        var xml = await WfsCapabilities.BuildAsync(map, layers, baseUrl, services, options, cancellationToken);
        return Results.Text(xml, "application/xml");
    }

    private static async Task<IResult> DescribeAsync(
        Map map, OgcParameters parameters, OgcRequestServices services, CancellationToken cancellationToken)
    {
        var layers = OgcLayers.Select(map, parameters.List("typenames"));
        var loaded = new List<OgcLayer>(layers.Count);
        foreach (var layer in layers)
        {
            loaded.Add(await services.LoadAsync(map, layer, cancellationToken));
        }

        return Results.Text(WfsSchema.Build(loaded), "application/xml");
    }

    private static async Task<IResult> GetFeatureAsync(
        Map map, OgcParameters parameters, OgcRequestServices services, OgcOptions options, HttpContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureGeoJson(parameters.Get("outputformat"));
        RejectUnsupportedFilters(parameters);
        var layers = OgcLayers.Select(map, parameters.List("typenames"));
        var count = Count(parameters.Get("count"), options.MaxFeatures);
        var startIndex = StartIndex(parameters.Get("startindex"));
        var bbox = ParseBbox(parameters.Get("bbox"));
        var targetCrs = TargetCrs(parameters.Get("srsname"));

        var loaded = new List<OgcLayer>(layers.Count);
        foreach (var layer in layers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            loaded.Add(await services.LoadAsync(map, layer, cancellationToken));
        }

        var sort = ParseSortBy(parameters.Get("sortby"), loaded);
        var matched = new List<MatchedFeature>();
        for (var index = 0; index < loaded.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var features = await ReadAsync(services, loaded[index], bbox, cancellationToken);
            foreach (var feature in features)
            {
                matched.Add(new MatchedFeature(loaded[index].Description, feature, index));
            }
        }

        SortMatched(matched, sort);
        var numberMatched = matched.Count;
        var page = matched.Skip(startIndex).Take(count).ToArray();
        var projected = ReprojectPage(services, page, targetCrs, cancellationToken);
        var next = startIndex + page.Length < numberMatched
            ? NextLink(context, parameters, startIndex + page.Length, count)
            : null;

        return Results.Bytes(
            GeoJson.FeatureCollection(projected, numberMatched, page.Length, next), "application/geo+json");
    }

    private static async Task<IReadOnlyList<Feature>> ReadAsync(
        OgcRequestServices services, OgcLayer layer, WfsBbox? bbox, CancellationToken cancellationToken)
    {
        var projected = Project(services, layer, bbox, cancellationToken);
        var batches = await services.Features(layer.Store).QueryAsync(layer.Layer.Dataset, projected, null, cancellationToken);
        return batches
            .SelectMany(batch => batch.Features)
            .ToArray();
    }

    private static BoundingBox? Project(OgcRequestServices services, OgcLayer layer, WfsBbox? bbox, CancellationToken cancellationToken)
    {
        if (bbox is null)
        {
            return null;
        }

        var layerCrs = $"EPSG:{layer.Description.Srid.ToString(CultureInfo.InvariantCulture)}";
        var projected = OgcGeometry.Transform(bbox.Envelope, bbox.Crs, layerCrs, services.Transforms, cancellationToken);
        return new BoundingBox(projected.MinX, projected.MinY, projected.MaxX, projected.MaxY);
    }

    private static void EnsureGeoJson(string? outputFormat)
    {
        if (outputFormat is null)
        {
            return;
        }

        var normalized = outputFormat.ToLowerInvariant();
        if (normalized is "application/geo+json" or "application/json" or "json")
        {
            return;
        }

        throw normalized.Contains("gml", StringComparison.Ordinal)
            ? OgcServiceException.Invalid("GML output is not supported; request outputFormat=application/geo+json.")
            : OgcServiceException.Invalid(
                $"Unsupported WFS output format '{outputFormat}'; supported: application/geo+json.");
    }

    private static void RejectUnsupportedFilters(OgcParameters parameters)
    {
        foreach (var name in new[] { "filter", "cql_filter", "ecql_filter", "resourceid", "resourceids", "featureid", "storedquery_id", "storedqueryid" })
        {
            if (parameters.Get(name) is not null)
            {
                throw OgcServiceException.Invalid(
                    $"The '{name}' parameter is not supported; only 'bbox' subsetting is implemented.");
            }
        }

        foreach (var name in new[] { "propertyname", "aliases", "alias" })
        {
            if (parameters.Get(name) is not null)
            {
                throw OgcServiceException.Invalid(
                    $"The '{name}' parameter is not supported; full features are always returned.");
            }
        }

        foreach (var name in new[] { "resolve", "resolvedepth", "resolvetimeout" })
        {
            if (parameters.Get(name) is not null)
            {
                throw OgcServiceException.Invalid(
                    $"The '{name}' parameter is not supported; xlink resolution is not implemented.");
            }
        }
    }

    private static int Count(string? text, int max) =>
        text is null
            ? max
            : int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) && value >= 0
                ? Math.Min(value, max)
                : throw OgcServiceException.Invalid($"The 'count' parameter must be a non-negative integer, got '{text}'.");

    private static int StartIndex(string? text) =>
        text is null
            ? 0
            : int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) && value >= 0
                ? value
                : throw OgcServiceException.Invalid($"The 'startIndex' parameter must be a non-negative integer, got '{text}'.");

    private static string? TargetCrs(string? text)
    {
        if (text is null)
        {
            return null;
        }

        return OgcCrs.Resolve(text).Crs;
    }

    private static List<SortKey> ParseSortBy(string? text, IReadOnlyList<OgcLayer> loaded)
    {
        if (text is null)
        {
            return [];
        }

        var keys = new List<SortKey>();
        foreach (var token in text.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            keys.Add(ParseSortKey(token, loaded));
        }

        return keys.Count == 0
            ? throw OgcServiceException.Invalid("The 'sortBy' parameter must name at least one property.")
            : keys;
    }

    private static SortKey ParseSortKey(string token, IReadOnlyList<OgcLayer> loaded)
    {
        string property;
        string? order;
        var plus = token.LastIndexOf('+');
        if (plus >= 0)
        {
            property = token[..plus].Trim();
            order = token[(plus + 1)..].Trim();
        }
        else
        {
            var parts = token.Split([' ', '\t'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length is not (1 or 2))
            {
                throw OgcServiceException.Invalid(
                    $"The 'sortBy' entry '{token}' must be a property name with an optional A/D or ASC/DESC order.");
            }

            property = parts[0];
            order = parts.Length == 2 ? parts[1] : null;
        }

        if (string.IsNullOrEmpty(property))
        {
            throw OgcServiceException.Invalid($"The 'sortBy' entry '{token}' must name a property.");
        }

        var descending = order?.ToUpperInvariant() switch
        {
            null or "" or "A" or "ASC" => false,
            "D" or "DESC" => true,
            _ => throw OgcServiceException.Invalid(
                $"The 'sortBy' entry '{token}' has an unknown sort order '{order}'; use A/D or ASC/DESC."),
        };

        var found = false;
        foreach (var layer in loaded)
        {
            var index = FieldIndex(layer.Description.Schema, property);
            if (index < 0)
            {
                throw OgcServiceException.Invalid(
                    $"The 'sortBy' property '{property}' is not defined by layer '{layer.Name}'.");
            }

            if (layer.Description.Schema[index].Kind == AttributeKind.Geometry)
            {
                throw OgcServiceException.Invalid(
                    $"The 'sortBy' property '{property}' is a geometry and cannot order features.");
            }

            found = true;
        }

        if (!found)
        {
            throw OgcServiceException.Invalid("The 'sortBy' parameter names no selectable layer.");
        }

        return new SortKey(property, descending);
    }

    private static int FieldIndex(FeatureSchema schema, string property)
    {
        var exact = schema.IndexOf(property);
        if (exact >= 0)
        {
            return exact;
        }

        for (var index = 0; index < schema.Count; index++)
        {
            if (string.Equals(schema[index].Name, property, StringComparison.OrdinalIgnoreCase))
            {
                return index;
            }
        }

        return -1;
    }

    private static void SortMatched(List<MatchedFeature> matched, IReadOnlyList<SortKey> sort)
    {
        matched.Sort((left, right) =>
        {
            foreach (var key in sort)
            {
                var comparison = CompareAttribute(ValueOf(left, key.Property), ValueOf(right, key.Property));
                if (comparison != 0)
                {
                    return key.Descending ? -comparison : comparison;
                }
            }

            var layer = left.LayerIndex.CompareTo(right.LayerIndex);
            return layer != 0
                ? layer
                : string.CompareOrdinal(left.Feature.Id.Value, right.Feature.Id.Value);
        });
    }

    private static AttributeValue ValueOf(MatchedFeature matched, string property)
    {
        var index = FieldIndex(matched.Dataset.Schema, property);
        return index < 0 ? AttributeValue.Null : matched.Feature[index];
    }

    private static int CompareAttribute(AttributeValue left, AttributeValue right)
    {
        if (left.IsNull && right.IsNull)
        {
            return 0;
        }

        if (left.IsNull)
        {
            return 1;
        }

        if (right.IsNull)
        {
            return -1;
        }

        if (left.Kind != right.Kind)
        {
            return left.Kind.CompareTo(right.Kind);
        }

        return left.Kind switch
        {
            AttributeKind.Boolean => left.BooleanValue.CompareTo(right.BooleanValue),
            AttributeKind.Int64 => left.Int64Value.CompareTo(right.Int64Value),
            AttributeKind.Double => left.DoubleValue.CompareTo(right.DoubleValue),
            AttributeKind.String => string.CompareOrdinal(left.StringValue, right.StringValue),
            AttributeKind.DateTimeOffset => left.DateTimeOffsetValue.CompareTo(right.DateTimeOffsetValue),
            AttributeKind.Guid => left.GuidValue.CompareTo(right.GuidValue),
            _ => throw OgcServiceException.Invalid("The 'sortBy' property cannot order geometry values."),
        };
    }

    private static IReadOnlyList<(DatasetDescription Dataset, Feature Feature)> ReprojectPage(
        OgcRequestServices services, MatchedFeature[] page, string? targetCrs, CancellationToken cancellationToken)
    {
        if (targetCrs is null)
        {
            return page.Select(match => (match.Dataset, match.Feature)).ToArray();
        }

        var projected = new List<(DatasetDescription Dataset, Feature Feature)>(page.Length);
        foreach (var match in page)
        {
            cancellationToken.ThrowIfCancellationRequested();
            projected.Add((match.Dataset, ReprojectFeature(services, match.Dataset, match.Feature, targetCrs, cancellationToken)));
        }

        return projected;
    }

    private static Feature ReprojectFeature(
        OgcRequestServices services, DatasetDescription dataset, Feature feature, string targetCrs, CancellationToken cancellationToken)
    {
        var index = dataset.Schema.IndexOf(dataset.GeometryColumn);
        if (index < 0 || feature[index].IsNull || feature[index].Kind != AttributeKind.Geometry)
        {
            return feature;
        }

        var source = $"EPSG:{dataset.Srid.ToString(CultureInfo.InvariantCulture)}";
        if (string.Equals(source, targetCrs, StringComparison.OrdinalIgnoreCase))
        {
            return feature;
        }

        var geometry = services.Transforms.Transform(feature[index].GeometryValue, source, targetCrs, cancellationToken);
        var attributes = feature.Attributes.ToArray();
        attributes[index] = AttributeValue.FromGeometry(geometry);
        return new Feature(feature.Id, feature.Schema, attributes);
    }

    private static string NextLink(HttpContext context, OgcParameters parameters, int nextStart, int count)
    {
        var baseUrl = $"{context.Request.Scheme}://{context.Request.Host}{context.Request.Path}";
        var query = new List<string> { "service=WFS", "request=GetFeature" };
        var typenames = parameters.List("typenames");
        if (typenames.Count > 0)
        {
            query.Add($"typeNames={Uri.EscapeDataString(string.Join(',', typenames))}");
        }

        foreach (var name in new[] { "bbox", "srsName", "sortBy", "outputFormat" })
        {
            if (parameters.Get(name) is { } value)
            {
                query.Add($"{name}={Uri.EscapeDataString(value)}");
            }
        }

        query.Add($"count={count.ToString(CultureInfo.InvariantCulture)}");
        query.Add($"startIndex={nextStart.ToString(CultureInfo.InvariantCulture)}");
        return $"{baseUrl}?{string.Join('&', query)}";
    }

    private static WfsBbox? ParseBbox(string? text)
    {
        if (text is null)
        {
            return null;
        }

        var parts = text.Split(',', StringSplitOptions.TrimEntries);
        if (parts.Length is not (4 or 5))
        {
            throw OgcServiceException.Invalid(
                $"The 'bbox' parameter must be minx,miny,maxx,maxy[,crs], got '{text}'.");
        }

        var (crs, yFirst) = OgcCrs.Resolve(parts.Length == 5 ? parts[4] : "CRS:84");
        var bounds = OgcGeometry.ParseBbox(string.Join(',', parts[..4]));
        var xFirst = yFirst ? new Envelope(bounds.MinY, bounds.MinX, bounds.MaxY, bounds.MaxX) : bounds;
        return new WfsBbox(xFirst, crs);
    }

    /// <summary>A parsed WFS bbox in x-first coordinates plus its CRS identity.</summary>
    private sealed record WfsBbox(Envelope Envelope, string Crs);

    /// <summary>A bbox-matched feature plus its requested layer order.</summary>
    private sealed record MatchedFeature(DatasetDescription Dataset, Feature Feature, int LayerIndex);

    /// <summary>One parsed <c>sortBy</c> entry: the property plus its direction.</summary>
    private sealed record SortKey(string Property, bool Descending);
}
