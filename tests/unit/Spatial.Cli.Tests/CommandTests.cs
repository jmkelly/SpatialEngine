using System.Text.Json;
using Spatial.Cli;
using Spatial.Client;
using Spatial.Core.Features;
using Spatial.PluginSdk.Providers;

namespace Spatial.Cli.Tests;

/// <summary>End-to-end command dispatch against a fake gateway (ADR-0052).</summary>
public sealed class CommandTests
{
    [Fact]
    public async Task Host_health_reports_the_stores()
    {
        var gateway = new FakeSpatialGateway { Health = new HostHealth("ready", ["demo", "memory"]) };

        var run = await CliHarness.RunAsync(gateway, "host", "health");

        Assert.Equal(ExitCodes.Success, run.ExitCode);
        Assert.Contains("ready", run.Output, StringComparison.Ordinal);
        Assert.Contains("demo, memory", run.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Host_health_json_uses_the_ok_envelope()
    {
        var gateway = new FakeSpatialGateway();

        var run = await CliHarness.RunAsync(gateway, "--json", "host", "health");

        using var document = JsonDocument.Parse(run.Output);
        Assert.True(document.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal("host health", document.RootElement.GetProperty("command").GetString());
    }

    [Fact]
    public async Task Dataset_list_renders_each_dataset()
    {
        var gateway = new FakeSpatialGateway
        {
            Datasets = [new DatasetSummary("public.world", "public", "world", "geom", 4326, 10)],
        };

        var run = await CliHarness.RunAsync(gateway, "dataset", "list");

        Assert.Equal(ExitCodes.Success, run.ExitCode);
        Assert.Contains("public.world", run.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Dataset_describe_reports_fields()
    {
        var gateway = new FakeSpatialGateway
        {
            Description = new DatasetDescription("public.world", "public", "world", "geom", 4326, "Polygon", 10, ["id"], new FeatureSchema([])),
        };

        var run = await CliHarness.RunAsync(gateway, "dataset", "describe", "public.world");

        Assert.Equal(ExitCodes.Success, run.ExitCode);
        Assert.Contains("public.world", run.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Map_show_returns_not_found_for_an_unknown_publication()
    {
        var run = await CliHarness.RunAsync(new FakeSpatialGateway(), "map", "show", "Missing");

        Assert.Equal(ExitCodes.NotFound, run.ExitCode);
        Assert.Contains("not.found", run.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Map_show_describes_layers_and_endpoint()
    {
        var gateway = new FakeSpatialGateway();
        gateway.MapsByName = new Dictionary<string, Map>(StringComparer.Ordinal)
        {
            ["World"] = new Map(
                "World",
                "memory",
                [new MapLayer("public.world", 0, "Countries", MapLibreStyleBuilder.Lower(new DrawRecipe(), GeometryFamily.Polygon))],
                [MapService.Map]),
        };

        var run = await CliHarness.RunAsync(gateway, "map", "show", "World");

        Assert.Equal(ExitCodes.Success, run.ExitCode);
        Assert.Contains("MapServer", run.Output, StringComparison.Ordinal);
        Assert.Contains("Countries", run.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Map_export_url_prints_only_the_endpoint()
    {
        var gateway = new FakeSpatialGateway();
        gateway.MapsByName = new Dictionary<string, Map>(StringComparer.Ordinal)
        {
            ["World"] = new Map("World", "memory", [], [MapService.Feature]),
        };

        var run = await CliHarness.RunAsync(gateway, "map", "export", "World", "--format", "url");

        Assert.Equal(ExitCodes.Success, run.ExitCode);
        Assert.Equal("http://127.0.0.1:5201/arcgis/rest/services/World/FeatureServer", run.Output.Trim());
    }

    [Fact]
    public async Task Unknown_command_is_a_usage_error()
    {
        var run = await CliHarness.RunAsync(new FakeSpatialGateway(), "map", "explode");

        Assert.Equal(ExitCodes.Usage, run.ExitCode);
        Assert.Contains("invalid.arguments", run.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Unknown_option_is_a_usage_error_without_misparsing_the_command()
    {
        var run = await CliHarness.RunAsync(new FakeSpatialGateway(), "--bogus", "map", "list");

        Assert.Equal(ExitCodes.Usage, run.ExitCode);
        Assert.Contains("Unknown option '--bogus'", run.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_option_the_command_does_not_accept_is_a_usage_error()
    {
        var run = await CliHarness.RunAsync(new FakeSpatialGateway(), "map", "list", "--color", "#ffffff");

        Assert.Equal(ExitCodes.Usage, run.ExitCode);
        Assert.Contains("Unknown option '--color'", run.Error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("0")]
    public async Task Invalid_timeout_is_a_usage_error(string value)
    {
        var run = await CliHarness.RunAsync(new FakeSpatialGateway(), "host", "health", "--timeout", value);

        Assert.Equal(ExitCodes.Usage, run.ExitCode);
        Assert.Contains("timeout", run.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Missing_required_option_is_a_usage_error()
    {
        var run = await CliHarness.RunAsync(new FakeSpatialGateway(), "dataset", "describe");

        Assert.Equal(ExitCodes.Usage, run.ExitCode);
    }

    [Fact]
    public async Task Store_unavailable_maps_to_its_exit_code()
    {
        var gateway = new FakeSpatialGateway { Failure = new SpatialClientException(503, "store.unavailable", "not configured") };

        var run = await CliHarness.RunAsync(gateway, "dataset", "list");

        Assert.Equal(ExitCodes.Unavailable, run.ExitCode);
        Assert.Contains("store.unavailable", run.Error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(typeof(HttpRequestException), ExitCodes.Unavailable)]
    [InlineData(typeof(OperationCanceledException), ExitCodes.Cancelled)]
    [InlineData(typeof(IOException), ExitCodes.Usage)]
    [InlineData(typeof(InvalidOperationException), ExitCodes.Failure)]
    public async Task Gateway_failures_map_to_stable_exit_codes(Type exceptionType, int expected)
    {
        var exception = (Exception)Activator.CreateInstance(exceptionType)!;
        var gateway = new FakeSpatialGateway { Failure = exception };

        var run = await CliHarness.RunAsync(gateway, "dataset", "list");

        Assert.Equal(expected, run.ExitCode);
    }

    [Fact]
    public async Task Help_is_printed_with_no_arguments()
    {
        var run = await CliHarness.RunAsync(new FakeSpatialGateway());

        Assert.Equal(ExitCodes.Success, run.ExitCode);
        Assert.Contains("Usage: spatial", run.Output, StringComparison.Ordinal);
    }
}
