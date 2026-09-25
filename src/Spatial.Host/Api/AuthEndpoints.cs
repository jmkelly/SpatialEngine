using Spatial.Contracts;
using Spatial.Contracts.Http;

namespace Spatial.Host.Api;

/// <summary>Username/password login and opaque bearer lifecycle (ADR-0071).</summary>
internal static class AuthEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        app.MapPost("/api/auth/login", Login).Produces<AuthTokenResponse>();
        app.MapPost("/api/auth/logout", Logout).Produces(StatusCodes.Status204NoContent);
        app.MapPost("/api/auth/refresh", Refresh).Produces<AuthTokenResponse>();
        app.MapGet("/api/auth/me", Me).Produces<AuthIdentityResponse>();
    }

    private static async Task<IResult> Login(LoginRequest request, IAuthService auth, CancellationToken token)
    {
        try
        {
            var result = await auth.LoginAsync(request.Username, request.Password, token);
            return Results.Ok(new AuthTokenResponse(result.Token, result.ExpiresAt));
        }
        catch (Exception exception)
        {
            return ErrorMapper.Map(exception);
        }
    }

    private static async Task<IResult> Logout(HttpContext context, IAuthService auth, CancellationToken token)
    {
        try
        {
            await auth.LogoutAsync(RequireBearer(context), token);
            return Results.NoContent();
        }
        catch (Exception exception)
        {
            return ErrorMapper.Map(exception);
        }
    }

    private static async Task<IResult> Refresh(HttpContext context, IAuthService auth, CancellationToken token)
    {
        try
        {
            var result = await auth.RefreshAsync(RequireBearer(context), token);
            return Results.Ok(new AuthTokenResponse(result.Token, result.ExpiresAt));
        }
        catch (Exception exception)
        {
            return ErrorMapper.Map(exception);
        }
    }

    private static async Task<IResult> Me(HttpContext context, IAuthService auth, CancellationToken token)
    {
        try
        {
            var identity = await auth.ValidateTokenAsync(RequireBearer(context), token);
            return Results.Ok(Response(identity));
        }
        catch (Exception exception)
        {
            return ErrorMapper.Map(exception);
        }
    }

    private static string Bearer(HttpContext context)
    {
        var header = context.Request.Headers.Authorization.ToString();
        return header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            ? header["Bearer ".Length..].Trim()
            : string.Empty;
    }

    private static string RequireBearer(HttpContext context)
    {
        var token = Bearer(context);
        return string.IsNullOrWhiteSpace(token)
            ? throw SpatialException.AuthenticationRequired("A bearer token is required.")
            : token;
    }

    private static AuthIdentityResponse Response(AuthIdentity identity) =>
        new(identity.Issuer, identity.Subject, identity.Username, identity.Roles);
}
