using Spatial.Contracts;
using Spatial.Contracts.Providers;
using Spatial.Core.Features;
using Spatial.Esri.Codec;

namespace Spatial.Adapter.GeoServices;

/// <summary>
/// Resolves what a relationship request addresses (ADR-0077): the origin
/// layer, the declaration the <c>relationshipId</c> names and the related
/// layer it points at, each with the store its records live in. Shared by
/// the read traversal and the relate/unrelate writes, so a write can never
/// traverse a different declaration than the read that advertised it.
/// </summary>
internal static class FeatureRelationshipTargets
{
    /// <summary>
    /// The resolved relationship: the origin layer's description and store,
    /// the declaration and the related layer's description, store and
    /// published name.
    /// </summary>
    internal sealed record RelationshipTarget(
        DatasetDescription Origin,
        string OriginStoreKey,
        IFeatureStore OriginStore,
        LayerRelationship Relationship,
        RelatedLayer Related,
        IFeatureStore RelatedStore);

    /// <summary>
    /// Resolves the addressed relationship. A layer that declares none, or a
    /// <c>relationshipId</c> naming no declaration on it, is a typed
    /// <c>invalid.arguments</c> naming what the layer does declare; an
    /// unknown layer is <c>not.found</c>.
    /// </summary>
    public static async Task<RelationshipTarget> ResolveAsync(
        GeoServicesCatalog catalog,
        IMapRegistry registry,
        string service,
        int layerId,
        string? relationshipId,
        IStoreRegistry stores,
        CancellationToken cancellationToken)
    {
        var resolved = await GeoServicesResolution.ResolveServiceAsync(catalog, registry, service, "FeatureServer", MapServiceKind.FeatureServer, cancellationToken);
        var origin = await GeoServicesResolution.DescribeAsync(stores, resolved, layerId, cancellationToken);
        var relationship = Select(GeoServicesResolution.Relationships(resolved, layerId), layerId, relationshipId);
        var related = await GeoServicesResolution.RelatedLayerAsync(stores, resolved, relationship, cancellationToken);
        var originStoreKey = GeoServicesResolution.LayerStore(resolved, layerId);
        return new RelationshipTarget(
            origin,
            originStoreKey,
            stores.Features(originStoreKey),
            relationship,
            related,
            stores.Features(related.Store));
    }

    /// <summary>
    /// The declared relationship a <c>relationshipId</c> names — the declared
    /// name, or its one-based position in the layer's declaration order, so a
    /// client that has read the metadata can address it either way. Absent, a
    /// single-declaration layer needs no id; anything else is a typed
    /// <c>invalid.arguments</c> naming the declarations on offer.
    /// </summary>
    internal static LayerRelationship Select(IReadOnlyList<LayerRelationship> declared, int layerId, string? relationshipId)
    {
        if (declared.Count == 0)
        {
            throw GeoServicesErrors.Invalid($"Layer {layerId} declares no relationship.");
        }

        if (string.IsNullOrWhiteSpace(relationshipId))
        {
            return declared.Count == 1
                ? declared[0]
                : throw GeoServicesErrors.RequireRelationshipId(layerId, declared);
        }

        var wanted = relationshipId.Trim();
        return declared.FirstOrDefault(relationship => string.Equals(relationship.Name, wanted, StringComparison.OrdinalIgnoreCase))
            ?? (int.TryParse(wanted, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var position) && position >= 1 && position <= declared.Count
                ? declared[position - 1]
                : throw GeoServicesErrors.RequireRelationshipId(layerId, declared));
    }

    /// <summary>
    /// The origin feature's key value for the declaration, or null when the
    /// record carries no key (and therefore relates to nothing).
    /// </summary>
    internal static AttributeValue? KeyValue(DatasetDescription origin, LayerRelationship relationship, Feature feature)
    {
        var index = origin.Schema.IndexOf(relationship.PrimaryKeyColumn);
        if (index < 0)
        {
            throw GeoServicesErrors.Invalid(
                $"Relationship '{relationship.Name}' names key column '{relationship.PrimaryKeyColumn}', which layer '{origin.Id}' does not have.");
        }

        var value = feature[index];
        return value.IsNull ? null : value;
    }

    /// <summary>The related feature's key value, read the same way as the origin's.</summary>
    internal static AttributeValue? RelatedKeyValue(DatasetDescription related, LayerRelationship relationship, Feature feature)
    {
        var index = related.Schema.IndexOf(relationship.RelatedKeyColumn);
        if (index < 0)
        {
            throw GeoServicesErrors.Invalid(
                $"Relationship '{relationship.Name}' names related key column '{relationship.RelatedKeyColumn}', which layer '{related.Id}' does not have.");
        }

        var value = feature[index];
        return value.IsNull ? null : value;
    }

    /// <summary>
    /// The join rows a many-to-many relationship traverses: the join dataset's
    /// features whose origin-side key column equals the origin record's key.
    /// The join dataset is resolved in the owning layer's store and described
    /// like any other dataset; a missing one is <c>not.found</c>, never an
    /// empty traversal that would answer "no related records".
    /// </summary>
    public static async Task<JoinRows> JoinRowsAsync(
        RelationshipTarget target, IStoreRegistry stores, Feature originFeature, CancellationToken cancellationToken)
    {
        var join = target.Relationship.Join!;
        var catalogue = stores.Catalogue(target.OriginStoreKey);
        var description = await catalogue.DescribeAsync(join.Dataset, cancellationToken);
        var store = stores.Features(target.OriginStoreKey);
        var key = KeyValue(target.Origin, target.Relationship, originFeature);
        if (key is null)
        {
            return new JoinRows(description, store, []);
        }

        var term = FeatureRelationshipKeys.Parse(FeatureRelationshipKeys.Equality(join.PrimaryKeyColumn, key.Value)!);
        var rows = new List<Feature>();
        foreach (var batch in await store.ScanAsync(description.Id, cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            rows.AddRange(batch.Features.Where(feature => term.Matches(feature)));
        }

        return new JoinRows(description, store, rows);
    }

    /// <summary>One relationship's join rows: the join dataset's description, its store and the rows the origin record reaches.</summary>
    internal sealed record JoinRows(DatasetDescription Description, IFeatureStore Store, IReadOnlyList<Feature> Rows);
}
