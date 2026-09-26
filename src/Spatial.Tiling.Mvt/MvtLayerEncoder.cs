using System.Globalization;
using Spatial.Contracts;
using Spatial.Core.Features;
using Spatial.Core.Geometry;

namespace Spatial.Tiling.Mvt;

/// <summary>
/// The per-feature encoding of one MVT layer (ADR-0043): every queried feature
/// becomes an <see cref="MvtEncodedFeature"/> whose geometry is transformed
/// into the tile CRS, whose id is the numeric feature identity when it has
/// one, and whose attributes become the layer's key/value index pairs. A
/// geometry with no MVT counterpart is omitted from the tile rather than
/// failing it.
/// </summary>
internal sealed class MvtLayerEncoder(ICoordinateTransforms transforms)
{
    private readonly ICoordinateTransforms _transforms = transforms;

    /// <summary>Encodes every queried feature, honouring cancellation between features.</summary>
    public List<MvtEncodedFeature> Encode(
        Feature[] features, LayerEncoding encoding, CancellationToken cancellationToken)
    {
        var encoded = new List<MvtEncodedFeature>(features.Length);
        foreach (var feature in features)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var one = EncodeFeature(feature, encoding, cancellationToken);
            if (one is not null)
            {
                encoded.Add(one);
            }
        }

        return encoded;
    }

    /// <summary>
    /// Encodes one feature, or <c>null</c> when the projected geometry has no
    /// MVT counterpart (a bare linear ring, say): the tile simply omits it.
    /// </summary>
    private MvtEncodedFeature? EncodeFeature(
        Feature feature, LayerEncoding encoding, CancellationToken cancellationToken)
    {
        if (feature[encoding.GeometryIndex].IsNull)
        {
            throw SpatialException.BadArguments(
                $"Vector tile layer '{encoding.LayerName}' contains a feature with no geometry.");
        }

        var geometry = _transforms.Transform(
            feature[encoding.GeometryIndex].GeometryValue, encoding.Source, encoding.Request.Crs, cancellationToken);
        var type = GeometryTypeOf(geometry);
        if (type is null)
        {
            return null;
        }

        return new MvtEncodedFeature(
            ParseId(feature.Id), type.Value, Tags(feature, encoding.Keys, encoding.Values),
            MvtTileWriter.WriteGeometry(geometry, encoding.Request.Bounds, encoding.Request.Extent));
    }

    /// <summary>The tile's attribute table: each non-null attribute's key index and its value index.</summary>
    private static uint[] Tags(Feature feature, FieldDefinition[] keys, MvtValueTable values)
    {
        var tags = new List<uint>();
        for (var i = 0; i < feature.Attributes.Count; i++)
        {
            if (feature[i].IsNull || feature[i].Kind == AttributeKind.Geometry)
            {
                continue;
            }

            var key = Array.FindIndex(keys, field => field.Name == feature.Schema[i].Name);
            if (key >= 0)
            {
                tags.Add((uint)key);
                tags.Add(values.Index(feature[i]));
            }
        }

        return tags.ToArray();
    }

    private static MvtGeometryType? GeometryTypeOf(IGeometry geometry) => geometry.Type switch
    {
        GeometryType.Point or GeometryType.MultiPoint => MvtGeometryType.Point,
        GeometryType.LineString or GeometryType.MultiLineString => MvtGeometryType.LineString,
        GeometryType.Polygon or GeometryType.MultiPolygon => MvtGeometryType.Polygon,
        _ => throw SpatialException.BadArguments($"MVT cannot encode a {geometry.Type} geometry; use a point, line or polygon collection."),
    };

    private static ulong? ParseId(FeatureId id) =>
        ulong.TryParse(id.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var value) ? value : null;
}

/// <summary>What one layer's features are encoded against: its keys, its value table and the tile request.</summary>
internal sealed record LayerEncoding(
    string LayerName,
    int GeometryIndex,
    FieldDefinition[] Keys,
    MvtValueTable Values,
    VectorTileRequest Request,
    string Source);
