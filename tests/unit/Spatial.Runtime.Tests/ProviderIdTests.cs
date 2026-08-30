using Spatial.PluginSdk.Capabilities;

namespace Spatial.Runtime.Tests;

/// <summary>
/// Provider id parsing, validation and ordering — the stable provider
/// identity used by deterministic resolution (plan §9 step 4, §10.4).
/// </summary>
public sealed class ProviderIdTests
{
    [Theory]
    [InlineData("nts@1", "nts", 1)]
    [InlineData("postgis@2", "postgis", 2)]
    [InlineData("example@1", "example", 1)]
    public void Parse_produces_name_and_version(string text, string name, int version)
    {
        var id = ProviderId.Parse(text);

        Assert.Equal(name, id.Name);
        Assert.Equal(version, id.Version);
    }

    [Theory]
    [InlineData("nts@1")]
    public void ToString_roundtrips(string text) =>
        Assert.Equal(text, ProviderId.Parse(text).ToString());

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("@1")]
    [InlineData("nts@0")]
    [InlineData("nts@-1")]
    [InlineData("nts")]
    [InlineData("nts@")]
    [InlineData("Nts@1")]
    [InlineData("n_ts@1")]
    [InlineData("nts@1x")]
    public void TryParse_rejects_invalid_ids(string? text) =>
        Assert.False(ProviderId.TryParse(text, out _));

    [Fact]
    public void Parse_throws_FormatException_for_invalid_text() =>
        Assert.Throws<FormatException>(() => ProviderId.Parse("nope"));

    [Fact]
    public void Equality_compares_name_and_version()
    {
        var first = ProviderId.Parse("nts@1");
        var second = new ProviderId("nts", 1);

        Assert.Equal(first, second);
        Assert.True(first == second);
        Assert.NotEqual(first, ProviderId.Parse("nts@2"));
        Assert.NotEqual(first, ProviderId.Parse("postgis@1"));
    }

    [Fact]
    public void Ordering_is_by_name_then_version()
    {
        var a1 = new ProviderId("a", 1);
        var a2 = new ProviderId("a", 2);
        var b1 = new ProviderId("b", 1);

        Assert.True(a1 < a2);
        Assert.True(a2 < b1);
        Assert.Equal(-1, a1.CompareTo(a2));
        Assert.Equal(1, b1.CompareTo(a2));
    }

    [Theory]
    [InlineData("")]
    [InlineData("Nts")]
    public void Constructor_rejects_invalid_names(string name) =>
        Assert.Throws<ArgumentException>(() => new ProviderId(name, 1));

    [Fact]
    public void Constructor_rejects_non_positive_versions() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new ProviderId("nts", 0));
}
