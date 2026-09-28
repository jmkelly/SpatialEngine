using Spatial.Core.Geometry;

namespace Spatial.Esri.Codec;

/// <summary>
/// Which extra ordinates an Esri JSON write carries (spec §10, §9.1.4
/// <c>returnZ</c>/<c>returnM</c>). The engine's geometry values carry Z and
/// M and the canonical binary codec round-trips both, so the projection —
/// not the engine — is where an ordinate is dropped: a false
/// <c>returnZ</c>/<c>returnM</c> narrows the output to the requested
/// ordinates and nothing else. The applied selection also decides the
/// <c>hasZ</c>/<c>hasM</c> flags, which are what make a three-ordinate Esri
/// coordinate array unambiguous (Z when only <c>hasZ</c> is set, M when
/// only <c>hasM</c> is set — the reader's own rule).
/// </summary>
/// <param name="ReturnZ">Whether Z may be written (the default, and what
/// <c>returnZ=true</c> asks for).</param>
/// <param name="ReturnM">Whether M may be written.</param>
public readonly record struct EsriOrdinateOutput(bool ReturnZ, bool ReturnM)
{
    /// <summary>Every ordinate the geometry carries (the default).</summary>
    public static readonly EsriOrdinateOutput All = new(true, true);

    /// <summary>X and Y only: what <c>returnZ=false&amp;returnM=false</c> asks for.</summary>
    public static readonly EsriOrdinateOutput Xy = new(false, false);

    /// <summary>The layout this selection writes for a geometry carrying <paramref name="layout"/>.</summary>
    public CoordinateLayout AppliedTo(CoordinateLayout layout) =>
        CoordinateLayoutExtensions.FromOrdinates(ReturnZ && layout.HasZ(), ReturnM && layout.HasM());
}
