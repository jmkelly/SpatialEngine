using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Spatial.Contracts;
using Spatial.Core.Features;
using Spatial.Core.Features.Query;
using Spatial.Core.Geometry;
using Spatial.QueryConformance;

namespace Spatial.Stores.ArcGisRest.Tests;

/// <summary>
/// The ArcGIS REST store's conformance with the shared pushdown-equals-reference
/// suite (ADR-0098 §3) — the fifth store to run it, and the only one whose rows
/// arrive over HTTP.
///
/// <para>
/// This provider is not a pushdown in the dialect sense: it has no SQL to push
/// a <c>GROUP BY</c> into, because the reduction happens on ArcGIS's side of
/// the wire or not at all. What it does have is a place where a plan can be
/// quietly answered wrongly — the plan's predicate and its box are rendered
/// into the remote service's own parameters, the remote pages the layer for
/// itself, and everything after that is the reference executor's judgement. A
/// remote that filtered, paged or ordered differently would hand back a
/// different row set, and the store would reduce that row set faithfully and
/// still be wrong.
/// </para>
///
/// <para>
/// So the suite runs against a fake FeatureServer: the conformance fixture's
/// rows served as Esri JSON, filtered and paged by the request parameters the
/// store actually sends. The fake is the remote dialect under test — it applies
/// <c>where</c>, the envelope and the <c>resultOffset</c> window, and throws
/// rather than ignoring a clause it does not implement, so a store that pushed
/// something new and unmodelled is a failing test here rather than a silently
/// unfiltered read in production.
/// </para>
/// </summary>
public sealed class ArcGisRestQueryConformanceTests
{
    [Fact]
    public async Task The_remote_store_matches_the_reference_over_the_conformance_fixture()
    {
        await QueryConformanceSuite.RunAsync(Store(), "arcgis.l0");
    }

    [Fact]
    public async Task The_fake_layer_declares_exactly_the_conformance_fixture()
    {
        // The fake's metadata and the shared fixture are two hand-written
        // descriptions of one dataset; this is what keeps them one dataset. A
        // field renamed or re-typed on one side only would quietly change which
        // field the suite sorts, groups and projects — and the suite would
        // still pass, over the wrong data.
        var schema = (await Store().DescribeAsync("arcgis.l0")).Schema;

        var attributes = schema.Fields.Where(field => field.Kind != AttributeKind.Geometry);
        Assert.Equal(
            QueryFixture.Schema.Fields.Where(field => field.Kind != AttributeKind.Geometry)
                .Select(field => (field.Name, field.Kind, field.Nullable)),
            attributes.Select(field => (field.Name, field.Kind, field.Nullable)));

        // The one deliberate difference: Esri carries geometry outside the
        // attribute object, so the remote layer has no such field to declare
        // and the store adds the engine's canonical, nullable geometry column.
        var geometry = Assert.Single(schema.Fields, field => field.Kind == AttributeKind.Geometry);
        Assert.Equal("geometry", geometry.Name);
        Assert.True(geometry.Nullable);
    }

    [Fact]
    public async Task The_fake_layer_is_the_spatial_reference_the_fixture_is_written_in()
    {
        Assert.Equal(4326, (await Store().DescribeAsync("arcgis.l0")).Srid);
    }

    /// <summary>
    /// The other half of the pushdown claim, and the half an answer-comparison
    /// cannot see: that the plan's restriction is asked of the remote at all.
    ///
    /// <para>
    /// The suite above compares answers, and an answer comparison is blind to
    /// <em>where</em> the work happened: a store that read the whole layer and
    /// dropped the rows itself returns exactly the reference's answer, over
    /// every row, which is the shallowness this bead exists to remove. So the
    /// restriction is pinned as a request: the predicate is rendered into the
    /// service's own <c>where</c>, the box becomes its <c>geometry</c>
    /// envelope, and the paging walk is served by <c>resultOffset</c> windows
    /// rather than by the engine re-slicing a materialised set.
    /// </para>
    /// </summary>
    [Fact]
    public async Task The_plan_s_restriction_is_asked_of_the_remote_rather_than_applied_locally()
    {
        var remote = FakeFeatureServer.Create();
        var store = new ArcGisRestStore(new HttpClient(remote), new ArcGisRestServiceOptions { Name = "remote", Url = FakeFeatureServer.BaseUrl });

        // A plan the remote can express end to end: a predicate, a box, an
        // order and a cap. Everything here should be in the request.
        await store.QueryAsync(
            "arcgis.l0",
            new FeatureQuery(
                Where: new Predicate.Compare(new FieldRef("score"), ComparisonOperator.GreaterOrEqual, Literal.FromInteger("20")),
                BoundingBox: QueryFixture.Box,
                Order: [new OrderTerm("score")],
                Limit: 2),
            CancellationToken.None);

        var queries = remote.Queries;
        Assert.NotEmpty(queries);

        // The predicate and the box crossed the wire in the service's own terms
        // — every page of the read carries them, not just the first.
        Assert.All(queries, page =>
        {
            Assert.Equal("score >= 20", page.GetValueOrDefault("where"));
            Assert.NotNull(page.GetValueOrDefault("geometry"));
        });

        // The sharpest evidence that the *remote* restricted rather than the
        // engine filtering afterwards: with the pushdown, the restricted read
        // is answered in a single window. Read the whole layer and drop the
        // rows locally instead and the same answer takes three — the page
        // count is the difference between pushing down and computing in
        // memory, and it is observable only from here.
        Assert.Equal(["0"], queries.Select(page => page["resultOffset"]));
        Assert.All(queries, page => Assert.Equal(FakeFeatureServer.PageSize.ToString(CultureInfo.InvariantCulture), page["resultRecordCount"]));
    }

    /// <summary>
    /// Paging is the remote's: an unrestricted read of the six-row fixture is
    /// served as a walk of <c>resultOffset</c> windows the store follows, each
    /// capped at the service's own <c>maxRecordCount</c>, rather than one
    /// generous read the engine slices afterwards. The offsets are the proof —
    /// they are the store asking the service for its next window, and the last
    /// one is reached only because the service said it had more.
    /// </summary>
    [Fact]
    public async Task Paging_is_the_service_s_own_window_walk_and_not_a_local_slice()
    {
        var remote = FakeFeatureServer.Create();
        var store = new ArcGisRestStore(new HttpClient(remote), new ArcGisRestServiceOptions { Name = "remote", Url = FakeFeatureServer.BaseUrl });

        await store.QueryAsync("arcgis.l0", FeatureQuery.All, CancellationToken.None);

        Assert.Equal(["0", "2", "4"], remote.Queries.Select(page => page["resultOffset"]));
    }

    /// <summary>A plan with nothing to push is asked without a restriction, so
    /// the absence of the parameters is itself pinned rather than assumed.</summary>
    [Fact]
    public async Task A_plan_with_no_predicate_and_no_box_asks_for_no_restriction()
    {
        var remote = FakeFeatureServer.Create();
        var store = new ArcGisRestStore(new HttpClient(remote), new ArcGisRestServiceOptions { Name = "remote", Url = FakeFeatureServer.BaseUrl });

        await store.QueryAsync("arcgis.l0", FeatureQuery.All, CancellationToken.None);

        Assert.All(remote.Queries, page =>
        {
            Assert.False(page.ContainsKey("where"));
            Assert.False(page.ContainsKey("geometry"));
        });
    }

    private static ArcGisRestStore Store() =>
        new(new HttpClient(FakeFeatureServer.Create()), new ArcGisRestServiceOptions { Name = "remote", Url = FakeFeatureServer.BaseUrl });

    /// <summary>
    /// A FeatureServer over the shared fixture: metadata plus a query endpoint
    /// that honours the three things the store pushes to the remote — the
    /// rendered <c>where</c>, the envelope, and the <c>resultOffset</c> /
    /// <c>resultRecordCount</c> window — and echoes <c>exceededTransferLimit</c>
    /// so the store's own remote paging is exercised rather than short-circuited
    /// by a single generous page.
    /// </summary>
    private sealed class FakeFeatureServer : HttpMessageHandler
    {
        public const string BaseUrl = "https://example.com/arcgis/rest/services/fixture/FeatureServer";

        /// <summary>Two rows a page, so every read of the six-row fixture is three
        /// round trips and a dropped or repeated row is a red test.</summary>
        internal const int PageSize = 2;

        private const string Metadata = """
            {
              "id": 0,
              "name": "Fixture",
              "type": "Feature Layer",
              "geometryType": "esriGeometryPoint",
              "objectIdField": "id",
              "spatialReference": { "wkid": 4326 },
              "maxRecordCount": 2,
              "fields": [
                { "name": "id", "type": "esriFieldTypeOID", "nullable": false },
                { "name": "category", "type": "esriFieldTypeString", "nullable": true },
                { "name": "score", "type": "esriFieldTypeInteger", "nullable": true },
                { "name": "ratio", "type": "esriFieldTypeDouble", "nullable": true },
                { "name": "name", "type": "esriFieldTypeString", "nullable": false }
              ]
            }
            """;

        private static readonly Regex Comparison = new(
            @"^(?<field>[A-Za-z_][A-Za-z0-9_]*)\s*(?<operator>>=|<>|<=|>|<|=)\s*(?<value>-?[0-9.eE+]+)$",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        private static readonly Regex NullTest = new(
            @"^(?<field>[A-Za-z_][A-Za-z0-9_]*)\s+IS\s+(?<negated>NOT\s+)?NULL$",
            RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

        private readonly IReadOnlyList<Feature> _features = QueryFixture.Features;

        /// <summary>Every query request the store issued, in order, so a test
        /// can assert what was asked of the remote rather than only what came
        /// back.</summary>
        public List<Dictionary<string, string>> Queries { get; } = [];

        public static FakeFeatureServer Create() => new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (!path.EndsWith("/query", StringComparison.Ordinal))
            {
                return Task.FromResult(Json(path.EndsWith("/0", StringComparison.Ordinal) ? Metadata : """{"layers":[],"tables":[]}"""));
            }

            var parameters = Parameters(request.RequestUri);
            Queries.Add(parameters);
            return Task.FromResult(Query(parameters));
        }

        private HttpResponseMessage Query(Dictionary<string, string> parameters)
        {
            var rows = _features
                .Where(Where(parameters.GetValueOrDefault("where")))
                .Where(InEnvelope(parameters))
                .ToList();
            var offset = Integer(parameters, "resultOffset");
            var count = Integer(parameters, "resultRecordCount", PageSize);
            var page = rows.Skip(offset).Take(count).ToList();
            var body = new JsonObject
            {
                ["features"] = new JsonArray(page.Select(Feature).ToArray()),
                ["exceededTransferLimit"] = offset + page.Count < rows.Count,
            };
            return Json(body.ToJsonString());
        }

        /// <summary>
        /// The row predicate for the request's rendered <c>where</c>: the two
        /// clause shapes the shared suite asks of a remote, a numeric comparison
        /// and an <c>IS NULL</c> test. A clause the fake has not been taught
        /// throws rather than being dropped, because a fake that ignored one
        /// would hand the store more rows than the plan asked for and hide the
        /// very pushdown the suite exists to pin.
        /// </summary>
        private static Func<Feature, bool> Where(string? where)
        {
            if (string.IsNullOrWhiteSpace(where))
            {
                return _ => true;
            }

            var nullTest = NullTest.Match(where);
            if (nullTest.Success)
            {
                var name = nullTest.Groups["field"].Value;
                var negated = nullTest.Groups["negated"].Success;
                return feature => feature[name].IsNull != negated;
            }

            var comparison = Comparison.Match(where);
            if (!comparison.Success)
            {
                throw new InvalidOperationException($"The fake FeatureServer does not implement the where clause '{where}'.");
            }

            var field = comparison.Groups["field"].Value;
            var bound = double.Parse(comparison.Groups["value"].Value, CultureInfo.InvariantCulture);
            var op = comparison.Groups["operator"].Value;
            return feature => !feature[field].IsNull && Compare(
                feature[field].Kind == AttributeKind.Int64 ? feature[field].Int64Value : feature[field].DoubleValue,
                op,
                bound);
        }

        private static bool Compare(double value, string op, double bound) => op switch
        {
            ">" => value > bound,
            ">=" => value >= bound,
            "<" => value < bound,
            "<=" => value <= bound,
            "=" => value == bound,
            "<>" => value != bound,
            _ => throw new InvalidOperationException($"The fake FeatureServer does not implement the operator '{op}'."),
        };

        /// <summary>The rows whose point falls in the request's envelope, or every
        /// row when the request names no geometry.</summary>
        private static Func<Feature, bool> InEnvelope(Dictionary<string, string> parameters)
        {
            if (!parameters.TryGetValue("geometry", out var geometry))
            {
                return _ => true;
            }

            var json = JsonNode.Parse(geometry)!.AsObject();
            var box = new BoundingBox(
                json["xmin"]!.GetValue<double>(),
                json["ymin"]!.GetValue<double>(),
                json["xmax"]!.GetValue<double>(),
                json["ymax"]!.GetValue<double>());
            return feature =>
            {
                var envelope = feature["shape"].GeometryValue.Envelope!.Value;
                return envelope.MaxX >= box.MinX && envelope.MinX <= box.MaxX
                    && envelope.MaxY >= box.MinY && envelope.MinY <= box.MaxY;
            };
        }

        /// <summary>A fixture row as Esri JSON: its attributes and its point.</summary>
        private static JsonNode Feature(Feature feature)
        {
            var attributes = new JsonObject();
            foreach (var field in QueryFixture.Schema.Fields.Where(field => field.Kind != AttributeKind.Geometry))
            {
                attributes[field.Name] = Value(feature[field.Name]);
            }

            var point = ((Point)feature["shape"].GeometryValue).Coordinate!.Value;
            return new JsonObject
            {
                ["attributes"] = attributes,
                ["geometry"] = new JsonObject { ["x"] = point.X, ["y"] = point.Y },
            };
        }

        private static JsonValue? Value(AttributeValue value) => value.Kind switch
        {
            AttributeKind.Int64 => JsonValue.Create(value.Int64Value),
            AttributeKind.Double => JsonValue.Create(value.DoubleValue),
            AttributeKind.String => JsonValue.Create(value.StringValue),
            _ => null,
        };

        private static int Integer(Dictionary<string, string> parameters, string name, int fallback = 0) =>
            parameters.TryGetValue(name, out var value)
            && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : fallback;

        /// <summary>The request's query string, unescaped — the store builds it
        /// with <see cref="Uri.EscapeDataString(string)"/>, so this is the exact
        /// inverse.</summary>
        private static Dictionary<string, string> Parameters(Uri uri)
        {
            var parameters = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var separator = pair.IndexOf('=', StringComparison.Ordinal);
                if (separator > 0)
                {
                    parameters[Uri.UnescapeDataString(pair[..separator])] = Uri.UnescapeDataString(pair[(separator + 1)..]);
                }
            }

            return parameters;
        }

        private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
            new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }
}
