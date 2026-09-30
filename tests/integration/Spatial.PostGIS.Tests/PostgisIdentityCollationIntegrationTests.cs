using Spatial.Core.Features;
using Spatial.Core.Geometry;

namespace Spatial.PostGIS.Tests;

/// <summary>
/// A text identity column under a case-folding collation, measured against a
/// real PostGIS container (ADR-0098 §3, ADR-0123, ADR-0126).
///
/// <para>
/// The stock database compares text by <c>en_US.utf8</c>, which orders the
/// contract's codes differently but never folds them, so a stock text key
/// cannot see this. A database that declares a <em>non-deterministic</em>
/// collation can: a <c>text</c> key column under one is a case-insensitive
/// identity, and the store's lookup for <c>delta</c> answers with the row whose
/// key is <c>Delta</c>. The collation is created here rather than assumed,
/// because that is the only way to make the two orders disagree.
/// </para>
/// </summary>
public sealed class PostgisIdentityCollationIntegrationTests : IClassFixture<PostgisContainerFixture>
{
    private const string Dataset = "public.identity_fold";

    private static readonly FeatureSchema Schema = new(
    [
        new FieldDefinition("code", AttributeKind.String, nullable: false),
        new FieldDefinition("population", AttributeKind.Int64, nullable: true),
        new FieldDefinition("geometry", AttributeKind.Geometry, nullable: true),
    ]);

    private readonly PostgisContainerFixture _fixture;

    public PostgisIdentityCollationIntegrationTests(PostgisContainerFixture fixture)
    {
        _fixture = fixture;
    }

    [SkippableFact]
    public async Task A_case_folding_text_identity_does_not_answer_for_another_case()
    {
        Skip.If(!_fixture.DockerAvailable, _fixture.SkipReason ?? "no reason");
        await using var context = PostgisTestContext.Create(_fixture.ConnectionString);
        await context.ExecuteAsync(
            $"DROP TABLE IF EXISTS {Dataset}; "
            + "DROP COLLATION IF EXISTS identity_fold; "
            + "CREATE COLLATION identity_fold (provider = icu, locale = 'und-u-ks-level2', deterministic = false); "
            + $"CREATE TABLE {Dataset} (\"code\" text COLLATE identity_fold PRIMARY KEY, "
            + "\"population\" bigint NULL, \"geometry\" geometry(Geometry, 4326))");
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
        // that matched `Delta` would rewrite a feature nobody asked about.
        Skip.If(!_fixture.DockerAvailable, _fixture.SkipReason ?? "no reason");
        await using var context = PostgisTestContext.Create(_fixture.ConnectionString);
        await context.ExecuteAsync(
            $"DROP TABLE IF EXISTS {Dataset}; "
            + "DROP COLLATION IF EXISTS identity_fold; "
            + "CREATE COLLATION identity_fold (provider = icu, locale = 'und-u-ks-level2', deterministic = false); "
            + $"CREATE TABLE {Dataset} (\"code\" text COLLATE identity_fold PRIMARY KEY, "
            + "\"population\" bigint NULL, \"geometry\" geometry(Geometry, 4326))");
        await context.Store.WriteAsync(Dataset, new FeatureBatch(Schema, [Feature("Delta", 10)]));

        var outcomes = await context.Editor.UpdateAsync(
            Dataset,
            new FeatureBatch(
                Schema,
                [
                    new Feature(
                        new FeatureId("delta"),
                        Schema,
                        [AttributeValue.FromString("delta"), AttributeValue.FromInt64(99), AttributeValue.Null]),
                ]));

        var outcome = Assert.Single(outcomes);
        Assert.False(outcome.Succeeded);
        Assert.Equal(
            10,
            await context.CountAsync($"SELECT population FROM {Dataset} WHERE \"code\" = 'Delta'"));
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
