using Spatial.Core.Features;
using Spatial.Core.Geometry;
using Spatial.PluginSdk;
using Spatial.PluginSdk.Providers;

namespace Spatial.Adapter.GeoServices;

/// <summary>
/// The Map Service (spec §4.0/§4.2) M0 facade: a data-only, read-only
/// projection of a <see cref="PublicationKind.Map"/> publication. It serves
/// the root, layer metadata (with the publication layer's
/// <see cref="LayerStyle"/> lowered to <c>drawingInfo</c>) and reuses the
/// Feature Service query engine for <c>query</c>; export and tiles (M2/M3 of
/// <c>map-service-plan.md</c>) are deliberately absent and the capability
/// string says so (ADR-0047).
/// </summary>
internal static class MapService
{
    /// <summary>Builds the MapServer root for resolved layers and a computed extent.</summary>
    public static EsriMapServerRoot Root(
        Publication publication,
        IReadOnlyList<EsriMapLayerRef> references,
        EsriExtentDto? extent,
        int srid) =>
        EsriMapModel.Root(
            publication,
            references,
            extent,
            EsriLayerModel.SpatialReference(srid),
            EsriMapModel.Units(srid));

    /// <summary>Builds one map layer's metadata, including its <c>drawingInfo</c>.</summary>
    public static EsriMapLayer Layer(PublishedMapLayer layer, DatasetDescription dataset) =>
        EsriMapModel.Layer(layer.Id, layer.Name, dataset, layer.Style);

    /// <summary>
    /// Computes the union envelope of every published layer by scanning the
    /// store. The stores expose no extent capability yet, so M0 pays one scan;
    /// a store-owned extent replaces this without changing the wire shape
    /// (ADR-0047).
    /// </summary>
    public static async Task<Envelope?> FullExtentAsync(
        IServiceProvider services, string store, PublishedMapLayer[] layers, CancellationToken cancellationToken)
    {
        var featureStore = services.GetRequiredKeyedService<IFeatureStore>(store);
        Envelope? union = null;
        foreach (var layer in layers)
        {
            foreach (var batch in await featureStore.ScanAsync(layer.Dataset, cancellationToken))
            {
                foreach (var feature in batch.Features)
                {
                    union = Union(union, FeatureEnvelope(feature));
                }
            }
        }

        return union;
    }

    /// <summary>The Esri extent for an envelope, or null when the data is empty or the CRS is unknown.</summary>
    public static EsriExtentDto? Extent(Envelope? envelope, int srid) =>
        envelope is { IsEmpty: false } value
            ? new EsriExtentDto(value.MinX, value.MinY, value.MaxX, value.MaxY, EsriLayerModel.SpatialReference(srid))
            : null;

    private static Envelope? FeatureEnvelope(Feature feature)
    {
        foreach (var attribute in feature.Attributes)
        {
            if (attribute.Kind == AttributeKind.Geometry)
            {
                return attribute.GeometryValue.Envelope;
            }
        }

        return null;
    }

    private static Envelope? Union(Envelope? current, Envelope? next)
    {
        if (next is not { IsEmpty: false } value)
        {
            return current;
        }

        return current is { IsEmpty: false } existing ? existing.Union(value) : value;
    }
}

/// <summary>One map layer resolved for serving: its stable id, dataset, published name and optional style.</summary>
internal sealed record PublishedMapLayer(int Id, string Dataset, string Name, LayerStyle? Style);
