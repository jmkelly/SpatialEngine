using Spatial.Contracts.TransformationSearch;

namespace Spatial.Transformations.ProjNet.Grids;

/// <summary>
/// Builds the grid-backed candidate for a pair of datums (ADR-0105).
/// <para>
/// A grid operation is not a new kind of graph: ADR-0087's structure already
/// has a hub at WGS 84 and a step that is a value with a method of its own,
/// so a grid slots in as one more step kind on the same path. Between two
/// datums the candidate is therefore the same concatenation, with the grid
/// standing in for the Helmert leg of whichever datum it serves and the other
/// leg left as the Helmert it always was.
/// </para>
/// <para>
/// The accuracy is derived the way ADR-0087 derives a Helmert one: the grid's
/// own worst-node accuracy replaces that datum's registered Helmert accuracy
/// and the legs still combine in quadrature. Nothing here restates a
/// published figure, so the listing cannot disagree with the file it was
/// derived from.
/// </para>
/// </summary>
internal static class GridShiftCandidate
{
    private const string Interpolation = "bilinear";

    /// <summary>
    /// The grid-backed candidate between two datums, or null when neither is
    /// served by a deployed bundle — in which case the Helmert candidates are
    /// the whole answer and say so themselves.
    /// </summary>
    public static CrsTransformation? Build(DatumNode from, DatumNode to, DatumShiftGridRegistry registry)
    {
        var fromGrid = registry.TryGet(from.Code, out var forwardGrid) ? forwardGrid : null;
        var toGrid = registry.TryGet(to.Code, out var backwardGrid) ? backwardGrid : null;
        if (fromGrid is null && toGrid is null)
        {
            return null;
        }

        var legs = Legs(from, to, fromGrid, toGrid);
        return new CrsTransformation(
            $"{from.Code}_To_{to.Code}_Grid_{fromGrid?.Name ?? toGrid!.Name}",
            Method(legs),
            [.. legs.Select(leg => leg.Step)],
            Area(legs),
            Math.Sqrt(legs.Sum(leg => leg.AccuracyMetres * leg.AccuracyMetres)),
            // A candidate is only as exact as its least exact leg, and a
            // Helmert leg is the approximation; a pure grid-to-world shift is
            // the published operation itself.
            Approximate: legs.Any(leg => leg.IsHelmert));
    }

    /// <summary>
    /// The legs of the path, in order: the source datum to WGS 84, then WGS 84
    /// to the target. A leg is a grid where a bundle serves the datum and a
    /// Helmert where it does not, and a datum that is WGS 84 itself has no leg
    /// and contributes no accuracy at all — which is what makes a grid against
    /// WGS 84 as accurate as the grid's own worst node.
    /// </summary>
    private static List<Leg> Legs(DatumNode from, DatumNode to, DatumShiftGrid? fromGrid, DatumShiftGrid? toGrid)
    {
        var world = DatumTransformationGraph.WorldCode;
        var legs = new List<Leg>();
        if (fromGrid is not null)
        {
            legs.Add(Leg.FromGrid(fromGrid, from.Code, world, transformForward: true));
        }
        else if (!HelmertAlgebra.IsNull(from.ToWgs84))
        {
            legs.Add(Leg.FromHelmert(from, to: null, from.ToWgs84));
        }

        if (toGrid is not null)
        {
            legs.Add(Leg.FromGrid(toGrid, world, to.Code, transformForward: false));
        }
        else if (!HelmertAlgebra.IsNull(to.ToWgs84))
        {
            legs.Add(Leg.FromHelmert(from: null, to, HelmertAlgebra.Invert(to.ToWgs84)));
        }

        return legs;
    }

    /// <summary>
    /// Where the whole operation can be executed. Unlike a Helmert
    /// concatenation this intersects rather than unions: a grid leg is only
    /// defined inside its own block, so that block bounds the operation
    /// whatever the other leg covers, and a client asking about ground no grid
    /// covers is not offered an operation that cannot run there.
    /// </summary>
    private static CrsAreaOfUse Area(List<Leg> legs)
    {
        var area = new CrsAreaOfUse("the WGS 84 pivot", -180.0, -90.0, 180.0, 90.0);
        foreach (var leg in legs)
        {
            area = DatumTransformationGraph.Intersect(area, leg.AreaOfUse);
        }

        return HelmertAlgebra.IsEmpty(area)
            ? new CrsAreaOfUse("the WGS 84 pivot", -180.0, -90.0, 180.0, 90.0)
            : area;
    }

    /// <summary>
    /// The method text. It names the bundle the shift came from and the
    /// interpolation, because those are the two facts a client cannot infer
    /// from a set of parameters; it says when a Helmert leg is still in the
    /// path; and it says plainly that the transform verb does not yet apply
    /// the grid.
    /// <para>
    /// That last clause is the price of ranking this candidate first. ADR-0087
    /// §6 promises that the first candidate is the path the engine applies, and
    /// the grid is the more accurate operation so it ranks first by accuracy —
    /// but the transform path still applies the classic Helmert (ADR-0105
    /// §applied). A listing that read as though the grid were being applied
    /// would be claiming a sub-metre answer the engine is not giving, so the
    /// method text carries the truth and the gap is recorded rather than papered
    /// over. The clause goes when the transform verb does.
    /// </para>
    /// </summary>
    private static string Method(List<Leg> legs)
    {
        var grids = legs.Where(leg => leg.Grid is not null).Select(leg => leg.Grid!).ToArray();
        var names = string.Join(" and ", grids.Select(grid => grid.Name));
        var blocks = grids.Length == 1 ? "the block the grid covers" : "the blocks the grids cover";
        var described = grids.Length == 1
            ? $"NTv2 grid shift, sub-grid {names} from bundle {grids[0].FileName}, {Interpolation}ly interpolated over {blocks}"
            : $"NTv2 grid shift concatenated through WGS 84, sub-grids {names} from bundles {string.Join(" and ", grids.Select(grid => grid.FileName))}, {Interpolation}ly interpolated over {blocks}";
        var composed = legs.Any(leg => leg.IsHelmert)
            ? $"{described}, with a classic Helmert on the leg no grid serves"
            : described;
        return $"{composed} (published from the deployed grid; the transform verb still applies the classic Helmert: ADR-0105 §applied)";
    }

    /// <summary>One leg of the path: the operation applied, and what it costs.</summary>
    private sealed record Leg(
        CrsTransformationStep Step,
        CrsAreaOfUse AreaOfUse,
        double AccuracyMetres,
        DatumShiftGrid? Grid)
    {
        public bool IsHelmert => Grid is null;

        /// <summary>
        /// A grid leg. The published coverage is the grid's own block rather
        /// than a registered extent, because the block is where the shift
        /// actually exists: a shift tabulated over Britain is not available
        /// over France whatever a registry says about where its datum is used.
        /// </summary>
        public static Leg FromGrid(DatumShiftGrid grid, string fromName, string toName, bool transformForward)
        {
            var name = $"{fromName}_To_{toName}_{grid.Name}";
            var method = $"NTv2 grid shift, sub-grid {grid.Name} from bundle {grid.FileName}, {Interpolation}ly interpolated";
            return new Leg(
                new CrsTransformationStep(
                    name,
                    transformForward,
                    method,
                    Parameters: null,
                    GridShift: new GridShiftParameters(
                        grid.Name,
                        grid.FileName,
                        GridFormats.Standard(grid.Format),
                        Interpolation,
                        grid.XMin,
                        grid.YMin,
                        grid.XMax,
                        grid.YMax)),
                new CrsAreaOfUse($"the block the {grid.Name} grid covers", grid.XMin, grid.YMin, grid.XMax, grid.YMax),
                grid.AccuracyMetres,
                grid);
        }

        /// <summary>
        /// A Helmert leg, still carrying the accuracy the registry states for
        /// that datum. The leg between WGS 84 and a datum runs the datum's own
        /// shift in reverse, which is the client's to apply, exactly as
        /// ADR-0087 publishes the concatenated operation.
        /// </summary>
        public static Leg FromHelmert(DatumNode? from, DatumNode? to, HelmertParameters parameters)
        {
            var (fromName, toName) = (from?.Code ?? DatumTransformationGraph.WorldCode, to?.Code ?? DatumTransformationGraph.WorldCode);
            var name = $"{fromName}_To_{toName}_Helmert";
            const string Method = "Position Vector transformation (geog2D domain)";
            var datum = from ?? to
                ?? throw new ArgumentException("A Helmert leg names at least one of the datums it shifts.", nameof(from));
            return new Leg(
                new CrsTransformationStep(name, true, Method, parameters),
                datum.AreaOfUse,
                datum.AccuracyMetres,
                Grid: null);
        }
    }
}
