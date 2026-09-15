using System.Globalization;
using Spatial.Core.Features;
using Spatial.Stores.PostGIS.Geometry;

namespace Spatial.Stores.PostGIS.Core;

/// <summary>
/// Maps one materialised row (boxed values from <see cref="Npgsql.NpgsqlDataReader"/>;
/// see the store) into a core <see cref="Feature"/> (ADR-0028). Pure and fully
/// unit-testable: nulls become <see cref="AttributeValue.Null"/> (schema
/// validation rejects null in non-nullable fields), every other value is
/// converted by attribute kind through a static dispatch table, geometry
/// values (EWKB byte arrays from Npgsql's plugin-free mapping) go through
/// <see cref="PostgisEwkb"/> — the plugin's single Core.Geometry surface. The
/// feature id is the primary-key values joined with <c>|</c>, or the row
/// ordinal when the table has no primary key.
/// </summary>
internal static class PostgisRowMapper
{
    /// <summary>Maps one row to a feature under the dataset's scan schema.</summary>
    public static Feature MapRow(IFeatureSchema schema, IReadOnlyList<int> identityIndexes, IReadOnlyList<object?> values, long ordinal)
    {
        var attributes = new AttributeValue[schema.Count];
        for (var i = 0; i < schema.Count; i++)
        {
            attributes[i] = MapAttribute(schema[i], values[i]);
        }

        var id = PostgisDiagnostics.FeatureIdentity(identityIndexes, values, ordinal);
        return new Feature(new FeatureId(id), Concrete(schema), attributes);
    }

    /// <summary>Feature construction requires the concrete implementation; discovery builds it for every schema this mapper sees.</summary>
    private static FeatureSchema Concrete(IFeatureSchema schema) =>
        schema as FeatureSchema
        ?? throw new InvalidOperationException("the row mapper only maps against FeatureSchema implementations.");

    /// <summary>The bound parameter values of one feature for an insert (geometry through the EWKB writer).</summary>
    public static object?[] Parameters(FeatureSchema schema, Feature feature, int srid)
    {
        var values = new object?[schema.Count];
        for (var i = 0; i < schema.Count; i++)
        {
            values[i] = ToParameter(schema[i], feature[i], srid);
        }

        return values;
    }

    /// <summary>Converts one attribute to its insert parameter; null stays null, everything else per-kind.</summary>
    private static object? ToParameter(FieldDefinition field, AttributeValue attribute, int srid)
    {
        if (attribute.IsNull)
        {
            return null;
        }

        return ParameterConverters[field.Kind](attribute, srid);
    }

    private static readonly Dictionary<AttributeKind, Func<AttributeValue, int, object>> ParameterConverters = new()
    {
        [AttributeKind.Boolean] = (attribute, _) => attribute.BooleanValue,
        [AttributeKind.Int64] = (attribute, _) => attribute.Int64Value,
        [AttributeKind.Double] = (attribute, _) => attribute.DoubleValue,
        [AttributeKind.String] = (attribute, _) => attribute.StringValue,
        [AttributeKind.Geometry] = (attribute, srid) => PostgisEwkb.WriteGeometry(attribute, srid),
        [AttributeKind.DateTimeOffset] = (attribute, _) => attribute.DateTimeOffsetValue,
        [AttributeKind.Guid] = (attribute, _) => attribute.GuidValue,
    };

    /// <summary>Converts one raw value to its attribute; null → Null, otherwise per-kind dispatch.</summary>
    private static AttributeValue MapAttribute(FieldDefinition field, object? value)
    {
        if (value is null or DBNull)
        {
            return AttributeValue.Null;
        }

        return Converters[field.Kind](value);
    }

    private static readonly Dictionary<AttributeKind, Func<object?, AttributeValue>> Converters = new()
    {
        [AttributeKind.Boolean] = value => AttributeValue.FromBoolean((bool)value!),
        [AttributeKind.Int64] = value => AttributeValue.FromInt64(System.Convert.ToInt64(value, CultureInfo.InvariantCulture)),
        [AttributeKind.Double] = value => AttributeValue.FromDouble(System.Convert.ToDouble(value, CultureInfo.InvariantCulture)),
        [AttributeKind.String] = value => AttributeValue.FromString((string)value!),
        [AttributeKind.Geometry] = value => PostgisEwkb.ReadGeometry((byte[])value!),
        [AttributeKind.DateTimeOffset] = value => AttributeValue.FromDateTimeOffset(ToOffset(value!)),
        [AttributeKind.Guid] = value => AttributeValue.FromGuid((Guid)value!),
    };

    private static DateTimeOffset ToOffset(object value) =>
        value switch
        {
            DateTimeOffset offset => offset,
            DateTime dateTime => PostgisDiagnostics.ToDateTimeOffset(dateTime),
            _ => throw new InvalidOperationException($"a date column read '{value.GetType().Name}', expected DateTime or DateTimeOffset."),
        };
}
