using Spatial.Contracts;
using Spatial.Contracts.Http;
using Spatial.Contracts.Providers;

namespace Spatial.Host.Api;

/// <summary>
/// Store-aware data routes (ADR-0033): every request names its store
/// (<c>demo</c> by default, <c>postgis</c> or <c>sqlserver</c> when
/// configured). The demo store is always available; the database stores throw
/// <c>store.unavailable</c> without a connection string.
/// </summary>
internal static class StoreEndpoints
{
    public const string Demo = "demo";
    public const string Postgis = "postgis";

    public const string SqlServer = "sqlserver";

    /// <summary>
    /// The seams every admin-authorized store route needs: the caller's
    /// context and its cancellation, the requested store and the registry
    /// that resolves it, and the authorization policy. Grouping them keeps
    /// each route's signature to the payload it carries.
    /// </summary>
    private sealed record StoreRoute(
        HttpContext Context,
        string? Store,
        IStoreRegistry Stores,
        AdminOptions Admin,
        AuthOptions AuthOptions,
        IAuthService Auth)
    {
        public CancellationToken Token => Context.RequestAborted;
    }

    public static void Map(
        IEndpointRouteBuilder app,
        AdminOptions admin,
        AuthOptions authOptions,
        IAuthService auth)
    {
        app.MapGet("/api/catalogue", Catalogue)
            .Produces<CatalogueResponse>();
        app.MapGet("/api/datasets/{id}", Describe)
            .Produces<DatasetDescription>()
            .Produces<ErrorResponse>(StatusCodes.Status404NotFound);
        app.MapPost("/api/datasets", (HttpContext context, CreateDatasetRequest request, string? store, IStoreRegistry stores, CancellationToken token) =>
            Create(Route(context, store, stores, admin, authOptions, auth), request))
            .Produces<CreateDatasetResponse>()
            .Produces<ErrorResponse>(StatusCodes.Status400BadRequest);
        app.MapPost("/api/features/scan", Scan)
            .Produces<FeatureBatchesResponse>();
        app.MapPost("/api/features/query", Query)
            .Produces<FeatureBatchesResponse>();
        app.MapPost("/api/features/write", (HttpContext context, FeatureWriteRequest request, string? store, IStoreRegistry stores, CancellationToken token) =>
            Write(Route(context, store, stores, admin, authOptions, auth), request))
            .Produces<FeatureWriteResponse>()
            .Produces<ErrorResponse>(StatusCodes.Status400BadRequest);
        app.MapPost("/api/transactions/begin", (HttpContext context, string? store, IStoreRegistry stores, CancellationToken token) =>
            Begin(Route(context, store, stores, admin, authOptions, auth)))
            .Produces<BeginTransactionResponse>();
        app.MapPost("/api/transactions/commit", (HttpContext context, TransactionRequest request, string? store, IStoreRegistry stores, CancellationToken token) =>
            Commit(Route(context, store, stores, admin, authOptions, auth), request))
            .Produces<TransactionResponse>();
        app.MapPost("/api/transactions/rollback", (HttpContext context, TransactionRequest request, string? store, IStoreRegistry stores, CancellationToken token) =>
            Rollback(Route(context, store, stores, admin, authOptions, auth), request))
            .Produces<TransactionResponse>();
        app.MapPost("/api/demo/sleep", Sleep)
            .Produces<SleepResponse>();
    }

    /// <summary>The route seams one request carries, ready for the store routes to use.</summary>
    private static StoreRoute Route(
        HttpContext context,
        string? store,
        IStoreRegistry stores,
        AdminOptions admin,
        AuthOptions authOptions,
        IAuthService auth) =>
        new(context, store, stores, admin, authOptions, auth);

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

    private static async Task<IResult> Create(StoreRoute route, CreateDatasetRequest request)
    {
        try
        {
            await Authorize(route);
            var catalogue = ResolveCatalogue(route.Stores, route.Store ?? Postgis);
            var batch = CodecWire.DecodeBatch(request.Batch, "batch");
            var created = await catalogue.CreateAsync(request.Dataset, batch, request.Srid, route.Token);
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

    private static async Task<IResult> Write(StoreRoute route, FeatureWriteRequest request)
    {
        try
        {
            await Authorize(route);
            var features = ResolveFeatures(route.Stores, route.Store ?? Postgis);
            var batch = CodecWire.DecodeBatch(request.Batch, "batch");
            var appended = await features.WriteAsync(request.Dataset, batch, request.Transaction, route.Token);
            return Results.Ok(new FeatureWriteResponse(appended));
        }
        catch (Exception exception)
        {
            return ErrorMapper.Map(exception);
        }
    }

    private static async Task<IResult> Begin(StoreRoute route)
    {
        try
        {
            await Authorize(route);
            var transactions = ResolveTransactions(route.Stores, route.Store ?? Postgis);
            return Results.Ok(new BeginTransactionResponse(await transactions.BeginAsync(route.Token)));
        }
        catch (Exception exception)
        {
            return ErrorMapper.Map(exception);
        }
    }

    private static async Task<IResult> Commit(StoreRoute route, TransactionRequest request)
    {
        try
        {
            await Authorize(route);
            var transactions = ResolveTransactions(route.Stores, route.Store ?? Postgis);
            return Results.Ok(new TransactionResponse(await transactions.CommitAsync(request.Transaction, route.Token)));
        }
        catch (Exception exception)
        {
            return ErrorMapper.Map(exception);
        }
    }

    private static async Task<IResult> Rollback(StoreRoute route, TransactionRequest request)
    {
        try
        {
            await Authorize(route);
            var transactions = ResolveTransactions(route.Stores, route.Store ?? Postgis);
            return Results.Ok(new TransactionResponse(await transactions.RollbackAsync(request.Transaction, route.Token)));
        }
        catch (Exception exception)
        {
            return ErrorMapper.Map(exception);
        }
    }

    private static async Task<IResult> Sleep(SleepRequest request, IDemoWork work, CancellationToken token)
    {
        try
        {
            var slept = await work.SleepAsync(request.Milliseconds, progress: null, token);
            return Results.Ok(new SleepResponse(slept));
        }
        catch (Exception exception)
        {
            return ErrorMapper.Map(exception);
        }
    }

    /// <summary>The admin gate every write and transaction route shares: a no-op while both policies are off.</summary>
    private static async Task Authorize(StoreRoute route)
    {
        if (route.Admin.Enabled || route.AuthOptions.Enabled)
        {
            await RequireAdminAsync(route.Context, route.Auth, route.Admin.Token, route.Token);
        }
    }

    private static async Task RequireAdminAsync(HttpContext context, IAuthService auth, string? configuredToken, CancellationToken token)
    {
        var header = context.Request.Headers.Authorization.ToString();
        var presented = header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            ? header["Bearer ".Length..].Trim()
            : null;
        await AuthGuard.RequireRoleAsync(auth, presented, configuredToken, AuthGuard.AdminRole, token);
    }

    internal static IDataCatalogue ResolveCatalogue(IStoreRegistry stores, string store) =>
        stores.Catalogue(store);

    internal static IFeatureStore ResolveFeatures(IStoreRegistry stores, string store) =>
        stores.Features(store);

    private static ITransactionStore ResolveTransactions(IStoreRegistry stores, string store) =>
        stores.Transactions(store)
        ?? throw SpatialException.BadArguments($"Store '{store}' does not provide transactions.");
}
