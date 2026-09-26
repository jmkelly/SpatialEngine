using Spatial.Contracts;
using Spatial.Core.Features;
using Spatial.Stores.SqlServer.Core;

namespace Spatial.Stores.SqlServer.Tests;

/// <summary>
/// The provider's pure argument surface (ADR-0072): the dataset-identifier
/// grammar that is the injection barrier, the field-name rules for discovered
/// column names, and the SQL Server type mapping. Every rejection names the
/// exact problem, because these messages reach a client.
/// </summary>
public sealed class SqlServerNameAndTypeTests
{
    [Theory]
    [InlineData("places", "dbo", "places")]
    [InlineData("dbo.places", "dbo", "places")]
    [InlineData("my_schema.my_table_2", "my_schema", "my_table_2")]
    public void A_strict_lowercase_identifier_parses(string text, string schema, string table)
    {
        Assert.True(SqlServerDatasetName.TryParse(text, out var name, out var reason), reason);

        Assert.Equal(schema, name.Schema);
        Assert.Equal(table, name.Table);
        Assert.Equal($"{schema}.{table}", name.Qualified);
        Assert.Equal($"[{schema}].[{table}]", name.QuoteQualified());
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Places")]
    [InlineData("dbo.Places")]
    [InlineData("dbo.places.extra")]
    [InlineData("dbo.places; DROP TABLE places")]
    [InlineData("dbo.[places]")]
    [InlineData("dbo.pl ace")]
    [InlineData("1places")]
    public void Anything_else_is_refused_with_a_reason(string text)
    {
        Assert.False(SqlServerDatasetName.TryParse(text, out _, out var reason));

        Assert.NotEmpty(reason);
    }

    [Fact]
    public void A_quoted_identifier_never_carries_a_bracket()
    {
        Assert.Equal("[weird]]name]", SqlServerIdentifier.Quote("weird]name"));
    }

    [Theory]
    [InlineData("LABELRANK", true)]
    [InlineData("magType", true)]
    [InlineData("with space", true)]
    [InlineData("", false)]
    [InlineData("]", false)]
    [InlineData("a]b", false)]
    [InlineData("with\0nul", false)]
    public void A_field_name_is_kept_verbatim_unless_sql_server_cannot_carry_it(string name, bool valid)
    {
        Assert.Equal(valid, SqlServerFieldName.IsValid(name));
    }

    [Fact]
    public void A_field_name_past_the_identifier_limit_is_refused()
    {
        Assert.False(SqlServerFieldName.TryValidate(new string('a', SqlServerFieldName.MaxBytes + 1), out var reason));
        Assert.Contains("128-byte", reason);
        Assert.True(SqlServerFieldName.IsValid(new string('a', SqlServerFieldName.MaxBytes)));
    }

    [Fact]
    public void RequireValid_throws_invalid_arguments_naming_the_field()
    {
        var schema = new FeatureSchema(
        [
            new FieldDefinition("ok", AttributeKind.String, true),
            new FieldDefinition("bad]", AttributeKind.String, true),
        ]);
        Assert.True(SqlServerDatasetName.TryParse("dbo.places", out var dataset, out _));

        var failure = Assert.Throws<SpatialException>(() => SqlServerFieldName.RequireValid(dataset, schema));

        Assert.Equal(SpatialException.InvalidArguments, failure.Code);
        Assert.Contains("bad]", failure.Message);
    }

    [Theory]
    [InlineData("bit", AttributeKind.Boolean)]
    [InlineData("tinyint", AttributeKind.Int64)]
    [InlineData("smallint", AttributeKind.Int64)]
    [InlineData("int", AttributeKind.Int64)]
    [InlineData("bigint", AttributeKind.Int64)]
    [InlineData("real", AttributeKind.Double)]
    [InlineData("float", AttributeKind.Double)]
    [InlineData("decimal", AttributeKind.Double)]
    [InlineData("money", AttributeKind.Double)]
    [InlineData("varchar", AttributeKind.String)]
    [InlineData("nvarchar", AttributeKind.String)]
    [InlineData("text", AttributeKind.String)]
    [InlineData("date", AttributeKind.DateTimeOffset)]
    [InlineData("datetime2", AttributeKind.DateTimeOffset)]
    [InlineData("datetimeoffset", AttributeKind.DateTimeOffset)]
    [InlineData("uniqueidentifier", AttributeKind.Guid)]
    [InlineData("geometry", AttributeKind.Geometry)]
    [InlineData("geography", AttributeKind.Geometry)]
    public void A_supported_column_type_maps_to_its_attribute_kind(string typeName, AttributeKind expected)
    {
        Assert.True(SqlServerTypeMapping.TryMap(typeName, out var kind, out _));

        Assert.Equal(expected, kind);
    }

    [Fact]
    public void An_unsupported_column_type_is_named_in_the_diagnostic()
    {
        Assert.False(SqlServerTypeMapping.TryMap("xml", out _, out var reason));

        Assert.Contains("'xml'", reason);
        Assert.Contains("uniqueidentifier", reason);
    }

    [Theory]
    [InlineData(AttributeKind.Boolean, "bit")]
    [InlineData(AttributeKind.Int64, "bigint")]
    [InlineData(AttributeKind.Double, "float")]
    [InlineData(AttributeKind.String, "nvarchar(max)")]
    [InlineData(AttributeKind.DateTimeOffset, "datetimeoffset")]
    [InlineData(AttributeKind.Guid, "uniqueidentifier")]
    [InlineData(AttributeKind.Geometry, "geometry")]
    public void A_creating_batch_field_has_a_column_type(AttributeKind kind, string expected)
    {
        Assert.Equal(expected, SqlServerTypeMapping.SqlType(kind));
    }

    [Fact]
    public void Spatial_types_are_recognised_for_discovery()
    {
        Assert.True(SqlServerTypeMapping.IsSpatialType("geometry"));
        Assert.True(SqlServerTypeMapping.IsSpatialType("GEOGRAPHY"));
        Assert.False(SqlServerTypeMapping.IsSpatialType("nvarchar"));
    }
}
