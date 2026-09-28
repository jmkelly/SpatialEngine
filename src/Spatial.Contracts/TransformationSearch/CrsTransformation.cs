namespace Spatial.Contracts.TransformationSearch;

/// <summary>
/// Where a datum-transformation operation applies (ADR-0074). The area of
/// use is a geographic bounding box in degrees, the unit EPSG records extents
/// in: a datum shift is only *meaningful* inside its datum's extent, which is
/// what makes <c>extentOfInterest</c> a filter over candidates rather than a
/// reason to refuse the search. Pure data — the intersection and containment
/// rules live in the provider that owns the catalogue.
/// </summary>
public sealed record CrsAreaOfUse(string Name, double XMin, double YMin, double XMax, double YMax);

/// <summary>
/// A seven-parameter Helmert transformation (EPSG method 9606, the position
/// vector convention the catalogue's TOWGS84 parameters use): the three
/// translations in metres, the three rotations in arc-seconds and the scale
/// in parts per million. A geocentric translation (the three-parameter form)
/// is the same value with zero rotations and scale. Parameters are the
/// *operation's* parameters, so a provider can publish them with the
/// operation rather than hide them in the adapter.
/// </summary>
public sealed record HelmertParameters(double Tx, double Ty, double Tz, double Rx, double Ry, double Rz, double ScalePpm);

/// <summary>
/// One step of a candidate transformation: a single Helmert operation, the
/// direction it runs in (<c>TransformForward</c> false means the operation is
/// applied in reverse — a datum shift is invertible) and the method name
/// clients display.
/// </summary>
public sealed record CrsTransformationStep(
    string Name,
    bool TransformForward,
    string Method,
    HelmertParameters Parameters);

/// <summary>
/// One candidate from a datum-transformation search: a named operation with
/// its steps, the parameters each step applies, the area where the operation
/// is valid, its stated accuracy in metres and whether it is an approximation
/// (a concatenated or reduced-form operation rather than the direct shift).
/// Candidates are values, so any interop surface — the Esri
/// <c>findTransformations</c> listing in particular — can publish them
/// without owning the graph that produced them.
/// </summary>
public sealed record CrsTransformation(
    string Name,
    string Method,
    IReadOnlyList<CrsTransformationStep> Steps,
    CrsAreaOfUse AreaOfUse,
    double AccuracyMetres,
    bool Approximate);

/// <summary>
/// A datum-transformation search: the two CRSs and the optional area of
/// interest (in the source CRS's own coordinates is the caller's business;
/// the contract carries it as the geographic box the provider filters on).
/// </summary>
public sealed record CrsTransformationQuery(string Source, string Target, CrsAreaOfUse? AreaOfInterest = null);
