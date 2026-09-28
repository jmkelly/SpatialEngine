using Spatial.Contracts;
using Spatial.Contracts.Providers;

namespace Spatial.Host.Api;

/// <summary>
/// The staged-upload surface (ADR-0090): a client that cannot send a large
/// document in one request appends it here, asks how far it got, and finally
/// asks the ingest route to load the staged bytes.
/// <para>
/// Every route is admin-gated like the rest of the mutation surface, including
/// the reads: a staged upload is a pending document holding caller-supplied
/// bytes, not a dataset, and nothing about it is public.
/// </para>
/// <para>
/// The protocol is deliberately the smallest one that can resume: a client
/// supplies or is given an id, appends chunks at a byte offset, and the host
/// reports how many bytes it holds. There is no vendor shape here — the Esri
/// chunked <c>/uploads/{id}/add</c> projection is a later decision (ADR-0041
/// §5), not the domain model.
/// </para>
/// </summary>
internal static class UploadEndpoints
{
    /// <summary>The seams every route here shares: the request, the gate and the caller's cancellation.</summary>
    private sealed record UploadRoute(
        HttpContext Context,
        AdminOptions Admin,
        IAuthService Auth,
        IUploadStaging Uploads,
        CancellationToken Token);

    public static void Map(IEndpointRouteBuilder app, AdminOptions admin, AuthOptions authOptions, IAuthService auth)
    {
        app.MapPost("/api/uploads", (HttpContext context, IUploadStaging uploads, CancellationToken token) =>
            Start(Route(context, admin, auth, uploads, token)));
        app.MapGet("/api/uploads", (HttpContext context, IUploadStaging uploads, CancellationToken token) =>
            List(Route(context, admin, auth, uploads, token)));
        app.MapGet("/api/uploads/{id}", (string id, HttpContext context, IUploadStaging uploads, CancellationToken token) =>
            Describe(Route(context, admin, auth, uploads, token), id));
        app.MapPut("/api/uploads/{id}", (string id, HttpContext context, IUploadStaging uploads, CancellationToken token) =>
            Append(Route(context, admin, auth, uploads, token), id));
        app.MapDelete("/api/uploads/{id}", (string id, HttpContext context, IUploadStaging uploads, CancellationToken token) =>
            Discard(Route(context, admin, auth, uploads, token), id));
    }

    private static UploadRoute Route(
        HttpContext context, AdminOptions admin, IAuthService auth, IUploadStaging uploads, CancellationToken token) =>
        new(context, admin, auth, uploads, token);

    private static async Task<IResult> Start(UploadRoute route)
    {
        var (context, admin, auth, uploads, token) = route;
        try
        {
            await Authorize(context, admin, auth, token);
            var query = context.Request.Query;
            var state = await uploads.StartAsync(
                query["id"].ToString(),
                Optional(query, "total"),
                EmptyToNull(query["sha256"].ToString()),
                token);
            return Results.Created($"/api/uploads/{Uri.EscapeDataString(state.UploadId)}", state);
        }
        catch (Exception exception)
        {
            return ErrorMapper.Map(exception);
        }
    }

    private static async Task<IResult> List(UploadRoute route)
    {
        var (context, admin, auth, uploads, token) = route;
        try
        {
            await Authorize(context, admin, auth, token);
            return Results.Ok(await uploads.ListAsync(token));
        }
        catch (Exception exception)
        {
            return ErrorMapper.Map(exception);
        }
    }

    private static async Task<IResult> Describe(UploadRoute route, string id)
    {
        var (context, admin, auth, uploads, token) = route;
        try
        {
            await Authorize(context, admin, auth, token);
            return Results.Ok(await uploads.DescribeAsync(Uri.UnescapeDataString(id), token));
        }
        catch (Exception exception)
        {
            return ErrorMapper.Map(exception);
        }
    }

    private static async Task<IResult> Append(UploadRoute route, string id)
    {
        var (context, admin, auth, uploads, token) = route;
        try
        {
            await Authorize(context, admin, auth, token);
            var query = context.Request.Query;
            var state = await uploads.AppendAsync(
                Uri.UnescapeDataString(id),
                context.Request.Body,
                new UploadAppend(RequiredOffset(query), Optional(query, "total"), EmptyToNull(query["sha256"].ToString())),
                token);
            return Results.Ok(state);
        }
        catch (Exception exception)
        {
            return ErrorMapper.Map(exception);
        }
    }

    private static async Task<IResult> Discard(UploadRoute route, string id)
    {
        var (context, admin, auth, uploads, token) = route;
        try
        {
            await Authorize(context, admin, auth, token);
            return Results.Ok(await uploads.DiscardAsync(Uri.UnescapeDataString(id), token));
        }
        catch (Exception exception)
        {
            return ErrorMapper.Map(exception);
        }
    }

    /// <summary>
    /// The offset is required, and named in the failure: a chunk addressed
    /// nowhere is the mistake resumability exists to prevent.
    /// </summary>
    private static long RequiredOffset(IQueryCollection query)
    {
        var value = query["offset"].ToString();
        return long.TryParse(value, System.Globalization.CultureInfo.InvariantCulture, out var offset) && offset >= 0
            ? offset
            : throw SpatialException.BadArguments(
                $"The 'offset' query parameter is required and must be a non-negative integer, got '{value}'.");
    }

    private static long? Optional(IQueryCollection query, string key)
    {
        var value = query[key].ToString();
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return long.TryParse(value, System.Globalization.CultureInfo.InvariantCulture, out var total) && total >= 0
            ? total
            : throw SpatialException.BadArguments(
                $"The '{key}' query parameter must be a non-negative integer, got '{value}'.");
    }

    private static string? EmptyToNull(string value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private static Task Authorize(HttpContext context, AdminOptions admin, IAuthService auth, CancellationToken token) =>
        AdminAuthorization.RequireAsync(context, admin, auth, token);
}
