using Spatial.Contracts;
using Spatial.Contracts.Providers;

namespace Spatial.Adapter.GeoServices;

/// <summary>
/// One attachment endpoint's request: the published service and layer plus
/// the seams every attachment resource needs. Grouping them keeps each
/// handler's signature to the ids its own resource addresses.
/// </summary>
internal sealed record AttachmentRequest(
    GeoServicesCatalog Catalog,
    IMapRegistry Registry,
    string Service,
    int LayerId,
    HttpContext Context,
    IStoreRegistry Stores,
    CancellationToken CancellationToken);
