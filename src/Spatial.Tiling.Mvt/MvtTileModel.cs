using Spatial.Core.Features;

namespace Spatial.Tiling.Mvt;

/// <summary>One encoded layer: its attribute keys, the shared value table, its features and the tile extent.</summary>
internal sealed record MvtEncodedLayer(
    string Name,
    IReadOnlyList<FieldDefinition> Keys,
    MvtValueTable Values,
    IReadOnlyList<MvtEncodedFeature> Features,
    int Extent);

/// <summary>One encoded feature: its optional numeric id, geometry type, attribute tags and packed geometry.</summary>
internal sealed record MvtEncodedFeature(ulong? Id, MvtGeometryType Type, uint[] Tags, byte[] Geometry);

/// <summary>The MVT geometry types this writer emits (spec 4.3.4).</summary>
internal enum MvtGeometryType
{
    /// <summary>Unset; never written.</summary>
    Unknown = 0,

    /// <summary>A point or multi-point feature.</summary>
    Point = 1,

    /// <summary>A line-string or multi-line feature.</summary>
    LineString = 2,

    /// <summary>A polygon or multi-polygon feature.</summary>
    Polygon = 3,
}

/// <summary>
/// The per-tile attribute value table: values are interned so a repeated
/// attribute is written once and referenced by index from every feature's tags.
/// </summary>
internal sealed class MvtValueTable
{
    private readonly Dictionary<string, uint> _indexes = new(StringComparer.Ordinal);

    /// <summary>The distinct values, in first-seen order; the index in this list is the wire value.</summary>
    public List<AttributeValue> Values { get; } = [];

    /// <summary>Interns a value and returns its wire index.</summary>
    public uint Index(AttributeValue value)
    {
        var key = $"{value.Kind}:{value}";
        if (_indexes.TryGetValue(key, out var index))
        {
            return index;
        }

        index = (uint)Values.Count;
        Values.Add(value);
        _indexes[key] = index;
        return index;
    }
}
