using System.Globalization;

namespace Spatial.Ingest.Codec;

/// <summary>
/// Resolves the CRS names a producer actually writes to an EPSG code. RFC 7946
/// removed the <c>crs</c> member and mandates WGS 84 lon/lat, but the
/// producers that have data in a projected CRS still emit the pre-RFC member
/// (GeoJSON 2008) in one of a handful of spellings, so honouring it means
/// accepting all of them rather than one.
/// </summary>
internal static class IngestCrsName
{
    /// <summary>CRS 84: lon/lat order on WGS 84, the GeoJSON default.</summary>
    public const int Crs84 = 4326;

    /// <summary>
    /// The EPSG code a CRS name denotes, or <c>null</c> when the name is a
    /// well-formed but non-EPSG authority (OGC, ESRI, a local datum), which the
    /// engine cannot resolve because its CRS identity is curated (ADR-0009).
    /// </summary>
    public static int? TryResolve(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        var trimmed = name.Trim();
        if (trimmed.Equals("CRS84", StringComparison.OrdinalIgnoreCase)
            || trimmed.Equals("urn:ogc:def:crs:OGC:1.3:CRS84", StringComparison.OrdinalIgnoreCase)
            || trimmed.Equals("urn:ogc:def:crs:OGC::CRS84", StringComparison.OrdinalIgnoreCase))
        {
            return Crs84;
        }

        // "EPSG:4326", "urn:ogc:def:crs:EPSG::4326", "urn:ogc:def:crs:EPSG:9.9.1:4326",
        // "http://www.opengis.net/def/crs/EPSG/0/4326". The authority is
        // checked, not just the trailing number: "…crs:ESRI:102100" ends in
        // digits and would otherwise be read as EPSG:100.
        var segments = trimmed.Split([':', '/'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (segments.Length < 2)
        {
            return null;
        }

        // "…:crs:EPSG::3857" and "…/crs/EPSG/0/3857" name the authority right
        // after "crs"; a bare "EPSG:3857" leads with it.
        var crs = Array.FindIndex(segments, segment => segment.Equals("crs", StringComparison.OrdinalIgnoreCase));
        var authority = segments[crs < 0 ? 0 : crs + 1];
        if (authority.Equals("EPSG", StringComparison.OrdinalIgnoreCase) is false)
        {
            return null;
        }

        return int.TryParse(segments[^1], NumberStyles.None, CultureInfo.InvariantCulture, out var code) && code > 0
            ? code
            : null;
    }

    /// <summary>
    /// The EPSG code a CRS name denotes, or a typed failure explaining that the
    /// engine cannot honour the declaration. A silently ignored declaration is
    /// the defect this replaces.
    /// </summary>
    public static int Resolve(string name)
    {
        var code = TryResolve(name);
        if (code is { } resolved)
        {
            return resolved;
        }

        throw new IngestFormatException(
            $"The document declares CRS '{name}', which is not an EPSG code; the engine only honours declared EPSG CRSs.");
    }
}
