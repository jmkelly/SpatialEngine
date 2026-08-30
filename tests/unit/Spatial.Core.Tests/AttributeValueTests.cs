using Spatial.Core.Features;
using Spatial.Core.Geometry;

namespace Spatial.Core.Tests;

/// <summary>
/// AttributeValue: factory/kind/getter discipline, structural equality
/// (NaN-equal doubles, ordinal strings, GeometryComparer, exact date-times),
/// hash consistency and diagnostics.
/// </summary>
public class AttributeValueTests
{
    [Fact]
    public void Factories_produce_the_declared_kinds()
    {
        Assert.Equal(AttributeKind.Null, AttributeValue.Null.Kind);
        Assert.True(AttributeValue.Null.IsNull);
        Assert.Equal(AttributeKind.Boolean, AttributeValue.FromBoolean(true).Kind);
        Assert.Equal(AttributeKind.Int64, AttributeValue.FromInt64(42).Kind);
        Assert.Equal(AttributeKind.Double, AttributeValue.FromDouble(3.5).Kind);
        Assert.Equal(AttributeKind.String, AttributeValue.FromString("x").Kind);
        Assert.Equal(AttributeKind.Geometry, AttributeValue.FromGeometry(GeometryFactory.CreatePoint(1, 2)).Kind);
        Assert.Equal(AttributeKind.DateTimeOffset, AttributeValue.FromDateTimeOffset(new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero)).Kind);
        Assert.Equal(AttributeKind.Guid, AttributeValue.FromGuid(Guid.Empty).Kind);
    }

    [Fact]
    public void Default_value_is_null()
    {
        var value = default(AttributeValue);
        Assert.True(value.IsNull);
        Assert.Equal(AttributeKind.Null, value.Kind);
        Assert.Equal(AttributeValue.Null, value);
    }

    [Fact]
    public void Getters_return_the_stored_value()
    {
        var dateTime = new DateTimeOffset(2024, 6, 15, 14, 30, 45, TimeSpan.FromHours(5.5));
        var geometry = GeometryFactory.CreateLineString([new Coordinate(0, 0), new Coordinate(1, 1)], CoordinateLayout.Xyz, CoordinateReference.Epsg(4326));
        var guid = new Guid("12345678-1234-1234-1234-123456789abc");

        Assert.True(AttributeValue.FromBoolean(true).BooleanValue);
        Assert.Equal(42L, AttributeValue.FromInt64(42).Int64Value);
        Assert.Equal(3.5, AttributeValue.FromDouble(3.5).DoubleValue);
        Assert.Equal("hello", AttributeValue.FromString("hello").StringValue);
        Assert.Same(geometry, AttributeValue.FromGeometry(geometry).GeometryValue);
        Assert.Equal(dateTime, AttributeValue.FromDateTimeOffset(dateTime).DateTimeOffsetValue);
        Assert.Equal(guid, AttributeValue.FromGuid(guid).GuidValue);
    }

    [Fact]
    public void DateTimeOffset_round_trips_offsets_and_ticks_exactly()
    {
        var inputs = new[]
        {
            new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.FromHours(14)),
            new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.FromHours(-5.5)),
            DateTimeOffset.MaxValue,
            DateTimeOffset.MinValue,
        };

        foreach (var input in inputs)
        {
            var value = AttributeValue.FromDateTimeOffset(input);
            Assert.Equal(input, value.DateTimeOffsetValue);
            Assert.Equal(input.Offset, value.DateTimeOffsetValue.Offset);
        }
    }

    [Theory]
    [InlineData(AttributeKind.Boolean)]
    [InlineData(AttributeKind.Int64)]
    [InlineData(AttributeKind.Double)]
    [InlineData(AttributeKind.String)]
    [InlineData(AttributeKind.Geometry)]
    [InlineData(AttributeKind.DateTimeOffset)]
    [InlineData(AttributeKind.Guid)]
    [InlineData(AttributeKind.Null)]
    public void Kind_checking_getters_throw_for_other_kinds(AttributeKind kind)
    {
        var value = SampleValue(kind);
        var getters = new (string Name, Action Read)[]
        {
            ("BooleanValue", () => { _ = value.BooleanValue; }),
            ("Int64Value", () => { _ = value.Int64Value; }),
            ("DoubleValue", () => { _ = value.DoubleValue; }),
            ("StringValue", () => { _ = value.StringValue; }),
            ("GeometryValue", () => { _ = value.GeometryValue; }),
            ("DateTimeOffsetValue", () => { _ = value.DateTimeOffsetValue; }),
            ("GuidValue", () => { _ = value.GuidValue; }),
        };

        foreach (var (name, read) in getters)
        {
            if (name == $"{kind}Value")
            {
                continue; // that getter must succeed
            }

            var exception = Assert.Throws<InvalidOperationException>(read);
            Assert.Contains(kind.ToString(), exception.Message);
            Assert.Contains(name[..^5], exception.Message);
        }
    }

    [Fact]
    public void Null_arguments_are_rejected()
    {
        Assert.Throws<ArgumentNullException>(() => AttributeValue.FromString(null!));
        Assert.Throws<ArgumentNullException>(() => AttributeValue.FromGeometry(null!));
    }

    [Fact]
    public void Doubles_compare_nan_equal()
    {
        var a = AttributeValue.FromDouble(double.NaN);
        var b = AttributeValue.FromDouble(double.NaN);
        var c = AttributeValue.FromDouble(double.PositiveInfinity);

        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
        Assert.NotEqual(a, c);
        // IEEE semantics: positive and negative zero are equal, and hash alike.
        Assert.Equal(AttributeValue.FromDouble(0), AttributeValue.FromDouble(-0.0));
        Assert.Equal(AttributeValue.FromDouble(0).GetHashCode(), AttributeValue.FromDouble(-0.0).GetHashCode());
    }

    [Fact]
    public void Strings_compare_ordinally()
    {
        Assert.Equal(AttributeValue.FromString("alpha"), AttributeValue.FromString("alpha"));
        Assert.Equal(
            AttributeValue.FromString("alpha").GetHashCode(),
            AttributeValue.FromString("alpha").GetHashCode());
        Assert.NotEqual(AttributeValue.FromString("Alpha"), AttributeValue.FromString("alpha"));
        Assert.NotEqual(AttributeValue.FromString("á"), AttributeValue.FromString("a"));
    }

    [Fact]
    public void Integers_and_booleans_compare_by_value()
    {
        Assert.Equal(AttributeValue.FromInt64(7), AttributeValue.FromInt64(7));
        Assert.Equal(AttributeValue.FromBoolean(false), AttributeValue.FromBoolean(false));
        Assert.NotEqual(AttributeValue.FromInt64(7), AttributeValue.FromInt64(8));
        Assert.NotEqual(AttributeValue.FromBoolean(false), AttributeValue.FromBoolean(true));
        Assert.NotEqual(AttributeValue.FromInt64(1), AttributeValue.FromDouble(1));
    }

    [Fact]
    public void Guids_compare_by_value()
    {
        var guid = new Guid("12345678-1234-1234-1234-123456789abc");
        Assert.Equal(AttributeValue.FromGuid(guid), AttributeValue.FromGuid(guid));
        Assert.NotEqual(AttributeValue.FromGuid(guid), AttributeValue.FromGuid(Guid.NewGuid()));
    }

    [Fact]
    public void Geometries_compare_through_the_geometry_comparer()
    {
        var point = GeometryFactory.CreatePoint(1.5, -2.25, CoordinateReference.Epsg(4326));
        Assert.Equal(AttributeValue.FromGeometry(point), AttributeValue.FromGeometry(GeometryFactory.CreatePoint(1.5, -2.25, CoordinateReference.Epsg(4326))));
        Assert.Equal(
            AttributeValue.FromGeometry(point).GetHashCode(),
            AttributeValue.FromGeometry(GeometryFactory.CreatePoint(1.5, -2.25, CoordinateReference.Epsg(4326))).GetHashCode());
        Assert.NotEqual(AttributeValue.FromGeometry(point), AttributeValue.FromGeometry(GeometryFactory.CreatePoint(1.5, -2.25)));
        Assert.NotEqual(AttributeValue.FromGeometry(point), AttributeValue.FromGeometry(GeometryFactory.CreateEmptyPoint()));
    }

    [Fact]
    public void Date_times_compare_by_instant_and_offset()
    {
        var utc = new DateTimeOffset(2024, 1, 1, 12, 0, 0, TimeSpan.Zero);
        var sameInstant = new DateTimeOffset(2024, 1, 1, 14, 0, 0, TimeSpan.FromHours(2));
        var later = new DateTimeOffset(2024, 1, 1, 13, 0, 0, TimeSpan.Zero);

        Assert.Equal(AttributeValue.FromDateTimeOffset(utc), AttributeValue.FromDateTimeOffset(utc));
        // The BCL considers these equal (same instant); the value model is
        // exact, so representation matters.
        Assert.NotEqual(AttributeValue.FromDateTimeOffset(utc), AttributeValue.FromDateTimeOffset(sameInstant));
        Assert.NotEqual(AttributeValue.FromDateTimeOffset(utc), AttributeValue.FromDateTimeOffset(later));
        Assert.NotEqual(
            AttributeValue.FromDateTimeOffset(utc).GetHashCode(),
            AttributeValue.FromDateTimeOffset(sameInstant).GetHashCode());
    }

    [Fact]
    public void Null_only_equals_null()
    {
        Assert.Equal(AttributeValue.Null, AttributeValue.Null);
        Assert.NotEqual(AttributeValue.Null, AttributeValue.FromString(string.Empty));
        Assert.NotEqual(AttributeValue.Null, AttributeValue.FromBoolean(false));
    }

    [Fact]
    public void ToString_is_informative_and_culture_invariant()
    {
        Assert.Equal("Null", AttributeValue.Null.ToString());
        Assert.Equal("Boolean(True)", AttributeValue.FromBoolean(true).ToString());
        Assert.Equal("Int64(42)", AttributeValue.FromInt64(42).ToString());
        Assert.Equal("Double(3.5)", AttributeValue.FromDouble(3.5).ToString());
        Assert.Contains("String(hello)", AttributeValue.FromString("hello").ToString());
        Assert.Contains("Geometry(Point (1, 2))", AttributeValue.FromGeometry(GeometryFactory.CreatePoint(1, 2)).ToString());
        Assert.Contains("Guid(00000000-0000-0000-0000-000000000000)", AttributeValue.FromGuid(Guid.Empty).ToString());
    }

    [Fact]
    public void Deserialization_shapes_are_not_stored_anywhere()
    {
        // Regression: equality/hash must not depend on the struct's padding
        // or on uninitialised fields of a Null value.
        var @null = AttributeValue.Null;
        Assert.Equal(0, @null.GetHashCode());
        Assert.Equal(@null, default(AttributeValue));
        Assert.Equal(@null.GetHashCode(), default(AttributeValue).GetHashCode());
    }

    private static AttributeValue SampleValue(AttributeKind kind) => kind switch
    {
        AttributeKind.Null => AttributeValue.Null,
        AttributeKind.Boolean => AttributeValue.FromBoolean(true),
        AttributeKind.Int64 => AttributeValue.FromInt64(1),
        AttributeKind.Double => AttributeValue.FromDouble(1),
        AttributeKind.String => AttributeValue.FromString("x"),
        AttributeKind.Geometry => AttributeValue.FromGeometry(GeometryFactory.CreatePoint(0, 0)),
        AttributeKind.DateTimeOffset => AttributeValue.FromDateTimeOffset(DateTimeOffset.UnixEpoch),
        AttributeKind.Guid => AttributeValue.FromGuid(Guid.Empty),
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };
}
