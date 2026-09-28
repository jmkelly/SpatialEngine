using Spatial.Contracts;
using Spatial.Contracts.Providers;
using Spatial.Core.Geometry;
using Spatial.Esri.Codec;

namespace Spatial.Adapter.GeoServices;

/// <summary>
/// The ImageService resources that select raster catalog items by a spatial
/// or textual criterion (spec §8, ADR-0051/0054): <c>identify</c>,
/// <c>query</c> and <c>find</c>. Split out of <see cref="ImageServerEndpoints"/>
/// — which maps the routes — so the catalog-selection fan-out (the Esri
/// query grammar, the raster identify request, the find engine) lives with
/// the resources that use it; the band and attribute summaries are
/// <see cref="ImageBandResources"/>.
/// </summary>
internal static class ImageQueryResources
{
    /// <summary>The <c>identify</c> resource (spec §8.2): the catalog item under the request point.</summary>
    internal static async Task<IResult> Identify(ImageServerRequest request)
    {
        try
        {
            var scope = await ImageServerScope.OpenAsync(request);
            var image = await scope.ImageAsync();
            var rasterCrs = image.Description.Raster.Crs;
            var srid = MapServerResources.SridOf(rasterCrs);
            var geometry = EsriValueParser.ParseGeometry(scope.Parameters.Require("geometry"), CoordinateReference.Epsg(srid));
            var result = await image.Catalogue.IdentifyAsync(
                image.Dataset,
                new RasterIdentifyRequest(geometry, geometry.CoordinateReference?.ToString() ?? rasterCrs),
                request.CancellationToken);
            var envelope = geometry.Envelope ?? Envelope.Empty;
            return ImageService.Identify(image.Description, result, envelope.CenterX, envelope.CenterY);
        }
        catch (Exception exception)
        {
            return EsriErrorMapper.Map(exception);
        }
    }

    /// <summary>The <c>query</c> resource (spec §8.5): the catalog items the query grammar selects.</summary>
    internal static async Task<IResult> Query(ImageServerRequest request, QueryServices services)
    {
        try
        {
            var scope = await ImageServerScope.OpenAsync(request);
            var image = await scope.CatalogueAsync();
            var layerCrs = EsriLayerModel.LayerCoordinateReference(MapServerResources.SridOf(image.Description.Raster.Crs));
            var query = EsriFeatureQuery.Parse(scope.Parameters, layerCrs);
            var items = await image.Catalogue.ListItemsAsync(image.Dataset, request.CancellationToken);
            return RasterCatalogQuery.Query(image.Description, items, query, services, request.CancellationToken);
        }
        catch (Exception exception)
        {
            return EsriErrorMapper.Map(exception);
        }
    }

    /// <summary>The catalog text search over the raster catalog's string fields.</summary>
    internal static async Task<IResult> Find(ImageServerRequest request, ICoordinateTransforms transforms)
    {
        try
        {
            var scope = await ImageServerScope.OpenAsync(request);
            var image = await scope.CatalogueAsync();
            var items = await image.Catalogue.ListItemsAsync(image.Dataset, request.CancellationToken);
            return ImageFindEngine.Find(image.Description, items, scope.Parameters, transforms, request.CancellationToken);
        }
        catch (Exception exception)
        {
            return EsriErrorMapper.Map(exception);
        }
    }
}
