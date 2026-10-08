using Spatial.Core.Features;
using Spatial.Core.Geometry;

namespace Spatial.Core.Tests;

/// <summary>
/// The <see cref="AttributeValue"/> diagnostics and structural equality arms:
/// every kind formats, equal payloads compare equal (and hash equal), and the
/// equality operators agree with <see cref="AttributeValue.Equals(AttributeValue)"/>.
/// </summary>
public sealed class AttributeValueFormattingTests
{
    [Theory]
    [InlineData(true, "Boolean(True)")]
    [InlineData(false, "Boolean(False)")]
    public void Boolean_formats_with_its_flag(bool flag, string expected) =>
        Assert.Equal(expected, AttributeValue.FromBoolean(flag).ToString());

    [Theory]
    [InlineData(0L, "Int64(0)")]
    [InlineData(-42L, "Int64(-42)")]
    public void Integer_formats_with_its_value(long value, string expected) =>
        Assert.Equal(expected, AttributeValue.FromInt64(value).ToString());

    [Fact]
    public void Double_formats_invariantly() =>
        Assert.Equal("Double(3.5)", AttributeValue.FromDouble(3.5).ToString());

    [Fact]
    public void Null_formats_as_null() =>
        Assert.Equal("Null", AttributeValue.Null.ToString());

    [Fact]
    public void String_formats_with_its_text() =>
        Assert.Equal("String(hello)", AttributeValue.FromString("hello").ToString());

    [Fact]
    public void Guid_formats_with_its_value()
    {
        var guid = new Guid("12345678-1234-1234-1234-456789abcdef");
        Assert.Equal($"Guid({guid})", AttributeValue.FromGuid(guid).ToString());
    }

    [Fact]
    public void Date_time_formats_as_round_trip() =>
        Assert.Equal(
            "DateTimeOffset(2024-01-01T00:00:00.0000000+00:00)",
            AttributeValue.FromDateTimeOffset(new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero)).ToString());

    [Fact]
    public void Geometry_formats_with_its_shape() =>
        Assert.StartsWith("Geometry(", AttributeValue.FromGeometry(GeometryFactory.CreatePoint(1, 2)).ToString());

    [Fact]
    public void Envelope_formats_with_its_bounds() =>
        Assert.StartsWith("Envelope(", AttributeValue.FromEnvelope(new Envelope(0, 0, 1, 1)).ToString());

    [Fact]
    public void Equality_operators_agree_with_equals()
    {
        Assert.True(AttributeValue.FromInt64(7) == AttributeValue.FromInt64(7));
        Assert.False(AttributeValue.FromInt64(7) != AttributeValue.FromInt64(7));
        Assert.True(AttributeValue.FromInt64(7) != AttributeValue.FromInt64(8));
        Assert.False(AttributeValue.FromInt64(7) == AttributeValue.FromInt64(8));
        Assert.False(AttributeValue.FromInt64(7) == AttributeValue.FromString("7"));
    }

    [Fact]
    public void Equal_values_hash_equal_for_every_kind()
    {
        var left = Values();
        var right = Values();
        for (var i = 0; i < left.Count; i++)
        {
            Assert.Equal(left[i], right[i]);
            Assert.Equal(left[i].GetHashCode(), right[i].GetHashCode());
        }
    }

    [Fact]
    public void Guid_and_envelope_compare_by_payload()
    {
        var guid = new Guid("12345678-1234-1234-1234-456789abcdef");
        Assert.Equal(AttributeValue.FromGuid(guid), AttributeValue.FromGuid(guid));
        Assert.NotEqual(AttributeValue.FromGuid(guid), AttributeValue.FromGuid(Guid.Empty));
        Assert.Equal(
            AttributeValue.FromEnvelope(new Envelope(0, 0, 1, 1)),
            AttributeValue.FromEnvelope(new Envelope(0, 0, 1, 1)));
        Assert.NotEqual(
            AttributeValue.FromEnvelope(new Envelope(0, 0, 1, 1)),
            AttributeValue.FromEnvelope(new Envelope(0, 0, 2, 2)));
    }

    [Fact]
    public void Date_times_with_different_offsets_are_not_equal()
    {
        var instant = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var shifted = instant.ToOffset(TimeSpan.FromHours(1));
        Assert.NotEqual(
            AttributeValue.FromDateTimeOffset(instant),
            AttributeValue.FromDateTimeOffset(shifted));
        Assert.Equal(
            AttributeValue.FromDateTimeOffset(instant).GetHashCode(),
            AttributeValue.FromDateTimeOffset(instant).GetHashCode());
    }

    private static List<AttributeValue> Values() =>
    [
        AttributeValue.Null,
        AttributeValue.FromBoolean(true),
        AttributeValue.FromInt64(42),
        AttributeValue.FromDouble(2.5),
        AttributeValue.FromString("x"),
        AttributeValue.FromGeometry(GeometryFactory.CreatePoint(1, 2)),
        AttributeValue.FromDateTimeOffset(new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero)),
        AttributeValue.FromGuid(new Guid("12345678-1234-1234-1234-456789abcdef")),
        AttributeValue.FromEnvelope(new Envelope(0, 0, 1, 1)),
    ];
}
