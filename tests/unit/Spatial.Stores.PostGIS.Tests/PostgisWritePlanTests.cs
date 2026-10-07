using Spatial.Contracts;
using Spatial.Contracts.Providers;
using Spatial.Core.Features;
using Spatial.Stores.PostGIS.Core;

namespace Spatial.Stores.PostGIS.Tests;

/// <summary>
/// The write path's pure planning leaves (ADR-0028, ADR-0040, ADR-0043):
/// <see cref="PostgisWriteOperations.PlanFeature"/> chooses the statement and
/// the bound values for one edit, and <see cref="PostgisWriteOperations.CheckWritable"/>
/// rejects a batch whose schema does not describe the dataset's columns.
///
/// <para>
/// These are the write path's counterpart to <c>PostgisQueriesTests</c> and
/// <c>PostgisPredicateSqlTests</c> on the read side: the statement is built
/// from discovered identifiers and bound parameters only, the values line up
/// with the placeholders the statement writes, and an add without an identity
/// omits the identity columns so the database assigns them (ADR-0043). No
/// Npgsql connection and no container are involved, so a divergence here is
/// caught by the fast gate on a loaded box rather than by a skipped
/// container-backed suite (ADR-0187, ADR-0189).
/// </para>
/// </summary>
public sealed class PostgisWritePlanTests
{
    private static readonly FeatureSchema Schema = new(
    [
        new FieldDefinition("id", AttributeKind.Int64),
        new FieldDefinition("name", AttributeKind.String),
        new FieldDefinition("score", AttributeKind.Int64, nullable: true),
    ]);

    private static PostgisDatasetName Dataset()
    {
        Assert.True(PostgisDatasetName.TryParse("public.places", out var dataset, out _));
        return dataset;
    }

    private static DatasetDescription Description(params string[] idColumns) =>
        new("places", "public", "places", "geom", 4326, "Geometry", 0, idColumns, Schema);

    private static Feature Feature(string id, string name, long? score) =>
        new(
            new FeatureId(id),
            Schema,
            [
                AttributeValue.FromInt64(long.Parse(id, System.Globalization.CultureInfo.InvariantCulture)),
                AttributeValue.FromString(name),
                score is null ? AttributeValue.Null : AttributeValue.FromInt64(score.Value),
            ]);

    /// <summary>
    /// An update binds the full schema and appends the feature's pre-edit
    /// identity after it, so the row is matched by identity rather than by the
    /// values the statement is about to write (ADR-0037).
    /// </summary>
    [Fact]
    public void An_update_plans_the_set_list_and_appends_the_identity_predicate()
    {
        var (sql, values) = PostgisWriteOperations.PlanFeature(
            Dataset(), Description("id"), Feature("7", "Alpha", 20), update: true, PostgisTextOrder.Locale);

        Assert.Equal(
            "UPDATE \"public\".\"places\" SET \"id\" = @p0, \"name\" = @p1, \"score\" = @p2 WHERE \"id\" = @p3",
            sql);
        Assert.Equal([7L, "Alpha", 20L, 7L], values);
    }

    /// <summary>
    /// An add whose feature already carries an identity binds the full schema
    /// and returns the identity columns, which the edit outcome reads back.
    /// </summary>
    [Fact]
    public void An_add_with_an_identity_plans_the_insert_and_returns_the_row()
    {
        var (sql, values) = PostgisWriteOperations.PlanFeature(
            Dataset(), Description("id"), Feature("7", "Alpha", 20), update: false, PostgisTextOrder.Locale);

        Assert.Equal(
            "INSERT INTO \"public\".\"places\" (\"id\", \"name\", \"score\") VALUES (@p0, @p1, @p2) RETURNING \"id\"",
            sql);
        Assert.Equal([7L, "Alpha", 20L], values);
    }

    /// <summary>
    /// An add with no identity omits the identity column from both the column
    /// list and the bound values, so the database assigns it and the values
    /// stay aligned with the placeholders the statement wrote (ADR-0043). The
    /// feature's own (unassigned) id value is the one position dropped.
    /// </summary>
    [Fact]
    public void An_add_without_an_identity_omits_the_identity_column_and_its_value()
    {
        var feature = new Feature(
            FeatureId.Unassigned,
            Schema,
            [AttributeValue.FromInt64(0), AttributeValue.FromString("Alpha"), AttributeValue.FromInt64(20)]);

        var (sql, values) = PostgisWriteOperations.PlanFeature(
            Dataset(), Description("id"), feature, update: false, PostgisTextOrder.Locale);

        Assert.Equal(
            "INSERT INTO \"public\".\"places\" (\"name\", \"score\") VALUES (@p0, @p1) RETURNING \"id\"",
            sql);
        Assert.Equal(["Alpha", 20L], values);
    }

    /// <summary>
    /// A text identity is compared by bytes, so a case-folding collation
    /// cannot fold the update onto a row that differs only in case
    /// (ADR-0126). A non-text identity carries no <c>COLLATE</c>, because the
    /// term is a string operator.
    /// </summary>
    [Fact]
    public void An_update_of_a_text_identity_states_the_byte_order_and_a_numeric_one_does_not()
    {
        var text = new FeatureSchema(
        [
            new FieldDefinition("code", AttributeKind.String),
            new FieldDefinition("name", AttributeKind.String),
        ]);
        var description = new DatasetDescription("places", "public", "places", "geom", 4326, "Geometry", 0, ["code"], text);
        var feature = new Feature(
            new FeatureId("delta"),
            text,
            [AttributeValue.FromString("delta"), AttributeValue.FromString("Delta Cafe")]);

        var (localized, _) = PostgisWriteOperations.PlanFeature(
            Dataset(), description, feature, update: true, PostgisTextOrder.Locale);
        var (bytes, _) = PostgisWriteOperations.PlanFeature(
            Dataset(), description, feature, update: true, PostgisTextOrder.ByteOrder);

        Assert.Contains("WHERE \"code\" COLLATE \"C\" = @p2", localized, StringComparison.Ordinal);
        Assert.DoesNotContain("COLLATE", bytes, StringComparison.Ordinal);
        Assert.Contains("WHERE \"code\" = @p2", bytes, StringComparison.Ordinal);
    }

    /// <summary>
    /// A batch's identity parameters are flattened in identity-column order,
    /// one tuple per requested feature, which is the order a statement's
    /// placeholders expect (ADR-0037). A composite identity is split on
    /// <c>|</c> and each part typed by its column (ADR-0126).
    /// </summary>
    [Fact]
    public void Identity_parameters_are_one_flattened_tuple_per_feature_in_column_order()
    {
        var schema = new FeatureSchema(
        [
            new FieldDefinition("id", AttributeKind.Int64),
            new FieldDefinition("tag", AttributeKind.String),
        ]);
        var description = new DatasetDescription("places", "public", "places", "geom", 4326, "Geometry", 0, ["id", "tag"], schema);

        var parameters = PostgisIdentity.Parameters(
            description, [new FeatureId("7|alpha"), new FeatureId("8|bravo")]);

        Assert.Equal([7L, "alpha", 8L, "bravo"], parameters);
    }

    /// <summary>A batch whose field is not a column of the dataset is rejected by name.</summary>
    [Fact]
    public void A_batch_with_an_unknown_field_is_rejected_before_any_statement()
    {
        var batch = new FeatureBatch(
            new FeatureSchema([new FieldDefinition("population", AttributeKind.Int64)]),
            [new Feature(new FeatureId("1"), new FeatureSchema([new FieldDefinition("population", AttributeKind.Int64)]), [AttributeValue.FromInt64(1)])]);

        var failure = Assert.Throws<SpatialException>(() => PostgisWriteOperations.CheckWritable(Description("id"), batch));

        Assert.Equal(SpatialException.InvalidArguments, failure.Code);
        Assert.Contains("population", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>A batch field with the right name but the wrong kind is rejected, not coerced.</summary>
    [Fact]
    public void A_batch_with_a_mismatched_field_kind_is_rejected()
    {
        var mismatched = new FeatureSchema([new FieldDefinition("score", AttributeKind.String, nullable: true)]);
        var batch = new FeatureBatch(
            mismatched,
            [new Feature(new FeatureId("1"), mismatched, [AttributeValue.FromString("20")])]);

        var failure = Assert.Throws<SpatialException>(() => PostgisWriteOperations.CheckWritable(Description("id"), batch));

        Assert.Equal(SpatialException.InvalidArguments, failure.Code);
        Assert.Contains("score", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>A batch whose fields all describe the dataset columns passes writability.</summary>
    [Fact]
    public void A_batch_that_describes_the_dataset_columns_is_writable()
    {
        var batch = new FeatureBatch(Schema, [Feature("7", "Alpha", 20)]);

        PostgisWriteOperations.CheckWritable(Description("id"), batch);
    }

    /// <summary>
    /// The batch's schema shapes the statement and its values, not the
    /// dataset's (ADR-0126): a batch that omits the identity column still
    /// inserts, the identity is left to the database, and the values are read
    /// in the batch's own order. Mapping by the dataset's schema instead reads
    /// a field the feature does not have (the out-of-bounds the write
    /// conformance suite reported) and, where the orders differ, writes the
    /// wrong value into the wrong column.
    /// </summary>
    [Fact]
    public void An_add_without_an_identity_maps_the_batch_schema_not_the_dataset_schema()
    {
        var stored = new FeatureSchema(
        [
            new FieldDefinition("id", AttributeKind.Int64),
            new FieldDefinition("name", AttributeKind.String),
            new FieldDefinition("score", AttributeKind.Int64, nullable: true),
        ]);
        var description = new DatasetDescription("places", "public", "places", "geom", 4326, "Geometry", 0, ["id"], stored);
        var batch = new FeatureSchema(
        [
            new FieldDefinition("name", AttributeKind.String),
            new FieldDefinition("score", AttributeKind.Int64, nullable: true),
        ]);
        var feature = new Feature(
            FeatureId.Unassigned, batch, [AttributeValue.FromString("Alpha"), AttributeValue.FromInt64(20)]);

        var (sql, values) = PostgisWriteOperations.PlanFeature(
            Dataset(), description, feature, update: false, PostgisTextOrder.Locale);

        Assert.Equal(
            "INSERT INTO \"public\".\"places\" (\"name\", \"score\") VALUES (@p0, @p1) RETURNING \"id\"",
            sql);
        Assert.Equal(["Alpha", 20L], values);
    }

    /// <summary>
    /// An update's <c>SET</c> values follow the batch's schema too, with the
    /// identity predicate appended after them (ADR-0126). Reading the values
    /// from the dataset's schema would bind the identity twice and the
    /// attributes in the wrong order.
    /// </summary>
    [Fact]
    public void An_update_maps_the_batch_schema_for_its_set_values()
    {
        var stored = new FeatureSchema(
        [
            new FieldDefinition("id", AttributeKind.Int64),
            new FieldDefinition("name", AttributeKind.String),
            new FieldDefinition("score", AttributeKind.Int64, nullable: true),
        ]);
        var description = new DatasetDescription("places", "public", "places", "geom", 4326, "Geometry", 0, ["id"], stored);
        var batch = new FeatureSchema(
        [
            new FieldDefinition("name", AttributeKind.String),
            new FieldDefinition("score", AttributeKind.Int64, nullable: true),
        ]);
        var feature = new Feature(new FeatureId("7"), batch, [AttributeValue.FromString("Alpha"), AttributeValue.FromInt64(20)]);

        var (sql, values) = PostgisWriteOperations.PlanFeature(
            Dataset(), description, feature, update: true, PostgisTextOrder.Locale);

        Assert.Equal(
            "UPDATE \"public\".\"places\" SET \"name\" = @p0, \"score\" = @p1 WHERE \"id\" = @p2",
            sql);
        Assert.Equal(["Alpha", 20L, 7L], values);
    }
}
