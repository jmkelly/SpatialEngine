using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Spatial.Client;
using Spatial.Contracts;
using Spatial.Contracts.Http;
using Spatial.Core.Features;
using Spatial.Core.Features.Query;
using Spatial.Core.Geometry;

namespace Spatial.Host.Tests;

/// <summary>
/// The feature-query plan over HTTP (ADR-0158): the JSON plan accepted on
/// <c>POST /api/features/query</c>, the page it answers with, and the filter
/// text that still answers the same page beside it — driven through the .NET
/// client SDK against the real host and the always available memory store.
/// Each test seeds its own <c>public.plan_&lt;guid&gt;</c> dataset, so one
/// host serves the whole class (ADR-0160, ADR-0161) without the shared memory
/// store leaking a dataset between tests.
/// </remarks>
public sealed class FeatureQueryPlanTests : IClassFixture<FeatureQueryPlanTests.QueryHost>
{
    private const string Store = "memory";

    private readonly QueryHost _host;

    public FeatureQueryPlanTests(QueryHost host) => _host = host;

    private static readonly FeatureSchema Schema = new(
    [
        new FieldDefinition("name", AttributeKind.String),
        new FieldDefinition("population", AttributeKind.Int64),
        new FieldDefinition("geometry", AttributeKind.Geometry),
    ]);

    /// <summary>
    /// The dataset every test reads: five named places, deliberately not in
    /// insertion order by name and spread over the globe, so an ordering that
    /// is silently ignored shows up as a sequence difference and a bbox that is
    /// shows up as a count.
    /// </summary>
    private static FeatureBatch Places() => new(
        Schema,
        [
            Feature(1, "Berlin", 3_600_000, 13.4, 52.5),
            Feature(2, "Amsterdam", 900_000, 4.9, 52.4),
            Feature(3, "Cairo", 10_000_000, 31.2, 30.0),
            Feature(4, "Dakar", 1_300_000, -17.4, 14.7),
            Feature(5, "Bergen", 280_000, 5.3, 60.4),
        ]);

    private static Feature Feature(int id, string name, long population, double x, double y) =>
        new(
            new FeatureId(id.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            Schema,
            [
                AttributeValue.FromString(name),
                AttributeValue.FromInt64(population),
                AttributeValue.FromGeometry(GeometryFactory.CreatePoint(x, y)),
            ]);

    private static SpatialClient Client(HttpClient http) => new(http);

    private static async Task<string> SeedAsync(SpatialClient client)
    {
        var dataset = $"public.plan_{Guid.NewGuid():N}";
        await client.CreateDatasetAsync(dataset, Places(), 4326, Store);
        await client.WriteAsync(dataset, Places(), null, Store);
        return dataset;
    }

    [Fact]
    public async Task The_plan_spelling_orders_pages_and_reports_the_page()
    {
        var client = Client(_host.Client);
        var dataset = await SeedAsync(client);

        var page = await client.QueryPlanAsync(dataset, new FeatureQuery(
            Order: [new OrderTerm("name")],
            Limit: 2), Store);

        Assert.Equal(["Amsterdam", "Bergen"], page.Features.Select(feature => feature.Attributes[0].StringValue));
        Assert.Equal(5, page.TotalCount);
        Assert.True(page.HasMore);
        Assert.NotNull(page.NextCursor);
    }

    [Fact]
    public async Task The_plan_spelling_filters_by_a_predicate_tree_and_projects()
    {
        var client = Client(_host.Client);
        var dataset = await SeedAsync(client);

        var page = await client.QueryPlanAsync(dataset, new FeatureQuery(
            Where: new Predicate.Compare(new FieldRef("population"), ComparisonOperator.GreaterOrEqual, Literal.FromInteger("1000000")),
            Projection: ["name"],
            Order: [new OrderTerm("population", SortDirection.Descending)]), Store);

        Assert.Equal(["Cairo", "Berlin", "Dakar"], page.Features.Select(feature => feature.Attributes[0].StringValue));
        Assert.Single(page.Batches[0].Schema.Fields);
        Assert.Equal("name", page.Batches[0].Schema.Fields[0].Name);
    }

    [Fact]
    public async Task The_cursor_continues_the_same_plan()
    {
        var client = Client(_host.Client);
        var dataset = await SeedAsync(client);
        var plan = new FeatureQuery(Order: [new OrderTerm("name")], Limit: 2);

        var first = await client.QueryPlanAsync(dataset, plan, Store);
        var second = await client.QueryPlanAsync(dataset, plan with { Cursor = first.NextCursor, Offset = null }, Store);
        var last = await client.QueryPlanAsync(dataset, plan with { Cursor = second.NextCursor, Offset = null }, Store);

        Assert.Equal(["Amsterdam", "Bergen"], Names(first));
        Assert.Equal(["Berlin", "Cairo"], Names(second));
        Assert.Equal(["Dakar"], Names(last));
        Assert.False(last.HasMore);
        Assert.Null(last.NextCursor);
    }

    [Fact]
    public async Task The_filter_text_and_the_plan_return_the_same_page()
    {
        var client = Client(_host.Client);
        var dataset = await SeedAsync(client);

        var text = await client.QueryAsync(dataset, filter: "population > 1000000", store: Store);
        var plan = await client.QueryPlanAsync(
            dataset,
            new FeatureQuery(Where: FeatureFilter.Parse("population > 1000000")),
            Store);

        Assert.Equal(text, plan.Batches);
        Assert.Equal(3, plan.TotalCount);
        Assert.False(plan.HasMore);
    }

    [Fact]
    public async Task The_bbox_sugar_and_the_plan_bbox_return_the_same_page()
    {
        var client = Client(_host.Client);
        var dataset = await SeedAsync(client);

        var sugar = await client.QueryAsync(dataset, new Contracts.BoundingBox(0, 0, 10, 61), null, Store);
        var plan = await client.QueryPlanAsync(
            dataset,
            new FeatureQuery(BoundingBox: new Contracts.BoundingBox(0, 0, 10, 61)),
            Store);

        Assert.Equal(
            ["Amsterdam", "Bergen"],
            sugar.SelectMany(batch => batch.Features).Select(feature => feature.Attributes[0].StringValue));
        Assert.Equal(sugar, plan.Batches);
    }

    [Fact]
    public async Task The_two_spellings_of_where_must_agree()
    {
        var http = _host.Client;
        var dataset = await SeedAsync(new SpatialClient(http));

        using var conflict = await http.PostAsJsonAsync(
            $"/api/features/query?store={Store}",
            new
            {
                dataset,
                filter = "population > 1000000",
                plan = new { where = new { op = "constant", truth = true } },
            },
            HostApiJson.Options);

        Assert.Equal(HttpStatusCode.BadRequest, conflict.StatusCode);
        Assert.Equal("invalid.arguments", (await conflict.Content.ReadFromJsonAsync<ErrorResponse>(HostApiJson.Options))!.Code);
    }

    [Theory]
    [InlineData("""{"op":"and","terms":[{"op":"constant","truth":true}],"field":"name"}""")]
    [InlineData("""{"op":"isNull","field":"name","terms":[]}""")]
    [InlineData("""{"op":"compare","field":"name","value":{"kind":"string","text":"a"}}""")]
    [InlineData("""{"op":"nonsense","field":"name"}""")]
    [InlineData("""{"op":"constant","value":{"kind":"boolean","boolean":true}}""")]
    public async Task A_node_that_spells_its_op_wrongly_is_invalid_arguments(string where)
    {
        var http = _host.Client;
        var dataset = await SeedAsync(new SpatialClient(http));

        var response = await http.PostAsJsonAsync(
            $"/api/features/query?store={Store}",
            new { dataset, plan = new { where = JsonDocument.Parse(where).RootElement } },
            HostApiJson.Options);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("invalid.arguments", (await response.Content.ReadFromJsonAsync<ErrorResponse>(HostApiJson.Options))!.Code);
    }

    [Fact]
    public async Task A_plan_naming_a_field_the_dataset_lacks_is_invalid_arguments()
    {
        var client = Client(_host.Client);
        var dataset = await SeedAsync(client);

        var exception = await Assert.ThrowsAsync<SpatialClientException>(() =>
            client.QueryPlanAsync(dataset, new FeatureQuery(Projection: ["mystery"]), Store));

        Assert.Equal(400, exception.StatusCode);
        Assert.Equal("invalid.arguments", exception.Code);
    }

    [Fact]
    public async Task A_query_over_a_dataset_that_does_not_exist_is_not_found()
    {
        var exception = await Assert.ThrowsAsync<SpatialClientException>(() =>
            Client(_host.Client).QueryPlanAsync("public.nowhere", FeatureQuery.All, Store));

        Assert.Equal(404, exception.StatusCode);
        Assert.Equal("not.found", exception.Code);
    }

    [Fact]
    public async Task A_cancelled_query_is_cancelled()
    {
        var client = Client(_host.Client);
        var dataset = await SeedAsync(client);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            client.QueryPlanAsync(dataset, FeatureQuery.All, Store, cancellation.Token));
    }

    private static IReadOnlyList<string?> Names(FeatureQueryPage page) =>
        [.. page.Features.Select(feature => feature.Attributes[0].StringValue)];

    /// <summary>
    /// One host for the class, configured as the tests configure it today:
    /// no settings at all, because every dataset is the test's own.
    /// </summary>
    public sealed class QueryHost : ClassHostFixture
    {
        public QueryHost()
            : base("spatial-query-plan")
        {
        }

        protected override void ConfigureHost(IWebHostBuilder builder)
        {
            // The class's settings are the host's defaults.
        }
    }
}
