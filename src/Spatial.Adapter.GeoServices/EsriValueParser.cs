using System.Globalization;
using System.Text.Json;
using Spatial.Core.Geometry;
using Spatial.Interop.Esri;

namespace Spatial.Adapter.GeoServices;

/// <summary>
/// Parsers for the GeoServices string parameters: JSON geometry arrays
/// (spec §7.0.4.2 always packages input and output as arrays), the
/// <c>inSR</c>/<c>outSR</c> spatial-reference forms, and numeric lists.
/// Malformed values are typed invalid-argument failures.
/// </summary>
internal static class EsriValueParser
{
    /// <summary>Parses a <c>geometries</c> JSON array (or object / simple syntax) into core geometries.</summary>
    public static List<IGeometry> ParseGeometries(string value, CoordinateReference? fallback)
    {
        var trimmed = value.Trim();
        if (trimmed.StartsWith('['))
        {
            using var document = JsonDocument.Parse(trimmed);
            var geometries = new List<IGeometry>(document.RootElement.GetArrayLength());
            foreach (var element in document.RootElement.EnumerateArray())
            {
                geometries.Add(EsriGeometryCodec.Decode(element, fallback));
            }

            return geometries;
        }

        if (trimmed.StartsWith('{'))
        {
            using var document = JsonDocument.Parse(trimmed);
            return [EsriGeometryCodec.Decode(document.RootElement, fallback)];
        }

        if (EsriGeometryCodec.TryParseSimple(trimmed, out var simple, fallback))
        {
            return [simple!];
        }

        throw GeoServicesErrors.Invalid("'geometries' must be a JSON array of Esri geometry objects or an array of coordinates.");
    }

    /// <summary>Parses a single <c>geometry</c> parameter (object or simple syntax).</summary>
    public static IGeometry ParseGeometry(string value, CoordinateReference? fallback)
    {
        var trimmed = value.Trim();
        if (trimmed.StartsWith('{'))
        {
            using var document = JsonDocument.Parse(trimmed);
            return EsriGeometryCodec.Decode(document.RootElement, fallback);
        }

        if (EsriGeometryCodec.TryParseSimple(trimmed, out var simple, fallback))
        {
            return simple!;
        }

        throw GeoServicesErrors.Invalid("'geometry' must be an Esri geometry object or coordinate list.");
    }

    /// <summary>Parses an <c>inSR</c>/<c>outSR</c> value (a spatial-reference object or a bare WKID).</summary>
    public static CoordinateReference? ParseSpatialReference(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        if (trimmed.StartsWith('{'))
        {
            using var document = JsonDocument.Parse(trimmed);
            return EsriSpatialReference.Decode(document.RootElement);
        }

        if (int.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out var wkid))
        {
            return EsriSpatialReference.Resolve(wkid);
        }

        throw GeoServicesErrors.Invalid($"'{value}' is not a spatial reference (expected a WKID or {{wkid}}).");
    }

    /// <summary>Parses a comma-separated list of doubles.</summary>
    public static IReadOnlyList<double> ParseDoubles(string value, string name)
    {
        var parts = value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            throw GeoServicesErrors.Invalid($"'{name}' must be a comma-separated list of numbers.");
        }

        var values = new double[parts.Length];
        for (var i = 0; i < parts.Length; i++)
        {
            if (!double.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out var number) || !double.IsFinite(number))
            {
                throw GeoServicesErrors.Invalid($"'{name}' must be a comma-separated list of finite numbers, got '{parts[i]}'.");
            }

            values[i] = number;
        }

        return values;
    }

    /// <summary>Parses a comma-separated list of integers.</summary>
    public static IReadOnlyList<long> ParseInt64s(string value, string name)
    {
        var parts = value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            throw GeoServicesErrors.Invalid($"'{name}' must be a comma-separated list of integers.");
        }

        var values = new long[parts.Length];
        for (var i = 0; i < parts.Length; i++)
        {
            if (!long.TryParse(parts[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out var number))
            {
                throw GeoServicesErrors.Invalid($"'{name}' must be a comma-separated list of integers, got '{parts[i]}'.");
            }

            values[i] = number;
        }

        return values;
    }
}
