using Spatial.Core.Geometry;
using Spatial.Rendering.Skia.Styling;

namespace Spatial.Rendering.Skia.Drawing;

/// <summary>
/// One position a symbol may be placed at: a pixel anchor and the rotation of
/// the label's own frame about it. A line candidate carries the local line
/// direction, so its text runs along the line and its <c>text-offset</c> reads
/// perpendicular to it (ADR-0075).
/// </summary>
internal readonly record struct SymbolCandidate(float X, float Y, float Degrees)
{
    /// <summary>Rotates a point in the candidate's frame into canvas pixels.</summary>
    public (float X, float Y) Apply(float localX, float localY)
    {
        if (Degrees == 0)
        {
            return (X + localX, Y + localY);
        }

        var radians = Degrees * Math.PI / 180;
        var (sin, cos) = (Math.Sin(radians), Math.Cos(radians));
        return (X + (float)(localX * cos - localY * sin), Y + (float)(localX * sin + localY * cos));
    }

    /// <summary>The axis-aligned box that encloses a rectangle in this frame, rotated about the anchor.</summary>
    public PixelBox Box(float left, float top, float width, float height)
    {
        var (ax, ay) = Apply(left, top);
        var (bx, by) = Apply(left + width, top + height);
        return new PixelBox(
            Math.Min(ax, bx), Math.Min(ay, by), Math.Max(ax, bx), Math.Max(ay, by));
    }
}

/// <summary>A pixel-space box, kept free of Skia types so candidate maths stays testable.</summary>
internal readonly record struct PixelBox(float Left, float Top, float Right, float Bottom)
{
    public float Width => Right - Left;

    public float Height => Bottom - Top;

    public bool Intersects(PixelBox other) => Left < other.Right && Right > other.Left
        && Top < other.Bottom && Bottom > other.Top;

    public PixelBox Inflate(float amount) => new(Left - amount, Top - amount, Right + amount, Bottom + amount);
}

/// <summary>
/// Generates the ordered candidates for one symbol feature (ADR-0075). The
/// order is fixed, so the greedy first-fit over the list is reproducible: a
/// point feature offers its position and then the four anchor offsets around
/// it, and a line feature offers positions along the line at
/// <c>symbol-spacing</c>, each aligned to the local direction, then the same
/// positions in reverse for a second pass along the line.
/// </summary>
internal static class SymbolCandidates
{
    /// <summary>
    /// The candidates for a feature, in placement order. Empty when the
    /// geometry carries no position to place at.
    /// </summary>
    public static IReadOnlyList<SymbolCandidate> Generate(
        IGeometry geometry, SymbolOptions options, ViewportProjection projection)
    {
        ArgumentNullException.ThrowIfNull(geometry);
        return options.Placement == SymbolPlacement.Line
            ? AlongLine(geometry, options, projection)
            : AtPoint(geometry, options, projection);
    }

    /// <summary>
    /// A point feature's own position, then the four anchor offsets a label
    /// falls back to when the first box is taken. A polygon or line falls back
    /// to its envelope centre, as before (ADR-0049).
    /// </summary>
    private static IReadOnlyList<SymbolCandidate> AtPoint(
        IGeometry geometry, SymbolOptions options, ViewportProjection projection)
    {
        if (Anchor(geometry) is not { } coordinate)
        {
            return [];
        }

        var (x, y) = projection.ToPixel(coordinate.X, coordinate.Y);
        var reach = (float)(options.Size + options.Padding) * 2;
        return
        [
            new SymbolCandidate(x, y, 0),
            new SymbolCandidate(x, y - reach, 0),
            new SymbolCandidate(x, y + reach, 0),
            new SymbolCandidate(x - reach, y, 0),
            new SymbolCandidate(x + reach, y, 0),
        ];
    }

    /// <summary>
    /// Positions along the projected line at <c>symbol-spacing</c> pixels,
    /// each carrying the direction of the segment it falls in. A line shorter
    /// than the spacing gets one candidate at its midpoint. The forward pass
    /// comes first, then the reverse pass, so a crowded start of the line does
    /// not hide a free label further along.
    /// </summary>
    private static List<SymbolCandidate> AlongLine(
        IGeometry geometry, SymbolOptions options, ViewportProjection projection)
    {
        var candidates = new List<SymbolCandidate>();
        foreach (var path in Paths(geometry, projection))
        {
            AddAlong(path, (float)options.Spacing, candidates);
        }

        if (candidates.Count == 0)
        {
            return candidates;
        }

        var reverse = new List<SymbolCandidate>(candidates.Count);
        for (var index = candidates.Count - 1; index > 0; index--)
        {
            reverse.Add(candidates[index]);
        }

        candidates.AddRange(reverse);
        return candidates;
    }

    private static void AddAlong(IReadOnlyList<(float X, float Y)> path, float spacing, List<SymbolCandidate> candidates)
    {
        if (path.Count < 2 || spacing <= 0)
        {
            return;
        }

        var total = Length(path);
        if (total <= 0)
        {
            return;
        }

        if (total <= spacing)
        {
            var (mid, midDirection) = At(path, total / 2);
            candidates.Add(new SymbolCandidate(mid.X, mid.Y, midDirection));
            return;
        }

        for (var distance = spacing / 2; distance <= total - spacing / 2; distance += spacing)
        {
            var (point, degrees) = At(path, distance);
            candidates.Add(new SymbolCandidate(point.X, point.Y, degrees));
        }
    }

    /// <summary>The point and the direction in degrees at an arc length along the path.</summary>
    private static ((float X, float Y) Point, float Degrees) At(IReadOnlyList<(float X, float Y)> path, double distance)
    {
        var walked = 0.0;
        for (var index = 1; index < path.Count; index++)
        {
            var (x0, y0) = path[index - 1];
            var (x1, y1) = path[index];
            var segment = Math.Sqrt(((x1 - x0) * (double)(x1 - x0)) + ((y1 - y0) * (double)(y1 - y0)));
            if (segment <= 0)
            {
                continue;
            }

            if (walked + segment < distance)
            {
                walked += segment;
                continue;
            }

            var t = (float)((distance - walked) / segment);
            var degrees = (float)(Math.Atan2(y1 - y0, x1 - x0) * 180 / Math.PI);
            return ((x0 + ((x1 - x0) * t), y0 + ((y1 - y0) * t)), degrees);
        }

        return (path[^1], 0);
    }

    private static double Length(IReadOnlyList<(float X, float Y)> path)
    {
        var total = 0.0;
        for (var index = 1; index < path.Count; index++)
        {
            var (x0, y0) = path[index - 1];
            var (x1, y1) = path[index];
            total += Math.Sqrt(((x1 - x0) * (double)(x1 - x0)) + ((y1 - y0) * (double)(y1 - y0)));
        }

        return total;
    }

    /// <summary>The pixel-space coordinate sequences a line geometry carries.</summary>
    private static IEnumerable<IReadOnlyList<(float X, float Y)>> Paths(IGeometry geometry, ViewportProjection projection)
    {
        switch (geometry)
        {
            case ILineString line:
                yield return Project(line, projection);
                break;
            case IMultiLineString multi:
                foreach (var path in MultiPaths(multi, projection))
                {
                    yield return path;
                }

                break;
            case IGeometryParts parts:
                foreach (var path in PartsPaths(parts, projection))
                {
                    yield return path;
                }

                break;
        }
    }

    /// <summary>One projected path per child of a multi-line.</summary>
    private static List<IReadOnlyList<(float X, float Y)>> MultiPaths(
        IMultiLineString multi, ViewportProjection projection)
    {
        var paths = new List<IReadOnlyList<(float X, float Y)>>(multi.LineStrings.Count);
        foreach (var child in multi.LineStrings)
        {
            paths.Add(Project(child, projection));
        }

        return paths;
    }

    /// <summary>The paths of every geometry a part collection carries, depth-first.</summary>
    private static List<IReadOnlyList<(float X, float Y)>> PartsPaths(
        IGeometryParts parts, ViewportProjection projection)
    {
        var paths = new List<IReadOnlyList<(float X, float Y)>>();
        foreach (var child in parts.Geometries)
        {
            paths.AddRange(Paths(child, projection));
        }

        return paths;
    }

    private static List<(float X, float Y)> Project(ILineString line, ViewportProjection projection)
    {
        var points = new List<(float X, float Y)>(line.Sequence.Count);
        for (var index = 0; index < line.Sequence.Count; index++)
        {
            points.Add(projection.ToPixel(line[index].X, line[index].Y));
        }

        return points;
    }

    private static Coordinate? Anchor(IGeometry geometry) => geometry switch
    {
        IPoint point => point.Coordinate,
        IMultiPoint multi => multi.Points.Select(child => child.Coordinate).FirstOrDefault(coordinate => coordinate is not null),
        _ => geometry.Envelope is { } envelope
            ? new Coordinate((envelope.MinX + envelope.MaxX) / 2, (envelope.MinY + envelope.MaxY) / 2)
            : null,
    };
}
