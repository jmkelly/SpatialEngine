using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.Json.Nodes;
using Spatial.Core.Geometry;
using Spatial.PluginSdk.Capabilities;
using Spatial.PluginSdk.Resources;
using Spatial.PluginSdk.Transformations;

namespace Spatial.PluginSdk.Codec;

/// <summary>
/// The language-neutral inline value codec shared by every public boundary
/// (ADR-0025/0030): the worker wire protocol (<c>Spatial.PluginHost.DotNet</c>)
/// and the HTTP host API (<c>Spatial.Host</c>) carry argument and result
/// values with this one encoding, so a value never changes shape between
/// boundaries. The codec carries scalars, <c>$i64</c>-tagged 64-bit integers,
/// base64 <c>$bytes</c>, <c>$geometry</c>-tagged canonical binary geometry
/// (ADR-0020, Phase 6), <c>$crs</c>-tagged CRS descriptions (ADR-0027,
/// Phase 7) and opaque <c>$resource</c> handles; spatial values other than
/// geometry (feature batches in particular) are rejected — they cross as
/// bounded streams, never inline.
/// </summary>
public static class ValueCodec
{
    /// <summary>
    /// Encodes a value to its wire form. Throws <see cref="ValueCodecException"/>
    /// for values the inline protocol does not carry (spatial values other than
    /// geometry in particular, with an ADR-0020 hint). Geometry travels as a
    /// <c>$geometry</c> tag carrying canonical binary interchange (SGEOM).
    /// </summary>
    public static JsonNode? Encode(object? value)
    {
        return value switch
        {
            null => null,
            bool flag => JsonValue.Create(flag),
            string text => JsonValue.Create(text),
            byte[] bytes => BytesNode(bytes),
            ResourceHandle handle => ResourceNode(handle),
            ProviderId id => JsonValue.Create(id.ToString()),
            IGeometry geometry => GeometryNode(geometry),
            CrsDescription description => CrsNode(description),
            _ => EncodeNumber(value),
        };
    }

    private static JsonNode? EncodeNumber(object value) => value switch
    {
        int number => JsonValue.Create(number),
        long number => Int64Node(number),
        double number => JsonValue.Create(number),
        float number => JsonValue.Create((double)number),
        _ => throw Unsupported(value),
    };

    /// <summary>
    /// Decodes a wire node back to a CLR value: JSON numbers become
    /// <see cref="int"/> when integral and in range, else <see cref="long"/>,
    /// else <see cref="double"/>; tagged nodes become their tagged type.
    /// A <c>$geometry</c> node decodes to an <see cref="IGeometry"/> via the
    /// canonical binary decoder (<see cref="Spatial.Core.Geometry.GeometryCodec"/>)
    /// and rejects malformed payloads with a byte-accurate error.
    /// </summary>
    public static object? Decode(JsonNode? node)
    {
        if (node is null)
        {
            return null;
        }

        if (node is JsonObject obj)
        {
            return DecodeObject(obj);
        }

        if (node is JsonArray)
        {
            throw new ValueCodecException(
                "arrays are not supported as inline values; an inline value is a scalar, $i64, $bytes, $geometry, $crs or $resource");
        }

        if (node is JsonValue value)
        {
            return DecodeValue(value);
        }

        throw new ValueCodecException($"unsupported value node {node.GetType().Name}");
    }

    private static object DecodeObject(JsonObject obj)
    {
        if (TryValueTag(obj, "$i64", out var i64Node))
        {
            return ReadInt64(i64Node, "$i64");
        }

        if (TryValueTag(obj, "$bytes", out var bytesNode))
        {
            return ReadBytes(bytesNode, "$bytes");
        }

        if (TryValueTag(obj, "$geometry", out var geometryNode))
        {
            return ReadGeometry(geometryNode);
        }

        if (TryObjectTag(obj, "$crs", out var crsNode))
        {
            return ReadCrs(crsNode);
        }

        if (TryObjectTag(obj, "$resource", out var resourceNode))
        {
            return ReadResource(resourceNode);
        }

        throw new ValueCodecException(
            "object values are not supported as inline values; "
            + "remember to tag 64-bit integers ($i64), bytes ($bytes), geometry ($geometry), CRS descriptions ($crs) and resource handles ($resource)");
    }

    /// <summary>Reads a scalar-valued tag (<c>$i64</c>, <c>$bytes</c>, <c>$geometry</c>).</summary>
    private static bool TryValueTag(JsonObject obj, string tag, [NotNullWhen(true)] out JsonValue? value)
    {
        if (obj.TryGetPropertyValue(tag, out var node) && node is JsonValue typed)
        {
            value = typed;
            return true;
        }

        value = null;
        return false;
    }

    /// <summary>Reads an object-valued tag (<c>$crs</c>, <c>$resource</c>).</summary>
    private static bool TryObjectTag(JsonObject obj, string tag, [NotNullWhen(true)] out JsonObject? value)
    {
        if (obj.TryGetPropertyValue(tag, out var node) && node is JsonObject typed)
        {
            value = typed;
            return true;
        }

        value = null;
        return false;
    }

    private static object DecodeValue(JsonValue value)
    {
        if (value.TryGetValue<bool>(out var flag))
        {
            return flag;
        }

        if (value.TryGetValue<int>(out var number))
        {
            return number;
        }

        if (value.TryGetValue<long>(out var longNumber))
        {
            return longNumber;
        }

        if (value.TryGetValue<double>(out var doubleNumber))
        {
            return doubleNumber;
        }

        if (value.TryGetValue<string>(out var text))
        {
            return text;
        }

        throw new ValueCodecException(
            $"the JSON value '{value.ToJsonString()}' is not a supported inline value");
    }

    private static JsonObject Int64Node(long number) =>
        new() { ["$i64"] = number.ToString(CultureInfo.InvariantCulture) };

    private static JsonObject BytesNode(byte[] bytes) =>
        new() { ["$bytes"] = Convert.ToBase64String(bytes) };

    private static JsonObject ResourceNode(ResourceHandle handle) => new()
    {
        ["$resource"] = new JsonObject
        {
            ["token"] = handle.Id.Value.ToString("N"),
            ["kind"] = handle.Kind.Name,
            ["owner"] = handle.Owner.ToString(),
            ["createdAt"] = handle.CreatedAt.ToString("O", CultureInfo.InvariantCulture),
        },
    };

    private static long ReadInt64(JsonValue node, string tag)
    {
        if (node.TryGetValue<string>(out var text)
            && long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var number))
        {
            return number;
        }

        throw new ValueCodecException(
            $"the {tag} tag must carry a decimal string, for example {{\"{tag}\":\"80\"}}");
    }

    private static byte[] ReadBytes(JsonValue node, string tag)
    {
        try
        {
            return Convert.FromBase64String(node.GetValue<string>());
        }
        catch (FormatException exception)
        {
            throw new ValueCodecException($"the {tag} tag must carry a base64 string: {exception.Message}", exception);
        }
    }

    private static JsonObject GeometryNode(IGeometry geometry) =>
        new() { ["$geometry"] = Convert.ToBase64String(GeometryCodec.Encode(geometry)) };

    private static JsonObject CrsNode(CrsDescription description) => new()
    {
        ["$crs"] = new JsonObject
        {
            ["authority"] = description.Authority,
            ["code"] = description.Code,
            ["name"] = description.Name,
            ["kind"] = description.Kind.ToString().ToLowerInvariant(),
            ["dimension"] = description.Dimension,
            ["axes"] = new JsonArray(description.Axes
                .Select(axis => (JsonNode)new JsonObject
                {
                    ["name"] = axis.Name,
                    ["orientation"] = axis.Orientation.ToString().ToLowerInvariant(),
                    ["unit"] = axis.UnitName,
                })
                .ToArray()),
            ["datum"] = description.Datum,
            ["ellipsoid"] = description.Ellipsoid is { } ellipsoid
                ? new JsonObject
                {
                    ["name"] = ellipsoid.Name,
                    ["semiMajor"] = ellipsoid.SemiMajorAxis,
                    ["semiMinor"] = ellipsoid.SemiMinorAxis,
                    ["unit"] = ellipsoid.UnitName,
                }
                : null,
        },
    };

    private static IGeometry ReadGeometry(JsonValue node)
    {
        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(node.GetValue<string>());
        }
        catch (Exception exception) when (exception is FormatException or InvalidOperationException)
        {
            throw new ValueCodecException($"the $geometry tag must carry a base64 canonical geometry: {exception.Message}", exception);
        }

        try
        {
            return GeometryCodec.Decode(bytes);
        }
        catch (CanonicalFormatException exception)
        {
            throw new ValueCodecException(
                $"the $geometry tag must carry canonical geometry bytes (SGEOM, ADR-0020): {exception.Message}", exception);
        }
    }

    private static CrsDescription ReadCrs(JsonObject crs)
    {
        try
        {
            var authority = RequiredString(crs, "authority");
            var code = RequiredString(crs, "code");
            var name = RequiredString(crs, "name");
            var kind = EnumParse<CrsKind>(RequiredString(crs, "kind"));
            var dimension = RequiredInt(crs, "dimension");
            var axes = ReadAxes(crs);
            var datum = crs["datum"]?.GetValue<string>();
            var ellipsoid = ReadEllipsoid(crs["ellipsoid"] as JsonObject);
            return new CrsDescription(authority, code, name, kind, dimension, axes, datum, ellipsoid);
        }
        catch (Exception exception) when (exception is FormatException or InvalidOperationException or ArgumentException)
        {
            throw new ValueCodecException(
                $"the $crs tag must carry a structured CRS description: {exception.Message}", exception);
        }
    }

    private static string RequiredString(JsonObject obj, string property) =>
        obj[property]?.GetValue<string>()
        ?? throw new FormatException($"'{property}' is missing");

    private static int RequiredInt(JsonObject obj, string property) =>
        obj[property]?.GetValue<int>()
        ?? throw new FormatException($"'{property}' is missing");

    private static T EnumParse<T>(string text)
        where T : struct, Enum =>
        Enum.TryParse<T>(text, ignoreCase: true, out var value)
            ? value
            : throw new FormatException($"'{text}' is not a valid {typeof(T).Name}");

    private static List<CrsAxis> ReadAxes(JsonObject crs)
    {
        if (crs["axes"] is not JsonArray axesNode)
        {
            throw new FormatException("'axes' is missing");
        }

        var axes = new List<CrsAxis>(axesNode.Count);
        foreach (var element in axesNode)
        {
            if (element is not JsonObject axis)
            {
                throw new FormatException("axes entries must be objects");
            }

            axes.Add(new CrsAxis(
                RequiredString(axis, "name"),
                EnumParse<AxisOrientation>(RequiredString(axis, "orientation")),
                RequiredString(axis, "unit")));
        }

        return axes;
    }

    private static CrsEllipsoid? ReadEllipsoid(JsonObject? ellipsoid) =>
        ellipsoid is null
            ? null
            : new CrsEllipsoid(
                RequiredString(ellipsoid, "name"),
                RequiredDouble(ellipsoid, "semiMajor"),
                RequiredDouble(ellipsoid, "semiMinor"),
                RequiredString(ellipsoid, "unit"));

    private static double RequiredDouble(JsonObject obj, string property) =>
        obj[property]?.GetValue<double>()
        ?? throw new FormatException($"'{property}' is missing");

    private static ResourceHandle ReadResource(JsonObject resource)
    {
        try
        {
            var token = resource["token"]?.GetValue<string>();
            var kind = resource["kind"]?.GetValue<string>();
            var owner = resource["owner"]?.GetValue<string>();
            var createdAt = resource["createdAt"]?.GetValue<string>();
            if (token is null || kind is null || owner is null || createdAt is null)
            {
                throw new ValueCodecException("the $resource tag must carry token, kind, owner and createdAt");
            }

            var handle = new ResourceHandle(
                new ResourceId(Guid.Parse(token)),
                ResourceKind.Parse(kind),
                ProviderId.Parse(owner),
                DateTimeOffset.Parse(createdAt, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind));
            return handle;
        }
        catch (Exception exception) when (exception is FormatException or ArgumentException or ValueCodecException)
        {
            throw new ValueCodecException($"the $resource tag is malformed: {exception.Message}", exception);
        }
    }

    private static ValueCodecException Unsupported(object value)
    {
        var type = value.GetType();
        var hint = type.FullName?.StartsWith("Spatial.Core.", StringComparison.Ordinal) == true
            ? " Spatial values other than geometry cross the boundary as canonical binary interchange (ADR-0020): geometry uses the $geometry tag, feature batches use streams."
            : string.Empty;
        return new ValueCodecException(
            $"'{type.FullName}' is not supported by the inline value codec; supported values are "
            + "null, bool, int32, int64 ($i64), double, string, byte[] ($bytes), IGeometry ($geometry), CrsDescription ($crs), ResourceHandle ($resource) and ProviderId."
            + hint);
    }
}