using System.Globalization;
using Spatial.Core.Features;
using Spatial.Esri.Codec;
using Spatial.PluginSdk.Providers;


namespace Spatial.Adapter.GeoServices;

/// <summary>
/// How a layer maps engine feature identity onto the Esri integer
/// <c>OBJECTID</c> (ADR-0037). A layer whose dataset declares exactly one
/// identity column of kind <see cref="AttributeKind.Int64"/> uses that
/// column's value; every other layer falls back to the stable scan ordinal
/// used by the read-only facade. Only an identity-backed layer can be
/// edited, because only then is an <c>OBJECTID</c> a durable, client
/// round-trippable key (the engine's string <see cref="FeatureId"/> is not).
/// </summary>
internal sealed record EsriObjectIdScheme(bool IsIdentity, int FieldIndex)
{
    /// <summary>Infers the layer's scheme from its dataset description.</summary>
    public static EsriObjectIdScheme For(DatasetDescription dataset)
    {
        if (dataset.IdColumns.Count == 1)
        {
            var index = dataset.Schema.IndexOf(dataset.IdColumns[0]);
            if (index >= 0 && dataset.Schema[index].Kind == AttributeKind.Int64)
            {
                return new EsriObjectIdScheme(true, index);
            }
        }

        return new EsriObjectIdScheme(false, -1);
    }

    /// <summary>Whether the layer exposes a durable object id that supports editing.</summary>
    public bool SupportsEditing => IsIdentity;

    /// <summary>Resolves the object id of one feature, or false when the identity column is not an integer.</summary>
    public bool TryResolve(IFeature feature, long ordinal, out long objectId)
    {
        ArgumentNullException.ThrowIfNull(feature);
        if (IsIdentity)
        {
            var attribute = feature[FieldIndex];
            if (attribute.Kind == AttributeKind.Int64)
            {
                objectId = attribute.Int64Value;
                return true;
            }

            objectId = 0;
            return false;
        }

        objectId = ordinal;
        return true;
    }

    /// <summary>
    /// Builds the store <see cref="FeatureId"/> an integer <c>OBJECTID</c>
    /// refers to, the inverse of <see cref="ResolveAssigned"/> (ADR-0038).
    /// Only called for identity-backed layers (editing is gated on
    /// <see cref="SupportsEditing"/>), where the engine stores a single
    /// integer identity column as its decimal string.
    /// </summary>
    public static FeatureId ToFeatureId(long objectId) =>
        new(objectId.ToString(CultureInfo.InvariantCulture));

    /// <summary>Parses a store-assigned identity back to a numeric object id.</summary>
    public long ResolveAssigned(FeatureId id)
    {
        if (IsIdentity && long.TryParse(id.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
        {
            return value;
        }

        throw GeoServicesErrors.Invalid(
            $"The store assigned feature identity '{id.Value}', which is not a numeric OBJECTID.");
    }
}

/// <summary>
/// How a layer maps engine feature identity onto the Esri string
/// <c>uniqueIds</c> (spec §9.1.4, 11.5+): a layer whose dataset declares
/// exactly one identity column of kind <see cref="AttributeKind.String"/>
/// exposes that column's value, and a layer with exactly one identity
/// column of kind <see cref="AttributeKind.Guid"/> exposes the value in
/// canonical form — lowercase <c>D</c> (<c>xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx</c>),
/// the same text the stores render a boxed <see cref="Guid"/> with, so the
/// served id round-trips through <see cref="FeatureId"/>:
/// <c>Guid.Parse(new FeatureId(uniqueId).Value).ToString("D") == uniqueId</c>.
/// Requested ids match the served ids exactly (ordinal); every other layer
/// has no string unique-id model, so <c>uniqueIds</c>/
/// <c>returnUniqueIdsOnly</c> are rejected by name and integer features stay
/// addressable through <c>objectIds</c>.
/// </summary>
internal sealed record EsriUniqueIdScheme(string FieldName, int FieldIndex)
{
    /// <summary>
    /// The canonical string form of a guid identity value: lowercase
    /// <c>D</c>, back through <see cref="Guid.Parse(string)"/> to the same
    /// <see cref="Guid"/> and through <see cref="FeatureId"/> unchanged.
    /// </summary>
    public static string CanonicalForm(Guid value) => value.ToString("D");

    /// <summary>Infers the layer's scheme from its dataset description, or null when the layer has no string-or-guid identity column.</summary>
    public static EsriUniqueIdScheme? For(DatasetDescription dataset)
    {
        if (dataset.IdColumns.Count == 1)
        {
            var index = dataset.Schema.IndexOf(dataset.IdColumns[0]);
            if (index >= 0 && dataset.Schema[index].Kind is AttributeKind.String or AttributeKind.Guid)
            {
                return new EsriUniqueIdScheme(dataset.Schema[index].Name, index);
            }
        }

        return null;
    }

    /// <summary>Resolves the unique id of one feature, or false when the identity column carries no (non-empty, non-null) unique id.</summary>
    public bool TryResolve(IFeature feature, out string uniqueId)
    {
        ArgumentNullException.ThrowIfNull(feature);
        var attribute = feature[FieldIndex];
        if (attribute.Kind == AttributeKind.String && !string.IsNullOrEmpty(attribute.StringValue))
        {
            uniqueId = attribute.StringValue;
            return true;
        }

        if (attribute.Kind == AttributeKind.Guid)
        {
            uniqueId = CanonicalForm(attribute.GuidValue);
            return true;
        }

        uniqueId = string.Empty;
        return false;
    }

    /// <summary>Resolves the unique id of one feature, or a typed server failure when the identity column carries none (null or empty).</summary>
    public string Resolve(IFeature feature, DatasetDescription dataset)
    {
        if (TryResolve(feature, out var uniqueId))
        {
            return uniqueId;
        }

        throw GeoServicesErrors.ServerError(
            $"The identity column '{FieldName}' of layer '{dataset.Id}' carries no unique id value.");
    }

    /// <summary>
    /// Resolves the unique id a query needs: null when the query names no
    /// unique-id param, otherwise the feature's id — or a typed
    /// invalid-argument failure naming the param when the layer has no
    /// string-or-guid unique-id model.
    /// </summary>
    public static string? ResolveFor(EsriFeatureQuery query, DatasetDescription dataset, IFeature feature)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(dataset);
        if (query.UniqueIds is null && !query.ReturnUniqueIdsOnly)
        {
            return null;
        }

        var scheme = For(dataset);
        if (scheme is null)
        {
            var parameter = query.UniqueIds is not null ? "uniqueIds" : "returnUniqueIdsOnly";
            throw GeoServicesErrors.Invalid(
                $"The '{parameter}' parameter is not supported on layer '{dataset.Id}': the layer has no string or guid unique-id field; address its integer features with 'objectIds'.");
        }

        return scheme.Resolve(feature, dataset);
    }
}
