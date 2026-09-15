using Spatial.Contracts;
using Spatial.Contracts.Http;

namespace Spatial.Client;

/// <summary>
/// The tile surface of the .NET client (ADR-0046): one cache-aware tile, an
/// ordered batch with server-side bounded parallelism, and scheme capability
/// discovery. Split from <see cref="SpatialClient"/> so the main client's
/// fan-out stays deliberate (ADR-0040); reach it through
/// <see cref="SpatialClient.Tiles"/>.
/// </summary>
public sealed class SpatialTileClient
{
    private readonly SpatialClientTransport _transport;

    internal SpatialTileClient(SpatialClientTransport transport) => _transport = transport;

    /// <summary>Renders one cache-aware tile; the request's format selects the path suffix.</summary>
    public async Task<RasterImage> RenderAsync(
        int z, int x, int y, TileRenderRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var format = request.Format.ToString().ToLowerInvariant();
        return await _transport.PostForImageAsync(
            $"/api/render/tiles/{z}/{x}/{y}.{format}", request, cancellationToken);
    }

    /// <summary>Renders an ordered tile batch with server-side bounded parallelism.</summary>
    public async Task<TileBatchResponse> RenderAsync(TileBatchRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return await _transport.PostAsync<TileBatchResponse>("/api/render/tiles/batch", request, cancellationToken);
    }

    /// <summary>Describes the registered tiling schemes, their levels of detail and the batch cap.</summary>
    public Task<TileCapabilitiesResponse> CapabilitiesAsync(CancellationToken cancellationToken = default) =>
        _transport.GetAsync<TileCapabilitiesResponse>("/api/render/tiles/capabilities", cancellationToken);
}
