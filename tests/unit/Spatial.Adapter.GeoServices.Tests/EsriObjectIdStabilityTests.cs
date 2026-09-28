using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Spatial.Contracts;
using Spatial.Contracts.Providers;
using Spatial.Core.Features;
using Spatial.Core.Features.Query;
using Spatial.Core.Geometry;
using Spatial.Esri.Codec;
using Spatial.Operations.NetTopologySuite;
using Spatial.Stores.Memory;
using Spatial.Transformations.ProjNet;

namespace Spatial.Adapter.GeoServices.Tests;

/// <summary>
/// The synthetic <c>OBJECTID</c> is a property of the <em>feature</em>, not of
/// the query (ADR-0037): on a layer with no integer identity column it is the
/// feature's ordinal in the whole-dataset scan, so the same feature carries the
/// same id whether or not a <c>where</c> clause, an id list or a geometry
/// filter was supplied.
///
/// This is what makes handing the attribute clause to the store (ADR-0074 §7)
/// safe. A store that returns only the matching rows has changed the row
/// numbering the facade numbers over, so a pushed-down clause would renumber
/// every surviving feature — and an <c>OBJECTID</c> that changes with the query
/// is not the durable, round-trippable key <c>objectIds</c>, the edit paths and
/// paging all assume. The facade must therefore only push a clause down when
/// the object id is store-derived, and keep it as a residual per-feature match
/// when the id is the scan ordinal.
/// </summary>
public sealed class EsriObjectIdStabilityTests
{
    private static QueryServices Services => new(Operations!, Relations!, new NtsGeometryMeasures(), Transforms!, Transforms!);

    private static readonly CoordinateReference Crs4326 = CoordinateReference.Epsg(4326);

    private static readonly NtsGeometryOperations Operations = new();
    private static readonly NtsGeometryRelations Relations = new();
    private static readonly ProjNetTransforms Transforms = new();

    private const string Dataset = "demo.places";

    /// <summary>A string identity column: a unique-id model but no integer one, so the object id is the scan ordinal.</summary>
    private static readonly FeatureSchema OrdinalSchema = new(
    [
        new FieldDefinition("guid", AttributeKind.String),
        new FieldDefinition("name", AttributeKind.String, nullable: true),
        new FieldDefinition("geometry", AttributeKind.Geometry),
    ]);

    private static readonly FeatureSchema IdentitySchema = new(
    [
        new FieldDefinition("id", AttributeKind.Int64),
        new FieldDefinition("name", AttributeKind.String, nullable: true),
        new FieldDefinition("geometry", AttributeKind.Geometry),
    ]);

    /// <summary>Three rows whose names are deliberately out of ordinal order, so a renumbering is visible.</summary>
    private static readonly string[] OrdinalNames = ["gamma", "alpha", "beta"];

    private static Feature OrdinalRow(int position) => Row(
        OrdinalSchema,
        AttributeValue.FromString($"g{position}"),
        OrdinalNames[position],
        position + 1,
        position + 1);

    private static Feature IdentityRow(long id, string name) => Row(
        IdentitySchema,
        AttributeValue.FromInt64(id),
        name,
        id,
        id);

    private static Feature Row(FeatureSchema schema, AttributeValue id, string name, double x, double y) => new(
        new FeatureId(id.Kind == AttributeKind.Int64
            ? id.Int64Value.ToString(System.Globalization.CultureInfo.InvariantCulture)
            : id.StringValue),
        schema,
        [
            id,
            AttributeValue.FromString(name),
            AttributeValue.FromGeometry(GeometryFactory.CreatePoint(x, y, Crs4326)),
        ]);

    private static DatasetDescription Layer(FeatureSchema schema, string[] idColumns, int count) =>
        new(Dataset, "memory", "places", "geometry", 4326, "Point", count, idColumns, schema);

    /// <summary>A real store seeded with the rows, so a test exercises the pushdown and not a double's filtering.</summary>
    private static async Task<MemoryStore> SeededAsync(FeatureSchema schema, params Feature[] rows)
    {
        var store = new MemoryStore();
        await store.CreateAsync(Dataset, new FeatureBatch(schema, [rows[0]]), 4326);
        await store.WriteAsync(Dataset, new FeatureBatch(schema, rows));
        return store;
    }

    private static Task<EsriFeatureQuery> ParseAsync(params (string Key, string Value)[] values) =>
        ParseWithTokenAsync(CancellationToken.None, values);

    private static async Task<EsriFeatureQuery> ParseWithTokenAsync(CancellationToken cancellationToken, params (string Key, string Value)[] values)
    {
        var context = new DefaultHttpContext();
        context.Request.QueryString = QueryString.Create(values.Select(value => new KeyValuePair<string, string?>(value.Key, value.Value)));
        return EsriFeatureQuery.Parse(await EsriRequestParameters.ReadAsync(context, cancellationToken), EsriLayerModel.LayerCoordinateReference(4326));
    }

    private static async Task<(string Name, long ObjectId)[]> QueryAsync(
        DatasetDescription dataset, IFeatureStore store, EsriFeatureQuery query, CancellationToken cancellationToken = default) =>
        await ReadAsync(await MatchAsync(dataset, store, query, cancellationToken));

    /// <summary>The facade's own work: the read is cancellable, and nothing is written before it is checked.</summary>
    private static async Task<IResult> MatchAsync(
        DatasetDescription dataset, IFeatureStore store, EsriFeatureQuery query, CancellationToken cancellationToken) =>
        await FeatureService.QueryAsync(dataset, store, query, Services, cancellationToken);

    /// <summary>Renders a result to its JSON body; the query has already run, so no token is in scope.</summary>
    private static async Task<(string Name, long ObjectId)[]> ReadAsync(IResult result)
    {
        var context = new DefaultHttpContext();
        context.RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider();
        context.Response.Body = new MemoryStream();
        await result.ExecuteAsync(context);
        context.Response.Body.Position = 0;
        using var document = await JsonDocument.ParseAsync(context.Response.Body);
        return document.RootElement.Clone()
            .GetProperty("features").EnumerateArray()
            .Select(feature => (
                feature.GetProperty("attributes").GetProperty("name").GetString()!,
                feature.GetProperty("attributes").GetProperty(EsriLayerModel.ObjectIdField).GetInt64()))
            .ToArray();
    }

    // ---- the invariant ----

    [Fact]
    public async Task A_where_clause_does_not_renumber_the_scan_ordinal_object_id()
    {
        // "alpha" is the second row of the scan, so it is object id 2 whether or
        // not a clause selected it. Pushed down, the store would return one row
        // and the facade would number it 1.
        var dataset = Layer(OrdinalSchema, ["guid"], 3);
        var store = await SeededAsync(OrdinalSchema, OrdinalRow(0), OrdinalRow(1), OrdinalRow(2));

        var rows = await QueryAsync(dataset, store, await ParseAsync(("where", "name = 'alpha'")));

        Assert.Equal([("alpha", 2L)], rows);
    }

    [Fact]
    public async Task A_where_clause_over_several_rows_keeps_every_scan_ordinal()
    {
        // The multi-row shape: the surviving ids stay 2 and 3, not 1 and 2.
        var dataset = Layer(OrdinalSchema, ["guid"], 3);
        var store = await SeededAsync(OrdinalSchema, OrdinalRow(0), OrdinalRow(1), OrdinalRow(2));

        var rows = await QueryAsync(dataset, store, await ParseAsync(("where", "name <> 'gamma'")));

        Assert.Equal([("alpha", 2L), ("beta", 3L)], rows);
    }

    [Fact]
    public async Task An_object_ids_filter_resolves_against_the_same_scan_ordinal()
    {
        // objectIds is the other face of the same key, so it has to agree with
        // what a where clause returns for the same feature.
        var dataset = Layer(OrdinalSchema, ["guid"], 3);
        var store = await SeededAsync(OrdinalSchema, OrdinalRow(0), OrdinalRow(1), OrdinalRow(2));

        var result = await MatchAsync(dataset, store, await ParseAsync(("objectIds", "2"), ("returnIdsOnly", "true")), CancellationToken.None);

        Assert.Equal(new long[] { 2 }, await ObjectIdsAsync(result));
    }

    /// <summary>The ids-only body, which carries the key and no attributes.</summary>
    private static async Task<long[]> ObjectIdsAsync(IResult result)
    {
        var context = new DefaultHttpContext();
        context.RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider();
        context.Response.Body = new MemoryStream();
        await result.ExecuteAsync(context);
        context.Response.Body.Position = 0;
        using var document = await JsonDocument.ParseAsync(context.Response.Body);
        return document.RootElement.Clone().GetProperty("objectIds").EnumerateArray().Select(id => id.GetInt64()).ToArray();
    }

    [Fact]
    public async Task An_identity_backed_layer_pushes_the_clause_down_and_keeps_its_object_ids()
    {
        // An integer identity column makes the object id store-derived, so the
        // pushdown is semantics-preserving — and it is the point of the change.
        var dataset = Layer(IdentitySchema, ["id"], 3);
        var store = await SeededAsync(IdentitySchema, IdentityRow(7, "gamma"), IdentityRow(9, "alpha"), IdentityRow(11, "beta"));

        var rows = await QueryAsync(dataset, store, await ParseAsync(("where", "name = 'alpha'")));

        Assert.Equal([("alpha", 9L)], rows);
    }

    // ---- the pushdown decision itself ----

    /// <summary>The parsed Esri clause, which is what the resolver takes.</summary>
    private static EsriWhere Clause(string text)
    {
        Assert.True(EsriWhere.TryParse(text, out var where, out var error), error);
        return where!;
    }

    [Fact]
    public void A_clause_stays_with_the_facade_when_the_object_id_is_the_scan_ordinal()
    {
        var clause = Clause("name = 'alpha'");
        var dataset = Layer(OrdinalSchema, ["guid"], 3);

        Assert.Null(EsriWhereResolver.Pushdown(clause, EsriObjectIdScheme.For(dataset), dataset));
    }

    [Fact]
    public void A_clause_is_pushed_down_when_the_object_id_is_store_derived()
    {
        var clause = Clause("name = 'alpha'");
        var dataset = Layer(IdentitySchema, ["id"], 3);

        var pushed = EsriWhereResolver.Pushdown(clause, EsriObjectIdScheme.For(dataset), dataset);

        var compare = Assert.IsType<Predicate.Compare>(pushed);
        Assert.Equal("name", compare.Field.Name);
    }

    [Fact]
    public void A_clause_naming_the_object_id_is_rewritten_onto_the_identity_column()
    {
        // OBJECTID is the facade's name for the identity column, so a clause over
        // it is pushed down renamed — otherwise no store could read the column.
        var clause = Clause("OBJECTID = 9");
        var dataset = Layer(IdentitySchema, ["id"], 3);

        var pushed = EsriWhereResolver.Pushdown(clause, EsriObjectIdScheme.For(dataset), dataset);

        var compare = Assert.IsType<Predicate.Compare>(pushed);
        Assert.Equal("id", compare.Field.Name);
        Assert.Equal(Literal.FromInteger("9"), compare.Value);
    }

    [Fact]
    public void A_query_with_no_clause_needs_no_pushdown()
    {
        var dataset = Layer(IdentitySchema, ["id"], 3);

        Assert.Null(EsriWhereResolver.Pushdown(null, EsriObjectIdScheme.For(dataset), dataset));
    }

    // ---- failure and cancellation ----

    [Fact]
    public async Task A_clause_naming_a_column_the_layer_does_not_have_is_a_typed_failure()
    {
        // The pushed-down plan is validated against the schema where it is
        // built, so a bad column is invalid.arguments, not a store error.
        var dataset = Layer(IdentitySchema, ["id"], 3);
        var store = await SeededAsync(IdentitySchema, IdentityRow(7, "gamma"), IdentityRow(9, "alpha"));

        var failure = await Assert.ThrowsAsync<SpatialException>(async () =>
            await QueryAsync(dataset, store, await ParseAsync(("where", "missing = 'x'"))));

        Assert.Equal("invalid.arguments", failure.Code);
    }

    [Fact]
    public async Task A_cancelled_query_fails_before_it_reads()
    {
        var dataset = Layer(OrdinalSchema, ["guid"], 3);
        var store = await SeededAsync(OrdinalSchema, OrdinalRow(0), OrdinalRow(1), OrdinalRow(2));
        var query = await ParseAsync(("where", "name = 'alpha'"));
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await MatchAsync(dataset, store, query, cancelled.Token));
    }
}
