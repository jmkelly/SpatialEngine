namespace Spatial.Adapter.Ogc;

/// <summary>
/// OGC CRS identity handling (ADR-0053 §3): the engine's contract is x-first
/// for every CRS, but WMS 1.3.0 declares EPSG:4326 with latitude first, so a
/// request bbox for that CRS must be swapped. The adapter accepts the simple
/// <c>EPSG:4326</c>/<c>CRS:84</c>/<c>EPSG:3857</c> identities plus the common
/// URN form.
/// </summary>
internal static class OgcCrs
{
    /// <summary>Resolves a request CRS to the engine identity and its axis order.</summary>
    public static (string Crs, bool YFirst) Resolve(string value)
    {
        var identity = Normalize(value).ToUpperInvariant();
        return identity switch
        {
            "EPSG:4326" or "URN:OGC:DEF:CRS:EPSG::4326" => ("EPSG:4326", true),
            "CRS:84" or "OGC:CRS84" => ("EPSG:4326", false),
            "EPSG:3857" or "EPSG:900913" => ("EPSG:3857", false),
            _ => throw OgcServiceException.Invalid(
                $"Unsupported CRS '{value}'; supported CRS identities are EPSG:4326, CRS:84 and EPSG:3857."),
        };
    }

    private static string Normalize(string value)
    {
        const string Urn = "urn:ogc:def:crs:";
        var trimmed = value.Trim();
        if (!trimmed.StartsWith(Urn, StringComparison.OrdinalIgnoreCase))
        {
            return trimmed;
        }

        var parts = trimmed[Urn.Length..].Split(':');
        return $"{parts[0]}:{parts[^1]}";
    }
}
