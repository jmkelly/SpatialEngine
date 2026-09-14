using Spatial.Core.Features;
using Spatial.PluginSdk.Providers;

namespace Spatial.Adapter.GeoServices.Tests;

/// <summary>
/// T-038 item 3: the layer-level <c>validateSQL</c> (S4
/// validate-sql-feature-service-layer/). Red-first: server-side WHERE
/// validation does not exist today — the closed grammar only rejects at
/// query time — so every test here fails while the surface is unmounted.
/// </summary>
public sealed class FeatureValidateSqlTests
{
    private static readonly FeatureSchema Schema = new(
    [
        new FieldDefinition("name", AttributeKind.String),
        new FieldDefinition("population", AttributeKind.Int64),
        new FieldDefinition("geometry", AttributeKind.Geometry),
    ]);

    private static DatasetDescription Layer() => new(
        "demo.cities", "demo", "cities", "geometry", 4326, "Point", 8, [], Schema);

    [Fact]
    public void A_supported_where_clause_validates()
    {
        var response = FeatureValidateSql.Validate(Layer(), "population > 1000000 AND name <> 'x'", null);

        Assert.True(response.IsValidSQL);
        Assert.Null(response.ValidationErrors);
    }

    [Fact]
    public void The_synthetic_object_id_validates()
    {
        var response = FeatureValidateSql.Validate(Layer(), "OBJECTID < 100", "where");

        Assert.True(response.IsValidSQL);
    }

    [Fact]
    public void A_match_all_constant_validates()
    {
        var response = FeatureValidateSql.Validate(Layer(), "1=1", null);

        Assert.True(response.IsValidSQL);
    }

    [Fact]
    public void A_clause_outside_the_closed_grammar_is_a_syntax_error()
    {
        var response = FeatureValidateSql.Validate(Layer(), "population > FUNC(1)", null);

        Assert.False(response.IsValidSQL);
        var error = Assert.Single(response.ValidationErrors!);
        Assert.Equal(FeatureValidateSql.CodeSyntaxError, error.ErrorCode);
    }

    [Fact]
    public void An_unknown_field_is_an_invalid_field_error_naming_the_field()
    {
        var response = FeatureValidateSql.Validate(Layer(), "some_date < CURRENT_TIMESTAMP", "where");

        Assert.False(response.IsValidSQL);
        var error = Assert.Single(response.ValidationErrors!);
        Assert.Equal(FeatureValidateSql.CodeInvalidFieldName, error.ErrorCode);
        Assert.Contains("some_date", error.Description, StringComparison.Ordinal);
    }

    [Fact]
    public void Expression_and_statement_types_are_not_supported()
    {
        foreach (var sqlType in new[] { "expression", "statement", "EXPRESSION" })
        {
            var response = FeatureValidateSql.Validate(Layer(), "population + 1", sqlType);

            Assert.False(response.IsValidSQL);
            var error = Assert.Single(response.ValidationErrors!);
            Assert.Equal(FeatureValidateSql.CodeNotSupported, error.ErrorCode);
        }
    }

    [Fact]
    public void A_missing_sql_is_a_typed_error()
    {
        var exception = Assert.Throws<Spatial.Interop.Esri.EsriInteropException>(() =>
            FeatureValidateSql.Validate(Layer(), null, null));

        Assert.Equal(Spatial.Interop.Esri.EsriErrorCodes.InvalidParameters, exception.Code);
        Assert.Contains("sql", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void An_unknown_sql_type_is_a_typed_error()
    {
        var exception = Assert.Throws<Spatial.Interop.Esri.EsriInteropException>(() =>
            FeatureValidateSql.Validate(Layer(), "1=1", "select"));

        Assert.Equal(Spatial.Interop.Esri.EsriErrorCodes.InvalidParameters, exception.Code);
        Assert.Contains("sqlType", exception.Message, StringComparison.Ordinal);
    }
}
