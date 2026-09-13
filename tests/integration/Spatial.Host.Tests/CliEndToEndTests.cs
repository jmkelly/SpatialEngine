using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Spatial.Cli;

namespace Spatial.Host.Tests;

/// <summary>
/// The Spatial CLI driven against a real host over its public HTTP API
/// (ADR-0052): ingest a file, publish a styled map, and prove the GeoServices
/// MapServer it projects is served — no Docker, no stub gateway.
/// </summary>
public sealed class CliEndToEndTests : IDisposable
{
    private const string Token = "cli-admin-token";

    private const string GeoJson = """
        {"type":"FeatureCollection","features":[
          {"type":"Feature","geometry":{"type":"Point","coordinates":[13.4,52.5]},"properties":{"name":"Berlin"}},
          {"type":"Feature","geometry":{"type":"Point","coordinates":[2.35,48.85]},"properties":{"name":"Paris"}}
        ]}
        """;

    private readonly string _directory = Directory.CreateTempSubdirectory("spatial-cli-").FullName;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Fact]
    public async Task Cli_ingests_and_serves_a_styled_map_against_the_real_host()
    {
        using var factory = new CliFactory(Path.Combine(_directory, "publications.json"));
        var geojson = Path.Combine(_directory, "points.geojson");
        await File.WriteAllTextAsync(geojson, GeoJson);

        var add = await RunAsync(
            factory,
            "dataset", "add", "--file", geojson, "--dataset", "public.cli_e2e",
            "--srid", "4326", "--store", "memory", "--identity", "auto", "--token", Token);
        Assert.Equal(ExitCodes.Success, add.ExitCode);

        var create = await RunAsync(
            factory,
            "map", "create", "--name", "CliMap", "--kind", "map", "--store", "memory",
            "--layer", "public.cli_e2e=Points", "--token", Token);
        Assert.Equal(ExitCodes.Success, create.ExitCode);

        var style = await RunAsync(
            factory,
            "map", "set-style", "--map", "CliMap", "--dataset", "public.cli_e2e",
            "--geometry", "point", "--color", "#ff0000", "--token", Token);
        Assert.Equal(ExitCodes.Success, style.ExitCode);

        // The endpoint the CLI reports really serves an ArcGIS MapServer.
        var export = await RunAsync(factory, "map", "export", "CliMap", "--format", "url");
        Assert.Equal(ExitCodes.Success, export.ExitCode);
        var endpoint = export.Output.Trim();
        Assert.EndsWith("/arcgis/rest/services/CliMap/MapServer", endpoint, StringComparison.Ordinal);

        using var client = factory.CreateClient();
        var response = await client.GetAsync(new Uri(endpoint + "?f=json"));
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Cli_applies_a_project_file_and_exports_it_back()
    {
        using var factory = new CliFactory(Path.Combine(_directory, "publications.json"));
        await File.WriteAllTextAsync(Path.Combine(_directory, "places.geojson"), GeoJson);
        var projectPath = Path.Combine(_directory, "spatial.json");
        await File.WriteAllTextAsync(projectPath, """
            {
              "version": 1,
              "datasets": [
                { "dataset": "public.proj", "srid": 4326, "source": "places.geojson", "format": "geojson", "identity": "auto" }
              ],
              "maps": [
                {
                  "name": "ProjMap",
                  "kind": "map",
                  "store": "memory",
                  "layers": [
                    { "dataset": "public.proj", "name": "Places", "geometry": "point", "style": { "color": "#00ff00" } }
                  ]
                }
              ]
            }
            """);

        var apply = await RunAsync(factory, "project", "apply", "--project", projectPath, "--store", "memory", "--token", Token);
        Assert.Equal(ExitCodes.Success, apply.ExitCode);

        // The declarative map is really served as a MapServer.
        using var client = factory.CreateClient();
        var response = await client.GetAsync("/arcgis/rest/services/ProjMap/MapServer?f=json");
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());

        var exportPath = Path.Combine(_directory, "exported.json");
        var export = await RunAsync(factory, "project", "export", "--project", exportPath, "--store", "memory");
        Assert.Equal(ExitCodes.Success, export.ExitCode);
        Assert.Contains("ProjMap", await File.ReadAllTextAsync(exportPath), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Cli_surfaces_a_host_error_as_its_structured_exit_code()
    {
        using var factory = new CliFactory(Path.Combine(_directory, "publications.json"));

        var run = await RunAsync(factory, "map", "show", "Missing");

        Assert.Equal(ExitCodes.NotFound, run.ExitCode);
        Assert.Contains("not.found", run.Error, StringComparison.Ordinal);
    }

    private static async Task<CliRun> RunAsync(WebApplicationFactory<Program> factory, params string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var code = await CliApplication.RunAsync(args, new TestConsole(output, error), _ => new HttpSpatialGateway(factory.CreateClient()));
        return new CliRun(code, output.ToString(), error.ToString());
    }

    private sealed record CliRun(int ExitCode, string Output, string Error);

    private sealed class TestConsole(TextWriter output, TextWriter error) : ICliConsole
    {
        public TextWriter Out { get; } = output;

        public TextWriter ErrorWriter { get; } = error;
    }

    private sealed class CliFactory(string mapsPath) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("Spatial:Admin:Token", Token);
            builder.UseSetting("Spatial:Maps:Path", mapsPath);
        }
    }
}
