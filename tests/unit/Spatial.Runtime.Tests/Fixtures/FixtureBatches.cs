using Spatial.Core.Features;
using Spatial.Core.Geometry;
using Spatial.PluginSdk.Capabilities;

namespace Spatial.Runtime.Tests.Fixtures;

/// <summary>
/// Helper batches built from core value types, used by the example
/// capabilities and the tests.
/// </summary>
public static class FixtureBatches
{
    public static FeatureBatch Points(params (double X, double Y)[] points)
    {
        var schema = new FeatureSchema(
        [
            new FieldDefinition("name", AttributeKind.String, nullable: true),
            new FieldDefinition("geometry", AttributeKind.Geometry, nullable: true),
        ]);

        var features = new Feature[points.Length];
        for (var i = 0; i < points.Length; i++)
        {
            var point = points[i];
            features[i] = new Feature(
                new FeatureId($"p{i}"),
                schema,
                [
                    AttributeValue.FromString($"p{i}"),
                    AttributeValue.FromGeometry(new Point(new Coordinate(point.X, point.Y))),
                ]);
        }

        return new FeatureBatch(schema, features);
    }
}