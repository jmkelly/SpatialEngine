using Spatial.Core.Features;
using Spatial.Core.Geometry;

namespace Spatial.Adapter.GeoServices;

/// <summary>
/// Geometry-field helpers shared by the Feature Service query and edit
/// engines: a dataset fixes at most one geometry column, and both paths need
/// its schema index and the feature's value.
/// </summary>
internal static class FeatureGeometry
{
    /// <summary>The schema index of the geometry field, or -1 when there is none.</summary>
    public static int Index(FeatureSchema schema)
    {
        for (var i = 0; i < schema.Count; i++)
        {
            if (schema[i].Kind == AttributeKind.Geometry)
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>The feature's geometry attribute, or null when it stores none.</summary>
    public static IGeometry? Find(Feature feature)
    {
        var index = Index(feature.Schema);
        return index >= 0 && feature[index].Kind == AttributeKind.Geometry ? feature[index].GeometryValue : null;
    }
}
