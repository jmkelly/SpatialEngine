namespace Spatial.Core.Geometry;

/// <summary>
/// Identity of a coordinate reference system: an authority plus a code, for
/// example EPSG:4326. Identity only — definitions and transformations are
/// plugin concerns (ADR-0009).
/// </summary>
public readonly record struct CoordinateReference
{
    public CoordinateReference(string authority, string code)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(authority);
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        Authority = authority;
        Code = code;
    }

    public string Authority { get; }

    public string Code { get; }

    /// <summary>Shorthand for an EPSG-based CRS identity.</summary>
    public static CoordinateReference Epsg(int code) =>
        new("EPSG", code.ToString(System.Globalization.CultureInfo.InvariantCulture));

    /// <inheritdoc />
    public override string ToString() => $"{Authority}:{Code}";
}
