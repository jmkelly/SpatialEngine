using Spatial.Contracts;
using Spatial.Core.Features;
using Spatial.Core.Geometry;
using Spatial.Stores.SqlServer.Core;

namespace Spatial.SqlServer.Tests;

/// <summary>
/// A text identity column under a case-folding collation, measured against a
/// real SQL Server container (ADR-0098 §3, ADR-0123, ADR-0126).
///
/// <para>
/// The database this provider is pointed at carries the collation SQL Server
/// ships by default, which is <em>case-insensitive</em>, so a key column that
/// declares none folds <c>Delta</c> onto <c>delta</c>: the store's lookup for
/// one feature answers with another, and the two are the same row. These are
/// the cases where that is the difference between an answer and the wrong one,
/// and they cannot be measured by a statement test alone.
/// </para>
/// </summary>
[Collection(SqlServerContainerDefinition.Name)]
public sealed class SqlServerIdentityCollationIntegrationTests : IClassFixture<SqlServerDatabaseFixture>
{
    private const string Dataset = "dbo.identity_ci";
    private const string BinaryKeyDataset = "dbo.identity_bin";

    private static readonly FeatureSchema Schema = new(
    [
        new FieldDefinition("code", AttributeKind.String, nullable: false),
        new FieldDefinition("population", AttributeKind.Int64, nullable: true),
        new FieldDefinition("geometry", AttributeKind.Geometry, nullable: true),
    ]);

    private readonly SqlServerDatabaseFixture _fixture;

    public SqlServerIdentityCollationIntegrationTests(SqlServerDatabaseFixture fixture)
    {
        _fixture = fixture;
    }

    [SkippableFact]
    public async Task A_case_folding_text_identity_does_not_answer_for_another_case()
    {
        // The key column declares no collation of its own, so it carries the
        // database's — the case-insensitive one SQL Server ships. The table
        // holds `Delta` and nothing else, and the contract's `delta` is a
        // different feature, so the lookup for it finds nothing: the fold must
        // not make the store's identity a case-insensitive one.
        Skip.If(!_fixture.DockerAvailable, _fixture.SkipReason ?? "no reason");
        await using var context = SqlServerTestContext.Create(_fixture.ConnectionString);
        await context.ExecuteAsync(
            $"IF OBJECT_ID(N'{Dataset}', N'U') IS NOT NULL DROP TABLE {Dataset}; "
            + $"CREATE TABLE {Dataset} ([code] nvarchar(64) NOT NULL PRIMARY KEY, "
            + "[population] bigint NULL, [geometry] geometry NULL)");
        await context.Store.WriteAsync(Dataset, new FeatureBatch(Schema, [Feature("Delta", 10)]));

        var folded = await context.Store.GetAsync(Dataset, [new FeatureId("delta")]);

        Assert.Empty(folded);
        var exact = await context.Store.GetAsync(Dataset, [new FeatureId("Delta")]);
        Assert.Equal("Delta", Assert.Single(exact)["code"].StringValue);
    }

    [SkippableFact]
    public async Task A_case_folding_text_identity_does_not_write_to_another_case()
    {
        // The same fold in the statement an *edit* makes: an update of `delta`
        // that matched `Delta` would rewrite a feature nobody asked about, and
        // a delete would take it away.
        Skip.If(!_fixture.DockerAvailable, _fixture.SkipReason ?? "no reason");
        await using var context = SqlServerTestContext.Create(_fixture.ConnectionString);
        await context.ExecuteAsync(
            $"IF OBJECT_ID(N'{Dataset}', N'U') IS NOT NULL DROP TABLE {Dataset}; "
            + $"CREATE TABLE {Dataset} ([code] nvarchar(64) NOT NULL PRIMARY KEY, "
            + "[population] bigint NULL, [geometry] geometry NULL)");
        await context.Store.WriteAsync(Dataset, new FeatureBatch(Schema, [Feature("Delta", 10)]));

        var outcomes = await context.Editor.UpdateAsync(
            Dataset,
            new FeatureBatch(
                Schema,
                [
                    new Feature(
                        new FeatureId("delta"),
                        Schema,
                        [
                            AttributeValue.FromString("delta"),
                            AttributeValue.FromInt64(99),
                            AttributeValue.FromGeometry(
                                GeometryFactory.CreatePoint(1, 2, CoordinateReference.Epsg(4326))),
                        ]),
                ]));

        // `delta` is not a feature of this table, so the edit is a typed
        // not-found rather than a rewrite of `Delta`.
        var outcome = Assert.Single(outcomes);
        Assert.False(outcome.Succeeded);
        Assert.Equal(SpatialException.NotFound, outcome.ErrorCode);
        Assert.Equal(10, await context.CountAsync($"SELECT population FROM {Dataset} WHERE [code] = N'Delta'"));
    }

    [SkippableFact]
    public async Task A_text_identity_declared_under_the_binary_collation_holds_both_cases()
    {
        // The decision ADR-0126 records, on the table the conformance fixture
        // is keyed on: a text identity is a byte identity, so the column that
        // carries it declares a binary collation. The two codes the contract
        // distinguishes are then two rows, and each is found by its own case.
        Skip.If(!_fixture.DockerAvailable, _fixture.SkipReason ?? "no reason");
        await using var context = SqlServerTestContext.Create(_fixture.ConnectionString);
        await context.ExecuteAsync(
            $"IF OBJECT_ID(N'{BinaryKeyDataset}', N'U') IS NOT NULL DROP TABLE {BinaryKeyDataset}; "
            + $"CREATE TABLE {BinaryKeyDataset} ([code] nvarchar(64) COLLATE {SqlServerPredicateSql.ByteOrderCollation} NOT NULL PRIMARY KEY, "
            + "[population] bigint NULL, [geometry] geometry NULL)");
        await context.Store.WriteAsync(
            BinaryKeyDataset,
            new FeatureBatch(Schema, [Feature("Delta", 10), Feature("delta", 20)]));

        var found = await context.Store.GetAsync(BinaryKeyDataset, [new FeatureId("delta"), new FeatureId("Delta")]);

        Assert.Equal(
            [("delta", 20L), ("Delta", 10L)],
            found
                .OrderByDescending(feature => feature["code"].StringValue, StringComparer.Ordinal)
                .Select(feature => (feature["code"].StringValue, feature["population"].Int64Value))
                .ToArray());
    }

    private static Feature Feature(string code, long population) =>
        new(
            new FeatureId(code),
            Schema,
            [
                AttributeValue.FromString(code),
                AttributeValue.FromInt64(population),
                AttributeValue.FromGeometry(GeometryFactory.CreatePoint(1, 2, CoordinateReference.Epsg(4326))),
            ]);
}
