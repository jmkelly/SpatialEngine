using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Spatial.Interop.Esri;

namespace Spatial.Adapter.GeoServices.Tests;

/// <summary>
/// T-041 offline/async rejects: every named non-goal rejects with its name in
/// the envelope, and cancellation maps to the 499 envelope (the handlers do no
/// store work, so the token is checked before service resolution).
/// </summary>
public sealed class MapOfflineRejectsTests
{
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

    [Fact]
    public async Task Offline_rejects_honour_cancellation()
    {
        var result = await MapOfflineRejects.MapExportTiles(null!, null!, "world", new CancellationToken(canceled: true));

        var (status, body) = await ExecuteAsync(result);

        Assert.Equal(StatusCodes.Status499ClientClosedRequest, status);
        Assert.Equal(EsriErrorCodes.RequestCancelled, body.GetProperty("error").GetProperty("code").GetInt32());
    }

    [Fact]
    public async Task Job_rejects_honour_cancellation()
    {
        var result = await MapOfflineRejects.Jobs(null!, null!, "world", new CancellationToken(canceled: true));

        var (status, _) = await ExecuteAsync(result);

        Assert.Equal(StatusCodes.Status499ClientClosedRequest, status);
    }
}
