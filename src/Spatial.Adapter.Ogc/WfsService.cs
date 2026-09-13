using System.Globalization;
using Microsoft.AspNetCore.Http;
using Spatial.Core.Features;
using Spatial.Core.Geometry;
using Spatial.PluginSdk;
using Spatial.PluginSdk.Providers;

namespace Spatial.Adapter.Ogc;

/// <summary>
/// The OGC Web Feature Service 2.0.0 projection (ADR-0052 §3):
/// GetCapabilities, DescribeFeatureType and GetFeature over a map's feature
/// layers. GetFeature returns GeoJSON (<c>application/geo+json</c>); GML is an
/// explicit non-goal and is rejected with a typed <c>InvalidParameterValue</c>
/// ServiceException. Queries go through <see cref="IFeatureStore.QueryAsync"/>.
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
            "GETFEATURE" => await GetFeatureAsync(map, parameters, services, options, cancellationToken),
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
        Map map, OgcParameters parameters, OgcRequestServices services, OgcOptions options, CancellationToken cancellationToken)
    {
        EnsureGeoJson(parameters.Get("outputformat"));
        var layers = OgcLayers.Select(map, parameters.List("typenames"));
        var count = Count(parameters.Get("count"), options.MaxFeatures);
        var bbox = ParseBbox(parameters.Get("bbox"));
        var features = new List<(DatasetDescription Dataset, Feature Feature)>();
        foreach (var layer in layers)
        {
            if (features.Count >= count)
            {
                break;
            }

            var loaded = await services.LoadAsync(map, layer, cancellationToken);
            features.AddRange(await ReadAsync(services, loaded, bbox, count - features.Count, cancellationToken));
        }

        return Results.Bytes(GeoJson.FeatureCollection(features), "application/geo+json");
    }

    private static async Task<IReadOnlyList<(DatasetDescription Dataset, Feature Feature)>> ReadAsync(
        OgcRequestServices services, OgcLayer layer, WfsBbox? bbox, int limit, CancellationToken cancellationToken)
    {
        var projected = Project(services, layer, bbox, cancellationToken);
        var batches = await services.Features(layer.Store).QueryAsync(layer.Layer.Dataset, projected, null, cancellationToken);
        return batches
            .SelectMany(batch => batch.Features)
            .Take(limit)
            .Select(feature => (layer.Description, feature))
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

    private static int Count(string? text, int max) =>
        text is null
            ? max
            : int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) && value >= 0
                ? Math.Min(value, max)
                : throw OgcServiceException.Invalid($"The 'count' parameter must be a non-negative integer, got '{text}'.");

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
}
