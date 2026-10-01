using Spatial.Contracts;
using Spatial.Contracts.Providers;
using Spatial.Esri.Codec;

namespace Spatial.Adapter.GeoServices;

/// <summary>
/// Shared service resolution: config-declared or runtime-map services to
/// stores, published layers, tables and dataset descriptions. Split out of
/// <see cref="GeoServicesEndpoints"/> so the route facade keeps only
/// mapping and the resolution fan-out (catalogue, map registry, layer
/// model) lives with the code that uses it (ADR-0040).
/// </summary>
internal static class GeoServicesResolution
{
    /// <summary>
    /// Resolves one GeoServices server (Feature, Map or Image): a
    /// config-declared service exposes its whole store (sorted, index-assigned
    /// layers), a runtime map exposes the persisted explicit layers that feed
    /// the requested service (ADR-0053). A map that does not expose the
    /// service, or an unknown name, is not-found.
    /// </summary>
    internal static async Task<ResolvedService> ResolveServiceAsync(
        GeoServicesCatalog catalog,
        IMapRegistry registry,
        string service,
        string serverType,
        MapServiceKind mapService,
        CancellationToken cancellationToken)
    {
        if (catalog.TryGet(service, out var entry) && entry.Type == serverType)
        {
            return new ResolvedService(entry.Store, null);
        }

        Map map;
        try
        {
            map = await registry.GetAsync(service, cancellationToken);
        }
        catch (SpatialException exception) when (exception.Code == SpatialException.NotFound)
        {
            throw GeoServicesErrors.NotFound($"Service '{service}' was not found.");
        }

        if (!map.Exposes(mapService))
        {
            throw GeoServicesErrors.NotFound($"Service '{service}' was not found.");
        }

        var layers = map.Layers.Where(layer => Feeds(mapService, layer.Kind)).ToArray();
        return new ResolvedService(map.Store, layers, map.Description, map.Copyright, map.MetadataXml);
    }

    /// <summary>Whether a layer of <paramref name="kind"/> feeds <paramref name="service"/>.</summary>
    internal static bool Feeds(MapServiceKind service, MapLayerKind kind) =>
        service == MapServiceKind.ImageServer ? kind == MapLayerKind.Image : kind == MapLayerKind.Feature;

    /// <summary>Lists the published layers: explicit for a runtime publication, whole-store (sorted) for a declared service.</summary>
    internal static async Task<IReadOnlyList<PublishedLayer>> ListLayersAsync(
        IStoreRegistry stores, ResolvedService resolved, CancellationToken cancellationToken)
    {
        if (resolved.Layers is { } layers)
        {
            return layers
                .OrderBy(layer => layer.LayerId)
                .Select(layer => new PublishedLayer(layer.LayerId, layer.Dataset, layer.Name ?? Table(layer.Dataset), layer.Style, layer.TimeFields))
                .ToArray();
        }

        var catalogue = stores.Catalogue(resolved.Store);
        var datasets = (await catalogue.ListAsync(null, cancellationToken)).OrderBy(dataset => dataset.Id, StringComparer.Ordinal).ToArray();
        return datasets.Select((dataset, index) => new PublishedLayer(index, dataset.Id, dataset.Table)).ToArray();
    }

    /// <summary>
    /// Splits published layers into spatial layers and geometry-less tables
    /// (spec §9.0): each dataset is described once and routed by
    /// <see cref="EsriLayerModel.IsTable"/>. Ids are preserved from the
    /// single layer/table id space.
    /// </summary>
    internal static async Task<(IReadOnlyList<PublishedLayer> Layers, IReadOnlyList<PublishedLayer> Tables)> SplitTablesAsync(
        IStoreRegistry stores, ResolvedService resolved, IReadOnlyList<PublishedLayer> layers, CancellationToken cancellationToken)
    {
        var catalogue = stores.Catalogue(resolved.Store);
        var spatial = new List<PublishedLayer>(layers.Count);
        var tables = new List<PublishedLayer>();
        foreach (var layer in layers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var description = await catalogue.DescribeAsync(layer.Dataset, cancellationToken);
            (EsriLayerModel.IsTable(description) ? tables : spatial).Add(layer);
        }

        return (spatial, tables);
    }

    internal static async Task<DatasetDescription> DescribeAsync(
        IStoreRegistry stores, ResolvedService resolved, int layerId, CancellationToken cancellationToken)
    {
        var layers = await ListLayersAsync(stores, resolved, cancellationToken);
        var layer = layers.FirstOrDefault(candidate => candidate.Id == layerId)
            ?? throw GeoServicesErrors.NotFound($"Layer {layerId} does not exist in the service.");
        var catalogue = stores.Catalogue(resolved.Store);
        return Designated(await catalogue.DescribeAsync(layer.Dataset, cancellationToken), layer);
    }

    /// <summary>
    /// The description a served layer is read under, carrying the start/end
    /// date fields its publication designates (ADR-0183). The designation is
    /// publication state stamped onto the store's description at the serving
    /// edge, so <c>GET /api/datasets/{id}</c> still reports what the store
    /// holds and every reader — the root, export, identify, the query plan —
    /// sees the same one. A layer that designates nothing leaves the
    /// description exactly as the store reported it.
    /// </summary>
    internal static DatasetDescription Designated(DatasetDescription description, PublishedLayer layer) =>
        layer.TimeFields is { } fields ? description with { TimeFields = fields } : description;

    /// <summary>Describes one published layer under its publication's designation.</summary>
    internal static async Task<DatasetDescription> DescribeAsync(
        IStoreRegistry stores, ResolvedService resolved, PublishedLayer layer, CancellationToken cancellationToken) =>
        Designated(await stores.Catalogue(LayerStore(resolved, layer.Id)).DescribeAsync(layer.Dataset, cancellationToken), layer);

    /// <summary>Whether the service is editable: the store exposes the keyed editing face (ADR-0037).</summary>
    internal static bool IsEditable(IStoreRegistry stores, string store) =>
        stores.EditStore(store) is not null;

    /// <summary>
    /// The relationships a published layer declares (ADR-0077). A
    /// configuration-declared whole-store service publishes no declaration,
    /// so it advertises none.
    /// </summary>
    internal static IReadOnlyList<LayerRelationship> Relationships(ResolvedService resolved, int layerId) =>
        resolved.Layers?.FirstOrDefault(layer => layer.LayerId == layerId)?.Relationships ?? [];

    /// <summary>
    /// The store a published layer's records live in: its own override, else
    /// the map's. A relationship that crosses a per-layer store override
    /// resolves its related layer through the same rule.
    /// </summary>
    internal static string LayerStore(ResolvedService resolved, int layerId) =>
        resolved.Layers?.FirstOrDefault(layer => layer.LayerId == layerId)?.Store ?? resolved.Store;

    /// <summary>
    /// The related layer a relationship points at: its store, its published
    /// name and its catalogue description. An unknown layer id is
    /// <c>not.found</c>, so a declaration can never traverse into nothing.
    /// </summary>
    internal static async Task<RelatedLayer> RelatedLayerAsync(
        IStoreRegistry stores, ResolvedService resolved, LayerRelationship relationship, CancellationToken cancellationToken)
    {
        var layers = await ListLayersAsync(stores, resolved, cancellationToken);
        var layer = layers.FirstOrDefault(candidate => candidate.Id == relationship.RelatedLayerId)
            ?? throw GeoServicesErrors.NotFound(
                $"Relationship '{relationship.Name}' targets layer {relationship.RelatedLayerId}, which this service does not publish.");
        return new RelatedLayer(
            LayerStore(resolved, layer.Id),
            layer,
            await stores.Catalogue(LayerStore(resolved, layer.Id)).DescribeAsync(layer.Dataset, cancellationToken));
    }

    /// <summary>Whether the layer advertises attachments: the store exposes the blob face (T-061, ADR-0066).</summary>
    internal static bool HasAttachments(IStoreRegistry stores, string store) =>
        stores.AttachmentStore(store) is not null;

    private static string Table(string dataset)
    {
        var dot = dataset.IndexOf('.');
        return dot < 0 ? dataset : dataset[(dot + 1)..];
    }
}

/// <summary>The layer a relationship targets (ADR-0077): the store its records live in, its published identity and its catalogue description.</summary>
internal sealed record RelatedLayer(string Store, PublishedLayer Layer, DatasetDescription Description);
