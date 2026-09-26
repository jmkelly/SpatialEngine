namespace Spatial.Client;

/// <summary>
/// The bearer one <see cref="SpatialClient"/> currently holds: set by
/// <c>LoginAsync</c>/<c>RefreshAsync</c>, cleared by <c>LogoutAsync</c>, and
/// defaulted into the admin routes (maps, ingest). Split out with the route
/// groups so every sub-client reads the same value the single client field
/// used to hold (ADR-0040).
/// </summary>
internal sealed class SpatialClientSession
{
    /// <summary>The bearer to send when a route does not name one, or null.</summary>
    public string? Token { get; set; }
}
