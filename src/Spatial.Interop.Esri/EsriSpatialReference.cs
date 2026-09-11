using System.Text.Json;
using Spatial.Core.Geometry;

namespace Spatial.Interop.Esri;

/// <summary>
/// The Esri <c>spatialReference</c> object (<c>{"wkid": 4326}</c>,
/// <c>{"latestWkid": 3857}</c>). Decoding is a curated WKID → EPSG lookup
/// (ADR-0035 §4); a bare <c>wkt</c> is rejected because the engine's CRS
/// identity is EPSG-only (ADR-0009). Encoding writes the WKID for an EPSG
/// identity known to the map.
/// </summary>
public static class EsriSpatialReference
{
    private static readonly string[] WkidProperties = ["wkid", "latestWkid"];

    /// <summary>
    /// Decodes a <c>spatialReference</c> object. A missing or empty object
    /// yields <c>null</c> (unspecified CRS); WKT-only and unknown WKIDs are
    /// rejected.
    /// </summary>
    public static CoordinateReference? Decode(JsonElement element)
    {
        if (element.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }

        if (element.ValueKind != JsonValueKind.Object)
        {
            throw EsriInteropException.Invalid("'spatialReference' must be an object.");
        }

        if (TryWkid(element, out var coordinateReference))
        {
            return coordinateReference;
        }

        if (HasWkt(element))
        {
            throw EsriInteropException.Invalid(
                "A 'wkt' spatial reference is not supported; the engine carries EPSG identities only. Send a 'wkid' from the curated map.");
        }

        throw EsriInteropException.Invalid("'spatialReference' must carry a numeric 'wkid' or 'latestWkid'.");
    }

    private static bool TryWkid(JsonElement element, out CoordinateReference coordinateReference)
    {
        foreach (var name in WkidProperties)
        {
            if (element.TryGetProperty(name, out var wkidElement)
                && wkidElement.ValueKind == JsonValueKind.Number
                && wkidElement.TryGetInt32(out var wkid))
            {
                coordinateReference = Resolve(wkid);
                return true;
            }
        }

        coordinateReference = default;
        return false;
    }

    private static bool HasWkt(JsonElement element) =>
        element.TryGetProperty("wkt", out var wkt) && wkt.ValueKind == JsonValueKind.String;

    /// <summary>Decodes the <c>spatialReference</c> property of a parent object, when present.</summary>
    public static CoordinateReference? DecodeFrom(JsonElement parent)
    {
        return parent.ValueKind == JsonValueKind.Object
            && parent.TryGetProperty("spatialReference", out var reference)
            ? Decode(reference)
            : null;
    }

    /// <summary>Resolves an Esri WKID to a core EPSG identity.</summary>
    public static CoordinateReference Resolve(int wkid)
    {
        if (!WkidMap.TryToEpsg(wkid, out var epsg))
        {
            throw EsriInteropException.Invalid(
                $"WKID {wkid} is not in the curated WKID to EPSG map; unknown codes are not guessed.");
        }

        return CoordinateReference.Epsg(epsg);
    }

    /// <summary>Writes a <c>spatialReference</c> object for the geometry's CRS, when present.</summary>
    public static void Write(Utf8JsonWriter writer, CoordinateReference? coordinateReference)
    {
        ArgumentNullException.ThrowIfNull(writer);
        if (coordinateReference is not { } crs)
        {
            return;
        }

        writer.WritePropertyName("spatialReference");
        writer.WriteStartObject();
        writer.WriteNumber("wkid", ToWkid(crs));
        writer.WriteEndObject();
    }

    /// <summary>The Esri WKID for an EPSG identity, or a typed failure when the map has none.</summary>
    public static int ToWkid(CoordinateReference coordinateReference)
    {
        if (string.Equals(coordinateReference.Authority, "EPSG", StringComparison.OrdinalIgnoreCase)
            && int.TryParse(coordinateReference.Code, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var epsg)
            && WkidMap.TryFromEpsg(epsg, out var wkid))
        {
            return wkid;
        }

        throw EsriInteropException.Invalid(
            $"Coordinate reference {coordinateReference} has no Esri WKID in the curated map; the engine cannot express it as a spatial reference.");
    }
}
