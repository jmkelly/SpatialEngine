using Spatial.Core.Geometry;

namespace Spatial.Ingest.Codec;

/// <summary>
/// One parsed source record before a schema exists: its optional source
/// identity, its attribute values keyed by source name, and its geometry
/// (already converted to a core value and stamped with the decode CRS).
/// </summary>
internal sealed record RawFeature(string? Id, IReadOnlyDictionary<string, object?> Properties, IGeometry? Geometry);

/// <summary>
/// The parsed records plus the union of their attribute names in first-seen
/// order, which becomes the schema's field order. Uniqueness and order are
/// established here so schema inference and feature building only ever look
/// at names that at least one record carried.
/// </summary>
internal sealed class RawFeatureSet
{
    private readonly List<string> _names = [];
    private readonly HashSet<string> _seen = new(StringComparer.Ordinal);
    private readonly List<RawFeature> _features = [];

    /// <summary>Attribute names in first-seen order (geometry excluded).</summary>
    public IReadOnlyList<string> Names => _names;

    /// <summary>The parsed records in document order.</summary>
    public IReadOnlyList<RawFeature> Features => _features;

    /// <summary>Adds one record, extending the name union in first-seen order.</summary>
    public void Add(string? id, IReadOnlyList<string> names, IReadOnlyDictionary<string, object?> properties, IGeometry? geometry)
    {
        foreach (var name in names)
        {
            if (_seen.Add(name))
            {
                _names.Add(name);
            }
        }

        _features.Add(new RawFeature(id, properties, geometry));
    }
}
