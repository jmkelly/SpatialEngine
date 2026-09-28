using Spatial.Contracts;
using Spatial.Core.Features;

namespace Spatial.Tiling.Mvt;

/// <summary>
/// MVT 2.1 vector-tile service. It reads resolved feature stores, projects
/// each geometry into the tile CRS, and writes the Mapbox Vector Tile
/// protobuf directly so no third-party encoding type enters the contracts.
/// The tile itself is this type's responsibility — validate the request, read
/// each layer through <see cref="MvtLayerReader"/>, write the protobuf —
/// while the per-layer query, projection and feature encoding live in
/// <see cref="MvtLayerReader"/>, <see cref="MvtTileProjection"/> and
/// <see cref="MvtLayerEncoder"/>.
/// </summary>
public sealed class MvtTileService : IVectorTileService
{
    private readonly ICoordinateTransforms _transforms;

    public MvtTileService(ICoordinateTransforms transforms) => _transforms = transforms;

    public async Task<VectorTile> RenderAsync(VectorTileRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        RequireRenderableTile(request);
        var layers = await ReadLayersAsync(request, cancellationToken).ConfigureAwait(false);
        return new VectorTile(MvtTileWriter.WriteTile(layers));
    }

    /// <summary>A tile needs a frame with extent on both axes, a CRS and an extent the MVT encoder can address.</summary>
    private static void RequireRenderableTile(VectorTileRequest request)
    {
        if (!IsRenderableTile(request))
        {
            throw SpatialException.BadArguments(UnrenderableTile);
        }
    }

    /// <summary>The one message every unrenderable tile rejects with, so the reason is the same whatever failed.</summary>
    internal const string UnrenderableTile =
        "A vector tile needs bounds with extent on both axes, a CRS and an extent between 1 and 65536.";

    private static bool IsRenderableTile(VectorTileRequest request) => HasUsableFrame(request) && InRange(request.Extent);

    /// <summary>
    /// Extent on both axes is also what makes the bounds finite: a non-finite
    /// bound yields a non-positive or NaN extent, so <see cref="MvtTileProjection.HasExtent"/>
    /// rejects empty, inverted and collapsed bounds alike.
    /// </summary>
    private static bool HasUsableFrame(VectorTileRequest request) =>
        MvtTileProjection.HasExtent(request.Bounds) && !string.IsNullOrWhiteSpace(request.Crs);

    private static bool InRange(int extent) => extent is >= 1 and <= 65536;

    private async Task<IReadOnlyList<MvtEncodedLayer>> ReadLayersAsync(
        VectorTileRequest request, CancellationToken cancellationToken)
    {
        var reader = new MvtLayerReader(_transforms);
        var layers = new List<MvtEncodedLayer>(request.Layers.Count);
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var layer in request.Layers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RequireUniqueName(names, layer);
            layers.Add(await reader.ReadAsync(request, layer, cancellationToken).ConfigureAwait(false));
        }

        return layers;
    }

    private static void RequireUniqueName(HashSet<string> names, VectorTileLayer layer)
    {
        if (!names.Add(layer.Name))
        {
            throw SpatialException.BadArguments($"Vector tile layer name '{layer.Name}' is duplicated.");
        }
    }
}
