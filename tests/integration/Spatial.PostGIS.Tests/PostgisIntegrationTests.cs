using Spatial.Core.Features;
using Spatial.Core.Features.Codec;
using Spatial.Core.Geometry;
using Spatial.PluginSdk.Capabilities;
using Spatial.PluginSdk.Providers;
using Spatial.PluginSdk.Resources;
using Spatial.Provider.PostGIS;
using Spatial.Provider.PostGIS.Configuration;

namespace Spatial.PostGIS.Tests;

/// <summary>
/// The containerised data-path tests (plan §18, ADR-0028): catalogue,
/// schema discovery, scan/query streaming with canonical feature batches,
/// bbox and parameterised attribute filtering, writing, result-table
/// creation, transactions (commit/rollback/enlist), mid-stream database
/// cancellation and secret redaction — against a real PostGIS container.
/// Every test skips with an explicit reason when no Docker daemon is
/// available.
/// </summary>
public sealed class PostgisIntegrationTests : IClassFixture<PostgisContainerFixture>
{
    private readonly PostgisContainerFixture _fixture;

    public PostgisIntegrationTests(PostgisContainerFixture fixture)
    {
        _fixture = fixture;
    }

    [SkippableFact]
    public async Task Catalogue_lists_the_seeded_datasets()
    {
        Skip.If(!_fixture.DockerAvailable, _fixture.SkipReason ?? "no reason");
        var context = PostgisTestContext.Create(_fixture.ConnectionString);

        var items = await context.ReadItemsAsync(CatalogueListContract.Id, new Dictionary<string, object?>());

        var ids = items.Cast<string>().Select(DatasetMetadataJson.ReadSummary).Select(summary => summary.Id).ToArray();
        Assert.Contains("public.places", ids);
        Assert.Contains("public.roads", ids);
        Assert.Contains("public.bigpoints", ids);
    }

    [SkippableFact]
    public async Task Catalogue_pattern_filters_the_listed_datasets()
    {
        Skip.If(!_fixture.DockerAvailable, _fixture.SkipReason ?? "no reason");
        var context = PostgisTestContext.Create(_fixture.ConnectionString);

        var items = await context.ReadItemsAsync(
            CatalogueListContract.Id,
            new Dictionary<string, object?> { [ProviderArguments.Pattern] = "%places%" });

        var ids = items.Cast<string>().Select(DatasetMetadataJson.ReadSummary).Select(summary => summary.Id).ToArray();
        Assert.Equal(["public.places"], ids);
    }

    [SkippableFact]
    public async Task Describe_reports_the_schema_geometry_srid_and_identity_columns()
    {
        Skip.If(!_fixture.DockerAvailable, _fixture.SkipReason ?? "no reason");
        var context = PostgisTestContext.Create(_fixture.ConnectionString);

        var items = await context.ReadItemsAsync(
            DatasetDescribeContract.Id,
            new Dictionary<string, object?> { [ProviderArguments.Dataset] = "public.places" });

        var description = DatasetMetadataJson.ReadDescription((string)Assert.Single(items)!);
        Assert.Equal("public.places", description.Id);
        Assert.Equal("geom", description.GeometryColumn);
        Assert.Equal(4326, description.Srid);
        Assert.Equal(["id"], description.IdColumns);
        Assert.Equal(
            [$"id|{AttributeKind.Int64}", $"name|{AttributeKind.String}", $"geom|{AttributeKind.Geometry}"],
            description.Schema.Fields.Select(field => $"{field.Name}|{field.Kind}").ToArray());
    }

    [SkippableFact]
    public async Task Describe_of_an_unknown_dataset_is_an_invalid_argument()
    {
        Skip.If(!_fixture.DockerAvailable, _fixture.SkipReason ?? "no reason");
        var context = PostgisTestContext.Create(_fixture.ConnectionString);

        var outcome = await context.InvokeAsync(
            DatasetDescribeContract.Id,
            new Dictionary<string, object?> { [ProviderArguments.Dataset] = "public.nowhere" });

        Assert.Equal(CapabilityErrorKind.InvalidArguments, outcome.Error!.Kind);
    }

    [SkippableFact]
    public async Task Scan_streams_canonical_batches_with_crs_stamped_geometry()
    {
        Skip.If(!_fixture.DockerAvailable, _fixture.SkipReason ?? "no reason");
        var context = PostgisTestContext.Create(_fixture.ConnectionString);

        var batches = await context.ReadBatchesAsync(
            FeatureScanContract.Id,
            new Dictionary<string, object?> { [ProviderArguments.Dataset] = "public.places" });

        var features = batches.SelectMany(batch => batch.Features).ToArray();
        Assert.Equal(3, features.Length);

        var berlin = features.Single(feature => feature["name"].StringValue == "Berlin");
        Assert.Equal("1", berlin.Id.Value);
        var point = Assert.IsType<Point>(berlin[2].GeometryValue);
        Assert.Equal(13.405, (double)point.X!, precision: 12);
        Assert.Equal(52.52, (double)point.Y!, precision: 12);
        Assert.Equal(new CoordinateReference("EPSG", "4326"), point.CoordinateReference);
    }

    [SkippableFact]
    public async Task Scan_of_a_table_without_a_primary_key_uses_ordinal_identities()
    {
        Skip.If(!_fixture.DockerAvailable, _fixture.SkipReason ?? "no reason");
        var context = PostgisTestContext.Create(_fixture.ConnectionString);

        var batches = await context.ReadBatchesAsync(
            FeatureScanContract.Id,
            new Dictionary<string, object?> { [ProviderArguments.Dataset] = "public.roads" });

        var features = batches.SelectMany(batch => batch.Features).ToArray();
        Assert.Equal(2, features.Length);
        Assert.Equal(["0", "1"], features.Select(feature => feature.Id.Value).ToArray());
        Assert.Equal(GeometryType.LineString, features[0][2].GeometryValue.Type);
    }

    [SkippableFact]
    public async Task Query_bounding_box_returns_only_intersecting_features()
    {
        Skip.If(!_fixture.DockerAvailable, _fixture.SkipReason ?? "no reason");
        var context = PostgisTestContext.Create(_fixture.ConnectionString);

        // A box around Berlin only.
        var batches = await context.ReadBatchesAsync(FeatureQueryContract.Id, new Dictionary<string, object?>
        {
            [ProviderArguments.Dataset] = "public.places",
            [ProviderArguments.MinX] = 13.0,
            [ProviderArguments.MinY] = 52.0,
            [ProviderArguments.MaxX] = 13.5,
            [ProviderArguments.MaxY] = 53.0,
        });

        var names = batches.SelectMany(batch => batch.Features).Select(feature => feature["name"].StringValue).ToArray();
        Assert.Equal(["Berlin"], names);
    }

    [SkippableFact]
    public async Task Query_attribute_filters_are_parameterised_and_resolved_against_the_schema()
    {
        Skip.If(!_fixture.DockerAvailable, _fixture.SkipReason ?? "no reason");
        var context = PostgisTestContext.Create(_fixture.ConnectionString);

        var byName = await context.ReadBatchesAsync(FeatureQueryContract.Id, new Dictionary<string, object?>
        {
            [ProviderArguments.Dataset] = "public.places",
            [ProviderArguments.Filter] = "name = 'Berlin'",
        });
        Assert.Equal(["Berlin"], byName.SelectMany(batch => batch.Features).Select(feature => feature["name"].StringValue).ToArray());

        var like = await context.ReadBatchesAsync(FeatureQueryContract.Id, new Dictionary<string, object?>
        {
            [ProviderArguments.Dataset] = "public.places",
            [ProviderArguments.Filter] = "name LIKE 'P%'",
        });
        Assert.Equal(["Paris"], like.SelectMany(batch => batch.Features).Select(feature => feature["name"].StringValue).ToArray());

        var prefix = await context.ReadBatchesAsync(FeatureQueryContract.Id, new Dictionary<string, object?>
        {
            [ProviderArguments.Dataset] = "public.places",
            [ProviderArguments.Filter] = "id < 3 AND name != 'London'",
        });
        Assert.Equal(["Berlin"], prefix.SelectMany(batch => batch.Features).Select(feature => feature["name"].StringValue).ToArray());

        var filteredBox = await context.ReadBatchesAsync(FeatureQueryContract.Id, new Dictionary<string, object?>
        {
            [ProviderArguments.Dataset] = "public.places",
            [ProviderArguments.MinX] = -1.0,
            [ProviderArguments.MinY] = 48.0,
            [ProviderArguments.MaxX] = 15.0,
            [ProviderArguments.MaxY] = 54.0,
            [ProviderArguments.Filter] = "id = 3",
        });
        Assert.Equal(["Paris"], filteredBox.SelectMany(batch => batch.Features).Select(feature => feature["name"].StringValue).ToArray());

        var unknownColumn = await context.InvokeAsync(FeatureQueryContract.Id, new Dictionary<string, object?>
        {
            [ProviderArguments.Dataset] = "public.places",
            [ProviderArguments.Filter] = "mystery = 1",
        });
        Assert.Equal(CapabilityErrorKind.InvalidArguments, unknownColumn.Error!.Kind);
        Assert.Contains("'mystery'", unknownColumn.Error.Message);

        var geometryColumn = await context.InvokeAsync(FeatureQueryContract.Id, new Dictionary<string, object?>
        {
            [ProviderArguments.Dataset] = "public.places",
            [ProviderArguments.Filter] = "geom = 1",
        });
        Assert.Equal(CapabilityErrorKind.InvalidArguments, geometryColumn.Error!.Kind);
        Assert.Contains("bounding box", geometryColumn.Error.Message);
    }

    [SkippableFact]
    public async Task Write_appends_features_in_a_single_transaction()
    {
        Skip.If(!_fixture.DockerAvailable, _fixture.SkipReason ?? "no reason");
        var context = PostgisTestContext.Create(_fixture.ConnectionString);
        await context.ExecuteAsync("DROP TABLE IF EXISTS public.write_target");
        await context.ExecuteAsync(
            "CREATE TABLE public.write_target (id bigint PRIMARY KEY, name text, geom geometry(Point, 3857))");

        var schema = new FeatureSchema(
        [
            new FieldDefinition("id", AttributeKind.Int64, false),
            new FieldDefinition("name", AttributeKind.String, true),
            new FieldDefinition("geom", AttributeKind.Geometry, true),
        ]);
        var batchBytes = FeatureBatchCodec.Encode(new FeatureBatch(schema,
        [
            new Feature(new FeatureId("10"), schema,
            [
                AttributeValue.FromInt64(10),
                AttributeValue.FromString("ten"),
                AttributeValue.FromGeometry(GeometryFactory.CreatePoint(10, 10, CoordinateReference.Epsg(3857))),
            ]),
            new Feature(new FeatureId("11"), schema,
            [
                AttributeValue.FromInt64(11),
                AttributeValue.FromString("eleven"),
                AttributeValue.FromGeometry(GeometryFactory.CreatePoint(11, 11, CoordinateReference.Epsg(3857))),
            ]),
        ]));

        var outcome = await context.InvokeAsync(FeatureWriteContract.Id, new Dictionary<string, object?>
        {
            [ProviderArguments.Dataset] = "public.write_target",
            [ProviderArguments.Batch] = batchBytes,
        });

        Assert.True(outcome.TryGetValue(out var value));
        Assert.Equal(2L, value);
        Assert.Equal(2, await context.CountAsync("SELECT count(*) FROM public.write_target"));
    }

    [SkippableFact]
    public async Task Write_rejects_a_batch_whose_schema_is_not_decodable_from_the_dataset()
    {
        Skip.If(!_fixture.DockerAvailable, _fixture.SkipReason ?? "no reason");
        var context = PostgisTestContext.Create(_fixture.ConnectionString);

        var schema = new FeatureSchema(
        [
            new FieldDefinition("mystery", AttributeKind.String, false),
        ]);
        var batchBytes = FeatureBatchCodec.Encode(new FeatureBatch(schema, []));

        var outcome = await context.InvokeAsync(FeatureWriteContract.Id, new Dictionary<string, object?>
        {
            [ProviderArguments.Dataset] = "public.places",
            [ProviderArguments.Batch] = batchBytes,
        });

        Assert.Equal(CapabilityErrorKind.InvalidArguments, outcome.Error!.Kind);
        Assert.Contains("decodable", outcome.Error.Message);
    }

    [SkippableFact]
    public async Task Create_builds_a_result_table_from_a_defining_batch()
    {
        Skip.If(!_fixture.DockerAvailable, _fixture.SkipReason ?? "no reason");
        var context = PostgisTestContext.Create(_fixture.ConnectionString);
        await context.ExecuteAsync("DROP TABLE IF EXISTS public.created");

        var schema = new FeatureSchema(
        [
            new FieldDefinition("name", AttributeKind.String, false),
            new FieldDefinition("geom", AttributeKind.Geometry, false),
        ]);
        var batchBytes = FeatureBatchCodec.Encode(new FeatureBatch(schema, []));

        var outcome = await context.InvokeAsync(DatasetCreateContract.Id, new Dictionary<string, object?>
        {
            [ProviderArguments.Dataset] = "public.created",
            [ProviderArguments.Batch] = batchBytes,
            [ProviderArguments.Srid] = 3857,
        });

        Assert.True(outcome.TryGetValue(out var value));
        Assert.Equal("public.created", value);

        var description = DatasetMetadataJson.ReadDescription((string)(await context.ReadItemsAsync(
            DatasetDescribeContract.Id, new Dictionary<string, object?> { [ProviderArguments.Dataset] = "public.created" })).Single()!);
        Assert.Equal(3857, description.Srid);
        Assert.Equal("geom", description.GeometryColumn);
        Assert.Equal(CapabilityErrorKind.InvalidArguments,
            (await context.InvokeAsync(DatasetCreateContract.Id, new Dictionary<string, object?>
            {
                [ProviderArguments.Dataset] = "public.created",
                [ProviderArguments.Batch] = batchBytes,
            })).Error!.Kind);
    }

    [SkippableFact]
    public async Task Committed_enlisted_writes_become_visible_after_commit()
    {
        Skip.If(!_fixture.DockerAvailable, _fixture.SkipReason ?? "no reason");
        var context = PostgisTestContext.Create(_fixture.ConnectionString);
        await context.ExecuteAsync("DROP TABLE IF EXISTS public.tx_target");
        await context.ExecuteAsync("CREATE TABLE public.tx_target (id bigint PRIMARY KEY, geom geometry(Point, 4326))");
        var schema = new FeatureSchema(
        [
            new FieldDefinition("id", AttributeKind.Int64, false),
            new FieldDefinition("geom", AttributeKind.Geometry, false),
        ]);

        var begin = await context.InvokeAsync(TransactionBeginContract.Id, new Dictionary<string, object?>());
        Assert.True(begin.TryGetValue(out var handleValue));
        var transaction = Assert.IsType<ResourceHandle>(handleValue);

        var batchBytes = FeatureBatchCodec.Encode(new FeatureBatch(schema,
        [
            new Feature(new FeatureId("1"), schema,
            [
                AttributeValue.FromInt64(1),
                AttributeValue.FromGeometry(GeometryFactory.CreatePoint(1, 2, CoordinateReference.Epsg(4326))),
            ]),
        ]));
        var write = await context.InvokeAsync(FeatureWriteContract.Id, new Dictionary<string, object?>
        {
            [ProviderArguments.Dataset] = "public.tx_target",
            [ProviderArguments.Batch] = batchBytes,
            [ProviderArguments.Transaction] = transaction,
        });
        Assert.True(write.TryGetValue(out var count));
        Assert.Equal(1L, count);
        // Uncommitted: invisible to other connections.
        Assert.Equal(0, await context.CountAsync("SELECT count(*) FROM public.tx_target"));

        var commit = await context.InvokeAsync(TransactionCommitContract.Id, new Dictionary<string, object?>
        {
            [ProviderArguments.Transaction] = transaction,
        });
        Assert.True(commit.TryGetValue(out var committed));
        Assert.Equal(true, committed);
        Assert.Equal(1, await context.CountAsync("SELECT count(*) FROM public.tx_target"));

        // The ended handle is inactive.
        var again = await context.InvokeAsync(TransactionCommitContract.Id, new Dictionary<string, object?>
        {
            [ProviderArguments.Transaction] = transaction,
        });
        Assert.Equal(CapabilityErrorKind.InvalidArguments, again.Error!.Kind);
        Assert.Contains("no longer active", again.Error.Message);
    }

    [SkippableFact]
    public async Task Rolled_back_enlisted_writes_never_become_visible()
    {
        Skip.If(!_fixture.DockerAvailable, _fixture.SkipReason ?? "no reason");
        var context = PostgisTestContext.Create(_fixture.ConnectionString);
        await context.ExecuteAsync("DROP TABLE IF EXISTS public.tx_target");
        await context.ExecuteAsync("CREATE TABLE public.tx_target (id bigint PRIMARY KEY, geom geometry(Point, 4326))");
        var schema = new FeatureSchema(
        [
            new FieldDefinition("id", AttributeKind.Int64, false),
            new FieldDefinition("geom", AttributeKind.Geometry, false),
        ]);
        var batchBytes = FeatureBatchCodec.Encode(new FeatureBatch(schema,
        [
            new Feature(new FeatureId("1"), schema,
            [
                AttributeValue.FromInt64(1),
                AttributeValue.FromGeometry(GeometryFactory.CreatePoint(1, 2, CoordinateReference.Epsg(4326))),
            ]),
        ]));

        var begin = await context.InvokeAsync(TransactionBeginContract.Id, new Dictionary<string, object?>());
        Assert.True(begin.TryGetValue(out var handleValue));
        var transaction = Assert.IsType<ResourceHandle>(handleValue);
        await context.InvokeAsync(FeatureWriteContract.Id, new Dictionary<string, object?>
        {
            [ProviderArguments.Dataset] = "public.tx_target",
            [ProviderArguments.Batch] = batchBytes,
            [ProviderArguments.Transaction] = transaction,
        });

        var rollback = await context.InvokeAsync(TransactionRollbackContract.Id, new Dictionary<string, object?>
        {
            [ProviderArguments.Transaction] = transaction,
        });
        Assert.True(rollback.TryGetValue(out var value));
        Assert.Equal(true, value);

        Assert.Equal(0, await context.CountAsync("SELECT count(*) FROM public.tx_target"));
    }

    [SkippableFact]
    public async Task A_write_failure_atomicity_keeps_the_implicit_transaction_pure()
    {
        Skip.If(!_fixture.DockerAvailable, _fixture.SkipReason ?? "no reason");
        var context = PostgisTestContext.Create(_fixture.ConnectionString);
        await context.ExecuteAsync("DROP TABLE IF EXISTS public.atomic_target");
        await context.ExecuteAsync("CREATE TABLE public.atomic_target (id bigint PRIMARY KEY, geom geometry(Point, 4326))");
        var schema = new FeatureSchema(
        [
            new FieldDefinition("id", AttributeKind.Int64, false),
            new FieldDefinition("geom", AttributeKind.Geometry, false),
        ]);
        // Two features with the SAME primary key: the second insert fails, and
        // the first must not survive (single-transaction append, ADR-0028).
        var batchBytes = FeatureBatchCodec.Encode(new FeatureBatch(schema,
        [
            FeatureWithId("1", schema),
            FeatureWithId("1", schema),
        ]));

        var outcome = await context.InvokeAsync(FeatureWriteContract.Id, new Dictionary<string, object?>
        {
            [ProviderArguments.Dataset] = "public.atomic_target",
            [ProviderArguments.Batch] = batchBytes,
        });

        Assert.Equal(CapabilityErrorKind.ProviderFailure, outcome.Error!.Kind);
        Assert.Equal(0, await context.CountAsync("SELECT count(*) FROM public.atomic_target"));
    }

    [SkippableFact]
    public async Task Cancelling_a_scan_mid_stream_fails_the_stream_with_operation_cancelled()
    {
        Skip.If(!_fixture.DockerAvailable, _fixture.SkipReason ?? "no reason");
        var context = PostgisTestContext.Create(_fixture.ConnectionString);
        using var cancellation = new CancellationTokenSource();

        var (handle, stream) = await context.InvokeScanStreamAsync(
            FeatureScanContract.Id,
            new Dictionary<string, object?> { [ProviderArguments.Dataset] = "public.bigpoints" },
            cancellation.Token);

        // The bounded buffer has produced items; the 200k-row scan is still
        // running. Cancel the invocation token — the Npgsql command breaks
        // and the stream fails with operation.cancelled.
        await stream.ReadBatchAsync(1, cancellation.Token);
        cancellation.Cancel();

        var completion = await stream.WaitForCompletionAsync();
        Assert.True(completion.IsFailed);
        Assert.Equal("operation.cancelled", completion.Error?.Code);
    }

    [SkippableFact]
    public async Task Diagnostics_never_leak_the_connection_secret()
    {
        Skip.If(!_fixture.DockerAvailable, _fixture.SkipReason ?? "no reason");
        var secret = $"TRUE-SECRET-{Guid.NewGuid():N}";
        var broken = PostgisConnectionConfiguration.FromConnectionString(
            $"Host={_fixture.ConnectionString.GetHost()};Port={_fixture.ConnectionString.GetPort()};Database=spatial;Username=spatial;Password={secret}");
        var registry = new Spatial.Runtime.Capabilities.CapabilityRegistry();
        registry.Register(new PostgisProvider(broken));
        var runtime = new Spatial.Runtime.Capabilities.CapabilityRuntime(registry);

        var outcome = await runtime.InvokeAsync(CapabilityInvocation.Create(
            FeatureScanContract.Id,
            new Dictionary<string, object?> { [ProviderArguments.Dataset] = "public.places" }) with
        {
            GrantedPermissions = new HashSet<Permission> { Permission.Parse("spatial.feature.read") },
        });

        Assert.Equal(CapabilityErrorKind.ProviderFailure, outcome.Error!.Kind);
        Assert.DoesNotContain(secret, outcome.Error.Message);
        Assert.Contains("'spatial'", outcome.Error.Message);
    }

    private static Feature FeatureWithId(string id, FeatureSchema schema) =>
        new(new FeatureId(id), schema,
        [
            AttributeValue.FromInt64(1),
            AttributeValue.FromGeometry(GeometryFactory.CreatePoint(1, 2, CoordinateReference.Epsg(4326))),
        ]);
}

internal static class ConnectionStringExtensions
{
    public static string GetHost(this string connectionString) =>
        connectionString.Split(';').First(part => part.StartsWith("Host=", StringComparison.Ordinal)).Split('=')[1];

    public static string GetPort(this string connectionString) =>
        connectionString.Split(';').First(part => part.StartsWith("Port=", StringComparison.Ordinal)).Split('=')[1];
}
