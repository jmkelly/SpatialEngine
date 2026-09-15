using System.Globalization;

namespace Spatial.Esri.Codec;

/// <summary>
/// The curated Esri unit-code table for the Geometry Service <c>unit</c>
/// parameter (ADR-0035 §4: Esri codes are never guessed, only mapped).
/// Codes are the numeric <c>esriSRUnitType</c> constants: linear units carry
/// a metres-per-unit factor, angular units a degrees-per-unit factor.
/// Factors were verified empirically against the hosted Geometry Service
/// on 2026-09-14 (buffer <c>distances=1</c> in 3857, radius in metres):
/// 9001→1, 9002→0.3048, 9003→0.3048006096, 9005→0.3047972654,
/// 9014→1.8288, 9030→1852, 9033→20.11684023368047,
/// 9034→0.20116840233680472, 9035→1609.3472186944375, 9036→1000,
/// 9093→1609.344, 9096→0.9144, 9097→20.1168, 109001→0.9144,
/// 109002→0.9144018288036577; 9001 is additionally anchored by the v1.0
/// specification (§7.0.6.2: "the value for meters is 9001") and 9035 by the
/// 10.x buffer sample (10/50 miles with <c>unit=9035</c>). Unknown codes are
/// rejected by the caller with the supported list.
/// </summary>
public static class EsriUnits
{
    private static readonly Dictionary<int, (string Name, double MetresPerUnit)> Linear =
        new Dictionary<int, (string, double)>
        {
            [9001] = ("metre", 1.0),
            [9002] = ("foot", 0.3048),
            [9003] = ("US survey foot", 0.304800609601219),
            [9005] = ("Clarke's foot", 0.3047972654),
            [9014] = ("fathom", 1.8288),
            [9030] = ("nautical mile", 1852.0),
            [9033] = ("US survey chain", 20.11684023368047),
            [9034] = ("US survey link", 0.20116840233680472),
            [9035] = ("US survey mile", 1609.347218694437),
            [9036] = ("kilometre", 1000.0),
            [9093] = ("statute mile", 1609.344),
            [9096] = ("yard", 0.9144),
            [9097] = ("chain", 20.1168),
            [109001] = ("yard", 0.9144),
            [109002] = ("US survey yard", 0.9144018288036577),
        };

    private static readonly Dictionary<int, (string Name, double DegreesPerUnit)> Angular =
        new Dictionary<int, (string, double)>
        {
            [9101] = ("radian", 180.0 / Math.PI),
            [9102] = ("decimal degree", 1.0),
        };

    /// <summary>Resolves a linear unit code to its name and metres per unit.</summary>
    public static bool TryGetLinear(int code, out string name, out double metresPerUnit)
    {
        if (Linear.TryGetValue(code, out var entry))
        {
            name = entry.Name;
            metresPerUnit = entry.MetresPerUnit;
            return true;
        }

        name = string.Empty;
        metresPerUnit = double.NaN;
        return false;
    }

    /// <summary>Resolves an angular unit code to its name and degrees per unit.</summary>
    public static bool TryGetAngular(int code, out string name, out double degreesPerUnit)
    {
        if (Angular.TryGetValue(code, out var entry))
        {
            name = entry.Name;
            degreesPerUnit = entry.DegreesPerUnit;
            return true;
        }

        name = string.Empty;
        degreesPerUnit = double.NaN;
        return false;
    }

    /// <summary>Whether the code names an angular unit (linear otherwise, when known).</summary>
    public static bool IsAngular(int code) => Angular.ContainsKey(code);

    /// <summary>The supported codes for invalid-argument messages.</summary>
    public static string DescribeSupported() =>
        string.Join(
            ", ",
            Linear.OrderBy(entry => entry.Key)
                .Select(entry => $"{entry.Key.ToString(CultureInfo.InvariantCulture)} ({entry.Value.Name})")
                .Concat(Angular.OrderBy(entry => entry.Key)
                    .Select(entry => $"{entry.Key.ToString(CultureInfo.InvariantCulture)} ({entry.Value.Name})")));
}
