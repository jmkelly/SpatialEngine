using Spatial.Core.Features;
using Spatial.Core.Geometry;
using Spatial.PluginSdk.Providers;

namespace Spatial.Provider.PostGIS.Tests;

/// <summary>
/// Test-side builders for core feature values and dataset descriptions.
/// </summary>
internal static class FeatureTests
{
    public static AttributeValue Geometry(IGeometry geometry) => AttributeValue.FromGeometry(geometry);

    public static FeatureSchema Schema(params (string Name, AttributeKind Kind, bool Nullable)[] fields) =>
        new(fields.Select(field => new FieldDefinition(field.Name, field.Kind, field.Nullable)));

    public static Feature Feature(string id, FeatureSchema schema, params object?[] values) =>
        new(new FeatureId(id), schema, schema.Fields.Select((field, index) => Value(field, values[index])).ToArray());

    public static AttributeValue Value(FieldDefinition field, object? value)
    {
        if (value is null)
        {
            return AttributeValue.Null;
        }

        return field.Kind switch
        {
            AttributeKind.Boolean => AttributeValue.FromBoolean((bool)value),
            AttributeKind.Int64 => AttributeValue.FromInt64((long)value),
            AttributeKind.Double => AttributeValue.FromDouble((double)value),
            AttributeKind.String => AttributeValue.FromString((string)value),
            AttributeKind.Geometry => Geometry((IGeometry)value),
            AttributeKind.DateTimeOffset => AttributeValue.FromDateTimeOffset((DateTimeOffset)value),
            AttributeKind.Guid => AttributeValue.FromGuid((Guid)value),
            _ => throw new ArgumentOutOfRangeException(nameof(field), field.Kind, "unknown kind"),
        };
    }

    /// <summary>Canonical batch bytes (FeatureBatchCodec v1) for a schema + features.</summary>
    public static byte[] BatchBytes(FeatureSchema schema, params Feature[] features) =>
        FeatureBatchCodec.Encode(new FeatureBatch(schema, features));

    /// <summary>The common places dataset: id, name, geometry + three attributes.</summary>
    public static FeatureSchema PlacesSchema => Schema(
        ("id", AttributeKind.Int64, false),
        ("name", AttributeKind.String, false),
        ("geom", AttributeKind.Geometry, false));

    /// <summary>A dataset description for <c>public.places</c> (srid 4326, PK 'id').</summary>
    public static DatasetDescription PlacesDescription(FeatureSchema? schema = null) =>
        new(
            "public.places",
            "public",
            "places",
            "geom",
            4326,
            "POINT",
            3,
            ["id"],
            schema ?? PlacesSchema);
}
