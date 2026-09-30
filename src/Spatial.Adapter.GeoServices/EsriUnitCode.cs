using System.Globalization;
using Spatial.Contracts.Transformations;
using Spatial.Core.Geometry;
using Spatial.Esri.Codec;

namespace Spatial.Adapter.GeoServices;

/// <summary>
/// The shared Esri unit-code resolution (ADR-0035 §4: Esri codes are never
/// guessed, only mapped). The Geometry Service's <c>unit</c> and the
/// Feature query's <c>units</c> are the same code table with the same rule —
/// a linear code needs a projected (metre-based) CRS, an angular code a
/// geographic (degree-based) one, because the engine's verbs are planar and
/// there is no geodesic one — so the parse and the factor live in one place
/// and neither surface can drift from the other.
/// </summary>
internal static class EsriUnitCode
{
    /// <summary>
    /// Parses a <c>esriSRUnitType</c> unit code against the curated table in
    /// either spelling: the numeric code, or the <c>esriSRUnit_*</c> symbolic
    /// name a real client sends (SpatialEngine-m3q; conformance-sources.md
    /// T9 records <c>units=esriSRUnit_Meter</c>). Both land on the same code,
    /// so the Geometry Service's <c>unit</c> and the query's <c>units</c>
    /// stay one surface.
    /// </summary>
    internal static int Parse(string raw, string parameterName)
    {
        if (!int.TryParse(raw.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var code))
        {
            if (EsriUnits.TryGetBySymbolicName(raw, out code))
            {
                return code;
            }

            throw GeoServicesErrors.Invalid(
                $"'{parameterName}' must be a numeric esriSRUnitType code or an esriSRUnit_* name, got '{raw}'. Supported: {EsriUnits.DescribeSupported()}.");
        }

        if (!EsriUnits.IsAngular(code) && !EsriUnits.TryGetLinear(code, out _, out _)
            || EsriUnits.IsAngular(code) && !EsriUnits.TryGetAngular(code, out _, out _))
        {
            throw GeoServicesErrors.Invalid(
                $"Unit code {code} is not in the curated unit table. Supported: {EsriUnits.DescribeSupported()}.");
        }

        return code;
    }

    /// <summary>
    /// The factor from <paramref name="code"/>'s unit to the CRS's own
    /// units: metres for a linear code in a projected CRS, degrees for an
    /// angular code in a geographic one. An absent code means the value is
    /// already in the CRS's own units.
    /// </summary>
    internal static double Factor(int code, CoordinateReference crs, CrsKind kind)
    {
        var angular = EsriUnits.IsAngular(code);
        if (!angular && kind == CrsKind.Projected && EsriUnits.TryGetLinear(code, out _, out var metres))
        {
            // Every projected CRS in the curated catalogue is metre-based.
            return metres;
        }

        if (angular && kind == CrsKind.Geographic && EsriUnits.TryGetAngular(code, out _, out var degrees))
        {
            return degrees;
        }

        if (!angular)
        {
            throw GeoServicesErrors.Invalid(
                $"Unit code {code} is linear but the CRS {crs} is {kind}: " +
                "the planar engine cannot buffer metres in degrees (no geodesic verb). Name a projected spatial reference.");
        }

        throw GeoServicesErrors.Invalid(
            $"Unit code {code} is angular but the CRS {crs} is {kind}: " +
            "name a geographic spatial reference or a linear unit with a projected one.");
    }
}
