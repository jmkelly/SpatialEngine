using System.Text.Json;
using Spatial.Contracts;
using Spatial.Core.Features;

namespace Spatial.Host.Tests;

/// <summary>
/// The field-definition JSON converter (the schema wire shape): names, kinds
/// and nullability round trip, including the optional description in both
/// its present and absent forms.
/// </summary>
public sealed class FieldDefinitionConverterTests
{
    private static readonly JsonSerializerOptions Options = new()
    {
        Converters = { new FieldDefinitionConverter() },
    };

    [Fact]
    public void Field_definitions_with_descriptions_round_trip()
    {
        var field = new FieldDefinition("name", AttributeKind.String, description: "display name");

        var json = JsonSerializer.Serialize(field, Options);

        Assert.Contains("display name", json, StringComparison.Ordinal);
        Assert.Equal(field, JsonSerializer.Deserialize<FieldDefinition>(json, Options));
    }

    [Fact]
    public void Field_definitions_without_descriptions_write_null()
    {
        var field = new FieldDefinition("count", AttributeKind.Int64);

        var json = JsonSerializer.Serialize(field, Options);

        Assert.Contains("null", json, StringComparison.Ordinal);
        Assert.Equal(field, JsonSerializer.Deserialize<FieldDefinition>(json, Options));
    }
}
