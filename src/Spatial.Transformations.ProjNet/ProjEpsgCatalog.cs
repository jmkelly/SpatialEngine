using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using ProjCs = ProjNet.CoordinateSystems;

namespace Spatial.Transformations.ProjNet;

/// <summary>
/// The EPSG catalogue of the transformation provider (ADR-0027): the common
/// geographic and projected CRSs, the projected families generated from one
/// template, and the definitions that exist only as WKT — all read from EPSG
/// WKT and all built through ProjNet's factory by the one programmatic
/// builder below.
/// <para>
/// The catalogue is WKT-defined but not WKT-*parsed* by ProjNet: ProjNet 2.1's
/// own WKT reader cannot read WKT2 at all, and where it can read WKT1 it takes
/// the projection class from the document, so the widely published
/// <c>Mercator_1SP</c> spelling of EPSG:3857 becomes a plain Mercator — 33 km
/// of northing on Web Mercator at Berlin's latitude.
/// <see cref="ProjWkt"/> therefore reads the definitions itself and intercepts
/// the Pseudo-Mercator case (by projection name, CRS name or EPSG authority)
/// before construction, so the WKT text is data and the construction path is
/// the one that is already trusted. Each definition also carries its
/// datum-to-WGS84 shift ("TOWGS84"): zero for modern datums, the classic
/// Helmert approximation for OSGB36 (the WKT1 library has no grid support —
/// ADR-0027 §accuracy).
/// </para>
/// <para>
/// Instances that differ only by one zone number are generated, not typed
/// out: the UTM families (zones 1-60 north and south, and the ETRS89 and
/// NAD83 bands that use the same Transverse Mercator parameter set with a
/// different geodetic datum) come from one template each, so every zone a
/// client asks for is served and no served code depends on a hand-written
/// row. The dictionary is built once and every definition is read and every
/// coordinate system built on first use, so generating a family costs one
/// dictionary entry per zone rather than a coordinate system.
/// </para>
///
/// EPSG Geodetic Parameter Dataset © IOGP/EPSG (subset reproduced here under
/// the EPSG terms of use; see ADR-0027).
/// </summary>
internal static class ProjEpsgCatalog
{
    /// <summary>Looks up a CRS definition by EPSG code.</summary>
    public static bool TryGet(int code, [NotNullWhen(true)] out ProjCs.CoordinateSystem? coordinateSystem)
    {
        if (Entries.TryGetValue(code, out var entry))
        {
            coordinateSystem = entry.System.Value;
            return true;
        }

        coordinateSystem = null;
        return false;
    }

    /// <summary>The EPSG codes served by this catalogue, generated families included.</summary>
    public static IEnumerable<int> Codes => Entries.Keys;

    /// <summary>
    /// The coordinate system a code builds, or null when the catalogue does not
    /// serve it. The counterpart to <see cref="GeographicBaseOf"/>, so a
    /// transform can pair a code's own system with the geographic base it is
    /// measured on.
    /// </summary>
    public static ProjCs.CoordinateSystem? SystemOf(int code) =>
        TryGet(code, out var system) ? system : null;

    /// <summary>
    /// The geographic coordinate system a CRS is expressed on: the CRS's own
    /// system for a geographic one, and a build of its base for a projected
    /// one. A datum shift is a shift between geographic coordinates, so this
    /// is what a grid-backed transform measures the point on before shifting it
    /// (ADR-0105). The base is built once per code and cached, because it is
    /// the same construction the projected CRS's own builder does and is needed
    /// on the transform's hot path.
    /// </summary>
    public static ProjCs.CoordinateSystem? GeographicBaseOf(int code)
    {
        if (!Entries.TryGetValue(code, out var entry))
        {
            return null;
        }

        if (entry.Definition.Value is not ProjectedDefinition projected)
        {
            return entry.System.Value;
        }

        return GeographicBases.GetOrAdd(code, _ => Build(projected.Base));
    }

    private static readonly ConcurrentDictionary<int, ProjCs.CoordinateSystem> GeographicBases = new();

    /// <summary>
    /// The coordinate system a code builds with its datum's WGS 84 shift
    /// zeroed, and the same for the geographic base it is measured on
    /// (ADR-0107).
    /// <para>
    /// A zeroed shift is not a different datum — it is the same ellipsoid, the
    /// same projection and the same axes with the conversion switched off — so
    /// a pair built from these is pure projection and ellipsoid maths, and a
    /// leg that pairs one of these with the pivot cannot apply a shift that
    /// some earlier leg has already applied. That is what lets the transform
    /// verb own the datum shift outright and choose per point between a
    /// deployed grid and the Helmert behind it.
    /// </para>
    /// <para>
    /// Built once per code and cached, for the same reason the ordinary
    /// systems are: a transform's hot path must not rebuild a coordinate
    /// system per request.
    /// </para>
    /// </summary>
    public static ProjCs.CoordinateSystem? ShiftFreeOf(int code) =>
        ShiftFreeSystems.GetOrAdd(code, _ => BuildShiftFree(ProjEpsgCatalog.DefinitionOf(code)));

    /// <summary>The shift-free counterpart of <see cref="GeographicBaseOf"/>.</summary>
    public static ProjCs.CoordinateSystem? ShiftFreeGeographicBaseOf(int code) =>
        ShiftFreeGeographicBases.GetOrAdd(code, _ => BuildShiftFree(GeographicDefinitionOf(ProjEpsgCatalog.DefinitionOf(code))));

    private static readonly ConcurrentDictionary<int, ProjCs.CoordinateSystem> ShiftFreeSystems = new();

    private static readonly ConcurrentDictionary<int, ProjCs.CoordinateSystem> ShiftFreeGeographicBases = new();

    /// <summary>
    /// Builds a definition with its datum's shift to WGS 84 zeroed, through
    /// the same construction path the catalogued systems take.
    /// </summary>
    private static ProjCs.CoordinateSystem BuildShiftFree(CrsDefinition definition) => definition switch
    {
        GeodeticDefinition geodetic => Build(geodetic with { ToWgs84 = NoShift }),
        ProjectedDefinition projected => Build(projected with { Base = ShiftFreeBase(projected.Base) }),
        _ => throw new InvalidOperationException($"The EPSG definition '{definition.Name}' is neither geodetic nor projected."),
    };

    /// <summary>A projected definition's own shift-free geodetic base.</summary>
    private static GeodeticDefinition ShiftFreeBase(GeodeticDefinition baseDefinition) =>
        baseDefinition with { ToWgs84 = NoShift };

    /// <summary>The geodetic base a definition is built on: itself, or the one a projected CRS names.</summary>
    private static GeodeticDefinition GeographicDefinitionOf(CrsDefinition definition) => definition switch
    {
        GeodeticDefinition geodetic => geodetic,
        ProjectedDefinition projected => projected.Base,
        _ => throw new InvalidOperationException($"The EPSG definition '{definition.Name}' is neither geodetic nor projected."),
    };

    /// <summary>The seven zeros a datum with no shift of its own carries.</summary>
    private static readonly double[] NoShift = [0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0];

    /// <summary>Test pin: whether a code's coordinate system has been built yet (the catalogue builds each one once, on first use).</summary>
    public static bool IsBuilt(int code) =>
        Entries.TryGetValue(code, out var entry) && entry.System.IsValueCreated;

    /// <summary>
    /// The definition a code was read from, for callers that need the EPSG
    /// data rather than the coordinate system it builds.
    /// </summary>
    public static CrsDefinition DefinitionOf(int code) => Entries[code].Definition.Value;

    /// <summary>
    /// The datum a CRS is on, for the datum-transformation graph (ADR-0087).
    /// A projected CRS resolves through its geographic base, so a search is
    /// keyed on datums rather than on CRSs.
    /// <para>
    /// The shift comes out of the definition the WKT was read into — the one
    /// place a datum's parameters live — and the accuracy and area of use come
    /// from the registered datum-to-WGS 84 operation
    /// (<see cref="EpsgDatumOperations"/>), because WKT states neither
    /// (ADR-0086). A datum the operation table does not name is reported as
    /// absent rather than given an accuracy nobody published.
    /// </para>
    /// </summary>
    public static bool TryGetDatum(int code, [NotNullWhen(true)] out DatumNode? datum)
    {
        if (Entries.TryGetValue(code, out var entry) && GeodeticOf(entry.Definition.Value) is { } geodetic)
        {
            return EpsgDatumOperations.TryGetNode(geodetic, out datum);
        }

        datum = null;
        return false;
    }

    /// <summary>
    /// The catalogue's WGS 84 datum: the pivot every other datum's shift is
    /// published against. It is read from the WKT definition like any other
    /// node, so the graph and the transform path cannot drift apart.
    /// </summary>
    public static DatumNode WorldDatum() =>
        TryGetDatum(WorldGeodeticCode, out var world)
            ? world
            : throw new InvalidOperationException($"The catalogue serves no WGS 84 datum (EPSG:{WorldGeodeticCode}), so it has no transformation pivot.");

    /// <summary>The EPSG code of the geodetic datum every other shift is published against.</summary>
    private const int WorldGeodeticCode = 4326;

    /// <summary>
    /// A definition's geodetic base, whether the definition is itself geodetic
    /// or the one a projected CRS names.
    /// </summary>
    private static GeodeticDefinition? GeodeticOf(CrsDefinition definition) => definition switch
    {
        GeodeticDefinition geodetic => geodetic,
        ProjectedDefinition projected => projected.Base,
        _ => null,
    };

    /// <summary>
    /// Test pin: reads a definition exactly as a vendored row is read, so the
    /// guard that stops a broken vendored definition reaching a caller is
    /// exercised rather than assumed.
    /// </summary>
    public static CrsDefinition ReadForTest(string template, IReadOnlyDictionary<string, string> tokens, int code) =>
        Read(template, new Dictionary<string, string>(tokens), code).Value;

    /// <summary>
    /// Builds a projected definition's coordinate system. Every CRS in the
    /// catalogue goes through here — a hand-written row, a generated family
    /// member and a definition that exists only as WKT alike — so a
    /// definition read from WKT is constructed exactly as a typed-out one
    /// would be.
    /// </summary>
    public static ProjCs.ProjectedCoordinateSystem Build(ProjectedDefinition definition)
    {
        var parameters = definition.Parameters
            .Select(parameter => new ProjCs.ProjectionParameter(parameter.Name, parameter.Value))
            .ToList();
        var projection = Factory.CreateProjection(definition.ProjectionClass, definition.ProjectionClass, parameters);
        return Factory.CreateProjectedCoordinateSystem(
            definition.Name, Build(definition.Base), projection, ProjCs.LinearUnit.Metre, EastingAxis, NorthingAxis);
    }

    private static ProjCs.GeographicCoordinateSystem Build(GeodeticDefinition definition)
    {
        var ellipsoid = Factory.CreateFlattenedSphere(definition.EllipsoidName, definition.SemiMajor, definition.InverseFlattening, ProjCs.LinearUnit.Metre);
        var datum = Factory.CreateHorizontalDatum(
            definition.DatumName,
            ProjCs.DatumType.HD_Geocentric,
            ellipsoid,
            new ProjCs.Wgs84ConversionInfo(
                definition.ToWgs84[0], definition.ToWgs84[1], definition.ToWgs84[2],
                definition.ToWgs84[3], definition.ToWgs84[4], definition.ToWgs84[5], definition.ToWgs84[6]));
        return Factory.CreateGeographicCoordinateSystem(
            definition.Name, ProjCs.AngularUnit.Degrees, datum, Greenwich, LonAxis, LatAxis);
    }

    /// <summary>A catalogue row: the definition read from WKT, and the coordinate system it builds, each on first use.</summary>
    private sealed record Entry(Lazy<CrsDefinition> Definition, Lazy<ProjCs.CoordinateSystem> System);

    private static Dictionary<int, Entry> BuildEntries()
    {
        var entries = new Dictionary<int, Entry>();
        foreach (var (code, wkt) in EpsgWktDefinitions.Geographic)
        {
            Add(entries, code, wkt, EpsgWktDefinitions.Tokens(wkt));
        }

        foreach (var (code, template, baseWkt) in EpsgWktDefinitions.Projected)
        {
            Add(entries, code, template, EpsgWktDefinitions.Tokens(baseWkt));
        }

        foreach (var family in EpsgWktDefinitions.Families)
        {
            for (var zone = family.FirstZone; zone <= family.LastZone; zone++)
            {
                var code = family.FirstCode + (zone - family.FirstZone);
                Add(entries, code, EpsgWktDefinitions.UtmZone, EpsgWktDefinitions.ZoneTokens(family, zone, code));
            }
        }

        return entries;
    }

    /// <summary>
    /// Adds one row. The code, the rendered WKT and the definition are
    /// captured per call, so each entry is read and built from its own
    /// template and stays a lazy single build like a hand-written row.
    /// </summary>
    private static void Add(Dictionary<int, Entry> entries, int code, string template, Dictionary<string, string> tokens)
    {
        var definition = Read(template, tokens, code);
        var system = new Lazy<ProjCs.CoordinateSystem>(() => definition.Value switch
        {
            GeodeticDefinition geodetic => Build(geodetic),
            ProjectedDefinition projected => Build(projected),
            _ => throw new InvalidOperationException($"The EPSG definition for {code} is neither geodetic nor projected."),
        });
        entries[code] = new Entry(definition, system);
    }

    /// <summary>Renders and reads a definition, failing the process rather than serving a half-built catalogue.</summary>
    private static Lazy<CrsDefinition> Read(string template, IReadOnlyDictionary<string, string> tokens, int code) => new(() =>
    {
        if (!ProjWkt.TryRender(template, tokens, out var wkt, out var renderError))
        {
            throw new InvalidOperationException($"The vendored EPSG definition for {code} does not render: {renderError}");
        }

        if (!ProjWkt.TryParse(wkt, out var definition, out var parseError))
        {
            throw new InvalidOperationException($"The vendored EPSG definition for {code} does not read: {parseError}");
        }

        return definition!;
    });

    private static readonly ProjCs.CoordinateSystemFactory Factory = new();

    private static readonly ProjCs.PrimeMeridian Greenwich =
        Factory.CreatePrimeMeridian("Greenwich", ProjCs.AngularUnit.Degrees, 0);

    private static readonly ProjCs.AxisInfo LonAxis = new("Lon", ProjCs.AxisOrientationEnum.East);
    private static readonly ProjCs.AxisInfo LatAxis = new("Lat", ProjCs.AxisOrientationEnum.North);
    private static readonly ProjCs.AxisInfo EastingAxis = new("Easting", ProjCs.AxisOrientationEnum.East);
    private static readonly ProjCs.AxisInfo NorthingAxis = new("Northing", ProjCs.AxisOrientationEnum.North);

    private static readonly Dictionary<int, Entry> Entries = BuildEntries();
}
