using Spatial.Stores.PostGIS.Core;

namespace Spatial.Stores.PostGIS.Tests;

/// <summary>
/// The field-name rule (ADR-0041 §3): a discovered column name may keep any
/// case and punctuation because every statement quotes it, and is refused only
/// when a quoted identifier cannot carry it.
/// </summary>
public sealed class PostgisFieldNameTests
{
    [Theory]
    [InlineData("name")]
    [InlineData("LABELRANK")]
    [InlineData("magType")]
    [InlineData("has space")]
    [InlineData("_leading")]
    [InlineData("ümlaut")]
    public void A_name_a_quoted_identifier_can_carry_is_valid(string name) =>
        Assert.True(PostgisFieldName.IsValid(name));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("a\"b")]
    [InlineData("a\0b")]
    public void A_name_a_quoted_identifier_cannot_carry_is_invalid(string? name) =>
        Assert.False(PostgisFieldName.IsValid(name));

    [Fact]
    public void The_identifier_limit_is_a_utf8_byte_limit()
    {
        Assert.True(PostgisFieldName.IsValid(new string('a', PostgisFieldName.MaxBytes)));
        Assert.False(PostgisFieldName.IsValid(new string('a', PostgisFieldName.MaxBytes + 1)));

        // A two-byte character: 31 fit in 63 bytes, 32 do not (64 bytes).
        Assert.True(PostgisFieldName.IsValid(new string('é', 31)));
        Assert.False(PostgisFieldName.IsValid(new string('é', 32)));
    }

    [Fact]
    public void The_reason_names_the_exact_problem()
    {
        Assert.False(PostgisFieldName.TryValidate("", out var empty));
        Assert.Contains("empty", empty);

        Assert.False(PostgisFieldName.TryValidate("a\"b", out var quote));
        Assert.Contains("double quote", quote);

        Assert.False(PostgisFieldName.TryValidate(new string('a', 64), out var tooLong));
        Assert.Contains("63-byte", tooLong);

        Assert.True(PostgisFieldName.TryValidate("fine", out var none));
        Assert.Equal(string.Empty, none);
    }
}
