using Spatial.Contracts;
using Spatial.Rendering.Skia.Drawing;

namespace Spatial.Rendering.Skia;

/// <summary>
/// Encodes a rasterized vector buffer, compositing it over configured imagery
/// through the injected <see cref="IRasterOperations"/> contract. When no
/// imagery pipeline is configured the vector-only path encodes with Skia
/// (PNG/JPEG); image formats that need libvips are rejected, not silently
/// downgraded.
/// </summary>
internal sealed class RasterComposer
{
    private readonly IRasterOperations? _operations;

    public RasterComposer(IRasterOperations? operations) => _operations = operations;

    public async Task<RasterImage> ComposeAsync(RasterBuffer vector, MapRenderRequest request, CancellationToken cancellationToken)
    {
        var imagery = request.Imagery ?? [];
        if (_operations is null)
        {
            return EncodeVectorOnly(vector, request, imagery.Count);
        }

        var layers = new List<RasterLayer>(imagery.Count + 1);
        layers.AddRange(imagery);
        layers.Add(new RasterBufferLayer(vector));
        var composite = new RasterCompositeRequest(
            request.Viewport, layers, request.Format, request.Quality, request.Background, request.Transparent);
        return await _operations.CompositeAsync(composite, cancellationToken);
    }

    private static RasterImage EncodeVectorOnly(RasterBuffer vector, MapRenderRequest request, int imageryCount)
    {
        if (imageryCount > 0)
        {
            throw SpatialException.BadArguments("Imagery sources were requested but no imagery pipeline is configured.");
        }

        if (!SkiaImageEncoder.Supports(request.Format))
        {
            throw SpatialException.BadArguments(
                $"Encoding '{request.Format}' requires the imagery pipeline, which is not configured.");
        }

        return SkiaImageEncoder.Encode(vector, request.Format, request.Quality);
    }
}
