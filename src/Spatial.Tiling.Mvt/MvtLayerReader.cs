using Spatial.Contracts;
using Spatial.Contracts.Providers;
using Spatial.Core.Features;

namespace Spatial.Tiling.Mvt;

/// <summary>
/// Reading one layer of an MVT tile (ADR-0043): the layer's dataset is
/// described, the tile rectangle is projected into the dataset's CRS, the
/// features inside it are queried, and the layer's attribute keys and value
/// table are resolved before <see cref="MvtLayerEncoder"/> encodes them. Each
/// layer names the store it reads, so a tile can span datasets.
/// </summary>
internal sealed class MvtLayerReader(ICoordinateTransforms transforms)
{
    private readonly MvtLayerEncoder _encoder = new(transforms);

    /// <summary>The queried, key-resolved and encoded layer.</summary>
    public async Task<MvtEncodedLayer> ReadAsync(
        VectorTileRequest request, VectorTileLayer layer, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(layer);
        if (string.IsNullOrWhiteSpace(layer.Name) || string.IsNullOrWhiteSpace(layer.Dataset))
        {
            throw SpatialException.BadArguments("Every vector tile layer needs a name and dataset.");
        }

        var description = await layer.Catalogue.DescribeAsync(layer.Dataset, cancellationToken).ConfigureAwait(false);
        var source = $"EPSG:{description.Srid}";
        var queryBounds = MvtTileProjection.ToSourceBounds(transforms, request.Bounds, request.Crs, source, cancellationToken);
        var batches = (await layer.Features
            .QueryAsync(layer.Dataset, new FeatureQuery(BoundingBox: queryBounds, Where: layer.Where), cancellationToken)
            .ConfigureAwait(false)).Batches;
        var features = batches.SelectMany(batch => batch.Features).ToArray();
        var schema = features.FirstOrDefault()?.Schema ?? description.Schema;
        var geometryIndex = FindGeometry(schema);
        if (geometryIndex < 0)
        {
            throw SpatialException.BadArguments($"Vector tile layer '{layer.Name}' has no geometry field.");
        }

        var keys = schema.Fields.Where(field => field.Kind != AttributeKind.Geometry).ToArray();
        var values = new MvtValueTable();
        var encoding = new LayerEncoding(layer.Name, geometryIndex, keys, values, request, source);
        var encoded = _encoder.Encode(features, encoding, cancellationToken);

        return new MvtEncodedLayer(layer.Name, keys, values, encoded, request.Extent);
    }

    private static int FindGeometry(FeatureSchema schema)
    {
        for (var i = 0; i < schema.Count; i++)
        {
            if (schema[i].Kind == AttributeKind.Geometry)
            {
                return i;
            }
        }
        return -1;
    }
}
