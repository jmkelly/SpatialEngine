using Spatial.Core.Features;
using Spatial.Core.Geometry;

namespace Spatial.Adapter.GeoServices.Tests;

/// <summary>
/// The <c>orderByFields</c> value comparer: nulls sort last ascending, equal
/// kinds compare naturally, and mismatched kinds fall back to kind ordering.
/// </summary>
public sealed class AttributeValueComparerTests
{
    private static readonly FeatureService.AttributeValueComparer Comparer = FeatureService.AttributeValueComparer.Instance;

    private static int Compare(AttributeValue left, AttributeValue right) => Comparer.Compare(left, right);

    [Fact]
    public void Nulls_sort_after_non_null_values()
    {
        Assert.True(Compare(AttributeValue.Null, AttributeValue.FromInt64(1)) > 0);
        Assert.True(Compare(AttributeValue.FromInt64(1), AttributeValue.Null) < 0);
        Assert.Equal(0, Compare(AttributeValue.Null, AttributeValue.Null));
    }

    [Fact]
    public void Values_of_the_same_kind_compare_naturally()
    {
        Assert.True(Compare(AttributeValue.FromBoolean(false), AttributeValue.FromBoolean(true)) < 0);
        Assert.True(Compare(AttributeValue.FromInt64(1), AttributeValue.FromInt64(2)) < 0);
        Assert.True(Compare(AttributeValue.FromDouble(2.5), AttributeValue.FromDouble(1.5)) > 0);
        Assert.True(Compare(AttributeValue.FromString("a"), AttributeValue.FromString("b")) < 0);
        Assert.True(Compare(
            AttributeValue.FromDateTimeOffset(DateTimeOffset.FromUnixTimeMilliseconds(1)),
            AttributeValue.FromDateTimeOffset(DateTimeOffset.FromUnixTimeMilliseconds(2))) < 0);
        Assert.True(Compare(AttributeValue.FromGuid(Guid.Empty), AttributeValue.FromGuid(Guid.Parse("11111111-1111-1111-1111-111111111111"))) < 0);
        Assert.Equal(0, Compare(AttributeValue.FromInt64(7), AttributeValue.FromInt64(7)));
    }

    [Fact]
    public void Mismatched_kinds_fall_back_to_kind_ordering()
    {
        var intValue = AttributeValue.FromInt64(1);
        var stringValue = AttributeValue.FromString("a");

        Assert.Equal(intValue.Kind.CompareTo(stringValue.Kind), Compare(intValue, stringValue));
        Assert.Equal(stringValue.Kind.CompareTo(intValue.Kind), Compare(stringValue, intValue));
    }

    [Fact]
    public void Geometry_values_have_no_natural_order()
    {
        var left = AttributeValue.FromGeometry(GeometryFactory.CreatePoint(1, 2));
        var right = AttributeValue.FromGeometry(GeometryFactory.CreatePoint(3, 4));

        Assert.Equal(0, Compare(left, right));
    }
}
