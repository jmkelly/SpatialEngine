using Spatial.Core.Features;

namespace Spatial.Adapter.Ogc.Tests;

/// <summary>
/// Quality-loop pass: pin <c>WfsService.CompareAttribute</c> ordering across
/// nulls, kinds and every scalar kind (covers the extracted null-rank and
/// same-kind helpers).
/// </summary>
public sealed class WfsCompareAttributeTests
{
    [Fact]
    public void Nulls_sort_last() =>
        Assert.True(WfsService.CompareAttribute(AttributeValue.Null, AttributeValue.FromInt64(1)) > 0);

    [Fact]
    public void Nulls_compare_equal() =>
        Assert.Equal(0, WfsService.CompareAttribute(AttributeValue.Null, AttributeValue.Null));

    [Fact]
    public void Non_null_sorts_before_null() =>
        Assert.True(WfsService.CompareAttribute(AttributeValue.FromInt64(1), AttributeValue.Null) < 0);

    [Fact]
    public void Different_kinds_order_by_kind()
    {
        var byKind = WfsService.CompareAttribute(AttributeValue.FromInt64(1), AttributeValue.FromString("a"));
        Assert.Equal(AttributeKind.Int64.CompareTo(AttributeKind.String), byKind);
    }

    [Theory]
    [InlineData(true, false, 1)]
    [InlineData(false, true, -1)]
    public void Booleans_compare(bool left, bool right, int sign) =>
        Assert.Equal(sign, Math.Sign(WfsService.CompareAttribute(
            AttributeValue.FromBoolean(left), AttributeValue.FromBoolean(right))));

    [Fact]
    public void Integers_compare() =>
        Assert.True(WfsService.CompareAttribute(AttributeValue.FromInt64(1), AttributeValue.FromInt64(2)) < 0);

    [Fact]
    public void Doubles_compare() =>
        Assert.True(WfsService.CompareAttribute(AttributeValue.FromDouble(2.5), AttributeValue.FromDouble(1.5)) > 0);

    [Fact]
    public void Strings_compare_ordinal() =>
        Assert.True(WfsService.CompareAttribute(AttributeValue.FromString("a"), AttributeValue.FromString("b")) < 0);

    [Fact]
    public void Timestamps_compare() =>
        Assert.True(WfsService.CompareAttribute(
            AttributeValue.FromDateTimeOffset(DateTimeOffset.FromUnixTimeMilliseconds(1)),
            AttributeValue.FromDateTimeOffset(DateTimeOffset.FromUnixTimeMilliseconds(2))) < 0);

    [Fact]
    public void Guids_compare() =>
        Assert.True(WfsService.CompareAttribute(
            AttributeValue.FromGuid(Guid.Empty),
            AttributeValue.FromGuid(Guid.Parse("11111111-1111-1111-1111-111111111111"))) < 0);

    [Fact]
    public void Geometry_throws_invalid() =>
        Assert.ThrowsAny<Exception>(() => WfsService.CompareAttribute(
            AttributeValue.FromGeometry(Spatial.Core.Geometry.GeometryFactory.CreatePoint(1, 2)),
            AttributeValue.FromGeometry(Spatial.Core.Geometry.GeometryFactory.CreatePoint(3, 4))));
}
