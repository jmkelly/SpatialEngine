using Spatial.Core.Features;
using Spatial.Provider.PostGIS.Geometry;

namespace Spatial.Provider.PostGIS.Core;

/// <summary>
/// The PostGIS typmod each geometry column is created with, resolved from the
/// data's coordinate layout (ADR-0028, ADR-0041): XY, XYZ, XYM or XYZM, so Z
/// and M ordinates survive the round trip instead of being refused by a 2D
/// <c>geometry(Geometry, srid)</c> column. The layout→typmod mapping lives on
/// the EWKB interchange (<see cref="PostgisEwkb"/>), the plugin's single
/// <c>Spatial.Core.Geometry</c> surface; this type only decides consistency.
/// A dataset must use one layout per column: a mix is an
/// <c>invalid.arguments</c> failure rather than a silent truncation or a
/// fabricated ordinate. Empty geometries are ignored — they carry no ordinates
/// to preserve.
/// </summary>
internal static class PostgisGeometryType
{
    /// <summary>The 2D typmod name, used for a geometry column with no non-empty value.</summary>
    public const string DefaultTypeName = "Geometry";

    /// <summary>
    /// Resolves the typmod name of every schema field from the geometries in
    /// <paramref name="features"/>: non-geometry fields get
    /// <see cref="DefaultTypeName"/> (ignored by callers), a geometry field with
    /// no non-empty value is 2D, and a field whose geometries do not all share
    /// one layout fails with an actionable <paramref name="error"/>.
    /// </summary>
    public static bool TryResolve(
        IFeatureSchema schema,
        IEnumerable<Feature> features,
        out string[] typeNames,
        out string error)
    {
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(features);

        typeNames = [];
        var resolved = new string?[schema.Count];
        var layouts = new string?[schema.Count];
        foreach (var feature in features)
        {
            for (var i = 0; i < schema.Count; i++)
            {
                if (schema[i].Kind != AttributeKind.Geometry || feature[i].Kind != AttributeKind.Geometry)
                {
                    continue;
                }

                if (feature[i].GeometryValue is not { IsEmpty: false })
                {
                    continue;
                }

                var observed = PostgisEwkb.SqlTypeName(feature[i]);
                if (resolved[i] is null)
                {
                    resolved[i] = observed;
                    layouts[i] = PostgisEwkb.LayoutName(feature[i]);
                    continue;
                }

                if (resolved[i] != observed)
                {
                    error = $"the geometry field '{schema[i].Name}' mixes {layouts[i]} and {PostgisEwkb.LayoutName(feature[i])} coordinates; use one coordinate layout per dataset.";
                    return false;
                }
            }
        }

        typeNames = new string[schema.Count];
        for (var i = 0; i < resolved.Length; i++)
        {
            typeNames[i] = resolved[i] ?? DefaultTypeName;
        }

        error = string.Empty;
        return true;
    }
}
