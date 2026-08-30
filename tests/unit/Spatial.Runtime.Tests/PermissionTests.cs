using Spatial.PluginSdk.Capabilities;

namespace Spatial.Runtime.Tests;

/// <summary>
/// Permission parsing and validation — the dotted permission names required
/// by capabilities (plan §9) and granted to invocations.
/// </summary>
public sealed class PermissionTests
{
    [Theory]
    [InlineData("spatial.feature.read", "spatial.feature.read")]
    [InlineData("spatial.feature.write", "spatial.feature.write")]
    [InlineData("read", "read")]
    public void Parse_produces_name(string text, string name)
    {
        var permission = Permission.Parse(text);

        Assert.Equal(name, permission.Name);
        Assert.Equal(name, permission.ToString());
    }

    [Theory]
    [InlineData("spatial.feature.read")]
    [InlineData("read")]
    public void TryParse_accepts_valid_names(string text) =>
        Assert.True(Permission.TryParse(text, out _));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("READ")]
    [InlineData("spatial..read")]
    [InlineData(".read")]
    [InlineData("read.")]
    [InlineData("read write")]
    [InlineData("1read")]
    public void TryParse_rejects_invalid_names(string? text) =>
        Assert.False(Permission.TryParse(text, out _));

    [Fact]
    public void Parse_throws_FormatException_for_invalid_text() =>
        Assert.Throws<FormatException>(() => Permission.Parse("nope nope"));

    [Fact]
    public void Constructor_rejects_invalid_names() =>
        Assert.Throws<ArgumentException>(() => new Permission("Nope"));

    [Fact]
    public void Equality_compares_names()
    {
        Assert.Equal(Permission.Parse("spatial.feature.read"), new Permission("spatial.feature.read"));
        Assert.NotEqual(Permission.Parse("spatial.feature.read"), Permission.Parse("spatial.feature.write"));
    }
}
