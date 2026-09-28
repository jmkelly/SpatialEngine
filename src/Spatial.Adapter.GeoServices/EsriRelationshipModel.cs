using Spatial.Contracts.Providers;

namespace Spatial.Adapter.GeoServices;

/// <summary>
/// Projects the engine's relationship declarations (ADR-0077) onto the Esri
/// layer <c>relationships</c> metadata (spec §9.1) that <c>getLayer</c>
/// consumers read. The engine vocabulary is the contract's
/// <see cref="LayerRelationshipCardinality"/>; the adapter owns the Esri
/// <c>esriRelationshipType*</c> names, so no Esri concept reaches the model.
/// Split out of <see cref="EsriLayerModel"/> so the layer metadata keeps to
/// one cohesive job and the relationship shape lives where it is used.
/// </summary>
internal static class EsriRelationshipModel
{
    private const string OneToOne = "esriRelationshipTypeOneToOne";
    private const string OneToMany = "esriRelationshipTypeOneToMany";
    private const string ManyToMany = "esriRelationshipTypeManyToMany";

    /// <summary>
    /// The advertised relationships of one layer, in declaration order. The
    /// title is the declaration's <c>TitleField</c> when it names one and the
    /// related layer's published name otherwise, so a client always has
    /// something to label the related records with.
    /// </summary>
    public static IReadOnlyList<EsriRelationship> Describe(
        IReadOnlyList<LayerRelationship> relationships, IReadOnlyDictionary<int, string> relatedLayerNames)
    {
        ArgumentNullException.ThrowIfNull(relationships);
        var described = new List<EsriRelationship>(relationships.Count);
        foreach (var relationship in relationships)
        {
            var relatedName = relatedLayerNames.GetValueOrDefault(relationship.RelatedLayerId, relationship.RelatedLayerId.ToString(System.Globalization.CultureInfo.InvariantCulture));
            described.Add(new(
                relationship.Name,
                relationship.Name,
                relationship.RelatedLayerId,
                string.IsNullOrWhiteSpace(relationship.TitleField) ? relatedName : relationship.TitleField,
                TypeOf(relationship.Cardinality)));
        }

        return described;
    }

    /// <summary>The Esri relationship-type constant of an engine cardinality.</summary>
    public static string TypeOf(LayerRelationshipCardinality cardinality) => cardinality switch
    {
        LayerRelationshipCardinality.OneToOne => OneToOne,
        LayerRelationshipCardinality.OneToMany => OneToMany,
        LayerRelationshipCardinality.ManyToMany => ManyToMany,
        _ => throw GeoServicesErrors.Invalid($"Relationship cardinality {cardinality} is not supported."),
    };
}
