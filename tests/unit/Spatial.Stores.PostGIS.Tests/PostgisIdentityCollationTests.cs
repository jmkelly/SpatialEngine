using Spatial.Contracts;
using Spatial.Contracts.Providers;
using Spatial.Core.Features;
using Spatial.Stores.PostGIS.Core;
using Spatial.Stores.PostGIS.Data;

namespace Spatial.Stores.PostGIS.Tests;

/// <summary>
/// The comparison a statement makes against a dataset's <em>identity</em>
/// columns, measured as SQL (ADR-0098 §3, ADR-0121, ADR-0123, ADR-0126).
///
/// <para>
/// A text identity column under a collation that folds case is a
/// case-insensitive identity: the store asks for <c>delta</c> and the
/// database answers with the row whose key is <c>Delta</c>, which is a feature
/// the contract distinguishes from the one that was asked for. Every statement
/// that selects a row by identity — the pushed restriction, the lookup, the
/// update's target, the delete's target and the attachment probe's — therefore
/// states the byte order the contract compares strings in, on the same terms
/// and under the same condition as a pushed attribute comparison
/// (ADR-0123).
/// </para>
///
/// <para>
/// Every case here is free of Npgsql and of a container: the shape is decided
/// by the database's collation and the identity column's kind, and those are
/// the two inputs this file varies. That a case-folding identity really does
/// return the wrong row is proved by
/// <c>PostgisIdentityCollationIntegrationTests</c>, over a table whose key
/// column declares a non-deterministic collation.
/// </para>
/// </summary>
public sealed class PostgisIdentityCollationTests
{
    private static readonly FeatureSchema Schema = FeatureTests.Schema(
        ("id", AttributeKind.Int64, false),
        ("code", AttributeKind.String, false),
        ("geom", AttributeKind.Geometry, false));

    private static readonly string[] Projection = ["\"id\"", "\"code\"", "ST_AsEWKB(\"geom\")"];

    private static PostgisDatasetName Dataset()
    {
        Assert.True(PostgisDatasetName.TryParse("public.places", out var dataset, out _));
        return dataset;
    }

    private static DatasetDescription Description(params string[] idColumns) =>
        new("places", "public", "places", "geom", 4326, "Geometry", 0, idColumns, Schema);

    [Fact]
    public void A_text_identity_states_the_byte_order_of_a_lookup()
    {
        // `code = 'delta'` under a case-folding collation is `code = 'Delta'`,
        // so a lookup returns a feature nobody asked for — and a single
        // identity is limited to one row, so it is the wrong one.
        Assert.Equal(
            $"SELECT {string.Join(", ", Projection)} FROM \"public\".\"places\" WHERE (\"code\" COLLATE \"C\" = @p0) LIMIT 1",
            PostgisQueries.SelectByIdentity(Dataset(), Schema, ["code"], 1, byteOrderText: false));
    }

    [Fact]
    public void A_text_identity_states_the_byte_order_of_every_row_of_a_batch()
    {
        var sql = PostgisQueries.SelectByIdentity(Dataset(), Schema, ["code"], 2, byteOrderText: false);

        Assert.Equal(
            $"SELECT {string.Join(", ", Projection)} FROM \"public\".\"places\" "
            + "WHERE (\"code\" COLLATE \"C\" = @p0) OR (\"code\" COLLATE \"C\" = @p1)",
            sql);
    }

    [Fact]
    public void A_text_identity_needs_no_term_in_a_database_that_compares_by_bytes()
    {
        // The database already answers this comparison the way the reference
        // does, so `COLLATE "C"` would only stop the planner using the key's
        // own index (ADR-0121) — and the key index is the one lookup there is.
        Assert.Equal(
            $"SELECT {string.Join(", ", Projection)} FROM \"public\".\"places\" WHERE (\"code\" = @p0) LIMIT 1",
            PostgisQueries.SelectByIdentity(Dataset(), Schema, ["code"], 1, byteOrderText: true));
    }

    [Fact]
    public void An_identity_of_anything_but_text_never_carries_a_collation()
    {
        // `COLLATE` is a string operator, and a number's identity is compared
        // as a number whatever the database's collation is.
        Assert.Equal(
            $"SELECT {string.Join(", ", Projection)} FROM \"public\".\"places\" WHERE (\"id\" = @p0) LIMIT 1",
            PostgisQueries.SelectByIdentity(Dataset(), Schema, ["id"], 1, byteOrderText: false));
    }

    [Fact]
    public void An_update_targets_a_text_identity_by_bytes()
    {
        // The row an update writes to is the row the identity named before the
        // edit, resolved against the *dataset's* schema rather than the
        // batch's: a batch need not carry the identity column at all.
        Assert.Equal(
            "UPDATE \"public\".\"places\" SET \"code\" = @p0, \"geom\" = ST_SetSRID(ST_GeomFromEWKB(@p1), 4326) "
            + "WHERE \"code\" COLLATE \"C\" = @p2",
            PostgisQueries.Update(
                Dataset(),
                FeatureTests.Schema(("code", AttributeKind.String, false), ("geom", AttributeKind.Geometry, false)),
                4326,
                ["code"],
                Schema,
                byteOrderText: false));
    }

    [Fact]
    public void A_delete_targets_a_text_identity_by_bytes() =>
        Assert.Equal(
            "DELETE FROM \"public\".\"places\" WHERE \"code\" COLLATE \"C\" = @p0",
            PostgisQueries.Delete(Dataset(), Schema, ["code"], byteOrderText: false));

    [Fact]
    public void The_attachment_probe_matches_a_text_identity_by_bytes()
    {
        // The probe decides whether a feature exists, so a folding match is an
        // attachment stored against a feature nobody asked for.
        Assert.Equal(
            "SELECT 1 FROM \"public\".\"places\" WHERE \"code\" COLLATE \"C\" = @p0 LIMIT 1",
            PostgisQueries.FeatureExists(Dataset(), Schema, ["code"], byteOrderText: false));
    }

    [Fact]
    public void A_pushed_identity_restriction_states_the_byte_order()
    {
        // The id restriction and the attribute clause are one `WHERE`, and
        // both halves of it compare strings by the same rule (ADR-0123).
        // The restriction binds the identity values itself, so the clause is
        // numbered from the first parameter of the statement it belongs to.
        var parameters = new List<object?>();
        var where = PostgisPlanQueries.Predicate(
            Dataset(),
            Description("code"),
            new FeatureQuery(Ids: [new FeatureId("delta")]),
            byteOrderText: false,
            parameters);

        Assert.Equal("\"code\" COLLATE \"C\" = @p0", where);
        Assert.Equal(["delta"], parameters);
    }

    [Fact]
    public void A_store_asks_whether_the_identity_compares_text_at_all()
    {
        // The catalog read that decides the term is a round trip, and a
        // dataset keyed on a number cannot use its answer — so the store asks
        // this question first, and only then reads the collation.
        Assert.True(PostgisIdentity.ComparesText(Description("code")));
        Assert.True(PostgisIdentity.ComparesText(Description("id", "code")));
        Assert.False(PostgisIdentity.ComparesText(Description("id")));
        Assert.False(PostgisIdentity.ComparesText(Description()));
    }
}
