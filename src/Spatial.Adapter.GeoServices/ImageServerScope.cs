using Spatial.Contracts;
using Spatial.Contracts.Providers;
using Spatial.Esri.Codec;

namespace Spatial.Adapter.GeoServices;

/// <summary>
/// One opened ImageServer request (spec §8): the negotiated Esri parameters,
/// before and after the resource resolves the publication behind them. Every
/// ImageService resource opens the request the same way — read the
/// parameters, then negotiate the format the resource serves, then resolve
/// the raster publication — and several of them validate their own
/// parameters in between, so the scope is two-phase: opening never resolves,
/// and a resource resolves exactly when it needs the catalogue. Split out of
/// the resource classes so the resolution fan-out lives in one place.
/// </summary>
/// <param name="Request">The request seams the resource was addressed with.</param>
/// <param name="Parameters">The negotiated Esri request parameters.</param>
internal sealed record ImageServerScope(ImageServerRequest Request, EsriRequestParameters Parameters)
{
    /// <summary>Reads the parameters and negotiates <c>f=json</c> for a JSON resource.</summary>
    public static async Task<ImageServerScope> OpenAsync(ImageServerRequest request)
    {
        var scope = await ReadAsync(request);
        EsriFormat.Ensure(scope.Parameter("f"));
        return scope;
    }

    /// <summary>
    /// Reads the parameters and negotiates the authored XML document
    /// (<c>f=xml</c>, ADR-0068) for a metadata resource.
    /// </summary>
    public static async Task<ImageServerScope> OpenXmlAsync(ImageServerRequest request)
    {
        var scope = await ReadAsync(request);
        EsriFormat.EnsureXml(scope.Parameter("f"));
        return scope;
    }

    /// <summary>Reads the parameters without negotiating the response format (<c>exportImage</c>).</summary>
    public static async Task<ImageServerScope> ReadAsync(ImageServerRequest request) =>
        new(request, await EsriRequestParameters.ReadAsync(request.Context, request.CancellationToken));

    /// <summary>Resolves the published Image Service to its raster catalogue and description.</summary>
    public Task<ImageContext> ImageAsync() =>
        ImageFileHandlers.ResolveImageAsync(
            Request.Catalog, Request.Registry, Request.Service, Request.Stores, Request.CancellationToken);

    /// <summary>
    /// Resolves the publication's raster catalogue; a service without an
    /// accessible one is a typed <c>invalid.arguments</c>.
    /// </summary>
    public async Task<ImageContext> CatalogueAsync()
    {
        var image = await ImageAsync();
        ImageFileHandlers.RequireCatalog(image.Description, Request.Service);
        return image;
    }

    /// <summary>The value of one Esri request parameter.</summary>
    public string? Parameter(string name) => Parameters.Get(name);
}

/// <summary>One ImageServer request: the published service, the request context and the store registry.</summary>
internal sealed record ImageServerRequest(
    GeoServicesCatalog Catalog,
    IMapRegistry Registry,
    string Service,
    HttpContext Context,
    IStoreRegistry Stores,
    CancellationToken CancellationToken);

/// <summary>The resolved raster catalogue, dataset name, description, copyright and authored service metadata of one request.</summary>
internal sealed record ImageContext(
    IRasterCatalogue Catalogue,
    string Dataset,
    RasterDatasetDescription Description,
    string? Copyright,
    string? MetadataXml = null);
