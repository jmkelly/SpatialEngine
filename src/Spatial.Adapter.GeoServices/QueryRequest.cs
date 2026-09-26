using Spatial.Contracts.Providers;

namespace Spatial.Adapter.GeoServices;

/// <summary>
/// One feature-query request's seams: the published service, the layer and
/// map registries, the caller's context and the store registry the layers
/// resolve through. Grouping them keeps each handler's signature to what that
/// one operation actually varies on.
/// </summary>
internal sealed record QueryRequest(
    GeoServicesCatalog Catalog,
    IMapRegistry Registry,
    string Service,
    HttpContext Context,
    IStoreRegistry Stores,
    CancellationToken CancellationToken);
