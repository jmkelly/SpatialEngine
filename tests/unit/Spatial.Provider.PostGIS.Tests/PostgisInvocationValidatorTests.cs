using Spatial.Core.Features;
using Spatial.PluginSdk.Capabilities;
using Spatial.PluginSdk.Providers;
using Spatial.PluginSdk.Resources;
using Spatial.Provider.PostGIS.Configuration;
using Spatial.Provider.PostGIS.Core;
using Spatial.Provider.PostGIS.Data;
using Spatial.Provider.PostGIS.Geometry;

namespace Spatial.Provider.PostGIS.Tests;

/// <summary>
/// Unit tests for the store-free validation surface of postgis@1
/// (ADR-0028, plan §18): the guard chaining (<see cref="PostgisInvocationValidator.FirstError"/>),
/// cancellation and facilities checks, argument reading (dataset, batch,
/// bbox, srid, filter, pattern, transaction) and the pure predicate builder.
/// The argument-validation matrix at the capability level lives in the
/// conformance suite; these tests pin the validator's own branches directly.
/// </summary>
public sealed class PostgisInvocationValidatorTests
{
    private static readonly CapabilityInvocation Invocation =
        CapabilityInvocation.Create(FeatureQueryContract.Id, new Dictionary<string, object?>());

    // ---- FirstError ----

    [Fact]
    public void FirstError_returns_the_first_non_null_error_in_order()
    {
        var first = CapabilityError.Cancelled(FeatureQueryContract.Id);
        var second = CapabilityError.ContractViolation("second");

        Assert.Same(first, PostgisInvocationValidator.FirstError(null, first, second));
        Assert.Same(second, PostgisInvocationValidator.FirstError(null, null, second));
    }

    [Fact]
    public void FirstError_returns_null_when_every_guard_passed()
    {
        Assert.Null(PostgisInvocationValidator.FirstError());
        Assert.Null(PostgisInvocationValidator.FirstError(null, null, null));
    }

    // ---- Cancellation + facilities ----

    [Fact]
    public void CheckCancelled_reports_a_pre_cancelled_invocation()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var error = PostgisInvocationValidator.CheckCancelled(Invocation with { CancellationToken = cancellation.Token });

        Assert.NotNull(error);
        Assert.Equal(CapabilityErrorKind.Cancelled, error!.Kind);
        Assert.Null(PostgisInvocationValidator.CheckCancelled(Invocation));
    }

    [Fact]
    public void RequireFacilities_requires_runtime_facilities()
    {
        var error = PostgisInvocationValidator.RequireFacilities(Invocation, out var facilities);

        Assert.NotNull(error);
        Assert.Equal(CapabilityErrorKind.InvalidArguments, error!.Kind);
        Assert.Contains("no runtime facilities", error.Message);
    }

    // ---- Dataset ----

    [Fact]
    public void ReadDataset_requires_a_dataset_identifier()
    {
        var error = PostgisInvocationValidator.ReadDataset(Invocation, out _);

        Assert.NotNull(error);
        Assert.Contains("'dataset'", error!.Message);
    }

    [Fact]
    public void ReadDataset_rejects_a_malformed_identifier()
    {
        var error = PostgisInvocationValidator.ReadDataset(
            Invocation with { Arguments = Args(ProviderArguments.Dataset, "places; drop table x") }, out _);

        Assert.NotNull(error);
        Assert.Contains("places; drop table x", error!.Message);
    }

    [Fact]
    public void ReadDataset_parses_schema_and_table()
    {
        var error = PostgisInvocationValidator.ReadDataset(
            Invocation with { Arguments = Args(ProviderArguments.Dataset, "public.places") }, out var dataset);

        Assert.Null(error);
        Assert.Equal("public", dataset.Schema);
        Assert.Equal("places", dataset.Table);
    }

    // ---- Batch + srid ----

    [Fact]
    public void ReadBatch_requires_canonical_batch_bytes()
    {
        var missing = PostgisInvocationValidator.ReadBatch(Invocation, out _);
        Assert.Contains("'batch'", missing!.Message);

        var malformed = PostgisInvocationValidator.ReadBatch(
            Invocation with { Arguments = Args(ProviderArguments.Batch, new byte[] { 42, 42 }) }, out _);
        Assert.NotNull(malformed);
        Assert.Contains("canonical feature batch", malformed.Message);
    }

    [Fact]
    public void ReadBatch_decodes_a_canonical_batch()
    {
        var bytes = FeatureTests.BatchBytes(FeatureTests.PlacesSchema);

        var error = PostgisInvocationValidator.ReadBatch(
            Invocation with { Arguments = Args(ProviderArguments.Batch, bytes) }, out var batch);

        Assert.Null(error);
        Assert.Equal(FeatureTests.PlacesSchema, batch.Schema);
    }

    [Fact]
    public void ReadSrid_defaults_to_4326_and_rejects_negatives()
    {
        var error = PostgisInvocationValidator.ReadSrid(Invocation, out var srid);
        Assert.Null(error);
        Assert.Equal(4326, srid);

        var negative = PostgisInvocationValidator.ReadSrid(
            Invocation with { Arguments = Args(ProviderArguments.Srid, -1) }, out _);
        Assert.Contains("'srid'", negative!.Message);

        var explicitOk = PostgisInvocationValidator.ReadSrid(
            Invocation with { Arguments = Args(ProviderArguments.Srid, 3857) }, out var explicitSrid);
        Assert.Null(explicitOk);
        Assert.Equal(3857, explicitSrid);
    }

    // ---- Bounding box ----

    [Fact]
    public void ReadBoundingBox_accepts_an_absent_bbox()
    {
        var error = PostgisInvocationValidator.ReadBoundingBox(Invocation, out var boundingBox);

        Assert.Null(error);
        Assert.Null(boundingBox);
    }

    [Fact]
    public void ReadBoundingBox_requires_all_four_bounds()
    {
        var error = PostgisInvocationValidator.ReadBoundingBox(
            Invocation with { Arguments = Args(ProviderArguments.MinX, 1.0) }, out _);

        Assert.NotNull(error);
        Assert.Contains("all four bounds", error!.Message);
    }

    [Fact]
    public void ReadBoundingBox_rejects_non_finite_and_swapped_bounds()
    {
        var nan = PostgisInvocationValidator.ReadBoundingBox(
            Invocation with
            {
                Arguments = Args(
                ProviderArguments.MinX, double.NaN, ProviderArguments.MinY, 0.0,
                ProviderArguments.MaxX, 1.0, ProviderArguments.MaxY, 1.0)
            }, out _);
        Assert.Contains("invalid", nan!.Message);

        var swapped = PostgisInvocationValidator.ReadBoundingBox(
            Invocation with
            {
                Arguments = Args(
                ProviderArguments.MinX, 2.0, ProviderArguments.MinY, 0.0,
                ProviderArguments.MaxX, 1.0, ProviderArguments.MaxY, 1.0)
            }, out _);
        Assert.Contains("invalid", swapped!.Message);
    }

    [Fact]
    public void ReadBoundingBox_accepts_integer_and_long_bounds()
    {
        var error = PostgisInvocationValidator.ReadBoundingBox(
            Invocation with
            {
                Arguments = Args(
                ProviderArguments.MinX, 1, ProviderArguments.MinY, 2L,
                ProviderArguments.MaxX, 3.0, ProviderArguments.MaxY, 4.0)
            }, out var boundingBox);

        Assert.Null(error);
        Assert.Equal(new BoundingBox(1, 2, 3, 4), boundingBox);
    }

    [Theory]
    [InlineData(double.NaN, 0, 1, 1)]
    [InlineData(0, double.PositiveInfinity, 1, 1)]
    [InlineData(0, 0, double.NaN, 1)]
    [InlineData(0, 0, 1, double.NegativeInfinity)]
    [InlineData(2, 0, 1, 1)]
    [InlineData(0, 2, 1, 1)]
    public void IsValidBounds_rejects_any_offending_bounds(double minx, double miny, double maxx, double maxy)
    {
        Assert.False(PostgisInvocationValidator.IsValidBounds(minx, miny, maxx, maxy));
    }

    [Fact]
    public void IsValidBounds_accepts_finite_ordered_bounds()
    {
        Assert.True(PostgisInvocationValidator.IsValidBounds(0, 1, 2, 3));
    }

    // ---- Filter ----

    [Fact]
    public void TryReadFilter_accepts_an_absent_filter()
    {
        var error = PostgisInvocationValidator.TryReadFilter(Invocation, out var filter);

        Assert.Null(error);
        Assert.Null(filter);
    }

    [Fact]
    public void TryReadFilter_requires_a_string_filter()
    {
        var error = PostgisInvocationValidator.TryReadFilter(
            Invocation with { Arguments = Args(ProviderArguments.Filter, 42) }, out _);

        Assert.NotNull(error);
        Assert.Contains("'filter'", error!.Message);
    }

    [Fact]
    public void TryReadFilter_parses_a_valid_expression()
    {
        var error = PostgisInvocationValidator.TryReadFilter(
            Invocation with { Arguments = Args(ProviderArguments.Filter, "population > 10") }, out var filter);

        Assert.Null(error);
        Assert.NotNull(filter);
    }

    [Fact]
    public void TryReadFilter_reports_a_parse_error()
    {
        var error = PostgisInvocationValidator.TryReadFilter(
            Invocation with { Arguments = Args(ProviderArguments.Filter, "population >") }, out _);

        Assert.NotNull(error);
        Assert.Contains("filter is not valid", error!.Message);
    }

    // ---- Batch schema ----

    [Fact]
    public void ValidateBatchSchema_rejects_invalid_column_names()
    {
        var schema = FeatureTests.Schema(("Bad Name", AttributeKind.Int64, false), ("geom", AttributeKind.Geometry, false));

        var error = PostgisInvocationValidator.ValidateBatchSchema(schema, Invocation);

        Assert.NotNull(error);
        Assert.Contains("Bad Name", error!.Message);
    }

    [Fact]
    public void ValidateBatchSchema_requires_a_geometry_field()
    {
        var schema = FeatureTests.Schema(("id", AttributeKind.Int64, false), ("name", AttributeKind.String, false));

        var error = PostgisInvocationValidator.ValidateBatchSchema(schema, Invocation);

        Assert.NotNull(error);
        Assert.Contains("no geometry field", error!.Message);
    }

    [Fact]
    public void ValidateBatchSchema_accepts_a_spatial_schema()
    {
        Assert.Null(PostgisInvocationValidator.ValidateBatchSchema(FeatureTests.PlacesSchema, Invocation));
    }

    // ---- Predicate building (pure SQL) ----

    [Fact]
    public void BuildPredicate_builds_a_combined_bbox_and_filter_predicate()
    {
        PostgisInvocationValidator.TryReadFilter(
            Invocation with { Arguments = Args(ProviderArguments.Filter, "name = 'x'") }, out var filter);

        var build = PostgisInvocationValidator.BuildPredicate(
            Invocation, FeatureTests.PlacesDescription(),
            new BoundingBox(1, 2, 3, 4), filter);

        Assert.True(build.IsValid);
        Assert.False(string.IsNullOrEmpty(build.Sql));
        Assert.Contains(" AND ", build.Sql);
        Assert.NotEmpty(build.Parameters);
    }

    [Fact]
    public void BuildPredicate_returns_null_predicate_without_bounds_or_filter()
    {
        var build = PostgisInvocationValidator.BuildPredicate(
            Invocation, FeatureTests.PlacesDescription(), null, null);

        Assert.True(build.IsValid);
        Assert.Null(build.Sql);
        Assert.Empty(build.Parameters);
    }

    [Fact]
    public void BuildPredicate_rejects_an_unknown_filter_column()
    {
        PostgisInvocationValidator.TryReadFilter(
            Invocation with { Arguments = Args(ProviderArguments.Filter, "population > 10") }, out var filter);

        var build = PostgisInvocationValidator.BuildPredicate(
            Invocation, FeatureTests.PlacesDescription(), null, filter);

        Assert.False(build.IsValid);
        Assert.NotNull(build.Error);
        Assert.Contains("not a field", build.Error!.Message);
    }

    // ---- Remaining validation guards ----

    [Fact]
    public void ReadPattern_accepts_absent_and_valid_patterns_but_requires_a_string()
    {
        var absent = PostgisInvocationValidator.ReadPattern(Invocation, out var absentPattern);
        Assert.Null(absent);
        Assert.Null(absentPattern);

        var wrongType = PostgisInvocationValidator.ReadPattern(
            Invocation with { Arguments = Args(ProviderArguments.Pattern, 42) }, out _);
        Assert.NotNull(wrongType);
        Assert.Contains("'pattern'", wrongType!.Message);

        var valid = PostgisInvocationValidator.ReadPattern(
            Invocation with { Arguments = Args(ProviderArguments.Pattern, "places%*") }, out var pattern);
        Assert.Null(valid);
        Assert.Equal("places%*", pattern);
    }

    [Fact]
    public void ReadOptionalTransaction_accepts_absent_handles_and_a_real_handle()
    {
        var absent = PostgisInvocationValidator.ReadOptionalTransaction(Invocation, out var absentId);
        Assert.Null(absent);
        Assert.Null(absentId);

        var handle = TransactionHandle();
        var present = PostgisInvocationValidator.ReadOptionalTransaction(
            Invocation with { Arguments = Args(ProviderArguments.Transaction, handle) }, out var transactionId);
        Assert.Null(present);
        Assert.Equal(handle.Id, transactionId);

        var wrongType = PostgisInvocationValidator.ReadOptionalTransaction(
            Invocation with { Arguments = Args(ProviderArguments.Transaction, "not-a-handle") }, out _);
        Assert.NotNull(wrongType);
        Assert.Contains("'transaction'", wrongType!.Message);
    }

    [Fact]
    public void ReadTransactionHandle_requires_a_transaction_handle()
    {
        var missing = PostgisInvocationValidator.ReadTransactionHandle(Invocation, out _);
        Assert.NotNull(missing);
        Assert.Contains("'transaction'", missing!.Message);

        var handle = TransactionHandle();
        var present = PostgisInvocationValidator.ReadTransactionHandle(
            Invocation with { Arguments = Args(ProviderArguments.Transaction, handle) }, out var id);
        Assert.Null(present);
        Assert.Equal(handle.Id, id);
    }

    [Fact]
    public void ReadBatchAndSchema_validates_the_whole_batch_and_schema_in_one_step()
    {
        var missing = PostgisInvocationValidator.ReadBatchAndSchema(Invocation, out _);
        Assert.Contains("'batch'", missing!.Message);

        var badSchema = FeatureTests.BatchBytes(FeatureTests.Schema(("Bad Name", AttributeKind.Int64, false), ("geom", AttributeKind.Geometry, false)));
        var schemaError = PostgisInvocationValidator.ReadBatchAndSchema(
            Invocation with { Arguments = Args(ProviderArguments.Batch, badSchema) }, out _);
        Assert.NotNull(schemaError);
        Assert.Contains("Bad Name", schemaError!.Message);

        var valid = FeatureTests.BatchBytes(FeatureTests.PlacesSchema);
        var ok = PostgisInvocationValidator.ReadBatchAndSchema(
            Invocation with { Arguments = Args(ProviderArguments.Batch, valid) }, out var batch);
        Assert.Null(ok);
        Assert.Equal(FeatureTests.PlacesSchema, batch.Schema);
    }

    [Fact]
    public void NotDecodable_names_the_dataset_and_its_fields()
    {
        Assert.True(PostgisDatasetName.TryParse("public.places", out var dataset, out _));

        var error = PostgisInvocationValidator.NotDecodable(
            Invocation, dataset, FeatureTests.PlacesDescription());

        Assert.NotNull(error);
        Assert.Contains("public.places", error.Message);
        Assert.Contains("'geom'", error.Message);
    }

    // ---- Store-executor failure mapping ----

    [Fact]
    public void MapFailure_maps_known_exceptions_to_invalid_arguments()
    {
        var executor = new PostgisStoreExecutor(
            PostgisConnectionConfiguration.FromEnvironmentValue(null),
            new Lazy<PostgisDataStore>(() => throw new InvalidOperationException("store never opened by MapFailure")));

        var unknown = executor.MapFailure(Invocation, new PostgisUnknownDatasetException("cannot read 'public.missing'"));
        Assert.Equal(CapabilityErrorKind.InvalidArguments, unknown.Kind);
        Assert.Contains("cannot read 'public.missing'", unknown.Message);

        var mismatch = executor.MapFailure(Invocation, new PostgisCrsMismatchException("the geometry carries EPSG:3857"));
        Assert.Equal(CapabilityErrorKind.InvalidArguments, mismatch.Kind);
        Assert.Contains("EPSG:3857", mismatch.Message);

        var inactive = executor.MapFailure(Invocation, new PostgisInactiveTransactionException("inactive"));
        Assert.Equal(CapabilityErrorKind.InvalidArguments, inactive.Kind);
        Assert.Contains("inactive", inactive.Message);

        var other = executor.MapFailure(Invocation, new InvalidOperationException("boom"));
        Assert.Equal(CapabilityErrorKind.ProviderFailure, other.Kind);
        Assert.Contains("boom", other.Message);
    }

    private static Dictionary<string, object?> Args(params object?[] pairs)
    {
        var arguments = new Dictionary<string, object?>(pairs.Length / 2);
        for (var i = 0; i < pairs.Length; i += 2)
        {
            arguments[(string)pairs[i]!] = pairs[i + 1];
        }

        return arguments;
    }

    /// <summary>A runtime-style transaction resource handle (any id; the validator only reads it).</summary>
    private static ResourceHandle TransactionHandle() =>
        new(ResourceId.Create(), ResourceKind.Parse("spatial.transaction.begin"), ProviderId.Parse("postgis@1"), DateTimeOffset.UtcNow);
}
