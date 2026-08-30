namespace Spatial.Core.Geometry;

/// <summary>
/// A single spatial coordinate. X and Y are always present; Z and M are
/// optional (<c>null</c> means the ordinate is absent, <see cref="double.NaN"/>
/// means the ordinate is present but unknown).
/// </summary>
public readonly record struct Coordinate(double X, double Y, double? Z = null, double? M = null);
