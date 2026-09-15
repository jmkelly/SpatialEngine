using Spatial.Contracts;
using Spatial.Core.Geometry;

namespace Spatial.Tiling.WebMercator;

/// <summary>
/// The Web-Mercator (EPSG:3857) XYZ tiling scheme (ADR-0046): the origin is
/// the top-left corner of the projected world, tiles are indexed from there,
/// and level <c>z</c> has <c>2^z</c> tiles per axis at a fixed
/// <see cref="TileSize"/>. It is the default <see cref="ITileScheme"/> and
/// the scheme Esri calls "Web Mercator"; another projection is a sibling
/// implementation of the same contract, never a change to the renderer.
/// </summary>
public sealed class WebMercatorTileScheme : ITileScheme
{
    /// <summary>Half the projected world extent — the standard Web-Mercator origin shift.</summary>
    public const double OriginShift = 20037508.342789244;

    private const double StandardDpi = 96.0;
    private const double MetresPerInch = 0.0254;
    private const double EarthRadius = 6378137.0;
    private const int DefaultTileSize = 256;
    private const int DefaultMaxZoom = 23;
    private const int HardMaxZoom = 30;

    private readonly double _initialResolution;
    private readonly TileLevel[] _levels;

    /// <summary>Creates the scheme for a square <paramref name="tileSize"/> and the highest supported <paramref name="maxZoom"/>.</summary>
    public WebMercatorTileScheme(int tileSize = DefaultTileSize, int maxZoom = DefaultMaxZoom)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(tileSize, 1);
        ArgumentOutOfRangeException.ThrowIfNegative(maxZoom);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maxZoom, HardMaxZoom);
        TileSize = tileSize;
        MaxZoom = maxZoom;
        _initialResolution = 2 * Math.PI * EarthRadius / tileSize;
        _levels = BuildLevels();
    }

    /// <inheritdoc />
    public string Id => "webmercator";

    /// <inheritdoc />
    public string Crs => "EPSG:3857";

    /// <inheritdoc />
    public int TileSize { get; }

    /// <inheritdoc />
    public int MinZoom => 0;

    /// <inheritdoc />
    public int MaxZoom { get; }

    /// <inheritdoc />
    public IReadOnlyList<TileLevel> Levels => _levels;

    /// <inheritdoc />
    public bool IsValid(TileCoordinate coordinate)
    {
        if (coordinate.Z < MinZoom || coordinate.Z > MaxZoom)
        {
            return false;
        }

        var span = 1L << coordinate.Z;
        return coordinate.X >= 0 && coordinate.X < span && coordinate.Y >= 0 && coordinate.Y < span;
    }

    /// <inheritdoc />
    public double Resolution(int zoom) => _initialResolution / Math.Pow(2, zoom);

    /// <inheritdoc />
    public Envelope Bounds(TileCoordinate coordinate)
    {
        if (!IsValid(coordinate))
        {
            throw SpatialException.BadArguments(
                FormattableString.Invariant($"Tile {coordinate.Z}/{coordinate.X}/{coordinate.Y} is outside the {Id} scheme."));
        }

        var span = 2 * OriginShift / (1L << coordinate.Z);
        var minX = (-OriginShift) + (coordinate.X * span);
        var maxY = OriginShift - (coordinate.Y * span);
        return new Envelope(minX, maxY - span, minX + span, maxY);
    }

    private TileLevel[] BuildLevels()
    {
        var levels = new TileLevel[MaxZoom + 1];
        for (var zoom = 0; zoom <= MaxZoom; zoom++)
        {
            var resolution = Resolution(zoom);
            levels[zoom] = new TileLevel(zoom, resolution, resolution * StandardDpi / MetresPerInch);
        }

        return levels;
    }
}
