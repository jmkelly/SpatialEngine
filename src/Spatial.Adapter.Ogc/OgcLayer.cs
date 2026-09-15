using Spatial.Contracts.Providers;

namespace Spatial.Adapter.Ogc;

/// <summary>
/// One feature layer resolved for serving (ADR-0053 §3): its map layer, the
/// OGC-visible name, its keyed store and its dataset description. The name is
/// the layer's <see cref="MapLayer.Name"/> when set, otherwise its dataset,
/// and is what capabilities advertise and requests select by.
/// </summary>
internal sealed record OgcLayer(
    MapLayer Layer,
    string Name,
    string Store,
    DatasetDescription Description);
