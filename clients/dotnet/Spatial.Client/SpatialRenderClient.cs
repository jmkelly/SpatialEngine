using Spatial.Contracts;
using Spatial.Contracts.Http;
using Spatial.Contracts.Providers;

namespace Spatial.Client;

/// <summary>
/// The render surface of the .NET client (ADR-0044): a styled vector and
/// imagery request, a map's persisted layer styles (ADR-0053) and the
/// configured raster capabilities. Split from <see cref="SpatialClient"/> so
/// the client's fan-out stays deliberate (ADR-0040); reach it through
/// <see cref="SpatialClient.Render"/>.
/// </summary>
public sealed class SpatialRenderClient
{
    private readonly SpatialClientTransport _transport;

    internal SpatialRenderClient(SpatialClientTransport transport) => _transport = transport;

    /// <summary>Renders a styled vector and imagery request to encoded image bytes.</summary>
    public async Task<RasterImage> RenderAsync(RenderRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return await _transport.PostForImageAsync("/api/render", request, cancellationToken);
    }

    /// <summary>Renders a map's datasets using its persisted layer styles (ADR-0053).</summary>
    public async Task<RasterImage> RenderMapAsync(
        string name, MapRenderRequestDto request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return await _transport.PostForImageAsync(
            $"/api/maps/{Uri.EscapeDataString(name)}/render", request, cancellationToken);
    }

    /// <summary>Describes the configured raster formats, pixel cap and imagery sources.</summary>
    public Task<RenderCapabilitiesResponse> RenderCapabilitiesAsync(CancellationToken cancellationToken = default) =>
        _transport.GetAsync<RenderCapabilitiesResponse>("/api/render/capabilities", cancellationToken);
}
