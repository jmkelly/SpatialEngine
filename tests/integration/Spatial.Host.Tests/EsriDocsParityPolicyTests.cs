using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Spatial.Adapter.GeoServices;

namespace Spatial.Host.Tests;

/// <summary>
/// Slice D part 3 (T-071): the strict-vs-lenient parity policy
/// (<c>tests/fixtures/esri-docs/policy.json</c>). Strict items must match
/// the Esri docs modulo the scaled tolerance — a red strict replay is a
/// bug. Lenient-documented items are intentional, explained deltas (engine
/// rows instead of sample rows, engine-native renders, ProjNet-vs-PE,
/// GPServer omission): each must carry <c>knownDeltas</c> in its fixture
/// and a live pin named in the policy. This class enforces the policy
/// mechanically: full coverage of every fixture case, non-empty rules and
/// deltas, and live pins for the catalog scope (no unserved types) and the
/// 499 cancellation contract (mapper-pinned, not HTTP-replayable).
/// </summary>
public sealed class EsriDocsParityPolicyTests : IClassFixture<WebApplicationFactory<Program>>
{
    private static readonly string CorpusDir =
        Path.Combine(AppContext.BaseDirectory, "esri-docs-fixtures");

    private static readonly JsonElement Policy =
        JsonDocument.Parse(File.ReadAllText(Path.Combine(CorpusDir, "policy.json"))).RootElement;

    private static readonly string[] Suites = ["geometryserver", "featureserver", "mapserver", "imageserver", "edgecases"];

    private readonly HttpClient _client;

    public EsriDocsParityPolicyTests(WebApplicationFactory<Program> factory) => _client = factory.CreateClient();

    /// <summary>Every fixture case in every suite is classified strict or lenient — no orphans either way.</summary>
    [Fact]
    public void Every_fixture_case_is_classified_and_every_classification_resolves()
    {
        var coverage = Policy.GetProperty("coverage");
        var cases = AllCases().ToArray();

        foreach (var (suite, name) in cases)
        {
            Assert.True(
                coverage.TryGetProperty($"{suite}/{name}", out var disposition),
                $"Case '{suite}/{name}' has no strict-vs-lenient classification in policy.json.");
            var text = disposition.GetString();
            Assert.True(text is "strict" or "lenient", $"Case '{suite}/{name}' has an unknown disposition '{text}'.");
        }

        var known = cases.Select(c => $"{c.Suite}/{c.Name}").ToHashSet(StringComparer.Ordinal);
        foreach (var entry in coverage.EnumerateObject())
        {
            Assert.Contains(entry.Name, known);
        }
    }

    /// <summary>Every lenient-documented case explains itself in knownDeltas; every policy row is non-vacuous.</summary>
    [Fact]
    public void Lenient_cases_carry_deltas_and_policy_rows_carry_text()
    {
        var coverage = Policy.GetProperty("coverage");
        foreach (var (suite, name) in AllCases())
        {
            if (!string.Equals(coverage.GetProperty($"{suite}/{name}").GetString(), "lenient", StringComparison.Ordinal))
            {
                continue;
            }

            var fixture = ReadFixture(suite, name);
            Assert.NotEmpty(fixture.GetProperty("knownDeltas").EnumerateArray());
        }

        foreach (var strict in Policy.GetProperty("strict").EnumerateArray())
        {
            Assert.False(string.IsNullOrWhiteSpace(strict.GetProperty("id").GetString()));
            Assert.False(string.IsNullOrWhiteSpace(strict.GetProperty("rule").GetString()));
            Assert.False(string.IsNullOrWhiteSpace(strict.GetProperty("pinnedBy").GetString()));
        }

        foreach (var lenient in Policy.GetProperty("lenient").EnumerateArray())
        {
            Assert.False(string.IsNullOrWhiteSpace(lenient.GetProperty("id").GetString()));
            Assert.False(string.IsNullOrWhiteSpace(lenient.GetProperty("delta").GetString()));
            Assert.False(string.IsNullOrWhiteSpace(lenient.GetProperty("pinnedBy").GetString()));
        }
    }

    /// <summary>
    /// Live pin for the geoprocessing-omitted lenient item: the catalog
    /// serves only served types — GPServer entries from the ground truth
    /// are honestly omitted, never half-served.
    /// </summary>
    [Fact]
    public async Task The_catalog_omits_unserved_types_as_the_policy_claims()
    {
        var response = await _client.GetAsync("/arcgis/rest/services?f=json");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var services = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement
            .GetProperty("services").EnumerateArray()
            .Select(service => service.GetProperty("type").GetString() ?? string.Empty)
            .ToArray();

        Assert.NotEmpty(services);
        Assert.DoesNotContain(services, type => string.Equals(type, "GPServer", StringComparison.Ordinal));
    }

    /// <summary>
    /// Live pin for the 499 error-code contract: cancellation maps to the
    /// 499 envelope. A cancelled HTTP request never yields a response to
    /// diff, so this contract is mapper-pinned rather than fixture-replayed
    /// (policy.json error-codes).
    /// </summary>
    [Fact]
    public async Task Cancellation_maps_to_the_499_envelope_as_the_policy_claims()
    {
        var (status, body) = await ExecuteAsync(EsriErrorMapper.Map(new OperationCanceledException()));

        Assert.Equal(StatusCodes.Status499ClientClosedRequest, status);
        Assert.Equal(499, body.GetProperty("error").GetProperty("code").GetInt32());
    }

    private static IEnumerable<(string Suite, string Name)> AllCases()
    {
        foreach (var suite in Suites)
        {
            var manifest = JsonDocument.Parse(
                File.ReadAllText(Path.Combine(CorpusDir, suite, "manifest.json"))).RootElement;
            foreach (var entry in manifest.GetProperty("cases").EnumerateArray())
            {
                yield return (suite, entry.GetProperty("name").GetString()!);
            }
        }
    }

    private static JsonElement ReadFixture(string suite, string name)
    {
        var manifest = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(CorpusDir, suite, "manifest.json"))).RootElement;
        var entry = manifest.GetProperty("cases").EnumerateArray()
            .Single(candidate => candidate.GetProperty("name").GetString() == name);
        return JsonDocument.Parse(
            File.ReadAllText(Path.Combine(CorpusDir, suite, entry.GetProperty("file").GetString()!))).RootElement;
    }

    private static async Task<(int Status, JsonElement Body)> ExecuteAsync(IResult result)
    {
        var context = new DefaultHttpContext();
        context.RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider();
        context.Response.Body = new MemoryStream();
        await result.ExecuteAsync(context);
        context.Response.Body.Position = 0;
        using var document = await JsonDocument.ParseAsync(context.Response.Body);
        return (context.Response.StatusCode, document.RootElement.Clone());
    }
}
