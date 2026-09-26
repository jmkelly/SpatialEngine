using Spatial.Contracts.Providers;
using Spatial.Esri.Codec;

namespace Spatial.Adapter.GeoServices;

/// <summary>
/// The Esri admin wire envelopes and how a registry map is projected onto
/// them: the service list, one service description with its layers, the
/// staged-upload reference and the create/delete/publish result. Split out of
/// <see cref="EsriAdminEndpoints"/>, which owns the routes and the token gate,
/// so the wire shapes sit with the code that fills them.
/// </summary>
internal static class EsriAdminResponses
{
    /// <summary>The service list envelope for every map exposing a FeatureServer.</summary>
    public static AdminServiceList List(IEnumerable<Map> maps) =>
        new(maps
            .Where(map => map.Exposes(MapServiceKind.FeatureServer))
            .Select(map => new AdminServiceEntry(map.Name, "FeatureServer"))
            .ToArray());

    /// <summary>One service description: its store and its feature layers.</summary>
    public static AdminService Service(string name, Map map) =>
        new(
            name,
            "FeatureServer",
            map.Store,
            map.Layers
                .Where(layer => layer.Kind == MapLayerKind.Feature)
                .Select(layer => new AdminLayer(layer.LayerId, layer.Name ?? layer.Dataset, layer.Dataset))
                .ToArray());

    /// <summary>The create/publish result: the service name and the layer ids it now serves.</summary>
    public static IResult Success(string name, IReadOnlyList<int> layerIds) => Result(true, name, layerIds);

    /// <summary>The create/delete/publish result: whether it applied, the service name and its layer ids.</summary>
    public static IResult Result(bool success, string name, IReadOnlyList<int> layerIds) =>
        EsriJson.Value(new AdminSuccess(success, name, layerIds));

    /// <summary>The staged-upload response: the item id a later publish names.</summary>
    public static IResult Upload(string itemId) => EsriJson.Value(new AdminUpload(true, new AdminItem(itemId)));
}

/// <summary>The admin service list envelope.</summary>
internal sealed record AdminServiceList(IReadOnlyList<AdminServiceEntry> Services);

/// <summary>One admin service entry.</summary>
internal sealed record AdminServiceEntry(string Name, string Type);

/// <summary>One admin service description.</summary>
internal sealed record AdminService(string Name, string Type, string Store, IReadOnlyList<AdminLayer> Layers);

/// <summary>One admin layer description.</summary>
internal sealed record AdminLayer(int Id, string Name, string Dataset);

/// <summary>The upload staging response.</summary>
internal sealed record AdminUpload(bool Success, AdminItem Item);

/// <summary>The staged item reference.</summary>
internal sealed record AdminItem(string ItemId);

/// <summary>The create/delete/publish result.</summary>
internal sealed record AdminSuccess(bool Success, string ServiceName, IReadOnlyList<int> LayerIds);
