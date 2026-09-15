using Spatial.Core.Features;
using Spatial.Stores.PostGIS.Core;

namespace Spatial.Stores.PostGIS.Tests;

/// <summary>
/// The Postgres → attribute-kind mapping and the SQL types of creating
/// batches.
/// </summary>
public sealed class PostgisTypeMappingTests
{
    [Theory]
    [InlineData("bool", AttributeKind.Boolean)]
    [InlineData("int2", AttributeKind.Int64)]
    [InlineData("int4", AttributeKind.Int64)]
    [InlineData("int8", AttributeKind.Int64)]
    [InlineData("float4", AttributeKind.Double)]
    [InlineData("float8", AttributeKind.Double)]
    [InlineData("numeric", AttributeKind.Double)]
    [InlineData("text", AttributeKind.String)]
    [InlineData("varchar", AttributeKind.String)]
    [InlineData("bpchar", AttributeKind.String)]
    [InlineData("geometry", AttributeKind.Geometry)]
    [InlineData("geography", AttributeKind.Geometry)]
    [InlineData("timestamptz", AttributeKind.DateTimeOffset)]
    [InlineData("timestamp", AttributeKind.DateTimeOffset)]
    [InlineData("date", AttributeKind.DateTimeOffset)]
    [InlineData("uuid", AttributeKind.Guid)]
    public void Supported_udt_names_map_to_kinds(string udtName, AttributeKind expected)
    {
        Assert.True(PostgisTypeMapping.TryMap(udtName, out var kind, out _));

        Assert.Equal(expected, kind);
    }

    [Theory]
    [InlineData("jsonb")]
    [InlineData("bytea")]
    [InlineData("hstore")]
    [InlineData("_int4")]
    [InlineData("point")]
    [InlineData("record")]
    public void Unsupported_udt_names_fail_with_an_actionable_reason(string udtName)
    {
        Assert.False(PostgisTypeMapping.TryMap(udtName, out _, out var reason));
        Assert.Contains(udtName, reason);
        Assert.Contains("supports", reason);
    }

    [Theory]
    [InlineData(AttributeKind.Boolean, "boolean")]
    [InlineData(AttributeKind.Int64, "bigint")]
    [InlineData(AttributeKind.Double, "double precision")]
    [InlineData(AttributeKind.String, "text")]
    [InlineData(AttributeKind.DateTimeOffset, "timestamptz")]
    [InlineData(AttributeKind.Guid, "uuid")]
    [InlineData(AttributeKind.Geometry, "geometry")]
    public void Sql_types_for_creating_batches(AttributeKind kind, string sqlType)
    {
        Assert.Equal(sqlType, PostgisTypeMapping.SqlType(kind));
    }
}
