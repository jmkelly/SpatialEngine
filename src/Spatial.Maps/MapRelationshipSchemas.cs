using Spatial.Contracts;
using Spatial.Contracts.Providers;
using Spatial.Core.Features;

namespace Spatial.Maps;

/// <summary>
/// Live-schema validation of a map's declared relationships (ADR-0077). The
/// structural shape of a declaration — name, cardinality, join pairing, and
/// the target being another published feature layer — is pure and lives in
/// <see cref="MapValidator"/>; what only a store can answer is whether the
/// named datasets and columns actually exist, and whether the two sides
/// carry comparable key kinds. This type asks the catalogue once per
/// declaration, so a relationship that could never match is rejected where it
/// is declared instead of silently answering no rows forever.
///
/// <para>The join dataset of a many-to-many relationship is resolved in the
/// owning layer's store, the same store that holds the origin records it
/// links.</para>
/// </summary>
public static class MapRelationshipSchemas
{
    /// <summary>Resolves one dataset's live description; a missing dataset is a <c>not.found</c> failure.</summary>
    public delegate Task<DatasetDescription> DescribeAsync(string store, string dataset, CancellationToken cancellationToken);

    /// <summary>
    /// Validates every declared relationship of <paramref name="map"/> against
    /// the live schemas, or fails as <c>invalid.arguments</c> naming the
    /// relationship and the reason. Maps without relationships cost one pass
    /// and no catalogue calls.
    /// </summary>
    public static async Task ValidateAsync(Map map, DescribeAsync describe, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(map);
        ArgumentNullException.ThrowIfNull(describe);
        if (!map.Layers.Any(layer => layer.Relationships is { Count: > 0 }))
        {
            return;
        }

        var layers = map.Layers.ToDictionary(layer => layer.LayerId);
        foreach (var layer in map.Layers)
        {
            foreach (var relationship in layer.Relationships ?? [])
            {
                cancellationToken.ThrowIfCancellationRequested();
                await ValidateOneAsync(map, layer, relationship, layers, describe, cancellationToken);
            }
        }
    }

    private static async Task ValidateOneAsync(
        Map map,
        MapLayer layer,
        LayerRelationship relationship,
        Dictionary<int, MapLayer> layers,
        DescribeAsync describe,
        CancellationToken cancellationToken)
    {
        var store = layer.Store ?? map.Store;
        var origin = await describe(store, layer.Dataset, cancellationToken);
        var relatedLayer = layers[relationship.RelatedLayerId];
        var relatedStore = relatedLayer.Store ?? map.Store;
        var related = await describe(relatedStore, relatedLayer.Dataset, cancellationToken);

        var originKind = KeyKind(map, layer, relationship, origin, relationship.PrimaryKeyColumn, store);
        var relatedKind = KeyKind(map, layer, relationship, related, relationship.RelatedKeyColumn, relatedStore);
        if (originKind != relatedKind)
        {
            throw SpatialException.BadArguments(
                $"Map '{map.Name}' relationship '{relationship.Name}' joins key '{relationship.PrimaryKeyColumn}' of kind {originKind} to key '{relationship.RelatedKeyColumn}' of kind {relatedKind}: both sides must carry the same kind.");
        }

        if (relationship.Join is not { } join)
        {
            return;
        }

        var joinDescription = await describe(store, join.Dataset, cancellationToken);
        var joinOriginKind = KeyKind(map, layer, relationship, joinDescription, join.PrimaryKeyColumn, store);
        var joinRelatedKind = KeyKind(map, layer, relationship, joinDescription, join.RelatedKeyColumn, store);
        if (joinOriginKind != originKind || joinRelatedKind != relatedKind)
        {
            throw SpatialException.BadArguments(
                $"Map '{map.Name}' relationship '{relationship.Name}' joins through '{join.Dataset}' whose key kinds ({joinOriginKind}, {joinRelatedKind}) do not match the related layers ({originKind}, {relatedKind}).");
        }
    }

    private static AttributeKind KeyKind(
        Map map, MapLayer layer, LayerRelationship relationship, DatasetDescription dataset, string column, string store)
    {
        var index = dataset.Schema.IndexOf(column);
        if (index < 0)
        {
            throw SpatialException.BadArguments(
                $"Map '{map.Name}' relationship '{relationship.Name}' on layer {layer.LayerId} names column '{column}', which dataset '{dataset.Id}' does not have.");
        }

        var kind = dataset.Schema[index].Kind;
        if (kind is AttributeKind.Geometry or AttributeKind.DateTimeOffset)
        {
            throw SpatialException.BadArguments(
                $"Map '{map.Name}' relationship '{relationship.Name}' on layer {layer.LayerId} names column '{column}' of dataset '{store}.{dataset.Id}', whose kind {kind} cannot be a relationship key.");
        }

        return kind;
    }
}
