using System.Diagnostics.CodeAnalysis;
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

    /// <summary>The method the registry publishes for a registered null operation (ADR-0163).</summary>
    private const string TranslationMethod = "Geocentric translations (geog2D domain)";

    /// <summary>
    /// The grid-backed candidate between two datums, or null when neither is
    /// served by a deployed bundle — in which case the Helmert candidates are
    /// the whole answer and say so themselves.
    /// </summary>
    public static CrsTransformation? Build(DatumNode from, DatumNode to, DatumShiftGridRegistry registry)
    {
        var fromGrid = registry.TryGet(from.Code, out var forwardGrid, out var fromOperation) ? forwardGrid : null;
        var toGrid = registry.TryGet(to.Code, out var backwardGrid, out var toOperation) ? backwardGrid : null;
        if (fromGrid is null && toGrid is null)
        {
            return null;
        }

        if (!ResolveTargets(fromOperation, toOperation, fromGrid, toGrid, out var fromTarget, out var toTarget))
        {
            return null;
        }

        return Assemble(from, to, fromGrid, toGrid, Legs(from, to, fromGrid, toGrid, fromTarget, toTarget));
    }

    /// <summary>
    /// The datums the two bundles' shifts land in. A row naming a datum the
    /// catalogue does not serve cannot be published at all, because the only
    /// way to carry on from a datum this engine has no node for is to pretend
    /// the bundle reached the pivot — which is the invented claim the
    /// catalogue exists to avoid. The Helmert candidates stand instead, and
    /// say so themselves.
    /// </summary>
    private static bool ResolveTargets(
        EpsgGridShiftOperations.GridShiftOperation? fromOperation,
        EpsgGridShiftOperations.GridShiftOperation? toOperation,
        DatumShiftGrid? fromGrid,
        DatumShiftGrid? toGrid,
        out DatumNode? fromTarget,
        out DatumNode? toTarget)
    {
        fromTarget = null;
        toTarget = null;
        // A row names the datum its bundle's shifts land in, and that is not
        // always the pivot: NADCON is registered for NAD27 to NAD83
        // (EPSG:1241), so the path continues from NAD83 rather than from WGS
        // 84.
        if (fromGrid is not null && !TargetOf(fromOperation, out fromTarget))
        {
            return false;
        }

        if (toGrid is not null && !TargetOf(toOperation, out toTarget))
        {
            return false;
        }

        return true;
    }

    /// <summary>The candidate over its legs: the grid's own worst-node accuracy replaces that datum's
    /// registered Helmert accuracy and the legs still combine in quadrature.</summary>
    private static CrsTransformation Assemble(
        DatumNode from, DatumNode to, DatumShiftGrid? fromGrid, DatumShiftGrid? toGrid, List<Leg> legs) =>
        new(
            $"{from.Code}_To_{to.Code}_Grid_{fromGrid?.Name ?? toGrid!.Name}",
            Method(legs),
            [.. legs.Select(leg => leg.Step)],
            Area(legs),
            Math.Sqrt(legs.Sum(leg => leg.AccuracyMetres * leg.AccuracyMetres)),
            // A candidate is only as exact as its least exact leg, and a
            // Helmert leg is the approximation; a pure grid-to-world shift is
            // the published operation itself. The registered null operation
            // behind a grid that reaches a datum rather than the pivot moves
            // nothing, so it is not an approximation either — it is the
            // operation EPSG registers for that pair, priced at the accuracy
            // it states (ADR-0163, ADR-0180).
            Approximate: legs.Any(leg => leg.IsHelmert));

    /// <summary>
    /// The node for the datum a grid operation's shifts land in, or false when
    /// the catalogue does not serve it.
    /// </summary>
    private static bool TargetOf(
        EpsgGridShiftOperations.GridShiftOperation? operation,
        [NotNullWhen(true)] out DatumNode? target)
    {
        target = null;
        return operation is not null && ProjEpsgCatalog.TryGetDatum(operation.TargetDatumCode, out target);
    }

    /// <summary>
    /// The legs of the path, in order: the source datum to WGS 84, then WGS 84
    /// to the target. A leg is a grid where a bundle serves the datum and a
    /// Helmert where it does not, and a datum that is WGS 84 itself has no leg
    /// and contributes no accuracy at all — which is what makes a grid against
    /// WGS 84 as accurate as the grid's own worst node.
    /// <para>
    /// A grid that lands on a datum other than the pivot is followed by that
    /// datum's own leg: the path a NADCON row publishes is NAD27 by the pair
    /// to NAD83, and then from NAD83 to WGS 84 by whatever EPSG registers for
    /// that pair — for NAD83, a shift of nothing worth 4.0 m (ADR-0163). The
    /// leg is published whether it moves or not, because an accuracy with no
    /// operation behind it is a number nobody published (ADR-0180).
    /// </para>
    /// </summary>
    private static List<Leg> Legs(
        DatumNode from,
        DatumNode to,
        DatumShiftGrid? fromGrid,
        DatumShiftGrid? toGrid,
        DatumNode? fromTarget,
        DatumNode? toTarget)
    {
        var world = DatumTransformationGraph.WorldCode;
        var legs = new List<Leg>();
        AddFromLeg(legs, from, fromGrid, fromTarget, world);
        AddToLeg(legs, to, toGrid, toTarget, world);
        return legs;
    }

    /// <summary>The source datum's leg: the grid where a bundle serves it, the Helmert where it does not.</summary>
    private static void AddFromLeg(List<Leg> legs, DatumNode from, DatumShiftGrid? fromGrid, DatumNode? fromTarget, string world)
    {
        if (fromGrid is not null)
        {
            legs.Add(Leg.FromGrid(fromGrid, from.Code, fromTarget!.Code, transformForward: true));
            if (Leg.FromTarget(fromTarget, world) is { } fromBehind)
            {
                legs.Add(fromBehind);
            }
        }
        else if (!HelmertAlgebra.IsNull(from.ToWgs84))
        {
            legs.Add(Leg.FromHelmert(from, to: null, from.ToWgs84));
        }
    }

    /// <summary>The target datum's leg: the grid where a bundle serves it, the Helmert where it does not.</summary>
    private static void AddToLeg(List<Leg> legs, DatumNode to, DatumShiftGrid? toGrid, DatumNode? toTarget, string world)
    {
        if (toGrid is not null)
        {
            legs.Add(Leg.FromGrid(toGrid, toTarget!.Code, to.Code, transformForward: false));
            if (Leg.FromTarget(toTarget, world) is { } toBehind)
            {
                legs.Add(toBehind);
            }
        }
        else if (!HelmertAlgebra.IsNull(to.ToWgs84))
        {
            legs.Add(Leg.FromHelmert(from: null, to, HelmertAlgebra.Invert(to.ToWgs84)));
        }
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
        var area = CrsAreaOfUse.One("the WGS 84 pivot", -180.0, -90.0, 180.0, 90.0);
        foreach (var leg in legs)
        {
            area = DatumTransformationGraph.Intersect(area, leg.AreaOfUse);
        }

        return DatumTransformationGraph.IsEmpty(area)
            ? CrsAreaOfUse.One("the WGS 84 pivot", -180.0, -90.0, 180.0, 90.0)
            : area;
    }

    /// <summary>
    /// The method text. It names the bundle the shift came from and the
    /// interpolation, because those are the two facts a client cannot infer
    /// from a set of parameters; and it says when a Helmert leg is still in the
    /// path.
    /// <para>
    /// It also says how the two are chosen, which is the part a client has to
    /// know to read a coordinate: the grid over the ground the grid covers and
    /// the Helmert everywhere else, per coordinate (ADR-0107). Until that slice
    /// landed this text also carried the opposite claim — that the transform
    /// verb still applied the Helmert — because ADR-0087 §6 promises the first
    /// candidate is the path the engine applies and that promise was knowingly
    /// broken. It is no longer broken, so the clause is gone rather than
    /// reworded.
    /// </para>
    /// </summary>
    private static string Method(List<Leg> legs)
    {
        var grids = legs.Where(leg => leg.Grid is not null).Select(leg => leg.Grid!).ToArray();
        var names = string.Join(" and ", grids.Select(grid => grid.Name));
        var blocks = grids.Length == 1 ? "the block the grid covers" : "the blocks the grids cover";
        var described = grids.Length == 1
            ? $"{GridFormats.Standard(grids[0].Format)} grid shift, sub-grid {names} from bundle {grids[0].FileName}, {Interpolation}ly interpolated over {blocks}"
            : $"{Standard(grids)} grid shift concatenated through WGS 84, sub-grids {names} from bundles {string.Join(" and ", grids.Select(grid => grid.FileName))}, {Interpolation}ly interpolated over {blocks}";
        var composed = legs.Where(leg => leg.Grid is null).Select(leg => leg.Described).ToArray();
        return composed.Length == 0
            ? $"{described} (the transform verb applies the grid per coordinate where it covers the ground, and the Helmert elsewhere)"
            : $"{described}, with {string.Join(" and ", composed)} on the leg no grid serves (the transform verb applies the grid per coordinate where it covers the ground, and the Helmert elsewhere)";
    }

    /// <summary>
    /// The standards behind a concatenated path, named once when they agree
    /// and separately when they do not: two grids in one candidate may have
    /// been read from different formats, and the client is told which.
    /// </summary>
    private static string Standard(DatumShiftGrid[] grids)
    {
        var formats = grids.Select(grid => GridFormats.Standard(grid.Format)).Distinct().ToArray();
        return formats.Length == 1 ? formats[0] : string.Join(" and ", formats);
    }

    /// <summary>One leg of the path: the operation applied, and what it costs.</summary>
    private sealed record Leg(
        CrsTransformationStep Step,
        CrsAreaOfUse AreaOfUse,
        double AccuracyMetres,
        DatumShiftGrid? Grid,
        bool RegisteredNull = false)
    {
        public bool IsHelmert => Grid is null && !RegisteredNull;

        /// <summary>
        /// How this leg is described in the method text, which is the sentence
        /// a client reads to know what is standing behind the grid.
        /// </summary>
        public string Described => RegisteredNull
            ? $"the registered null operation {Step.Name}"
            : "a classic Helmert";

        /// <summary>
        /// The leg from a datum a grid lands on to the pivot, or null when that
        /// datum is the pivot and the grid is the whole of the path. A datum
        /// that realises WGS 84 without moving is not the absence of a leg: it
        /// is the operation EPSG registers for the pair, published with the
        /// accuracy that record states (ADR-0163) and with the parameters that
        /// record carries, all of them zero.
        /// </summary>
        public static Leg? FromTarget(DatumNode target, string world)
        {
            if (string.Equals(target.Code, world, StringComparison.Ordinal))
            {
                return null;
            }

            var name = $"{target.Code}_To_{world}_Geocentric_Translation";
            return HelmertAlgebra.IsNull(target.ToWgs84)
                ? new Leg(
                    new CrsTransformationStep(name, true, TranslationMethod, target.ToWgs84),
                    target.AreaOfUse,
                    target.AccuracyMetres,
                    Grid: null,
                    RegisteredNull: true)
                : FromHelmert(from: null, target, HelmertAlgebra.Invert(target.ToWgs84));
        }

        /// <summary>
        /// A grid leg. The published coverage is the grid's own block rather
        /// than a registered extent, because the block is where the shift
        /// actually exists: a shift tabulated over Britain is not available
        /// over France whatever a registry says about where its datum is used.
        /// </summary>
        public static Leg FromGrid(DatumShiftGrid grid, string fromName, string toName, bool transformForward)
        {
            var name = $"{fromName}_To_{toName}_{grid.Name}";
            var method = $"{GridFormats.Standard(grid.Format)} grid shift, sub-grid {grid.Name} from bundle {grid.FileName}, {Interpolation}ly interpolated";
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
                CrsAreaOfUse.One($"the block the {grid.Name} grid covers", grid.XMin, grid.YMin, grid.XMax, grid.YMax),
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
