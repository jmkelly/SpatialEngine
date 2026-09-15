namespace Spatial.Esri.Codec;

/// <summary>
/// The curated WKID ↔ EPSG map (ADR-0035 §4). Esri WKIDs are not EPSG
/// codes in general, so the conversion is an explicit table — never a
/// guess. Unknown codes are rejected by the codec.
/// </summary>
/// <remarks>
/// The reverse table deliberately prefers the modern EPSG-number WKID for
/// each code; the forward table still accepts historic Esri aliases such as
/// 102100 and 102113 (both EPSG:3857).
/// </remarks>
public static class WkidMap
{
    private static readonly IReadOnlyDictionary<int, int> ToEpsg = new Dictionary<int, int>
    {
        [4326] = 4326,
        [4258] = 4258,
        [4269] = 4269,
        [4277] = 4277,
        [4171] = 4171,
        [3857] = 3857,
        [102100] = 3857,
        [102113] = 3857,
        [32610] = 32610,
        [32612] = 32612,
        [32632] = 32632,
        [32633] = 32633,
        [25832] = 25832,
        [25833] = 25833,
        [26910] = 26910,
        [27700] = 27700,
        [2154] = 2154,
    };

    private static readonly Dictionary<int, int> FromEpsg =
        ToEpsg.Values.Distinct().ToDictionary(epsg => epsg, epsg => epsg);

    /// <summary>The WKIDs the codec accepts.</summary>
    public static IReadOnlyCollection<int> Wkids => (IReadOnlyCollection<int>)ToEpsg.Keys;

    /// <summary>Whether the WKID is in the curated catalogue.</summary>
    public static bool Contains(int wkid) => ToEpsg.ContainsKey(wkid);

    /// <summary>Resolves an Esri WKID to an EPSG code, or false when unknown.</summary>
    public static bool TryToEpsg(int wkid, out int epsg) => ToEpsg.TryGetValue(wkid, out epsg);

    /// <summary>Resolves an EPSG code to its preferred Esri WKID, or false when unknown.</summary>
    public static bool TryFromEpsg(int epsg, out int wkid) => FromEpsg.TryGetValue(epsg, out wkid);
}
