using System.Globalization;
using Spatial.Core.Features;
using Spatial.Interop.Esri;
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

    /// <summary>Parses a store-assigned identity back to a numeric object id.</summary>
    public long ResolveAssigned(FeatureId id)
    {
        if (IsIdentity && long.TryParse(id.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
        {
            return value;
        }

        throw EsriInteropException.Invalid(
            $"The store assigned feature identity '{id.Value}', which is not a numeric OBJECTID.");
    }
}
