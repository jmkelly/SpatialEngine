using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Spatial.PluginSdk;
using Spatial.PluginSdk.Providers;

namespace Spatial.Host.Tests;

/// <summary>
/// Slice D part 2 (T-071): edge-case replays. Every unhappy path the Esri
/// docs imply gets a first-class fixture in
/// <c>tests/fixtures/esri-docs/edgecases/</c>: empty geometry, unknown
/// WKID, out-of-area project, invalid SQL, <c>f=html</c> rejection, the 498
/// token contract and <c>rollbackOnFailure</c>. The suite replays each
/// fixture's method + path + query (+ form for POST) verbatim against a
/// host carrying an admin token and the writable <c>editable</c> service,
/// and applies the shared semantic diff
/// (<see cref="EsriDocsReplayTests.Compare"/>) or the error-contract check.
/// No behaviour was changed for this slice: fixtures pin honest answers in
/// <c>expect</c>/<c>expectError</c> and explain intentional deltas in
/// <c>knownDeltas</c>.
/// </summary>
/// <remarks>
/// The 499 cancellation contract is mapper-pinned, not HTTP-replayable: a
/// cancelled HTTP request never yields a response to diff, so the contract
/// (cancellation maps to the 499 envelope) is pinned live by
/// <see cref="EsriDocsParityPolicyTests"/> through the error mapper. See
/// <c>policy.json</c> (error-codes).
/// </remarks>
public sealed class EsriDocsEdgeCaseReplayTests : IDisposable
{
    private const string Token = "test-admin-token";

    private static readonly string FixtureDir =
        Path.Combine(AppContext.BaseDirectory, "esri-docs-fixtures", "edgecases");

    private static readonly JsonElement Manifest =
        JsonDocument.Parse(File.ReadAllText(Path.Combine(FixtureDir, "manifest.json"))).RootElement;

    private static readonly double Tolerance = Manifest.GetProperty("tolerance").GetDouble();

    private readonly string _directory = Directory.CreateTempSubdirectory("spatial-esri-edge-").FullName;
    private readonly WebApplicationFactory<Program> _factory;

    public EsriDocsEdgeCaseReplayTests() => _factory = new EdgeFactory(_directory, Token);

    public void Dispose()
    {
        _factory.Dispose();
        Directory.Delete(_directory, recursive: true);
    }

    public static IEnumerable<object[]> CaseNames() =>
        Manifest.GetProperty("cases").EnumerateArray()
            .Select(entry => new object[] { entry.GetProperty("name").GetString()! });

    /// <summary>Replays the fixture's verbatim method/path/query/form and checks the answer.</summary>
    [Theory]
    [MemberData(nameof(CaseNames))]
    public async Task Edge_case_replays_against_our_host(string name)
    {
        using var client = _factory.CreateClient();
        var fixture = ReadFixture(name);
        var request = fixture.GetProperty("request");
        var path = request.GetProperty("path").GetString()!;
        var query = request.TryGetProperty("query", out var queryElement) ? queryElement.GetString()! : string.Empty;
        var url = string.IsNullOrEmpty(query) ? path : $"{path}?{query}";
        var method = request.GetProperty("method").GetString()!;

        var response = method switch
        {
            "GET" => await client.GetAsync(url),
            "PUT" => await client.PutAsync(url, new StringContent(string.Empty)),
            "POST" => await client.PostAsync(url, Form(request)),
            _ => throw new InvalidOperationException($"Fixture '{name}' names an unsupported method '{method}'."),
        };
        Assert.Equal(fixture.GetProperty("expectStatus").GetInt32(), (int)response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        if (fixture.TryGetProperty("expectError", out var expectError))
        {
            var error = body.RootElement.GetProperty("error");
            Assert.Equal(expectError.GetProperty("code").GetInt32(), error.GetProperty("code").GetInt32());
            Assert.Contains(
                expectError.GetProperty("messageContains").GetString()!,
                error.GetProperty("message").GetString()!);
            return;
        }

        var deltas = new List<string>();
        EsriDocsReplayTests.Compare(fixture.GetProperty("expect"), body.RootElement, "$", deltas, Tolerance);
        Assert.True(
            deltas.Count == 0,
            $"Semantic diff for '{name}' ({deltas.Count} deltas):\n{string.Join("\n", deltas.Take(10))}");
    }

    /// <summary>The corpus loop for slice D part 2: every edge facet has a case.</summary>
    [Fact]
    public void The_edgecases_manifest_covers_the_slice()
    {
        string[] required =
        [
            "emptyGeometry", "unknownWkid", "outOfAreaProject", "invalidSql",
            "formatReject", "tokenRequired", "rollbackOnFailure",
        ];
        var cases = Manifest.GetProperty("cases").EnumerateArray().ToArray();
        foreach (var facet in required)
        {
            Assert.Contains(cases, entry =>
                string.Equals(entry.GetProperty("covers").GetString(), facet, StringComparison.Ordinal));
        }

        foreach (var entry in cases)
        {
            var fixture = ReadFixture(entry.GetProperty("name").GetString()!);
            Assert.False(string.IsNullOrWhiteSpace(fixture.GetProperty("source").GetString()));
            Assert.False(string.IsNullOrWhiteSpace(fixture.GetProperty("notes").GetString()));
            Assert.NotEmpty(fixture.GetProperty("knownDeltas").EnumerateArray());
        }
    }

    private static FormUrlEncodedContent Form(JsonElement request)
    {
        var form = request.GetProperty("form");
        return new FormUrlEncodedContent(
            form.EnumerateObject().Select(property =>
                new KeyValuePair<string, string>(property.Name, property.Value.GetString()!)));
    }

    private static JsonElement ReadFixture(string name)
    {
        var entry = Manifest.GetProperty("cases").EnumerateArray()
            .Single(candidate => candidate.GetProperty("name").GetString() == name);
        return JsonDocument.Parse(
            File.ReadAllText(Path.Combine(FixtureDir, entry.GetProperty("file").GetString()!))).RootElement;
    }

    /// <summary>Admin token plus the writable <c>editable</c> service (fresh store per test).</summary>
    private sealed class EdgeFactory(string directory, string token) : WebApplicationFactory<Program>
    {
        private readonly WritableMemoryStore _store = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("Spatial:Admin:Token", token);
            builder.UseSetting("Spatial:Maps:Path", Path.Combine(directory, "publications.json"));
            builder.ConfigureAppConfiguration((_, configuration) =>
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Spatial:GeoServices:Services:1:Name"] = "editable",
                    ["Spatial:GeoServices:Services:1:Store"] = "editable",
                    ["Spatial:GeoServices:Services:1:Type"] = "FeatureServer",
                }));
            builder.ConfigureServices(services =>
            {
                services.AddKeyedSingleton<IDataCatalogue>("editable", _store);
                services.AddKeyedSingleton<IFeatureStore>("editable", _store);
                services.AddKeyedSingleton<IFeatureEditStore>("editable", new WritableMemoryEditor(_store));
                services.AddKeyedSingleton<ITransactionStore>("editable", _store);
            });
        }
    }
}
