using Spatial.Contracts;
using Spatial.Contracts.Providers;
using Spatial.Esri.Codec;

namespace Spatial.Adapter.GeoServices;

/// <summary>
/// The ImageService root and <c>exportImage</c> resources (spec §8,
/// ADR-0051): the served metadata document and the rendered image response,
/// including the whole-dataset render the per-item image resource reuses.
/// Split out of <see cref="ImageServerEndpoints"/> — which maps the routes —
/// so each class carries its own fan-out.
/// </summary>
internal static class ImageServerRootResources
{
    /// <summary>The Image Service root: the served metadata document (spec §8.1).</summary>
    internal static async Task<IResult> Root(ImageServerRequest request, GeoServicesOptions options)
    {
        try
        {
            var scope = await ImageServerScope.OpenAsync(request);
            var image = await scope.ImageAsync();
            return EsriJson.Value(ImageService.Root(image.Description, image.Copyright, options));
        }
        catch (Exception exception)
        {
            return EsriErrorMapper.Map(exception);
        }
    }

    /// <summary>The <c>exportImage</c> resource (spec §8.4): the requested frame as encoded image bytes.</summary>
    internal static async Task<IResult> Export(ImageServerRequest request, ICoordinateTransforms transforms)
    {
        try
        {
            var scope = await ImageServerScope.ReadAsync(request);
            var image = await scope.ImageAsync();
            var rasterId = ImageFileHandlers.ParseExportRasterId(scope.Parameters, image.Description, request.Service);
            return await ImageFileHandlers.ExportImageAsync(
                new(image, scope.Parameters, transforms, rasterId, DefaultBbox: null, DefaultCrs: null),
                request.Context, request.CancellationToken);
        }
        catch (Exception exception)
        {
            return EsriErrorMapper.Map(exception);
        }
    }
}
