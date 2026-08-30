using Spatial.Core.Features;
using Spatial.Core.Geometry;

namespace Spatial.Core.Tests;

/// <summary>
/// Seeded pseudo-random feature-batch round trips: a deterministic generator
/// builds random schemas and features (every attribute kind, nullability,
/// NaN ordinates, unicode strings, all geometry shapes); every batch must
/// survive encode → decode → re-encode byte-for-byte, and projection to a
/// random decodable prefix must match a fresh encode of the projected batch.
/// Seeds are fixed so the suite is reproducible in CI.
/// </summary>
public class FeatureBatchCodecPropertyTests
{
    private static readonly IGeometry[] GeometryPool =
    [
        GeometryFactory.CreatePoint(1.5, -2.25),
        GeometryFactory.CreatePoint(1, 2, 3, CoordinateReference.Epsg(4326)),
        GeometryFactory.CreateEmptyPoint(layout: CoordinateLayout.Xyz),
        GeometryFactory.CreateLineString([new Coordinate(0, 0), new Coordinate(5, 5, Z: double.NaN)], CoordinateLayout.Xyz, CoordinateReference.Epsg(4326)),
        GeometryFactory.CreateEmptyLineString(CoordinateLayout.Xym),
        GeometryFactory.CreatePolygon(
            GeometryFactory.CreateLineString([new Coordinate(0, 0), new Coordinate(10, 0), new Coordinate(10, 10), new Coordinate(0, 10), new Coordinate(0, 0)]),
            [GeometryFactory.CreateLineString([new Coordinate(4, 4), new Coordinate(6, 4), new Coordinate(6, 6), new Coordinate(4, 6), new Coordinate(4, 4)])]),
        GeometryFactory.CreateMultiPoint(GeometryFactory.CreatePoint(1, 2), GeometryFactory.CreatePoint(3, 4, 5), GeometryFactory.CreateEmptyPoint()),
        GeometryFactory.CreateGeometryCollection(GeometryFactory.CreatePoint(9, 9), GeometryFactory.CreateEmptyPoint()),
    ];

    private static readonly DateTimeOffset UnixEpoch = DateTimeOffset.UnixEpoch;

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(42)]
    [InlineData(1_337)]
    [InlineData(20_260_214)]
    [InlineData(987_654_321)]
    public void Random_batches_round_trip_exactly(int seed)
    {
        var random = new Random(seed);
        for (var iteration = 0; iteration < 150; iteration++)
        {
            var schema = RandomSchema(random, maxFields: 6);
            var batch = new FeatureBatch(schema, RandomFeatures(random, schema, maxFeatures: 10));
            var bytes = FeatureBatchCodec.Encode(batch);
            var decoded = FeatureBatchCodec.Decode(bytes);

            Assert.True(batch.Equals(decoded), $"Seed {seed} iteration {iteration} failed: {batch} vs {decoded}");
            Assert.Equal(batch.GetHashCode(), decoded.GetHashCode());
            Assert.Equal(bytes, FeatureBatchCodec.Encode(decoded));
        }
    }

    [Theory]
    [InlineData(3)]
    [InlineData(21)]
    [InlineData(123_456)]
    public void Random_batches_project_to_every_valid_prefix(int seed)
    {
        var random = new Random(seed);
        for (var iteration = 0; iteration < 100; iteration++)
        {
            var schema = RandomSchema(random, maxFields: 6);
            if (schema.Count == 0)
            {
                continue;
            }

            var batch = new FeatureBatch(schema, RandomFeatures(random, schema, maxFeatures: 8));
            var bytes = FeatureBatchCodec.Encode(batch);
            var target = new FeatureSchema(schema.Fields.Take(random.Next(1, schema.Count + 1)));

            var projected = FeatureBatchCodec.Decode(bytes, target);
            Assert.Equal(target, projected.Schema);
            Assert.Equal(batch.Count, projected.Count);
            for (var f = 0; f < batch.Count; f++)
            {
                for (var a = 0; a < target.Count; a++)
                {
                    Assert.Equal(batch[f][a], projected[f][a]);
                }
            }

            Assert.Equal(FeatureBatchCodec.Encode(projected), FeatureBatchCodec.Encode(FeatureBatchCodec.Decode(FeatureBatchCodec.Encode(projected), target)));
        }
    }

    private static FeatureSchema RandomSchema(Random random, int maxFields)
    {
        var count = random.Next(maxFields + 1);
        var fields = new FieldDefinition[count];
        for (var i = 0; i < count; i++)
        {
            var kind = (AttributeKind)random.Next(1, 8); // never Null
            fields[i] = new FieldDefinition($"f{i}", kind, nullable: random.Next(4) == 0, description: random.Next(2) == 0 ? $"field {i}" : null);
        }

        return new FeatureSchema(fields);
    }

    private static Feature[] RandomFeatures(Random random, FeatureSchema schema, int maxFeatures)
    {
        var features = new Feature[random.Next(maxFeatures + 1)];
        for (var i = 0; i < features.Length; i++)
        {
            var values = new AttributeValue[schema.Count];
            for (var a = 0; a < schema.Count; a++)
            {
                var field = schema[a];
                values[a] = field.Nullable && random.Next(4) == 0
                    ? AttributeValue.Null
                    : RandomValue(random, field.Kind);
            }

            features[i] = new Feature(new FeatureId($"feature-{random.Next(100_000)}"), schema, values);
        }

        return features;
    }

    private static AttributeValue RandomValue(Random random, AttributeKind kind) => kind switch
    {
        AttributeKind.Boolean => AttributeValue.FromBoolean(random.Next(2) == 0),
        AttributeKind.Int64 => AttributeValue.FromInt64(random.NextInt64()),
        AttributeKind.Double => AttributeValue.FromDouble(random.Next(5) == 0 ? double.NaN : random.NextDouble() * 1_000_000 - 500_000),
        AttributeKind.String => AttributeValue.FromString(RandomString(random)),
        AttributeKind.Geometry => AttributeValue.FromGeometry(GeometryPool[random.Next(GeometryPool.Length)]),
        AttributeKind.DateTimeOffset => AttributeValue.FromDateTimeOffset(RandomDateTimeOffset(random)),
        AttributeKind.Guid => AttributeValue.FromGuid(RandomGuid(random)),
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unexpected attribute kind."),
    };

    private static string RandomString(Random random)
    {
        // Whole strings (never lone surrogate halves): unpaired surrogates do
        // not survive strict UTF-8, so the pool must consist of valid pieces.
        string[] pieces = ["a", "b", " ", "c", "d", "é", "ü", "Ω", "🚀", "\t", "\n"];
        var length = random.Next(12);
        var builder = new System.Text.StringBuilder();
        for (var i = 0; i < length; i++)
        {
            builder.Append(pieces[random.Next(pieces.Length)]);
        }

        return builder.ToString();
    }

    private static DateTimeOffset RandomDateTimeOffset(Random random)
    {
        var ticks = UnixEpoch.UtcTicks + random.NextInt64(0, 365L * 100 * TimeSpan.TicksPerDay);
        var offsetMinutes = (short)(random.Next(2) == 0
            ? random.Next(-840, 841)
            : random.Next(-14, 15) * 60);
        return new DateTimeOffset(
            new DateTime(ticks + (offsetMinutes * TimeSpan.TicksPerMinute), DateTimeKind.Unspecified),
            TimeSpan.FromMinutes(offsetMinutes));
    }

    private static Guid RandomGuid(Random random)
    {
        var bytes = new byte[16];
        random.NextBytes(bytes);
        return new Guid(bytes);
    }
}
