using Spatial.Imagery.Vips.Imagery;
using Spatial.PluginSdk;

namespace Spatial.Imagery.Vips;

/// <summary>
/// The NetVips/libvips imagery implementation of <see cref="IRasterOperations"/>
/// (ADR-0044): read and normalise a configured source, blend a bottom-to-top
/// layer stack and encode. libvips types stay inside this assembly (ADR-0005);
/// the source map comes from host configuration, never from the request.
/// </summary>
public sealed class VipsRasterOperations : IRasterOperations
{
    private readonly IReadOnlyDictionary<string, string> _sources;

    public VipsRasterOperations(IReadOnlyDictionary<string, string> sources)
    {
        ArgumentNullException.ThrowIfNull(sources);
        _sources = sources;
    }

    public Task<RasterImage> ReadAsync(RasterReadRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Task.Run(() => Read(request, cancellationToken), cancellationToken);
    }

    public Task<RasterImage> CompositeAsync(RasterCompositeRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Task.Run(() => Composite(request, cancellationToken), cancellationToken);
    }

    private RasterImage Read(RasterReadRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!request.Viewport.IsValid)
        {
            throw SpatialException.BadArguments("The viewport requires a non-empty bounds and a positive pixel size.");
        }

        var path = ImageryLoader.Resolve(_sources, request.Source);
        using var image = ImageryLoader.LoadExact(path, request.Viewport.Width, request.Viewport.Height);
        return VipsEncoder.Encode(image, request.Format, 90);
    }

    private RasterImage Composite(RasterCompositeRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var composition = VipsCompositor.Compose(
            request.Viewport, request.Layers, _sources, request.Background, request.Transparent);
        cancellationToken.ThrowIfCancellationRequested();
        return VipsEncoder.Encode(composition.Result, request.Format, request.Quality);
    }
}
