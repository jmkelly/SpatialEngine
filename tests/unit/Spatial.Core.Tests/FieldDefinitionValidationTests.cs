using Spatial.Core.Features;

namespace Spatial.Core.Tests;

/// <summary>
/// The <see cref="FieldDefinition"/> constructor guards: the reserved kinds
/// are refused, unknown kinds are refused, and a description is null or
/// non-whitespace.
/// </summary>
public sealed class FieldDefinitionValidationTests
{
    [Fact]
    public void A_named_field_of_every_column_kind_is_valid()
    {
        foreach (var kind in new[]
                 {
                     AttributeKind.Boolean, AttributeKind.Int64, AttributeKind.Double,
                     AttributeKind.String, AttributeKind.Geometry, AttributeKind.DateTimeOffset,
                     AttributeKind.Guid,
                 })
        {
            var field = new FieldDefinition("column", kind, nullable: true, description: "a column");
            Assert.Equal("column", field.Name);
            Assert.Equal(kind, field.Kind);
        }
    }

    [Fact]
    public void The_null_kind_is_refused() =>
        Assert.Throws<ArgumentException>(() => new FieldDefinition("column", AttributeKind.Null));

    [Fact]
    public void The_envelope_kind_is_refused() =>
        Assert.Throws<ArgumentException>(() => new FieldDefinition("column", AttributeKind.Envelope));

    [Fact]
    public void An_unknown_kind_is_refused() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new FieldDefinition("column", (AttributeKind)200));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void A_blank_description_is_refused(string description) =>
        Assert.Throws<ArgumentException>(() => new FieldDefinition("column", AttributeKind.String, description: description));

    [Fact]
    public void A_missing_name_is_refused() =>
        Assert.Throws<ArgumentException>(() => new FieldDefinition("  ", AttributeKind.String));
}
