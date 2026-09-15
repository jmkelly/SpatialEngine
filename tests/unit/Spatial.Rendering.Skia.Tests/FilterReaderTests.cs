using System.Text.Json;
using Spatial.Contracts;
using Spatial.Core.Features;
using Spatial.Rendering.Skia.Styling;

namespace Spatial.Rendering.Skia.Tests;

public sealed class FilterReaderTests
{
    [Theory]
    [InlineData("[\"==\",\"name\",\"London\"]", true)]
    [InlineData("[\"==\",\"name\",\"Paris\"]", false)]
    [InlineData("[\"!=\",\"name\",\"Paris\"]", true)]
    [InlineData("[\"has\",\"name\"]", true)]
    [InlineData("[\"has\",\"missing\"]", false)]
    [InlineData("[\"!has\",\"missing\"]", true)]
    [InlineData("[\"!has\",\"name\"]", false)]
    [InlineData("[\"in\",\"name\",\"Paris\",\"London\"]", true)]
    [InlineData("[\"in\",\"name\",\"Paris\",\"Rome\"]", false)]
    [InlineData("[\"all\",[\"==\",\"name\",\"London\"],[\"==\",\"population\",900]]", false)]
    [InlineData("[\"all\",[\"==\",\"name\",\"London\"],[\"==\",\"population\",8900000]]", true)]
    [InlineData("[\"any\",[\"==\",\"name\",\"Paris\"],[\"==\",\"name\",\"London\"]]", true)]
    [InlineData("[\"none\",[\"==\",\"name\",\"Paris\"],[\"==\",\"name\",\"Rome\"]]", true)]
    [InlineData("[\"!\",[\"==\",\"name\",\"Paris\"]]", true)]
    [InlineData("[\"==\",\"population\",8900000.0]", true)]
    public void Matches_LowersTheSubset(string filter, bool expected)
    {
        var feature = TestFeatures.Point("London", 0, 0, population: 8_900_000);
        Assert.Equal(expected, Read(filter).Matches(feature));
    }

    [Fact]
    public void Matches_TreatsUnknownFieldAsNull()
    {
        var feature = TestFeatures.Point("London", 0, 0);
        Assert.False(Read("[\"==\",\"missing\",\"x\"]").Matches(feature));
        Assert.True(Read("[\"!=\",\"missing\",\"x\"]").Matches(feature));
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("[\"nonsense\",\"name\"]")]
    [InlineData("[\"==\",\"name\"]")]
    [InlineData("[\"has\"]")]
    [InlineData("[\"in\",\"name\"]")]
    [InlineData("[\"!\"]")]
    [InlineData("[\"==\",1,\"x\"]")]
    [InlineData("[\"==\",\"name\",null]")]
    [InlineData("[\"==\",\"name\",{}]")]
    public void Read_RejectsUnsupportedExpressions(string filter)
    {
        Assert.Throws<SpatialException>(() => Read(filter));
    }

    private static StyleFilter Read(string filter)
    {
        using var document = JsonDocument.Parse(filter);
        return FilterReader.Read(document.RootElement);
    }
}
