namespace Spatial.Contracts.Providers;

/// <summary>
/// How many records one side of a declared relationship holds. The values
/// are the engine's own vocabulary (ADR-0077): a protocol adapter maps them
/// onto its own relationship-type names, so no Esri concept reaches the
/// model. A one-to-one or one-to-many relationship is carried by a key
/// column on the related layer; a many-to-many one is carried by a join
/// dataset named by <see cref="LayerRelationship.Join"/>.
/// </summary>
public enum LayerRelationshipCardinality
{
    /// <summary>At most one related record per origin record (the related key column is unique).</summary>
    OneToOne,

    /// <summary>Any number of related records per origin record (the default).</summary>
    OneToMany,

    /// <summary>Any number on both sides, through a join dataset carrying both keys.</summary>
    ManyToMany,
}

/// <summary>
/// The join dataset a many-to-many relationship travels through
/// (ADR-0077): <see cref="Dataset"/> holds
/// <see cref="PrimaryKeyColumn"/> (the origin key) and
/// <see cref="RelatedKeyColumn"/> (the related key), one row per related
/// pair. The engine holds no join semantics of its own — the declaration
/// only says which columns carry the two keys, and the stores stay plain
/// feature datasets.
/// </summary>
public sealed record LayerRelationshipJoin(
    string Dataset,
    string PrimaryKeyColumn,
    string RelatedKeyColumn);

/// <summary>
/// One declared relationship from the layer that owns it to a
/// <see cref="RelatedLayerId"/> of the same map (ADR-0077). The declaration
/// is publication state, not store state: the stores hold the two datasets
/// and the join rows, while the map says how they are related, so the same
/// datasets can be published with different relationships (principle 10 —
/// stores are providers, the declaration lives in the map model).
///
/// <para><see cref="PrimaryKeyColumn"/> is a column of the owning layer and
/// <see cref="RelatedKeyColumn"/> a column of the related layer; a
/// one-to-one or one-to-many relationship reads the related records whose
/// related key equals the origin record's primary key, and the relate write
/// sets that column. A many-to-many relationship needs
/// <see cref="Join"/>, and no other cardinality accepts one.</para>
///
/// <para><see cref="Name"/> is the relationship's identity inside its layer
/// (unique per layer, identifier-shaped) and is what
/// <c>relationshipId</c> addresses; the engine assigns no numeric
/// relationship ids.</para>
/// </summary>
public sealed record LayerRelationship(
    string Name,
    int RelatedLayerId,
    string PrimaryKeyColumn,
    string RelatedKeyColumn,
    LayerRelationshipCardinality Cardinality = LayerRelationshipCardinality.OneToMany,
    string? TitleField = null,
    LayerRelationshipJoin? Join = null)
{
    public override string ToString() =>
        $"{Name} ({Cardinality} -> layer {RelatedLayerId} on {RelatedKeyColumn})";
}
