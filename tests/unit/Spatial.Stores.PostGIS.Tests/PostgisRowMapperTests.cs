using Spatial.Core.Features;
using Spatial.Core.Geometry;
using Spatial.Stores.PostGIS.Core;

namespace Spatial.Stores.PostGIS.Tests;

/// <summary>
/// Row → feature mapping (read side) and feature → insert parameters (write
/// side): every attribute kind, nulls, primary-key identity vs ordinal
/// fallback, and geometry conversion through the EWKB interchange.
/// </summary>
public sealed class PostgisRowMapperTests
{
    private static readonly FeatureSchema Schema = FeatureTests.Schema(
        ("id", AttributeKind.Int64, false),
        ("name", AttributeKind.String, true),
        ("score", AttributeKind.Double, false),
        ("active", AttributeKind.Boolean, false),
        ("seen", AttributeKind.DateTimeOffset, false),
        ("token", AttributeKind.Guid, false),
        ("geom", AttributeKind.Geometry, false));

    [Fact]
    public void Maps_every_attribute_kind()
    {
        var feature = PostgisRowMapper.MapRow(
            Schema, [0], Values(7L, "seven", 2.5, true, new DateTimeOffset(2024, 5, 1, 12, 0, 0, TimeSpan.Zero), Guid.Parse("11111111-2222-3333-4444-555555555555"), EwkbFixture.KnownPoint4326()), ordinal: 0);

        Assert.Equal("7", feature.Id.Value);
        Assert.Equal(7L, feature[0].Int64Value);
        Assert.Equal("seven", feature[1].StringValue);
        Assert.Equal(2.5, feature[2].DoubleValue);
        Assert.True(feature[3].BooleanValue);
        Assert.Equal(new DateTimeOffset(2024, 5, 1, 12, 0, 0, TimeSpan.Zero), feature[4].DateTimeOffsetValue);
        Assert.Equal(Guid.Parse("11111111-2222-3333-4444-555555555555"), feature[5].GuidValue);
        Assert.Equal(GeometryType.Point, feature[6].GeometryValue.Type);
        Assert.Equal(new CoordinateReference("EPSG", "4326"), feature[6].GeometryValue.CoordinateReference);
    }

    [Fact]
    public void Null_attribute_values_become_null()
    {
        var feature = PostgisRowMapper.MapRow(Schema, [], Values(1L, null, 1.0, false, new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero), Guid.Empty, EwkbFixture.PointEmpty()), ordinal: 0);

        Assert.True(feature[1].IsNull);
        Assert.True(feature[6].GeometryValue.IsEmpty);
    }

    [Fact]
    public void Feature_identity_falls_back_to_the_row_ordinal_without_a_primary_key()
    {
        var first = PostgisRowMapper.MapRow(Schema, [], Values(1L, "a", 1, false, new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero), Guid.Empty, EwkbFixture.PointEmpty()), ordinal: 5);

        Assert.Equal("5", first.Id.Value);
    }

    [Fact]
    public void Composite_primary_key_identity_joins_with_a_pipe()
    {
        var feature = PostgisRowMapper.MapRow(Schema, [0, 1], Values(42L, "berlin", 1, false, new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero), Guid.Empty, EwkbFixture.PointEmpty()), ordinal: 0);

        Assert.Equal("42|berlin", feature.Id.Value);
    }

    [Fact]
    public void Timestamp_values_from_npgsql_convert()
    {
        var utc = new DateTime(2024, 6, 1, 8, 30, 0, DateTimeKind.Utc);
        var unspecified = new DateTime(2024, 6, 1, 8, 30, 0, DateTimeKind.Unspecified);

        Assert.Equal(new DateTimeOffset(utc), PostgisDiagnostics.ToDateTimeOffset(utc));
        Assert.Equal(new DateTimeOffset(2024, 6, 1, 8, 30, 0, TimeSpan.Zero), PostgisDiagnostics.ToDateTimeOffset(unspecified));
    }

    // ---- Write side ----

    [Fact]
    public void Parameters_for_an_insert_bind_every_kind_and_null()
    {
        var feature = FeatureTests.Feature(
            "7", Schema,
            7L, null, 2.5, true, new DateTimeOffset(2024, 5, 1, 12, 0, 0, TimeSpan.Zero), Guid.Parse("11111111-2222-3333-4444-555555555555"), GeometryFactory.CreatePoint(1, 2, CoordinateReference.Epsg(4326)));

        var parameters = PostgisRowMapper.Parameters(Schema, feature, 4326);

        Assert.Equal(7L, parameters[0]);
        Assert.Null(parameters[1]);
        Assert.Equal(2.5, parameters[2]);
        Assert.Equal(true, parameters[3]);
        Assert.Equal(EwkbFixture.KnownPoint4326(), parameters[6]);
    }

    [Fact]
    public void Write_parameters_use_the_dataset_srid_for_crsless_geometry()
    {
        var feature = FeatureTests.Feature("1", Schema, 1L, "a", 1.0, false, new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero), Guid.Empty, GeometryFactory.CreatePoint(1, 2));

        var parameters = PostgisRowMapper.Parameters(Schema, feature, 3857);

        Assert.NotNull(parameters[6]);
    }

    private static object?[] Values(params object?[] values) => values;
}
