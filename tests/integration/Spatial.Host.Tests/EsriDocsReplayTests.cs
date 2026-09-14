using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Spatial.Host.Tests;

/// <summary>
/// The Esri-docs parity replay suite, slices A+B: GeometryServer (T-064)
/// and FeatureServer query (T-069).
/// Every case in <c>tests/fixtures/esri-docs/&lt;suite&gt;/</c> carries a
/// doc-pattern request (method + path + wire-encoded query string) and the
/// Esri-shaped reference response; the suite replays the stored query string
/// unchanged against our host and applies a semantic JSON diff against the
/// recorded <c>expect</c> body. Numbers (including geometry coordinates)
/// compare with scaled tolerance <c>1e-6</c>:
/// <c>|a-b| &lt;= tol * max(1,|a|,|b|)</c>, so large projected coordinates
/// do not fail on float formatting noise.
/// No Docker, no network: <see cref="WebApplicationFactory{TEntryPoint}"/>.
/// Later slices reuse this harness: MapServer (T-070),
/// ImageServer + edge-cases (T-071).
/// </summary>
/// <remarks>
/// ProjNet-vs-Esri-PE note: <c>project</c> and <c>outSR</c> replays agree
/// within the scaled tolerance because both engines evaluate the same
/// closed-form projections inside one CRS family; datum steps are
/// deliberately out of scope (the engine ships no datum tables — see the
/// fixtures README and <c>findTransformations</c>). Where the host honestly
/// differs (input aliases, arc segmentation, sample-vs-demo rows) the
/// fixture records the delta in <c>knownDeltas</c> instead of chasing
/// parity; behaviour changes are never smuggled in through this suite.
/// Slice B (T-069) replays FeatureServer query over the fixed 8-city demo
/// snapshot (layer 0): the wire shape is Esri's, the rows are ours, and no
/// FeatureServer behaviour was changed to satisfy the suite.
/// </remarks>
public sealed class EsriDocsReplayTests : IClassFixture<WebApplicationFactory<Program>>
{
    private static readonly string FixtureDir =
        Path.Combine(AppContext.BaseDirectory, "esri-docs-fixtures", "geometryserver");

    private static readonly string FeatureFixtureDir =
        Path.Combine(AppContext.BaseDirectory, "esri-docs-fixtures", "featureserver");

    private static readonly JsonElement Manifest =
        JsonDocument.Parse(File.ReadAllText(Path.Combine(FixtureDir, "manifest.json"))).RootElement;

    private static readonly JsonElement FeatureManifest =
        JsonDocument.Parse(File.ReadAllText(Path.Combine(FeatureFixtureDir, "manifest.json"))).RootElement;

    private static readonly double Tolerance = Manifest.GetProperty("tolerance").GetDouble();

    private static readonly double FeatureTolerance = FeatureManifest.GetProperty("tolerance").GetDouble();

    private readonly HttpClient _client;

    public EsriDocsReplayTests(WebApplicationFactory<Program> factory) => _client = factory.CreateClient();

    public static IEnumerable<object[]> CaseNames() =>
        Manifest.GetProperty("cases").EnumerateArray()
            .Select(entry => new object[] { entry.GetProperty("name").GetString()! });

    /// <summary>Replays the fixture's verbatim query string and diffs the answer.</summary>
    [Theory]
    [MemberData(nameof(CaseNames))]
    public async Task Docs_request_replays_against_our_host(string name)
    {
        var fixture = ReadFixture(name);
        var request = fixture.GetProperty("request");
        var url = $"{request.GetProperty("path").GetString()}?{request.GetProperty("query").GetString()}";

        var response = await _client.GetAsync(url);
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
        Compare(fixture.GetProperty("expect"), body.RootElement, "$", deltas, Tolerance);
        Assert.True(
            deltas.Count == 0,
            $"Semantic diff for '{name}' ({deltas.Count} deltas):\n{string.Join("\n", deltas.Take(10))}");
    }

    /// <summary>The corpus loop: the manifest covers the whole slice.</summary>
    [Fact]
    public void The_manifest_covers_every_geometry_server_operation()
    {
        string[] required = [
            "buffer", "project", "simplify", "areasAndLengths", "lengths",
            "generalize", "intersect", "union", "convexHull", "labelPoints",
        ];
        var cases = Manifest.GetProperty("cases").EnumerateArray().ToArray();
        foreach (var operation in required)
        {
            Assert.Contains(cases, entry =>
                string.Equals(entry.GetProperty("operation").GetString(), operation, StringComparison.Ordinal));
        }

        foreach (var entry in cases)
        {
            var fixture = ReadFixture(entry.GetProperty("name").GetString()!);
            Assert.False(string.IsNullOrWhiteSpace(fixture.GetProperty("source").GetString()));
            Assert.False(string.IsNullOrWhiteSpace(fixture.GetProperty("notes").GetString()));
            Assert.NotEmpty(fixture.GetProperty("knownDeltas").EnumerateArray());
        }
    }

    /// <summary>The recorded Esri answers are intact reference data.</summary>
    [Theory]
    [MemberData(nameof(CaseNames))]
    public void The_recorded_esri_response_has_its_documented_shape(string name)
    {
        var fixture = ReadFixture(name);
        var esri = fixture.GetProperty("esriResponse");
        if (fixture.GetProperty("expectStatus").GetInt32() == (int)HttpStatusCode.OK)
        {
            Assert.True(
                esri.TryGetProperty("geometries", out _) || esri.TryGetProperty("areas", out _)
                || esri.TryGetProperty("lengths", out _) || esri.TryGetProperty("capabilities", out _),
                $"Case '{name}' records no recognisable Esri answer shape.");
        }
    }

    private static JsonElement ReadFixture(string name)
    {
        var entry = Manifest.GetProperty("cases").EnumerateArray()
            .Single(candidate => candidate.GetProperty("name").GetString() == name);
        return JsonDocument.Parse(
            File.ReadAllText(Path.Combine(FixtureDir, entry.GetProperty("file").GetString()!))).RootElement;
    }

    public static IEnumerable<object[]> FeatureCaseNames() =>
        FeatureManifest.GetProperty("cases").EnumerateArray()
            .Select(entry => new object[] { entry.GetProperty("name").GetString()! });

    /// <summary>
    /// Slice B (T-069): replays the fixture's verbatim FeatureServer query
    /// string and diffs the answer with the same semantic JSON diff.
    /// </summary>
    [Theory]
    [MemberData(nameof(FeatureCaseNames))]
    public async Task Feature_docs_request_replays_against_our_host(string name)
    {
        var fixture = ReadFeatureFixture(name);
        var request = fixture.GetProperty("request");
        var url = $"{request.GetProperty("path").GetString()}?{request.GetProperty("query").GetString()}";

        var response = await _client.GetAsync(url);
        Assert.Equal(fixture.GetProperty("expectStatus").GetInt32(), (int)response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        var deltas = new List<string>();
        Compare(fixture.GetProperty("expect"), body.RootElement, "$", deltas, FeatureTolerance);
        Assert.True(
            deltas.Count == 0,
            $"Semantic diff for '{name}' ({deltas.Count} deltas):\n{string.Join("\n", deltas.Take(10))}");
    }

    /// <summary>The corpus loop for slice B: every query facet has a case.</summary>
    [Fact]
    public void The_featureserver_manifest_covers_the_query_slice()
    {
        string[] required =
        [
            "where", "objectIds", "geometry", "outSR", "returnGeometry",
            "orderByFields", "paging", "outStatistics", "validateSQL",
        ];
        var cases = FeatureManifest.GetProperty("cases").EnumerateArray().ToArray();
        foreach (var facet in required)
        {
            Assert.Contains(cases, entry =>
                string.Equals(entry.GetProperty("covers").GetString(), facet, StringComparison.Ordinal));
        }

        Assert.Contains(cases, entry =>
            string.Equals(entry.GetProperty("operation").GetString(), "validateSQL", StringComparison.Ordinal));

        foreach (var entry in cases)
        {
            var fixture = ReadFeatureFixture(entry.GetProperty("name").GetString()!);
            Assert.False(string.IsNullOrWhiteSpace(fixture.GetProperty("source").GetString()));
            Assert.False(string.IsNullOrWhiteSpace(fixture.GetProperty("notes").GetString()));
            Assert.NotEmpty(fixture.GetProperty("knownDeltas").EnumerateArray());
        }
    }

    /// <summary>The recorded Esri query answers carry the documented shape.</summary>
    [Theory]
    [MemberData(nameof(FeatureCaseNames))]
    public void The_recorded_feature_esri_response_has_its_documented_shape(string name)
    {
        var fixture = ReadFeatureFixture(name);
        var esri = fixture.GetProperty("esriResponse");
        Assert.True(
            esri.TryGetProperty("features", out _) || esri.TryGetProperty("isValidSQL", out _),
            $"Case '{name}' records no recognisable Esri query answer shape.");
    }

    private static JsonElement ReadFeatureFixture(string name)
    {
        var entry = FeatureManifest.GetProperty("cases").EnumerateArray()
            .Single(candidate => candidate.GetProperty("name").GetString() == name);
        return JsonDocument.Parse(
            File.ReadAllText(Path.Combine(FeatureFixtureDir, entry.GetProperty("file").GetString()!))).RootElement;
    }

    private static void Compare(JsonElement expected, JsonElement actual, string path, List<string> deltas, double tolerance)
    {
        if (expected.ValueKind == JsonValueKind.Number && actual.ValueKind == JsonValueKind.Number)
        {
            var want = expected.GetDouble();
            var got = actual.GetDouble();
            var allowed = tolerance * Math.Max(1.0, Math.Max(Math.Abs(want), Math.Abs(got)));
            if (Math.Abs(want - got) > allowed)
            {
                deltas.Add($"{path}: expected {want} but got {got} (allowed {allowed}).");
            }

            return;
        }

        if (expected.ValueKind != actual.ValueKind)
        {
            deltas.Add($"{path}: expected {expected.ValueKind} but got {actual.ValueKind}.");
            return;
        }

        switch (expected.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in expected.EnumerateObject())
                {
                    if (!actual.TryGetProperty(property.Name, out var child))
                    {
                        deltas.Add($"{path}.{property.Name}: missing in host answer.");
                    }
                    else
                    {
                        Compare(property.Value, child, $"{path}.{property.Name}", deltas, tolerance);
                    }
                }

                break;
            case JsonValueKind.Array:
                var wantItems = expected.EnumerateArray().ToArray();
                var gotItems = actual.EnumerateArray().ToArray();
                if (wantItems.Length != gotItems.Length)
                {
                    deltas.Add($"{path}: expected {wantItems.Length} items but got {gotItems.Length}.");
                    return;
                }

                for (var i = 0; i < wantItems.Length; i++)
                {
                    Compare(wantItems[i], gotItems[i], $"{path}[{i}]", deltas, tolerance);
                }

                break;
            default:
                if (expected.ToString() != actual.ToString())
                {
                    deltas.Add($"{path}: expected {expected} but got {actual}.");
                }

                break;
        }
    }
}
