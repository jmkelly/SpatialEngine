using System.Globalization;
using System.Text;
using System.Text.Json;
using Spatial.Core.Geometry;

namespace Spatial.Interop.Ingest;

/// <summary>
/// Parses GeoJSON into raw records: a single <c>Feature</c>, a
/// <c>FeatureCollection</c>, or newline-delimited <c>Feature</c> objects.
/// Property values are reduced to JSON scalars (nested objects and arrays
/// become their raw JSON text) and geometries are converted immediately to
/// core values.
/// </summary>
internal static class GeoJsonIngest
{
    /// <summary>Parses a FeatureCollection or a single Feature.</summary>
    public static RawFeatureSet ReadCollection(Stream stream, CoordinateReference crs)
    {
        using var document = JsonDocument.Parse(stream);
        var root = document.RootElement;
        var type = StringProperty(root, "type");
        var set = new RawFeatureSet();
        if (type == "FeatureCollection")
        {
            if (!root.TryGetProperty("features", out var features) || features.ValueKind != JsonValueKind.Array)
            {
                throw new IngestFormatException("A GeoJSON FeatureCollection needs an array 'features'.");
            }

            foreach (var feature in features.EnumerateArray())
            {
                Add(set, feature, crs);
            }

            return set;
        }

        if (type == "Feature")
        {
            Add(set, root, crs);
            return set;
        }

        throw new IngestFormatException("The document is not a GeoJSON Feature or FeatureCollection.");
    }

    /// <summary>Parses one Feature per non-blank line.</summary>
    public static RawFeatureSet ReadDelimited(Stream stream, CoordinateReference crs)
    {
        var set = new RawFeatureSet();
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
        var lineNumber = 0;
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            lineNumber++;
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(line);
            }
            catch (JsonException exception)
            {
                throw new IngestFormatException($"Line {lineNumber} is not valid JSON: {exception.Message}", exception);
            }

            using (document)
            {
                Add(set, document.RootElement, crs);
            }
        }

        return set;
    }

    private static void Add(RawFeatureSet set, JsonElement element, CoordinateReference crs)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new IngestFormatException("A GeoJSON feature must be a JSON object.");
        }

        var names = new List<string>();
        var properties = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (element.TryGetProperty("properties", out var props) && props.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in props.EnumerateObject())
            {
                if (properties.ContainsKey(property.Name) is false)
                {
                    names.Add(property.Name);
                }

                properties[property.Name] = Scalar(property.Value);
            }
        }

        set.Add(ReadId(element), names, properties, ReadGeometry(element, crs));
    }

    private static IGeometry? ReadGeometry(JsonElement element, CoordinateReference crs)
    {
        if (!element.TryGetProperty("geometry", out var geometry) || geometry.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }

        if (geometry.ValueKind != JsonValueKind.Object)
        {
            throw new IngestFormatException("A GeoJSON 'geometry' must be an object or null.");
        }

        return GeoJsonGeometryCodec.Decode(geometry, crs);
    }

    private static string? ReadId(JsonElement element)
    {
        if (!element.TryGetProperty("id", out var id))
        {
            return null;
        }

        if (id.ValueKind == JsonValueKind.String)
        {
            return id.GetString();
        }

        if (id.ValueKind == JsonValueKind.Number)
        {
            return id.TryGetInt64(out var integer)
                ? integer.ToString(CultureInfo.InvariantCulture)
                : id.GetDouble().ToString("R", CultureInfo.InvariantCulture);
        }

        return null;
    }

    private static string? StringProperty(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static object? Scalar(JsonElement element)
    {
        if (IsNull(element))
        {
            return null;
        }

        if (TryGetBool(element, out var boolean))
        {
            return boolean;
        }

        if (TryGetNumber(element, out var number))
        {
            return number;
        }

        if (element.ValueKind == JsonValueKind.String)
        {
            return element.GetString();
        }

        return element.GetRawText();
    }

    private static bool IsNull(JsonElement element) =>
        element.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined;

    private static bool TryGetBool(JsonElement element, out bool value)
    {
        if (element.ValueKind == JsonValueKind.True)
        {
            value = true;
            return true;
        }

        if (element.ValueKind == JsonValueKind.False)
        {
            value = false;
            return true;
        }

        value = false;
        return false;
    }

    private static bool TryGetNumber(JsonElement element, out object? value)
    {
        value = null;
        if (element.ValueKind != JsonValueKind.Number)
        {
            return false;
        }

        value = element.TryGetInt64(out var integer) ? (object)integer : element.GetDouble();
        return true;
    }
}
