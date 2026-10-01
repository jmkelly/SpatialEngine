using Spatial.Contracts.TransformationSearch;
using Spatial.Transformations.ProjNet.Grids;

namespace Spatial.Transformations.ProjNet;

/// <summary>
/// One node of the datum-transformation graph: a datum, the Helmert shift that
/// takes it to WGS 84, the accuracy of that shift in metres and the area the
/// datum is used over. Nodes are values, so a search never has to know how the
/// catalogue stores them — nor whether the shift came out of a definition and
/// the accuracy out of the operation table (ADR-0086).
/// </summary>
internal sealed record DatumNode(
    string Code,
    string Name,
    HelmertParameters ToWgs84,
    double AccuracyMetres,
    CrsAreaOfUse AreaOfUse);

/// <summary>
/// The datum-transformation graph (ADR-0087). Every datum node carries its
/// shift to WGS 84, so the graph is a hub-and-spoke of Helmert operations
/// rather than a table of hand-written pairs: between two datums it composes
/// the direct geocentric operation, the path concatenated through the WGS 84
/// pivot, and the same shift reduced to a three-parameter translation. Where a
/// datum shift grid is deployed the graph adds a grid-backed candidate in
/// front of them (ADR-0105), because a grid is the operation the engine
/// actually applies and the Helmert is the fallback behind it.
///
/// Area of use follows the EPSG rule for the two shapes of Helmert operation —
/// a direct shift is valid where both datums apply (the intersection), a
/// concatenated operation wherever either step applies (the union) — which is
/// what turns <c>extentOfInterest</c> into a filter instead of a refusal. A
/// grid-backed operation is the one case that intersects instead: a step that
/// can only be executed inside its block bounds the whole operation to that
/// block, whatever the other leg covers. Accuracies combine in quadrature, and
/// the reduced translation carries the first-order bound on the rotation it
/// drops, so no accuracy here is a round number picked for convenience.
/// <para>
/// Both set operations are rectangle algebra over the boxes of an area of use
/// and neither of them merges, because an area of use may be two rectangles
/// either side of the antimeridian and the merge that would collapse them
/// would fabricate one running from the east across the world (ADR-0111).
/// </para>
/// </summary>
internal static class DatumTransformationGraph
{
    private const string PositionVectorMethod = "Position Vector transformation (geog2D domain)";
    private const string TranslationMethod = "Geocentric translations (geog2D domain)";
    private const string OrdnanceSurvey = "Ordnance Survey of Great Britain 1936";

    /// <summary>
    /// The pivot every datum in the catalogue is published against: the
    /// catalogue's own WGS 84 definition, not a literal restated here, so the
    /// shift the graph composes is the shift the engine applies.
    /// </summary>
    private static readonly DatumNode World = ProjEpsgCatalog.WorldDatum();

    /// <summary>
    /// The pivot's short token, as the operation table spells it. Read from
    /// the node rather than typed out, so the name a published step carries is
    /// the one the catalogue uses.
    /// </summary>
    internal static string WorldCode => World.Code;

    /// <summary>
    /// The candidate operations between two datum nodes, best accuracy first.
    ///
    /// The search is symmetric: operations are built in the catalogued datum
    /// order and a reversed request runs each of them backwards, so a client
    /// asking 4277→4326 is answered with the same operations 4326→4277 lists,
    /// marked <c>transformForward: false</c>, rather than with a second,
    /// differently-parameterised set.
    /// </summary>
    public static IReadOnlyList<CrsTransformation> Search(
        DatumNode from,
        DatumNode to,
        DatumShiftGridRegistry grids,
        CrsAreaOfUse? areaOfInterest = null)
    {
        var forward = string.CompareOrdinal(CanonicalKey(from), CanonicalKey(to)) <= 0;
        var candidates = forward ? Candidates(from, to, grids) : Candidates(to, from, grids);
        return candidates
            .Where(candidate => Covers(candidate.AreaOfUse, areaOfInterest))
            .Select(candidate => forward ? candidate : Backwards(candidate))
            // A stable sort by accuracy alone: the candidates are built in
            // preference order, so an operation that is no less accurate
            // (the full Helmert against its own three-parameter reduction)
            // keeps that order.
            .OrderBy(candidate => candidate.AccuracyMetres)
            .ToArray();
    }

    private static CrsTransformation[] Candidates(
        DatumNode from,
        DatumNode to,
        DatumShiftGridRegistry grids)
    {
        var direct = HelmertAlgebra.Compose(from.ToWgs84, HelmertAlgebra.Invert(to.ToWgs84));
        var grid = GridShiftCandidate.Build(from, to, grids);
        if (HelmertAlgebra.IsNull(direct))
        {
            // A composed shift of nothing is not the absence of an operation:
            // it is the operation EPSG registers when a datum realises WGS 84
            // without moving, and the registry states what that realisation is
            // worth. See ADR-0163 and NullOperation below.
            return IsRegisteredNullPair(from, to)
                ? grid is null ? [NullOperation(from, to, direct)] : [grid, NullOperation(from, to, direct)]
                : grid is null ? [] : [grid];
        }
        var candidates = new List<CrsTransformation> { Direct(from, to, direct, grid is not null) };
        if (grid is not null)
        {
            // Ahead of the Helmert it replaces. The ranking below is by stated
            // accuracy and a deployed grid is the more accurate operation, so
            // putting it here as well is what makes it the candidate ADR-0087
            // promises is the path the engine applies.
            candidates.Insert(0, grid);
        }

        candidates.Add(Concatenated(from, to));
        candidates.Add(Reduced(from, to, direct));
        return [.. candidates];
    }

    /// <summary>
    /// Whether a pair whose composed shift moves nothing is a pair the registry
    /// registers an operation for. Two datums that both realise WGS 84 without
    /// moving are published as one registered null operation carrying the
    /// accuracy EPSG states for them (ADR-0163); a datum against itself is not
    /// a pair at all, and a pair whose legs are both the unregistered pivot has
    /// nothing behind it.
    /// </summary>
    private static bool IsRegisteredNullPair(DatumNode from, DatumNode to) =>
        !string.Equals(from.Code, to.Code, StringComparison.Ordinal) && CombinedAccuracy(from, to) > 0.0;

    /// <summary>
    /// The registered null operation: one geocentric-translations step whose
    /// composed parameters are all zero, published with the accuracy the two
    /// datums' registered operations state between them, over the ground both
    /// apply (ADR-0163).
    /// <para>
    /// It is published alone, and that is the whole of the decision. The
    /// concatenated form would have no steps at all — both legs are null — and
    /// the reduced form would restate the same three zero translations with a
    /// wider area of use at the same accuracy, which is a broader claim for no
    /// extra ground of applicability. Three names for a shift that moves
    /// nothing is three claims about the same ground.
    /// </para>
    /// <para>
    /// The method is the registry's own for these operations (EPSG:9603
    /// geocentric translations in the geog2D domain), not the position-vector
    /// method the Helmert candidates carry: there are no rotations and no scale
    /// difference to apply, and a method string naming them would be decoration.
    /// </para>
    /// </summary>
    private static CrsTransformation NullOperation(DatumNode from, DatumNode to, HelmertParameters parameters)
    {
        var name = $"{from.Code}_To_{to.Code}_Geocentric_Translation";
        return new CrsTransformation(
            name,
            TranslationMethod,
            [new CrsTransformationStep(name, true, TranslationMethod, parameters)],
            Intersect(from.AreaOfUse, to.AreaOfUse),
            CombinedAccuracy(from, to),
            Approximate: false);
    }

    /// <summary>
    /// The direct shift: one Helmert step carrying the composed parameters.
    /// This is the fallback path once a grid is deployed, and the path the
    /// engine applies when none is, so it is offered first among the Helmert
    /// candidates and an interop surface can name it back to a client.
    /// </summary>
    private static CrsTransformation Direct(
        DatumNode from,
        DatumNode to,
        HelmertParameters parameters,
        bool gridDeployed)
    {
        var name = OperationName(from, to);
        var accuracy = CombinedAccuracy(from, to);
        var method = Method(PositionVectorMethod, from, to, accuracy, gridDeployed);
        return new CrsTransformation(
            name,
            method,
            [new CrsTransformationStep(name, true, method, parameters)],
            Intersect(from.AreaOfUse, to.AreaOfUse),
            accuracy,
            Approximate: false);
    }

    /// <summary>
    /// The same shift written the way a concatenated operation is published:
    /// each datum's own step against WGS 84, in order. Where one datum is WGS
    /// 84 itself there is nothing to concatenate and the single remaining step
    /// is the whole operation — numerically the direct one, and published
    /// separately because the two claim different areas of validity.
    /// </summary>
    private static CrsTransformation Concatenated(DatumNode from, DatumNode to)
    {
        var steps = new List<CrsTransformationStep>();
        if (!HelmertAlgebra.IsNull(from.ToWgs84))
        {
            steps.Add(Step(OperationName(from, World), from, World, from.ToWgs84));
        }

        if (!HelmertAlgebra.IsNull(to.ToWgs84))
        {
            steps.Add(Step(OperationName(World, to), World, to, HelmertAlgebra.Invert(to.ToWgs84)));
        }

        return new CrsTransformation(
            $"{OperationName(from, to)}_via_{World.Code}",
            $"{PositionVectorMethod} concatenated through {World.Name}",
            steps,
            Union(from.AreaOfUse, to.AreaOfUse),
            CombinedAccuracy(from, to),
            Approximate: true);
    }

    /// <summary>
    /// The same shift reduced to its three translations — the operation a
    /// toolchain applies when it cannot carry rotations or scale. Dropping
    /// them costs the stated bound on the deviation they caused: metres for
    /// OSGB36, nothing at all for the datums that have none.
    /// </summary>
    private static CrsTransformation Reduced(DatumNode from, DatumNode to, HelmertParameters direct)
    {
        var name = $"{OperationName(from, to)}_Geocentric_Translation";
        return new CrsTransformation(
            name,
            TranslationMethod,
            [new CrsTransformationStep(name, true, TranslationMethod, HelmertAlgebra.ToTranslation(direct))],
            Intersect(from.AreaOfUse, to.AreaOfUse),
            CombinedAccuracy(from, to) + HelmertAlgebra.DroppedLinearResidualMetres(direct),
            Approximate: true);
    }

    /// <summary>The same operation read the other way round: the steps apply in
    /// reverse order, each marked as running backwards. The parameters stay the
    /// ones the operation is defined by — an inverse Helmert is applied by the
    /// client, not substituted by the service, and a grid shift is inverted by
    /// the same rule.</summary>
    private static CrsTransformation Backwards(CrsTransformation candidate) =>
        candidate with
        {
            Steps = candidate.Steps
                .Reverse()
                .Select(step => step with { TransformForward = false })
                .ToArray(),
        };

    private static CrsTransformationStep Step(string name, DatumNode from, DatumNode to, HelmertParameters parameters) =>
        new(name, true, Method(PositionVectorMethod, from, to, 0.0, gridDeployed: false), parameters);

    /// <summary>
    /// The accuracy of a shift between two datums: their accuracies against
    /// WGS 84 combine in quadrature, as independent errors do.
    /// </summary>
    private static double CombinedAccuracy(DatumNode from, DatumNode to) =>
        Math.Sqrt((from.AccuracyMetres * from.AccuracyMetres) + (to.AccuracyMetres * to.AccuracyMetres));

    /// <summary>The Esri-facing method text for a Helmert step.</summary>
    private static string Method(
        string method,
        DatumNode from,
        DatumNode to,
        double accuracyMetres,
        bool gridDeployed) =>
        from.Name == OrdnanceSurvey || to.Name == OrdnanceSurvey
            ? $"{method} ({Fallback(accuracyMetres, gridDeployed)})"
            : method;

    /// <summary>
    /// The note an Ordnance Survey Helmert candidate carries, and the whole of
    /// the fallback contract as a client reads it: which of the two situations
    /// this is — no bundle deployed, or one deployed and this is not it — and
    /// what the shift costs either way. Stating the accuracy in the method
    /// text is deliberate (ADR-0105 §fallback): the Esri listing has no field
    /// for it, and a candidate that is quietly second-best reads as though it
    /// were the path applied.
    /// <para>
    /// Where a bundle is deployed this Helmert is not superseded but demoted:
    /// it is still what the engine applies to every coordinate the grid does
    /// not cover (ADR-0107), so the text says that rather than calling the
    /// operation unused, which would be a claim about the whole plane when it
    /// is a claim about the part of it outside the block.
    /// </para>
    /// </summary>
    private static string Fallback(double accuracyMetres, bool gridDeployed) =>
        gridDeployed
            ? $"OSGB36 classic Helmert approximation, stated at {accuracyMetres:F1} m: a grid is deployed and ranked ahead of this, and this is what the transform verb applies to every point the grid does not cover"
            : $"OSGB36 classic Helmert approximation, stated at {accuracyMetres:F1} m: no grid is deployed, so this is the operation applied and the Helmert is the fallback";

    private static bool Covers(CrsAreaOfUse areaOfUse, CrsAreaOfUse? areaOfInterest) =>
        !IsEmpty(areaOfUse) && (areaOfInterest is null || Contains(areaOfUse, areaOfInterest));

    /// <summary>
    /// The order operations are published in, so both directions of a search
    /// name the same operation: the WGS 84 pivot first, then the catalogued
    /// datums by code.
    /// </summary>
    private static string CanonicalKey(DatumNode node) => node.Code == World.Code ? "0" : $"1 {node.Code:D5}";

    /// <summary>
    /// Whether an area of use covers any ground at all: where it does not,
    /// no operation stands for it anywhere. Emptiness is the absence of
    /// boxes, never a pair of bounds the wrong way round — an extent that
    /// crosses the antimeridian is two boxes, and reading its western box's
    /// east bound against its eastern box's west bound used to conclude that
    /// a datum used nowhere shares no ground with WGS 84 (ADR-0111).
    /// </summary>
    internal static bool IsEmpty(CrsAreaOfUse areaOfUse) => areaOfUse.Boxes.Count == 0;

    /// <summary>
    /// Whether the area of use covers the area of interest in full. A filter,
    /// not a sample: a request over ground that straddles a candidate's edge
    /// is not a request the candidate can answer, and answering it with the
    /// candidate anyway is how a wrapped extent used to be served by an
    /// operation that does not reach the strip west of the antimeridian.
    /// </summary>
    private static bool Contains(CrsAreaOfUse areaOfUse, CrsAreaOfUse areaOfInterest) =>
        areaOfInterest.Boxes.Count > 0
        && areaOfInterest.Boxes.All(box => areaOfUse.Boxes.Any(candidate => Covers(candidate, box)));

    /// <summary>
    /// Whether one rectangle covers another. The degenerate cases count: two
    /// identical rectangles cover each other, which is what lets a union drop
    /// a duplicate rather than publish the ground twice.
    /// </summary>
    private static bool Covers(CrsAreaOfUseBox outer, CrsAreaOfUseBox inner) =>
        inner.XMin >= outer.XMin && inner.YMin >= outer.YMin
        && inner.XMax <= outer.XMax && inner.YMax <= outer.YMax;

    /// <summary>
    /// Where both datums apply. A direct operation is valid nowhere else, so
    /// an empty intersection drops the candidate outright — that is how
    /// OSGB36-to-NAD83 comes back with only the concatenated path. The
    /// intersection is rectangle by rectangle, because the two extents may
    /// share ground in more than one place and min/max over a set of boxes
    /// answers a question about their bounding box rather than about them
    /// (ADR-0111).
    /// </summary>
    internal static CrsAreaOfUse Intersect(CrsAreaOfUse left, CrsAreaOfUse right)
    {
        var boxes = new List<CrsAreaOfUseBox>();
        foreach (var one in left.Boxes)
        {
            foreach (var other in right.Boxes)
            {
                var box = new CrsAreaOfUseBox(
                    Math.Max(one.XMin, other.XMin),
                    Math.Max(one.YMin, other.YMin),
                    Math.Min(one.XMax, other.XMax),
                    Math.Min(one.YMax, other.YMax));
                if (box.XMin <= box.XMax && box.YMin <= box.YMax)
                {
                    boxes.Add(box);
                }
            }
        }

        return boxes.Count == 0
            ? new CrsAreaOfUse($"no shared area of use between {left.Name} and {right.Name}", [])
            : new CrsAreaOfUse($"the {left.Name} and {right.Name} areas of use", WithoutDuplicates(boxes));
    }

    /// <summary>Where either step applies. The two sets of rectangles, with
    /// the parts already inside another rectangle dropped: a concatenation
    /// through WGS 84 covers the world where the world is one of the
    /// operands, and publishing New Zealand's two boxes beside it would draw
    /// New Zealand twice. Nothing is merged, because the only two boxes that
    /// could be are the ones either side of the antimeridian, and joining
    /// those would fabricate a rectangle running from 160.6E to 171.2W
    /// (ADR-0111).</summary>
    internal static CrsAreaOfUse Union(CrsAreaOfUse left, CrsAreaOfUse right) =>
        new($"{left.Name} and {right.Name}", WithoutDuplicates([.. left.Boxes, .. right.Boxes]));

    /// <summary>The rectangles of <paramref name="boxes"/> that no other one
    /// of them covers. A duplicate is dropped in favour of the one that comes
    /// first, so the result is a set rather than a multiset.</summary>
    private static List<CrsAreaOfUseBox> WithoutDuplicates(List<CrsAreaOfUseBox> boxes)
    {
        var kept = new List<CrsAreaOfUseBox>();
        for (var index = 0; index < boxes.Count; index++)
        {
            var covered = false;
            for (var other = 0; other < boxes.Count && !covered; other++)
            {
                covered = other != index && Covers(boxes[other], boxes[index]) && (other < index || !boxes[other].Equals(boxes[index]));
            }

            if (!covered)
            {
                kept.Add(boxes[index]);
            }
        }

        return kept;
    }

    private static string OperationName(DatumNode from, DatumNode to) => $"{from.Code}_To_{to.Code}_Helmert";
}
