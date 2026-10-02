using Npgsql;
using Spatial.Contracts;
using Spatial.Core.Features;
using Spatial.Core.Features.Query;
using Spatial.Core.Geometry;
using Spatial.Stores.PostGIS;

namespace Spatial.PostGIS.Tests;

/// <summary>
/// A sort key over a text column that declares a collation of its own, measured
/// against a real PostGIS container (ADR-0098 §3, ADR-0121 §2, ADR-0136).
///
/// <para>
/// The store decides whether a pushed-down order term needs <c>COLLATE "C"</c>
/// from the <em>database's</em> collation, which is right for every column that
/// declares none — the case for every table the store creates. It is not right
/// for a hand-authored table: <c>"label" text COLLATE "de-x-icu"</c> sorts by
/// the <em>column's</em> collation whatever the database's, so a database that
/// compares by bytes does not by itself make the term unnecessary.
/// </para>
///
/// <para>
/// That needs a database whose collation is <c>C</c>, which the stock fixture
/// is not — <c>en_US.utf8</c> is a locale and every column declares nothing, so
/// the term is written and the two collations never disagree. The database is
/// therefore created here from <c>template0</c> with <c>LOCALE 'C'</c>, which is
/// the only shape in which the term is skipped and the column still needs it.
/// </para>
/// </summary>
[Collection(PostgisContainerDefinition.Name)]
public sealed class PostgisColumnCollationOrderTests : IClassFixture<PostgisDatabaseFixture>
{
    private const string ByteOrderDatabase = "byte_order_collation";

    private readonly PostgisDatabaseFixture _fixture;

    public PostgisColumnCollationOrderTests(PostgisDatabaseFixture fixture)
    {
        _fixture = fixture;
    }

    private static readonly FeatureSchema Schema = new(
    [
        new FieldDefinition("id", AttributeKind.Int64),
        new FieldDefinition("label", AttributeKind.String, nullable: true),
        new FieldDefinition("plain", AttributeKind.String, nullable: true),
        new FieldDefinition("geometry", AttributeKind.Geometry, nullable: true),
    ]);

    [SkippableFact]
    public async Task An_order_term_follows_the_collation_the_column_actually_carries()
    {
        Skip.If(!_fixture.DockerAvailable, _fixture.SkipReason ?? "no reason");
        var connectionString = await ByteOrderDatabaseAsync();
        await using var context = PostgisTestContext.Create(connectionString);
        const string dataset = "public.collated_labels";
        await context.ExecuteAsync(
            $"DROP TABLE IF EXISTS {dataset}; "
            + "DROP COLLATION IF EXISTS de_x_icu; "
            + "CREATE COLLATION de_x_icu (provider = icu, locale = 'de-x-icu'); "
            + $"CREATE TABLE {dataset} (\"id\" bigint PRIMARY KEY, "
            + "\"label\" text COLLATE de_x_icu NULL, "
            + "\"plain\" text NULL, "
            + "\"geometry\" geometry(Point, 4326) NULL)");
        // The reference compares strings by bytes: "A" (65) before "_c" (95)
        // before "a" (97). `de-x-icu` orders the same three values `_c, a, A`,
        // so the two answers cannot agree by accident.
        for (var id = 0; id < Labels.Length; id++)
        {
            await context.Store.WriteAsync(
                dataset,
                new FeatureBatch(
                    Schema,
                    [
                        new Feature(
                            new FeatureId(id.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                            Schema,
                            [
                                AttributeValue.FromInt64(id),
                                AttributeValue.FromString(Labels[id]),
                                AttributeValue.FromString(Labels[id]),
                                AttributeValue.FromGeometry(GeometryFactory.CreatePoint(id, id)),
                            ]),
                    ]));
        }

        var byLabel = (await context.Store.QueryAsync(dataset, new FeatureQuery(Order: [new OrderTerm("label")]))).Batches;
        Assert.Equal(Labels, byLabel.Select(batch => batch.Features).SelectMany(features => features).Select(feature => feature["label"].StringValue).ToArray());

        // The column that declares no collation inherits the database's, which
        // here is `C`: the reference's order, with nothing to state. The same
        // three values, through the same plan, are the store's own two cases of
        // one rule.
        var byPlain = (await context.Store.QueryAsync(dataset, new FeatureQuery(Order: [new OrderTerm("plain")]))).Batches;
        Assert.Equal(Labels, byPlain.Select(batch => batch.Features).SelectMany(features => features).Select(feature => feature["plain"].StringValue).ToArray());
    }

    /// <summary>The three values the two collations order differently, in the reference's order.</summary>
    private static readonly string[] Labels = ["A", "_c", "a"];

    /// <summary>
    /// A connection string into a database created with <c>LOCALE 'C'</c>, so
    /// the store's catalog probe reads a byte-order collation and skips the
    /// term for a column that inherits it.
    /// </summary>
    private async Task<string> ByteOrderDatabaseAsync()
    {
        var builder = new NpgsqlConnectionStringBuilder(_fixture.ConnectionString) { Database = ByteOrderDatabase };
        await using (var dataSource = NpgsqlDataSource.Create(_fixture.ConnectionString))
        await using (var connection = await dataSource.OpenConnectionAsync())
        await using (var command = connection.CreateCommand())
        {
            command.CommandText =
                "SELECT 1 FROM pg_database WHERE datname = @p0";
            command.Parameters.AddWithValue("p0", ByteOrderDatabase);
            if (await command.ExecuteScalarAsync() is null)
            {
                command.CommandText =
                    $"CREATE DATABASE {ByteOrderDatabase} TEMPLATE template0 ENCODING 'UTF8' LOCALE 'C'";
                await command.ExecuteNonQueryAsync();
            }
        }

        await using (var dataSource = NpgsqlDataSource.Create(builder.ConnectionString))
        await using (var connection = await dataSource.OpenConnectionAsync())
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "CREATE EXTENSION IF NOT EXISTS postgis";
            await command.ExecuteNonQueryAsync();
        }

        return builder.ConnectionString;
    }
}
