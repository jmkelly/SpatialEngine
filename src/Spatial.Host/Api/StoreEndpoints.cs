using Spatial.PluginSdk;
using Spatial.PluginSdk.Http;
using Spatial.PluginSdk.Providers;

namespace Spatial.Host.Api;

/// <summary>
/// Store-aware data routes (ADR-0033): every request names its store
/// (<c>demo</c> by default, <c>postgis</c> when configured). The demo store
/// is always available; PostGIS throws <c>store.unavailable</c> without a
/// connection string.
/// </summary>
internal static class StoreEndpoints
{
    public const string Demo = "demo";
    public const string Postgis = "postgis";

    public static void Map(IEndpointRouteBuilder app)
    {
        app.MapGet("/api/catalogue", Catalogue)
            .Produces<CatalogueResponse>();
        app.MapGet("/api/datasets/{id}", Describe)
            .Produces<DatasetDescription>()
            .Produces<ErrorResponse>(StatusCodes.Status404NotFound);
        app.MapPost("/api/datasets", Create)
            .Produces<CreateDatasetResponse>()
            .Produces<ErrorResponse>(StatusCodes.Status400BadRequest);
        app.MapPost("/api/features/scan", Scan)
            .Produces<FeatureBatchesResponse>();
        app.MapPost("/api/features/query", Query)
            .Produces<FeatureBatchesResponse>();
        app.MapPost("/api/features/write", Write)
            .Produces<FeatureWriteResponse>()
            .Produces<ErrorResponse>(StatusCodes.Status400BadRequest);
        app.MapPost("/api/transactions/begin", Begin)
            .Produces<BeginTransactionResponse>();
        app.MapPost("/api/transactions/commit", Commit)
            .Produces<TransactionResponse>();
        app.MapPost("/api/transactions/rollback", Rollback)
            .Produces<TransactionResponse>();
        app.MapPost("/api/demo/sleep", Sleep)
            .Produces<SleepResponse>();
    }

    private static async Task<IResult> Catalogue(
        string? store, string? pattern, IStoreRegistry stores, CancellationToken token)
    {
        try
        {
            var catalogue = ResolveCatalogue(stores, store ?? Demo);
            var datasets = await catalogue.ListAsync(pattern, token);
            return Results.Ok(new CatalogueResponse(datasets));
        }
        catch (Exception exception)
        {
            return ErrorMapper.Map(exception);
        }
    }

    private static async Task<IResult> Describe(
        string id, string? store, IStoreRegistry stores, CancellationToken token)
    {
        try
        {
            var catalogue = ResolveCatalogue(stores, store ?? Demo);
            return Results.Ok(await catalogue.DescribeAsync(Uri.UnescapeDataString(id), token));
        }
        catch (Exception exception)
        {
            return ErrorMapper.Map(exception);
        }
    }

    private static async Task<IResult> Create(
        CreateDatasetRequest request, string? store, IStoreRegistry stores, CancellationToken token)
    {
        try
        {
            var catalogue = ResolveCatalogue(stores, store ?? Postgis);
            var batch = CodecWire.DecodeBatch(request.Batch, "batch");
            var created = await catalogue.CreateAsync(request.Dataset, batch, request.Srid, token);
            return Results.Ok(new CreateDatasetResponse(created));
        }
        catch (Exception exception)
        {
            return ErrorMapper.Map(exception);
        }
    }

    private static async Task<IResult> Scan(
        ScanRequest request, string? store, IStoreRegistry stores, CancellationToken token)
    {
        try
        {
            var features = ResolveFeatures(stores, store ?? Demo);
            var batches = await features.ScanAsync(request.Dataset, token);
            return Results.Ok(new FeatureBatchesResponse(batches.Select(CodecWire.EncodeBatch).ToArray()));
        }
        catch (Exception exception)
        {
            return ErrorMapper.Map(exception);
        }
    }

    private static async Task<IResult> Query(
        FeatureQueryRequest request, string? store, IStoreRegistry stores, CancellationToken token)
    {
        try
        {
            var features = ResolveFeatures(stores, store ?? Demo);
            BoundingBox? bbox = request.Bbox is null
                ? null
                : new BoundingBox(request.Bbox.MinX, request.Bbox.MinY, request.Bbox.MaxX, request.Bbox.MaxY);
            var batches = await features.QueryAsync(request.Dataset, bbox, request.Filter, token);
            return Results.Ok(new FeatureBatchesResponse(batches.Select(CodecWire.EncodeBatch).ToArray()));
        }
        catch (Exception exception)
        {
            return ErrorMapper.Map(exception);
        }
    }

    private static async Task<IResult> Write(
        FeatureWriteRequest request, string? store, IStoreRegistry stores, CancellationToken token)
    {
        try
        {
            var features = ResolveFeatures(stores, store ?? Postgis);
            var batch = CodecWire.DecodeBatch(request.Batch, "batch");
            var appended = await features.WriteAsync(request.Dataset, batch, request.Transaction, token);
            return Results.Ok(new FeatureWriteResponse(appended));
        }
        catch (Exception exception)
        {
            return ErrorMapper.Map(exception);
        }
    }

    private static async Task<IResult> Begin(string? store, IStoreRegistry stores, CancellationToken token)
    {
        try
        {
            var transactions = ResolveTransactions(stores, store ?? Postgis);
            return Results.Ok(new BeginTransactionResponse(await transactions.BeginAsync(token)));
        }
        catch (Exception exception)
        {
            return ErrorMapper.Map(exception);
        }
    }

    private static async Task<IResult> Commit(TransactionRequest request, string? store, IStoreRegistry stores, CancellationToken token)
    {
        try
        {
            var transactions = ResolveTransactions(stores, store ?? Postgis);
            return Results.Ok(new TransactionResponse(await transactions.CommitAsync(request.Transaction, token)));
        }
        catch (Exception exception)
        {
            return ErrorMapper.Map(exception);
        }
    }

    private static async Task<IResult> Rollback(TransactionRequest request, string? store, IStoreRegistry stores, CancellationToken token)
    {
        try
        {
            var transactions = ResolveTransactions(stores, store ?? Postgis);
            return Results.Ok(new TransactionResponse(await transactions.RollbackAsync(request.Transaction, token)));
        }
        catch (Exception exception)
        {
            return ErrorMapper.Map(exception);
        }
    }

    private static async Task<IResult> Sleep(SleepRequest request, IDemoJobs jobs, CancellationToken token)
    {
        try
        {
            var slept = await jobs.SleepAsync(request.Milliseconds, progress: null, token);
            return Results.Ok(new SleepResponse(slept));
        }
        catch (Exception exception)
        {
            return ErrorMapper.Map(exception);
        }
    }

    internal static IDataCatalogue ResolveCatalogue(IStoreRegistry stores, string store) =>
        stores.Catalogue(store);

    internal static IFeatureStore ResolveFeatures(IStoreRegistry stores, string store) =>
        stores.Features(store);

    private static ITransactionStore ResolveTransactions(IStoreRegistry stores, string store) =>
        stores.Transactions(store)
        ?? throw SpatialException.BadArguments($"Store '{store}' does not provide transactions.");
}
