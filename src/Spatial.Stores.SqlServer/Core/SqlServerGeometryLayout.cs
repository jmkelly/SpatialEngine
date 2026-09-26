using Spatial.Contracts;
using Spatial.Core.Features;
using Spatial.Stores.SqlServer.Geometry;

namespace Spatial.Stores.SqlServer.Core;

/// <summary>
/// The coordinate layout check of a creating batch (ADR-0028, ADR-0041).
/// SQL Server's <c>geometry</c>/<c>geography</c> columns are planar and
/// two-dimensional, so a dataset may only carry XY geometries: Z and M would
/// be dropped by the server on the way in, so they are refused here as
/// <c>invalid.arguments</c> rather than silently lost. Unlike the PostGIS
/// provider there is no typmod to choose — the rule is one per-field test
/// applied to every geometry of the batch, and the layout knowledge itself
/// stays on the interchange (<see cref="SqlServerWkb"/>, the plugin's single
/// core-geometry surface).
/// </summary>
internal static class SqlServerGeometryLayout
{
    /// <summary>
    /// Rejects the first geometry field whose values are not XY, naming the
    /// field and the layout that cannot be stored.
    /// </summary>
    public static void RequireStorable(IFeatureSchema schema, IEnumerable<Feature> features)
    {
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(features);
        foreach (var feature in features)
        {
            for (var i = 0; i < schema.Count; i++)
            {
                if (schema[i].Kind == AttributeKind.Geometry && !SqlServerWkb.IsStorable(feature[i]))
                {
                    throw SpatialException.BadArguments(
                        $"the geometry field '{schema[i].Name}' is {SqlServerWkb.LayoutName(feature[i])}, "
                        + "but SQL Server spatial columns store XY coordinates only; drop the Z and M ordinates.");
                }
            }
        }
    }
}
