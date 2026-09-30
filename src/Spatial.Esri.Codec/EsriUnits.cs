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
///
/// Each code also carries its <c>esriSRUnitType</c> symbolic name
/// (<c>esriSRUnit_Meter</c> and friends), because a real client sends that
/// spelling: the ArcGIS REST JS allowlist research
/// (research/arcgis/conformance-sources.md T9) records
/// <c>?distance=100&units=esriSRUnit_Meter</c>, and a request in the symbolic
/// spelling used to be a typed invalid-arguments failure. The names are the
/// enum member names the v1.0 specification points at
/// (http://links.esri.com/esriSRUnitTypeConstants, §7.0.6.2 and the buffer
/// section); only the names of the curated codes are mapped, and the mapping
/// is name → the same code, so the two spellings cannot drift. 9096 and
/// 109001 share the name <c>esriSRUnit_Yard</c> at an identical factor, so
/// the name resolves to the lower code.
/// </summary>
public static class EsriUnits
{
    /// <summary>The <c>esriSRUnitType</c> enum prefix a symbolic name carries.</summary>
    private const string SymbolicPrefix = "esriSRUnit_";

    private static readonly Dictionary<int, (string Name, string Symbolic, double MetresPerUnit)> Linear =
        new Dictionary<int, (string, string, double)>
        {
            [9001] = ("metre", "Meter", 1.0),
            [9002] = ("foot", "Foot", 0.3048),
            [9003] = ("US survey foot", "Foot_US", 0.304800609601219),
            [9005] = ("Clarke's foot", "Foot_Clarke", 0.3047972654),
            [9014] = ("fathom", "Fathom", 1.8288),
            [9030] = ("nautical mile", "NauticalMile", 1852.0),
            [9033] = ("US survey chain", "Chain_US", 20.11684023368047),
            [9034] = ("US survey link", "Link_US", 0.20116840233680472),
            [9035] = ("US survey mile", "Mile_US", 1609.347218694437),
            [9036] = ("kilometre", "Kilometer", 1000.0),
            [9093] = ("statute mile", "Mile", 1609.344),
            [9096] = ("yard", "Yard", 0.9144),
            [9097] = ("chain", "Chain", 20.1168),
            [109001] = ("yard", "Yard", 0.9144),
            [109002] = ("US survey yard", "Yard_US_Survey", 0.9144018288036577),
        };

    private static readonly Dictionary<int, (string Name, string Symbolic, double DegreesPerUnit)> Angular =
        new Dictionary<int, (string, string, double)>
        {
            [9101] = ("radian", "Radian", 180.0 / Math.PI),
            [9102] = ("decimal degree", "Degree", 1.0),
        };

    /// <summary>Symbolic <c>esriSRUnit_*</c> name → code, lower code winning a duplicate.</summary>
    private static readonly Dictionary<string, int> BySymbolic = Linear.Concat(Angular)
        .GroupBy(entry => entry.Value.Symbolic, StringComparer.OrdinalIgnoreCase)
        .ToDictionary(
            group => group.Key,
            group => group.Min(entry => entry.Key),
            StringComparer.OrdinalIgnoreCase);

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

    /// <summary>
    /// Resolves an <c>esriSRUnit_*</c> symbolic name to its curated code, so
    /// the spelling a real client sends lands on the same code its number
    /// would. The prefix is required and the comparison ignores case; an
    /// unprefixed or unknown name misses rather than being guessed at.
    /// </summary>
    public static bool TryGetBySymbolicName(string symbolicName, out int code)
    {
        if (!string.IsNullOrWhiteSpace(symbolicName)
            && symbolicName.Trim().StartsWith(SymbolicPrefix, StringComparison.OrdinalIgnoreCase)
            && BySymbolic.TryGetValue(symbolicName.Trim()[SymbolicPrefix.Length..], out var resolved))
        {
            code = resolved;
            return true;
        }

        code = 0;
        return false;
    }

    /// <summary>The <c>esriSRUnit_*</c> name a curated code answers to.</summary>
    public static bool TryGetSymbolicName(int code, out string symbolicName)
    {
        symbolicName = Linear.TryGetValue(code, out var linear)
            ? SymbolicPrefix + linear.Symbolic
            : Angular.TryGetValue(code, out var angular)
                ? SymbolicPrefix + angular.Symbolic
                : string.Empty;

        return symbolicName.Length > SymbolicPrefix.Length;
    }

    /// <summary>Whether the code names an angular unit (linear otherwise, when known).</summary>
    public static bool IsAngular(int code) => Angular.ContainsKey(code);

    /// <summary>
    /// The supported codes for invalid-argument messages, each with the
    /// symbolic name that resolves to it so a client is told both spellings.
    /// </summary>
    public static string DescribeSupported() =>
        string.Join(
            ", ",
            Linear.OrderBy(entry => entry.Key)
                .Select(entry => Describe(entry.Key, entry.Value.Name, entry.Value.Symbolic))
                .Concat(Angular.OrderBy(entry => entry.Key)
                    .Select(entry => Describe(entry.Key, entry.Value.Name, entry.Value.Symbolic))));

    private static string Describe(int code, string name, string symbolic) =>
        $"{code.ToString(CultureInfo.InvariantCulture)} ({name}, {SymbolicPrefix}{symbolic})";
}
