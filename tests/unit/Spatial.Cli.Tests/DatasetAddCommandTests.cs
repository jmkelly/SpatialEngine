using System.Text.Json;
using Spatial.Cli;
using Spatial.Client;
using Spatial.PluginSdk.Providers;

namespace Spatial.Cli.Tests;

/// <summary>
/// Unit tests of <c>dataset add</c> (ADR-0052): source/format/identity
/// validation, the exact upload handed to the gateway, the admin-token rule,
/// and the dry-run/JSON output. No network.
/// </summary>
public sealed class DatasetAddCommandTests
{
    [Fact]
    public async Task Add_file_uploads_the_expected_request()
    {
        var gateway = new FakeSpatialGateway();

        var run = await CliHarness.RunAsync(
            gateway,
            "dataset", "add",
            "--file", "/data/world.geojson",
            "--dataset", "public.world",
            "--srid", "4326",
            "--format", "ndjson",
            "--identity", "source",
            "--identity-field", "gid",
            "--publish", "World",
            "--source-srid", "3857",
            "--store", "memory",
            "--token", "admin-token");

        Assert.Equal(ExitCodes.Success, run.ExitCode);
        var call = Assert.Single(gateway.IngestCalls);
        Assert.Equal("/data/world.geojson", call.Source);
        Assert.Equal("admin-token", call.Token);
        Assert.Equal("world.geojson", call.Upload.FileName);
        Assert.Equal("public.world", call.Upload.Dataset);
        Assert.Equal(4326, call.Upload.Srid);
        Assert.Equal("ndjson", call.Upload.Format);
        Assert.Equal("memory", call.Upload.Store);
        Assert.Equal("source", call.Upload.Identity);
        Assert.Equal("gid", call.Upload.IdentityField);
        Assert.Equal("World", call.Upload.Publish);
        Assert.Equal(3857, call.Upload.SourceSrid);
    }

    [Fact]
    public async Task Add_defaults_to_geojson_and_auto_identity()
    {
        var gateway = new FakeSpatialGateway();

        var run = await CliHarness.RunAsync(
            gateway,
            "dataset", "add", "--file", "/tmp/world.geojson",
            "--dataset", "public.world", "--srid", "4326", "--token", "t");

        Assert.Equal(ExitCodes.Success, run.ExitCode);
        var upload = Assert.Single(gateway.IngestCalls).Upload;
        Assert.Equal("geojson", upload.Format);
        Assert.Equal("auto", upload.Identity);
        Assert.Null(upload.IdentityField);
        Assert.Null(upload.SourceSrid);
        Assert.Null(upload.Publish);
    }

    [Fact]
    public async Task Add_url_records_the_url_and_derives_the_filename()
    {
        var gateway = new FakeSpatialGateway();

        var run = await CliHarness.RunAsync(
            gateway,
            "dataset", "add",
            "--url", "https://example.com/data/places.geojson?token=abc",
            "--dataset", "public.places", "--srid", "4326", "--token", "t");

        Assert.Equal(ExitCodes.Success, run.ExitCode);
        var call = Assert.Single(gateway.IngestCalls);
        Assert.Equal("https://example.com/data/places.geojson?token=abc", call.Source);
        Assert.Equal("places.geojson", call.Upload.FileName);
    }

    [Fact]
    public async Task Add_url_without_a_filename_defaults_to_upload_geojson()
    {
        var gateway = new FakeSpatialGateway();

        var run = await CliHarness.RunAsync(
            gateway,
            "dataset", "add", "--url", "https://example.com/",
            "--dataset", "public.places", "--srid", "4326", "--token", "t");

        Assert.Equal(ExitCodes.Success, run.ExitCode);
        Assert.Equal("upload.geojson", Assert.Single(gateway.IngestCalls).Upload.FileName);
    }

    [Fact]
    public async Task Add_requires_a_source()
    {
        var gateway = new FakeSpatialGateway();

        var run = await CliHarness.RunAsync(
            gateway,
            "dataset", "add", "--dataset", "public.world", "--srid", "4326", "--token", "t");

        Assert.Equal(ExitCodes.Usage, run.ExitCode);
        Assert.Empty(gateway.IngestCalls);
    }

    [Fact]
    public async Task Add_rejects_both_file_and_url()
    {
        var gateway = new FakeSpatialGateway();

        var run = await CliHarness.RunAsync(
            gateway,
            "dataset", "add",
            "--file", "/tmp/world.geojson",
            "--url", "https://example.com/world.geojson",
            "--dataset", "public.world", "--srid", "4326", "--token", "t");

        Assert.Equal(ExitCodes.Usage, run.ExitCode);
        Assert.Empty(gateway.IngestCalls);
    }

    [Theory]
    [InlineData("--format", "shapefile")]
    [InlineData("--format", "geojsonl")]
    [InlineData("--identity", "random")]
    public async Task Add_rejects_unknown_format_or_identity(string option, string value)
    {
        var gateway = new FakeSpatialGateway();

        var run = await CliHarness.RunAsync(
            gateway,
            "dataset", "add", "--file", "/tmp/world.geojson",
            "--dataset", "public.world", "--srid", "4326", "--token", "t",
            option, value);

        Assert.Equal(ExitCodes.Usage, run.ExitCode);
        Assert.Empty(gateway.IngestCalls);
    }

    [Fact]
    public async Task Add_source_identity_requires_an_identity_field()
    {
        var gateway = new FakeSpatialGateway();

        var run = await CliHarness.RunAsync(
            gateway,
            "dataset", "add", "--file", "/tmp/world.geojson",
            "--dataset", "public.world", "--srid", "4326",
            "--identity", "source", "--token", "t");

        Assert.Equal(ExitCodes.Usage, run.ExitCode);
        Assert.Empty(gateway.IngestCalls);
    }

    [Fact]
    public async Task Add_identity_field_requires_source_identity()
    {
        var gateway = new FakeSpatialGateway();

        var run = await CliHarness.RunAsync(
            gateway,
            "dataset", "add", "--file", "/tmp/world.geojson",
            "--dataset", "public.world", "--srid", "4326",
            "--identity", "auto", "--identity-field", "gid", "--token", "t");

        Assert.Equal(ExitCodes.Usage, run.ExitCode);
        Assert.Empty(gateway.IngestCalls);
    }

    [Fact]
    public async Task Add_requires_an_admin_token()
    {
        var gateway = new FakeSpatialGateway();

        var run = await CliHarness.RunAsync(
            gateway,
            "dataset", "add", "--file", "/tmp/world.geojson",
            "--dataset", "public.world", "--srid", "4326");

        Assert.Equal(ExitCodes.Usage, run.ExitCode);
        Assert.Contains("Admin token required", run.Error, StringComparison.Ordinal);
        Assert.Empty(gateway.IngestCalls);
    }

    [Fact]
    public async Task Add_dry_run_prints_the_plan_without_calling_the_host()
    {
        var gateway = new FakeSpatialGateway();

        var run = await CliHarness.RunAsync(
            gateway,
            "dataset", "add", "--file", "/tmp/world.geojson",
            "--dataset", "public.world", "--srid", "4326",
            "--publish", "World", "--dry-run", "--token", "t");

        Assert.Equal(ExitCodes.Success, run.ExitCode);
        Assert.Empty(gateway.IngestCalls);
        Assert.Contains("/tmp/world.geojson", run.Output, StringComparison.Ordinal);
        Assert.Contains("public.world", run.Output, StringComparison.Ordinal);
        Assert.Contains("memory", run.Output, StringComparison.Ordinal);
        Assert.Contains("published as World", run.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Add_reports_loaded_features()
    {
        var gateway = new FakeSpatialGateway();

        var run = await CliHarness.RunAsync(
            gateway,
            "dataset", "add", "--file", "/tmp/world.geojson",
            "--dataset", "public.world", "--srid", "4326", "--token", "t");

        Assert.Equal(ExitCodes.Success, run.ExitCode);
        Assert.Contains("public.world: 3 feature(s) loaded into memory", run.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Add_reports_the_publication_when_the_outcome_has_one()
    {
        var run = await CliHarness.RunAsync(
            new PublishingGateway(new FakeSpatialGateway()),
            "dataset", "add", "--file", "/tmp/world.geojson",
            "--dataset", "public.world", "--srid", "4326",
            "--publish", "World", "--token", "t");

        Assert.Equal(ExitCodes.Success, run.ExitCode);
        Assert.Contains("public.world: 5 feature(s) loaded into memory, published as World", run.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Add_json_reports_an_ok_envelope()
    {
        var gateway = new FakeSpatialGateway();

        var run = await CliHarness.RunAsync(
            gateway,
            "--json",
            "dataset", "add", "--file", "/tmp/world.geojson",
            "--dataset", "public.world", "--srid", "4326", "--token", "t");

        Assert.Equal(ExitCodes.Success, run.ExitCode);
        using var document = JsonDocument.Parse(run.Output);
        Assert.True(document.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal("dataset add", document.RootElement.GetProperty("command").GetString());
    }

    /// <summary>
    /// A gateway that returns an ingest outcome carrying a map so the
    /// published human form is exercised; every other call delegates to a fake.
    /// </summary>
    private sealed class PublishingGateway(FakeSpatialGateway inner) : ISpatialGateway
    {
        public Task<HostHealth> CheckHealthAsync(CancellationToken cancellationToken = default) =>
            inner.CheckHealthAsync(cancellationToken);

        public Task<IReadOnlyList<DatasetSummary>> ListDatasetsAsync(string store, string? pattern, CancellationToken cancellationToken = default) =>
            inner.ListDatasetsAsync(store, pattern, cancellationToken);

        public Task<DatasetDescription> DescribeDatasetAsync(string dataset, string store, CancellationToken cancellationToken = default) =>
            inner.DescribeDatasetAsync(dataset, store, cancellationToken);

        public Task<IngestOutcome> IngestAsync(string source, IngestUpload upload, string? adminToken, CancellationToken cancellationToken = default) =>
            Task.FromResult(new IngestOutcome(
                upload.Dataset,
                5,
                upload.Srid,
                "id",
                new Map(upload.Publish ?? "CliE2E", upload.Store, [], [MapServiceKind.FeatureServer])));

        public Task<IReadOnlyList<Map>> ListMapsAsync(CancellationToken cancellationToken = default) =>
            inner.ListMapsAsync(cancellationToken);

        public Task<Map?> FindMapAsync(string name, CancellationToken cancellationToken = default) =>
            inner.FindMapAsync(name, cancellationToken);

        public Task<Map> PutMapAsync(Map map, string? adminToken, CancellationToken cancellationToken = default) =>
            inner.PutMapAsync(map, adminToken, cancellationToken);

        public Task<bool> DeleteMapAsync(string name, string? adminToken, CancellationToken cancellationToken = default) =>
            inner.DeleteMapAsync(name, adminToken, cancellationToken);

        public void Dispose() => inner.Dispose();
    }
}
