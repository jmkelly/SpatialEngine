using System.Text;
using Spatial.Esri.Codec;

namespace Spatial.Adapter.GeoServices;

/// <summary>
/// The authored-metadata resources (ADR-0068): the service-level
/// <c>metadata</c> document and the per-item <c>{rasterId}/metadata</c>
/// document, both served byte-faithful as <c>application/xml</c> like the
/// reference. Without authoring a resource is a typed <c>not.found</c>,
/// never an invented document; only an absent format or <c>f=xml</c>
/// passes. Split out of <see cref="ImageServerEndpoints"/> — which maps the
/// routes — so the XML negotiation and the two documents live together.
/// </summary>
internal static class ImageMetadataResources
{
    /// <summary>
    /// The service-level Metadata (S3 metadata/): the publication's
    /// authored XML document. This replaces the T-042 JSON dataset
    /// projection.
    /// </summary>
    internal static async Task<IResult> Service(ImageServerRequest request)
    {
        try
        {
            var scope = await ImageServerScope.OpenXmlAsync(request);
            var image = await scope.ImageAsync();
            return Document(request.Service, image.MetadataXml);
        }
        catch (Exception exception)
        {
            return EsriErrorMapper.Map(exception);
        }
    }

    /// <summary>
    /// The per-item Raster Metadata (S3 raster-metadata/): the catalog
    /// item's authored XML document. An item without authored metadata — or
    /// an unknown item, or a service without a catalog — is a typed
    /// <c>not.found</c>/<c>invalid.arguments</c> failure.
    /// </summary>
    internal static async Task<IResult> Item(ImageServerRequest request, long rasterId)
    {
        try
        {
            var scope = await ImageServerScope.OpenXmlAsync(request);
            var image = await scope.CatalogueAsync();
            var item = await ImageFileHandlers.FindItemAsync(image, rasterId, request.CancellationToken);
            return item.MetadataXml is null
                ? throw GeoServicesErrors.NotFound($"Raster catalog item {rasterId} does not have authored metadata.")
                : Results.Text(item.MetadataXml, "application/xml", Encoding.UTF8);
        }
        catch (Exception exception)
        {
            return EsriErrorMapper.Map(exception);
        }
    }

    /// <summary>
    /// Serves one authored metadata document byte-faithful as
    /// <c>application/xml</c>; a service without authoring answers a typed
    /// <c>not.found</c> instead of an invented document.
    /// </summary>
    private static IResult Document(string service, string? xml) =>
        xml is null
            ? throw GeoServicesErrors.NotFound($"Image Service '{service}' does not have authored service metadata.")
            : Results.Text(xml, "application/xml", Encoding.UTF8);
}
