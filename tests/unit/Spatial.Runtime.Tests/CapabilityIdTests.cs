using Spatial.PluginSdk.Capabilities;

namespace Spatial.Runtime.Tests;

/// <summary>
/// Capability id parsing, validation, equality and ordering — the stable,
/// versioned contract names of the plan (§9, ADR-0007).
/// </summary>
public sealed class CapabilityIdTests
{
    [Theory]
    [InlineData("spatial.geometry.buffer@1", "spatial.geometry.buffer", 1)]
    [InlineData("spatial.feature.scan@12", "spatial.feature.scan", 12)]
    [InlineData("a@1", "a", 1)]
    public void Parse_produces_name_and_version(string text, string name, int version)
    {
        var id = CapabilityId.Parse(text);

        Assert.Equal(name, id.Name);
        Assert.Equal(version, id.Version);
    }

    [Theory]
    [InlineData("spatial.geometry.buffer@1")]
    [InlineData("spatial.feature.scan@7")]
    public void ToString_roundtrips(string text) =>
        Assert.Equal(text, CapabilityId.Parse(text).ToString());

    [Theory]
    [InlineData("spatial.geometry.buffer@1")]
    [InlineData("spatial.feature.scan@42")]
    [InlineData("a@1")]
    public void TryParse_accepts_valid_ids(string text)
    {
        Assert.True(CapabilityId.TryParse(text, out var id));
        Assert.Equal(text, id.ToString());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("@1")]
    [InlineData("spatial@0")]
    [InlineData("spatial@-1")]
    [InlineData("spatial")]
    [InlineData("spatial@")]
    [InlineData("spatial@@1")]
    [InlineData("spatial@1@2")]
    [InlineData("Spatial@1")]
    [InlineData("1spatial@1")]
    [InlineData("spatial..geometry@1")]
    [InlineData(".spatial@1")]
    [InlineData("spatial.@1")]
    [InlineData("spatial.geometry@ 1")]
    [InlineData("spatial.geometry.buffer@1.5")]
    [InlineData("spatial.geometry.buffer@x")]
    public void TryParse_rejects_invalid_ids(string? text) =>
        Assert.False(CapabilityId.TryParse(text, out _));

    [Fact]
    public void Parse_throws_FormatException_for_invalid_text() =>
        Assert.Throws<FormatException>(() => CapabilityId.Parse("not an id"));

    [Fact]
    public void Equality_compares_name_and_version()
    {
        var first = CapabilityId.Parse("spatial.geometry.buffer@1");
        var same = new CapabilityId("spatial.geometry.buffer", 1);
        var otherVersion = CapabilityId.Parse("spatial.geometry.buffer@2");
        var otherName = CapabilityId.Parse("spatial.feature.scan@1");

        Assert.Equal(first, same);
        Assert.True(first == same);
        Assert.False(first == otherVersion);
        Assert.NotEqual(first, otherName);
        Assert.Equal(first.GetHashCode(), same.GetHashCode());
    }

    [Fact]
    public void Ordering_is_by_name_then_version()
    {
        var a1 = new CapabilityId("a", 1);
        var a2 = new CapabilityId("a", 2);
        var b1 = new CapabilityId("b", 1);

        Assert.True(a1 < a2);
        Assert.True(a1 <= a2);
        Assert.True(a2 > a1);
        Assert.True(a2 >= a1);
        Assert.True(a2 < b1);
        Assert.Equal(-1, a1.CompareTo(a2));
        Assert.Equal(0, a1.CompareTo(new CapabilityId("a", 1)));
        Assert.Equal(1, a2.CompareTo(a1));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("Spatial")]
    [InlineData("spatial..buffer")]
    [InlineData("spatial-buffer")]
    [InlineData("1spatial")]
    [InlineData("spatial.")]
    [InlineData("spatial buffer")]
    public void Constructor_rejects_invalid_names(string name) =>
        Assert.Throws<ArgumentException>(() => new CapabilityId(name, 1));

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Constructor_rejects_non_positive_versions(int version) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new CapabilityId("spatial.feature.scan", version));
}