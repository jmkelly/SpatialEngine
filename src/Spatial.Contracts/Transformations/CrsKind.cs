namespace Spatial.Contracts.Transformations;

/// <summary>
/// The family of a coordinate reference system, as reported by the
/// CRS description service (ADR-0027/ADR-0033). The engine's value
/// model knows CRS identities structurally (ADR-0009); the description —
/// including the family — is service knowledge from the transformation
/// adapter's catalogue.
/// </summary>
public enum CrsKind
{
    /// <summary>A latitude/longitude (angular) CRS on an ellipsoid.</summary>
    Geographic,

    /// <summary>A projected (planar, linear) CRS, typically from a map projection.</summary>
    Projected,

    /// <summary>A three-dimensional CRS with an origin at the earth's centre.</summary>
    Geocentric,

    /// <summary>A vertical CRS (gravity-related height or ellipsoidal height).</summary>
    Vertical,

    /// <summary>Two or more independent CRSs stacked into one (e.g. horizontal + vertical).</summary>
    Compound,

    /// <summary>Anything the provider could not classify otherwise.</summary>
    Other,
}
