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
/// A datum shift read out of a published grid bundle (ADR-0105): the sub-grid
/// that answered, the file it was read from, the format it is stored in, how
/// the shift is interpolated, and the block of ground the grid covers.
/// <para>
/// This is a step's parameters in place of a <see cref="HelmertParameters"/>,
/// not a seventh Helmert column, because a grid shift is not a seven-parameter
/// transformation in any sense a client could apply: it is a table the
/// provider looks the point up in, and the only honest way to publish one is
/// to name the table. The coverage is published because it is the grid's real
/// extent — a point outside it is not a slightly worse shift but no shift
/// at all, which is what tells a client the provider falls back rather than
/// extrapolates.
/// </para>
/// </summary>
public sealed record GridShiftParameters(
    string GridName,
    string FileName,
    string Format,
    string Interpolation,
    double XMin,
    double YMin,
    double XMax,
    double YMax);

/// <summary>
/// One step of a candidate transformation: a single operation, the direction it
/// runs in (<c>TransformForward</c> false means the operation is applied in
/// reverse — a datum shift is invertible) and the method name clients display.
/// <para>
/// A step is either a Helmert operation or a grid shift, and exactly one of
/// <paramref name="Parameters"/> and <paramref name="GridShift"/> is set: a
/// grid has no seven parameters to publish, and inventing a zero Helmert to
/// stand in for one would be a decorative value a client could mistake for a
/// real one.
/// </para>
/// </summary>
public sealed record CrsTransformationStep(
    string Name,
    bool TransformForward,
    string Method,
    HelmertParameters? Parameters,
    GridShiftParameters? GridShift = null);

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
