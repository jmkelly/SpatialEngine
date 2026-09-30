using System.Globalization;
using System.Text;

namespace Spatial.Transformations.ProjNet;

/// <summary>A CRS definition read from WKT: the data the catalogue builds from, whatever dialect it was spelled in.</summary>
internal abstract record CrsDefinition(string Name);

/// <summary>
/// A geodetic CRS definition: its datum, that datum's ellipsoid, and the
/// datum's shift to WGS84 (zero for the modern datums, the classic Helmert
/// approximation for the datums the grid-free provider has no NTv2 for —
/// ADR-0027 §accuracy).
/// </summary>
internal sealed record GeodeticDefinition(
    string Name,
    string DatumName,
    string EllipsoidName,
    double SemiMajor,
    double InverseFlattening,
    double[] ToWgs84) : CrsDefinition(Name);

/// <summary>
/// A projected CRS definition: a geodetic base, the projection to apply to
/// it, and that projection's parameters. <paramref name="ProjectionClass"/> is
/// the ProjNet projection class, resolved from the WKT's own method name (or,
/// for the identifiable Pseudo-Mercator spellings, corrected to the
/// known-good one).
/// </summary>
internal sealed record ProjectedDefinition(
    string Name,
    GeodeticDefinition Base,
    string ProjectionClass,
    (string Name, double Value)[] Parameters) : CrsDefinition(Name);

/// <summary>
/// The catalogue's WKT reader (ADR-0027): it reads an EPSG definition in
/// either the OGC WKT1 dialect (<c>GEOGCS</c>, <c>SPHEROID</c>,
/// <c>PROJECTION</c>) or the WKT2 dialect (<c>GEOGCRS</c>, <c>ELLIPSOID</c>,
/// <c>CONVERSION</c>/<c>METHOD</c>), and produces the definitions the
/// catalogue's programmatic builder consumes — so the WKT text is data and
/// construction stays on the one path that is already trusted.
/// <para>
/// It is deliberately not ProjNet's own WKT reader. That reader cannot read
/// WKT2 at all ("'PROJCRS' is not recognized"), and where it can read WKT1
/// it takes the projection class from the document: EPSG:3857 is very widely
/// published with the projection named <c>Mercator_1SP</c> (the Google,
/// OSRM, GeoServer and EPSG:900913 dialects), and a plain Mercator puts Web
/// Mercator 33 km too far south at Berlin's latitude. So this reader
/// intercepts the Pseudo-Mercator case before construction — the projection
/// names, the CRS names and the EPSG 3857/3785/900913/102100 authorities are
/// all identifiable — and routes it to the same programmatic construction the
/// catalogue has always used, which is pinned byte-for-byte by test.
/// </para>
/// </summary>
internal static class ProjWkt
{
    /// <summary>
    /// Reads one CRS definition. <paramref name="definition"/> is null and
    /// <paramref name="error"/> names the construct that could not be read
    /// when the text is not a CRS this engine can build.
    /// </summary>
    public static bool TryParse(string wkt, out CrsDefinition? definition, out string? error)
    {
        definition = null;
        error = null;

        if (string.IsNullOrWhiteSpace(wkt))
        {
            error = "the WKT is empty.";
            return false;
        }

        if (!TryParseNode(wkt, 0, out var node, out var at, out error))
        {
            return false;
        }

        if (at < wkt.Length && !string.IsNullOrWhiteSpace(wkt[at..]))
        {
            error = $"the WKT has trailing text after the '{node!.Keyword}' node.";
            return false;
        }

        return Read(node!, out definition, out error);
    }

    /// <summary>
    /// Substitutes a template's <c>{name}</c> tokens — the one operation a
    /// generated family needs, since a family's members differ only in their
    /// zone number and the two parameters that follow from it. Values are
    /// substituted verbatim, so a template carrying a round-tripped double
    /// ("R") gives back the exact same double.
    /// </summary>
    public static bool TryRender(string template, IReadOnlyDictionary<string, string> values, out string rendered, out string? error)
    {
        var text = new StringBuilder(template.Length);
        var index = 0;
        while (index < template.Length)
        {
            var open = template.IndexOf('{', index);
            if (open < 0)
            {
                text.Append(template, index, template.Length - index);
                break;
            }

            var close = template.IndexOf('}', open);
            if (close < 0)
            {
                error = $"the WKT template has an unclosed '{{' at position {open}.";
                rendered = string.Empty;
                return false;
            }

            var name = template[(open + 1)..close];
            if (!values.TryGetValue(name, out var value))
            {
                error = $"the WKT template needs a value for '{name}', which the family does not supply.";
                rendered = string.Empty;
                return false;
            }

            text.Append(template, index, open - index).Append(value);
            index = close + 1;
        }

        rendered = text.ToString();
        error = null;
        return true;
    }

    private static bool Read(WktNode node, out CrsDefinition? definition, out string? error)
    {
        definition = null;
        return node.Keyword switch
        {
            "GEOGCS" or "GEOGCRS" or "GEODCRS" or "BASEGEODCRS" => ReadGeodetic(node, out definition, out error),
            "PROJCS" or "PROJCRS" => ReadProjected(node, out definition, out error),
            _ => Fail($"'{node.Keyword}' is not a CRS node this reader builds (GEOGCS, GEOGCRS, PROJCS or PROJCRS).", out error),
        };
    }

    private static bool ReadGeodetic(WktNode node, out CrsDefinition? definition, out string? error)
    {
        definition = null;
        if (node.Text is not { } name)
        {
            return Fail($"the '{node.Keyword}' node has no name.", out error);
        }

        var datum = Child(node, "DATUM");
        if (datum is null)
        {
            return Fail(
                Child(node, "ENSEMBLE") is not null
                    ? $"the '{name}' geodetic CRS uses an ENSEMBLE datum, which this reader does not build; give the member datum's own DATUM node."
                    : $"the '{name}' geodetic CRS has no DATUM node.",
                out error);
        }

        var ellipsoid = Child(datum, "ELLIPSOID") ?? Child(datum, "SPHEROID");
        if (ellipsoid is null)
        {
            return Fail($"the '{name}' datum has no ELLIPSOID or SPHEROID node.", out error);
        }

        if (!TryFlattening(ellipsoid, out var semiMajor, out var inverseFlattening, out error))
        {
            return false;
        }

        var shift = Child(datum, "TOWGS84")?.Numbers
            ?? [0, 0, 0, 0, 0, 0, 0];
        if (shift.Count is not (0 or 7))
        {
            return Fail($"the '{name}' datum's TOWGS84 node has {shift.Count} parameters; it needs seven or none.", out error);
        }

        definition = new GeodeticDefinition(
            name,
            datum.Text ?? name,
            ellipsoid.Text ?? name,
            semiMajor,
            inverseFlattening,
            shift.Count == 7 ? [.. shift] : [0, 0, 0, 0, 0, 0, 0]);
        error = null;
        return true;
    }

    private static bool ReadProjected(WktNode node, out CrsDefinition? definition, out string? error)
    {
        definition = null;
        if (node.Text is not { } name)
        {
            return Fail($"the '{node.Keyword}' node has no name.", out error);
        }

        if (!ReadBase(node, name, out var geodetic, out error)
            || geodetic is null
            || !ReadMethod(node, name, out var resolved, out error)
            || resolved is null)
        {
            return false;
        }

        var parameters = ReadParameters(name, node, geodetic, resolved, out error);
        if (parameters is null)
        {
            return false;
        }

        definition = new ProjectedDefinition(name, geodetic, resolved.Class, parameters);
        return true;
    }

    /// <summary>The geodetic CRS a projected definition projects from, built the same way as a geographic one.</summary>
    private static bool ReadBase(WktNode node, string name, out GeodeticDefinition? geodetic, out string? error)
    {
        geodetic = null;
        var baseNode = Child(node, "BASEGEOGCRS") ?? Child(node, "GEOGCRS") ?? Child(node, "GEOGCS");
        if (baseNode is null)
        {
            return Fail($"the '{name}' projected CRS has no base geographic CRS (BASEGEOGCRS, GEOGCS).", out error);
        }

        if (ReadGeodetic(baseNode, out var read, out error) && read is GeodeticDefinition definition)
        {
            geodetic = definition;
            return true;
        }

        return false;
    }

    /// <summary>
    /// The projection a definition applies, and the ProjNet projection class
    /// it resolves to. WKT2 names the method in a METHOD node and hangs the
    /// PARAMETERs off the CONVERSION; WKT1's PROJECTION is the method node.
    /// </summary>
    private static bool ReadMethod(WktNode node, string name, out ResolvedMethod? method, out string? error)
    {
        method = null;
        var conversion = Child(node, "CONVERSION") ?? Child(node, "PROJECTION");
        if (conversion is null)
        {
            return Fail($"the '{name}' projected CRS has no CONVERSION or PROJECTION node.", out error);
        }

        var methodNode = Child(conversion, "METHOD") ?? conversion;
        if (methodNode.Text is not { } methodName)
        {
            return Fail($"the '{name}' projected CRS names no projection method.", out error);
        }

        if (!TryProjectionClass(methodName, name, node, out var projectionClass, out error) || projectionClass is null)
        {
            return false;
        }

        method = new ResolvedMethod(conversion, methodNode, projectionClass, IsPolarStereographicVariantB(methodName));
        return true;
    }

    /// <summary>
    /// Whether the WKT's method is EPSG's Polar Stereographic (variant B) —
    /// the parameterisation that states a latitude of standard parallel rather
    /// than a scale factor at a pole. It is the same projection as variant A
    /// and resolves to the same ProjNet class; it states its scale factor
    /// differently, and <see cref="ReadParameters"/> reads it that way.
    /// </summary>
    private static bool IsPolarStereographicVariantB(string methodName) =>
        Normalise(methodName) == PolarStereographicVariantBMethod;

    private static readonly string PolarStereographicVariantBMethod = Normalise("Polar Stereographic (variant B)");

    private sealed record ResolvedMethod(WktNode Conversion, WktNode MethodNode, string Class, bool PolarVariantB);

    /// <summary>
    /// The projection parameters, in the order the document lists them (the
    /// projection reads them by name, so order is the document's). The two
    /// dialects hang them off different nodes — WKT1 off the PROJCS itself,
    /// WKT2 off the CONVERSION — and a few documents nest them inside the
    /// METHOD, so all three are read. A Pseudo-Mercator intercept gets the
    /// four standard parameters even when the document's dialect omits them,
    /// which is what the ESRI spelling does; and the polar stereographic
    /// variant B's latitude of standard parallel becomes the pole and the
    /// scale factor at that pole, on the definition's own ellipsoid, rather
    /// than a parameter the projection does not read.
    /// </summary>
    private static (string Name, double Value)[]? ReadParameters(
        string crsName, WktNode crs, GeodeticDefinition geodetic, ResolvedMethod resolved, out string? error)
    {
        var parameters = new List<(string Name, double Value)>();
        var declared = Children(crs, "PARAMETER")
            .Concat(Children(resolved.Conversion, "PARAMETER"))
            .Concat(Children(resolved.MethodNode, "PARAMETER"));
        foreach (var parameter in declared)
        {
            if (parameter.Text is not { } raw
                || parameter.Items.OfType<WktNumber>().FirstOrDefault() is not { } value)
            {
                continue;
            }

            if (!Parameters.TryGetValue(Normalise(raw), out var name))
            {
                error = $"'{crsName}' uses the projection parameter '{raw}', which is not one the engine's projections read.";
                return null;
            }

            parameters.Add((name, value.Value));
        }

        if (resolved.PolarVariantB)
        {
            return PolarStereographicVariantB(crsName, parameters, geodetic, out error);
        }

        if (parameters.Exists(parameter => parameter.Name == StandardParallelParameter))
        {
            error = $"'{crsName}' states a latitude of standard parallel, which only the polar stereographic variant B method reads.";
            return null;
        }

        if (parameters.Count == 0 && IsPseudoMercator(resolved.Class))
        {
            parameters.AddRange(PseudoMercatorDefaults);
        }

        error = null;
        return [.. parameters];
    }

    /// <summary>
    /// EPSG's polar stereographic variant B, read as the variant A projection
    /// ProjNet has: the pole the standard parallel's sign names, and the scale
    /// factor at that pole, in place of the standard parallel itself — the
    /// parameter ProjNet does not read, and one the reader must therefore not
    /// hand on. Everything else the document states is left as it stands, so
    /// a variant B definition's longitude of origin and false offsets are
    /// read exactly as a variant A definition's are.
    /// </summary>
    private static (string Name, double Value)[]? PolarStereographicVariantB(
        string crsName,
        List<(string Name, double Value)> declared,
        GeodeticDefinition geodetic,
        out string? error)
    {
        var both = declared.FindIndex(parameter => parameter.Name is "scale_factor" or "latitude_of_origin");
        if (both >= 0)
        {
            error = $"'{crsName}' states a scale factor at natural origin as well as a latitude of standard parallel, so it is a variant A and a variant B definition at once.";
            return null;
        }

        var standardParallel = declared.FindIndex(parameter => parameter.Name == StandardParallelParameter);
        if (standardParallel < 0)
        {
            error = $"'{crsName}' is a polar stereographic variant B definition with no latitude of standard parallel.";
            return null;
        }

        var parallel = declared[standardParallel].Value;
        if (parallel == 0)
        {
            error = $"'{crsName}' has a latitude of standard parallel of 0 degrees, which names no pole to project from.";
            return null;
        }

        var parameters = new List<(string Name, double Value)>(declared.Count + 1);
        parameters.AddRange(declared.Take(standardParallel));
        parameters.Add(("latitude_of_origin", parallel < 0 ? -90.0 : 90.0));
        parameters.Add(("scale_factor", PolarScaleFactor(Math.PI * parallel / 180.0, geodetic)));
        parameters.AddRange(declared.Skip(standardParallel + 1));

        error = null;
        return [.. parameters];
    }

    /// <summary>
    /// The scale factor at the pole that makes the polar stereographic's
    /// scale true on the standard parallel, by PROJ's own expression for
    /// <c>+proj=stere +lat_ts=</c> (<c>src/projections/stere.cpp</c> and
    /// <c>src/tsfn.cpp</c>, PROJ 9.8.1): the variant A constant
    /// <c>2/sqrt((1+e)^(1+e) (1-e)^(1-e))</c> divided into the
    /// isometric-latitude expression PROJ substitutes for it, on the
    /// definition's own ellipsoid and at the standard parallel's absolute
    /// latitude — PROJ takes the hemisphere from the pole, and the reader
    /// takes it from the sign of the same number.
    /// </summary>
    private static double PolarScaleFactor(double latitude, GeodeticDefinition geodetic)
    {
        var flattening = 1.0 / geodetic.InverseFlattening;
        var e = Math.Sqrt(flattening * (2.0 - flattening));
        var eSine = e * Math.Sin(Math.Abs(latitude));
        var t = Math.Tan((Math.PI / 2.0 - Math.Abs(latitude)) / 2.0) * Math.Exp(e * Math.Atanh(eSine));
        var variantA = Math.Sqrt(Math.Pow(1 + e, 1 + e) * Math.Pow(1 - e, 1 - e));

        return Math.Cos(Math.Abs(latitude)) * variantA / (2 * t * Math.Sqrt(1 - (eSine * eSine)));
    }

    /// <summary>The name the latitude of standard parallel is read under, on the polar stereographic variant B method.</summary>
    private const string StandardParallelParameter = "latitude_of_standard_parallel";

    /// <summary>
    /// Resolves the WKT's projection method to the ProjNet projection class,
    /// intercepting Pseudo-Mercator first. The intercept keys on the method
    /// name, the CRS name and the identifiers: all three are how the
    /// Pseudo-Mercator case is identifiable, because the parameter set alone
    /// (a zero-latitude Mercator) is not.
    /// </summary>
    private static bool TryProjectionClass(
        string methodName, string crsName, WktNode crs, out string? projectionClass, out string? error)
    {
        if (IsPseudoMercatorName(methodName) || IsPseudoMercatorName(crsName) || HasPseudoMercatorAuthority(crs))
        {
            projectionClass = PseudoMercatorClass;
            error = null;
            return true;
        }

        if (Projections.TryGetValue(Normalise(methodName), out var mapped))
        {
            projectionClass = mapped;
            error = null;
            return true;
        }

        projectionClass = null;
        return Fail($"'{crsName}' uses the projection method '{methodName}', which is not one the engine's projections read.", out error);
    }

    private static bool IsPseudoMercator(string projectionClass) => projectionClass == PseudoMercatorClass;

    private static bool IsPseudoMercatorName(string name) => PseudoMercatorNames.Contains(Normalise(name));

    /// <summary>The EPSG authorities that are Pseudo-Mercator however the document spells the projection.</summary>
    private static bool HasPseudoMercatorAuthority(WktNode node) => Identifiers(node).Any(authority => PseudoMercatorAuthorities.Contains(authority.Code));

    private static IEnumerable<(string Authority, string Code)> Identifiers(WktNode node) =>
        Children(node, "ID")
            .Concat(Children(node, "AUTHORITY"))
            .Select(identifier => (
                Authority: Text(identifier, 0) ?? string.Empty,
                Code: Text(identifier, 1) ?? string.Empty))
            .Where(pair => pair.Code.Length > 0);

    private static WktNode? Child(WktNode node, string keyword) =>
        node.Items.OfType<WktChild>().Select(child => child.Node).FirstOrDefault(child => child.Keyword == keyword);

    private static IEnumerable<WktNode> Children(WktNode node, string keyword) =>
        node.Items.OfType<WktChild>().Select(child => child.Node).Where(child => child.Keyword == keyword);

    private static string? Text(WktNode node, int index) =>
        node.Items.OfType<WktText>().Skip(index).Select(text => text.Value).FirstOrDefault();

    /// <summary>
    /// The ellipsoid's semi-major axis and inverse flattening. WKT spells the
    /// third value as an inverse flattening in both dialects (a few hundred,
    /// against a semi-major axis of millions of metres) and as a semi-minor
    /// axis in the oldest WKT1 documents; the engine's builder takes the
    /// inverse flattening, so a semi-minor axis is converted here.
    /// </summary>
    private static bool TryFlattening(WktNode ellipsoid, out double semiMajor, out double inverseFlattening, out string? error)
    {
        semiMajor = 0;
        inverseFlattening = 0;
        var numbers = ellipsoid.Numbers;
        if (numbers.Count < 2)
        {
            error = $"the '{ellipsoid.Text}' ellipsoid gives {numbers.Count} numbers; it needs a semi-major axis and a second eccentricity or inverse flattening.";
            return false;
        }

        if (numbers[0] <= 0)
        {
            error = $"the '{ellipsoid.Text}' ellipsoid has a non-positive semi-major axis ({numbers[0]}).";
            return false;
        }

        semiMajor = numbers[0];
        if (numbers[1] == 0)
        {
            error = $"the '{ellipsoid.Text}' ellipsoid is a sphere; the engine's projections need an ellipsoid.";
            return false;
        }

        if (numbers[1] < semiMajor / 1000)
        {
            inverseFlattening = numbers[1];
            error = null;
            return true;
        }

        if (numbers[1] >= semiMajor)
        {
            error = $"the '{ellipsoid.Text}' ellipsoid's second number ({numbers[1]}) is not an inverse flattening or a semi-minor axis.";
            return false;
        }

        inverseFlattening = semiMajor / (semiMajor - numbers[1]);
        error = null;
        return true;
    }

    private static bool Fail(string message, out string? error)
    {
        error = message;
        return false;
    }

    /// <summary>Keyword matching ignores case and underscores, the way WKT dialects differ.</summary>
    private static string Normalise(string name)
    {
        var text = new StringBuilder(name.Length);
        foreach (var character in name)
        {
            if (char.IsLetterOrDigit(character))
            {
                text.Append(char.ToLower(character, CultureInfo.InvariantCulture));
            }
        }

        return text.ToString();
    }

    private const string PseudoMercatorClass = "Popular Visualisation Pseudo-Mercator";

    private static readonly (string Name, double Value)[] PseudoMercatorDefaults =
    [
        ("latitude_of_origin", 0.0),
        ("central_meridian", 0.0),
        ("false_easting", 0.0),
        ("false_northing", 0.0),
    ];

    /// <summary>
    /// The WKT projection-method spellings that mean Pseudo-Mercator: the EPSG
    /// names in either dialect, and the two ESRI names that mean the same
    /// thing.
    /// </summary>
    private static readonly HashSet<string> PseudoMercatorNames =
    [
        Normalise("Popular Visualisation Pseudo-Mercator"),
        Normalise("Popular Visualisation Pseudo Mercator"),
        Normalise("Popular_Visualisation_Pseudo_Mercator"),
        Normalise("PopularVisualisationCRSMercator"),
        Normalise("Mercator Auxiliary Sphere"),
        Normalise("Mercator_Auxiliary_Sphere"),
        Normalise("WGS_1984_Web_Mercator_Auxiliary_Sphere"),
        Normalise("WGS 1984 Web Mercator"),
    ];

    /// <summary>The EPSG codes that are Pseudo-Mercator however a document spells the projection.</summary>
    private static readonly HashSet<string> PseudoMercatorAuthorities = ["3857", "3785", "900913", "102100", "102113"];

    /// <summary>
    /// The WKT projection methods this reader resolves, by normalised name.
    /// <para>
    /// The list is deliberately short: it holds the projections whose ProjNet
    /// implementation has been measured against PROJ and found to agree, and
    /// no others, because resolving a method name to a ProjNet projection is a
    /// claim that the two compute the same thing, and that claim is only
    /// worth making for a method that has been checked. Anything else is a
    /// named, actionable failure rather than a silent approximation, so a
    /// definition is never served with numbers that are quietly wrong.
    /// </para>
    /// <para>
    /// The measurements (SpatialEngine-u2x.26), against PROJ 9.8.1 through
    /// pyproj — always_xy, on each definition's own ellipsoid, with no datum
    /// shift, forward and inverse, at points inside the definition's area of
    /// use. "PROJ" is the reference: a ProjNet projection is in this list
    /// only where it lands on PROJ's own coordinates for a named EPSG code.
    /// The control points are in
    /// <c>ProjWktProjectionMethodTests</c>.
    /// </para>
    /// <list type="table">
    /// <listheader>
    /// <term>WKT method</term>
    /// <term>EPSG</term>
    /// <term>measured against</term>
    /// <term>outcome</term>
    /// </listheader>
    /// <item>
    /// <term>Transverse Mercator</term><term>9807</term><term>EPSG:27700</term>
    /// <term>in the map; the 27700/UTM control points hold to 1 cm</term>
    /// </item>
    /// <item>
    /// <term>Lambert Conformal Conic (2SP)</term><term>9801</term><term>EPSG:2154</term>
    /// <term>in the map; the byte-identical pre-change table holds</term>
    /// </item>
    /// <item>
    /// <term>Mercator (variant A)</term><term>9804</term><term>EPSG:3395</term>
    /// <term>in the map; agrees with EPSG Guidance Note 7-2 to the last bit</term>
    /// </item>
    /// <item>
    /// <term>Popular Visualisation Pseudo Mercator</term><term>1024</term><term>EPSG:3857</term>
    /// <term>intercepted above, not read as a method</term>
    /// </item>
    /// <item>
    /// <term>Albers Equal Area</term><term>9822</term><term>EPSG:5070, EPSG:3005</term>
    /// <term>added by u2x.26: agrees to 1e-6 m over the Conus, false offsets included</term>
    /// </item>
    /// <item>
    /// <term>Lambert Azimuthal Equal Area</term><term>9820</term><term>EPSG:3035, EPSG:3571, EPSG:3574</term>
    /// <term>added by u2x.26: agrees to 1e-6 m, the 4,321 km false offsets included</term>
    /// </item>
    /// <item>
    /// <term>Polar Stereographic (variant A)</term><term>9810</term><term>EPSG:5041, EPSG:5482</term>
    /// <term>added by u2x.26: agrees to 1e-6 m at both poles, 2,000 km offsets included</term>
    /// </item>
    /// <item>
    /// <term>Hotine Oblique Mercator (variant B)</term><term>9815</term><term>EPSG:2056, EPSG:2057, EPSG:29873</term>
    /// <term>added by u2x.26: agrees to 1e-6 m; variant B is the no-rotation form</term>
    /// </item>
    /// <item>
    /// <term>Polar Stereographic (variant B)</term><term>9829</term><term>EPSG:3031, EPSG:3032, EPSG:3413</term>
    /// <term>added by g2m: the latitude of standard parallel is read as the variant A pole and scale factor, agreeing to 1e-6 m at both poles, 6,000 km offsets included</term>
    /// </item>
    /// <item>
    /// <term>Hotine Oblique Mercator (variant A)</term><term>9812</term><term>EPSG:3078, EPSG:3375</term>
    /// <term>left out: ProjNet applies the false offsets at the projection centre, PROJ at the natural origin (2,047 km)</term>
    /// </item>
    /// <item>
    /// <term>Krovak</term><term>9819</term><term>EPSG:5513, EPSG:2065</term>
    /// <term>left out: the arithmetic agrees but the axes are the south-oriented ones, and the catalogue serves easting/northing</term>
    /// </item>
    /// </list>
    /// </summary>
    private static readonly Dictionary<string, string> Projections = new()
    {
        [Normalise("Transverse Mercator")] = "Transverse_Mercator",
        [Normalise("Transverse_Mercator")] = "Transverse_Mercator",
        [Normalise("Lambert Conformal Conic (2SP)")] = "Lambert_Conformal_Conic_2SP",
        [Normalise("Lambert Conformal Conic 2SP")] = "Lambert_Conformal_Conic_2SP",
        [Normalise("Lambert_Conformal_Conic_2SP")] = "Lambert_Conformal_Conic_2SP",
        [Normalise("Mercator (variant A)")] = "Mercator_1SP",
        [Normalise("Mercator variant A")] = "Mercator_1SP",
        [Normalise("Mercator_1SP")] = "Mercator_1SP",
        [Normalise("Albers Equal Area")] = "Albers_Conic_Equal_Area",
        [Normalise("Albers_Conic_Equal_Area")] = "Albers_Conic_Equal_Area",
        [Normalise("Lambert Azimuthal Equal Area")] = "Lambert_Azimuthal_Equal_Area",
        [Normalise("Lambert_Azimuthal_Equal_Area")] = "Lambert_Azimuthal_Equal_Area",
        [Normalise("Polar Stereographic (variant A)")] = "Polar_Stereographic",
        [Normalise("Polar_Stereographic")] = "Polar_Stereographic",
        [PolarStereographicVariantBMethod] = "Polar_Stereographic",
        [Normalise("Hotine Oblique Mercator (variant B)")] = "Hotine_Oblique_Mercator",
        [Normalise("Hotine_Oblique_Mercator")] = "Hotine_Oblique_Mercator",
    };

    /// <summary>
    /// The WKT parameter spellings the engine's projections read, by
    /// normalised name: the EPSG WKT2 parameter names and the WKT1 and ESRI
    /// names, all mapped onto the parameter name the projection uses.
    /// </summary>
    private static readonly Dictionary<string, string> Parameters = new()
    {
        [Normalise("latitude_of_origin")] = "latitude_of_origin",
        [Normalise("Latitude of natural origin")] = "latitude_of_origin",
        [Normalise("Latitude of false origin")] = "latitude_of_origin",
        [Normalise("Latitude of projection centre")] = "latitude_of_origin",
        [Normalise("central_meridian")] = "central_meridian",
        [Normalise("Longitude of natural origin")] = "central_meridian",
        [Normalise("Longitude of false origin")] = "central_meridian",
        [Normalise("Longitude of origin")] = "central_meridian",
        [Normalise("longitude_of_center")] = "central_meridian",
        [Normalise("Longitude of projection centre")] = "central_meridian",
        [Normalise("scale_factor")] = "scale_factor",
        [Normalise("Scale factor at natural origin")] = "scale_factor",
        [Normalise("false_easting")] = "false_easting",
        [Normalise("False easting")] = "false_easting",
        [Normalise("Easting at false origin")] = "false_easting",
        [Normalise("false_northing")] = "false_northing",
        [Normalise("False northing")] = "false_northing",
        [Normalise("Northing at false origin")] = "false_northing",
        [Normalise("standard_parallel_1")] = "standard_parallel_1",
        [Normalise("Latitude of 1st standard parallel")] = "standard_parallel_1",
        [Normalise("Latitude of first standard parallel")] = "standard_parallel_1",
        [Normalise("latitude_of_1st_standard_parallel")] = "standard_parallel_1",
        [Normalise("Standard_Parallel_1")] = "standard_parallel_1",
        [Normalise("standard_parallel_2")] = "standard_parallel_2",
        [Normalise("Latitude of 2nd standard parallel")] = "standard_parallel_2",
        [Normalise("Latitude of second standard parallel")] = "standard_parallel_2",
        [Normalise("latitude_of_2nd_standard_parallel")] = "standard_parallel_2",
        [Normalise("Standard_Parallel_2")] = "standard_parallel_2",

        // The polar stereographic variant B's own parameter, which the reader
        // turns into the pole and the scale factor at that pole rather than
        // handing on: ProjNet's polar stereographic is the variant A
        // formulation, which has no standard parallel to read.
        [Normalise("Latitude of standard parallel")] = StandardParallelParameter,

        // The oblique Mercator's own parameters, which EPSG 9815 states as
        // the azimuth of the initial line and the angle from the rectified to
        // the skew grid. They are read because the no-rotation (variant B)
        // form is the one ProjNet and PROJ agree on; the variant A
        // definitions are not in the method map, so nothing that reaches
        // here depends on the difference between the two conventions.
        // EPSG spells parameter 8813 two ways — "Azimuth of initial line" on
        // the Swiss grids and "Azimuth at projection centre" on the Borneo
        // ones (EPSG:29873) — and both are the same number, so both are
        // read; taking only one leaves the Borneo grid unreadable.
        [Normalise("Azimuth of initial line")] = "azimuth",
        [Normalise("Azimuth at projection centre")] = "azimuth",
        [Normalise("azimuth")] = "azimuth",
        [Normalise("Angle from Rectified to Skew Grid")] = "rectified_grid_angle",
        [Normalise("rectified_grid_angle")] = "rectified_grid_angle",
        [Normalise("Scale factor at projection centre")] = "scale_factor",
        [Normalise("Easting at projection centre")] = "false_easting",
        [Normalise("Northing at projection centre")] = "false_northing",
    };

    // ---- the WKT grammar: nodes are KEYWORD["text", item...], items are nodes, quoted text or numbers ----

    private sealed record WktNode(string Keyword, string? Text, IReadOnlyList<WktItem> Items)
    {
        public IReadOnlyList<double> Numbers => [.. Items.OfType<WktNumber>().Select(number => number.Value)];
    }

    private abstract record WktItem;

    private sealed record WktChild(WktNode Node) : WktItem;

    private sealed record WktText(string Value) : WktItem;

    private sealed record WktNumber(double Value) : WktItem;

    private static bool TryParseNode(string wkt, int start, out WktNode? node, out int end, out string? error)
    {
        node = null;
        end = start;
        var index = SkipWhitespace(wkt, start);
        if (index >= wkt.Length || !char.IsLetter(wkt[index]))
        {
            end = index;
            error = $"expected a WKT keyword at position {index}.";
            return false;
        }

        var keywordStart = index;
        while (index < wkt.Length && (char.IsLetterOrDigit(wkt[index]) || wkt[index] == '_'))
        {
            index++;
        }

        var keyword = wkt[keywordStart..index];
        var items = new List<WktItem>();
        string? text = null;

        index = SkipWhitespace(wkt, index);
        if (index < wkt.Length && wkt[index] == '[')
        {
            if (!TryParseItems(wkt, index + 1, ']', items, out index, out error))
            {
                return false;
            }

            text = items.OfType<WktText>().FirstOrDefault()?.Value;
        }

        node = new WktNode(keyword.ToUpperInvariant(), text, items);
        end = index;
        error = null;
        return true;
    }

    /// <summary>
    /// Parses one node's items — a comma-separated list of nodes, quoted names
    /// and numbers — up to its closing bracket.
    /// </summary>
    private static bool TryParseItems(string wkt, int start, char close, List<WktItem> items, out int end, out string? error)
    {
        var index = start;
        end = start;
        while (true)
        {
            index = SkipWhitespace(wkt, index);
            if (index < wkt.Length && wkt[index] == ',')
            {
                index++;
                continue;
            }

            if (index < wkt.Length && wkt[index] == close)
            {
                end = index + 1;
                error = null;
                return true;
            }

            if (index >= wkt.Length)
            {
                end = index;
                error = $"the WKT has no closing '{close}'.";
                return false;
            }

            if (TryParseItem(wkt, ref index, items, out error))
            {
                continue;
            }

            end = index;
            return false;
        }
    }

    /// <summary>Parses the next item: a node, a quoted name or a number.</summary>
    private static bool TryParseItem(string wkt, ref int index, List<WktItem> items, out string? error)
    {
        error = null;
        if (char.IsLetter(wkt[index]))
        {
            if (TryParseNode(wkt, index, out var child, out index, out error))
            {
                items.Add(new WktChild(child!));
                return true;
            }

            return false;
        }

        if (wkt[index] is '"' or '\'')
        {
            if (!TryParseText(wkt, index, out var value, out index))
            {
                error = $"the quoted name at position {index} is not closed.";
                return false;
            }

            items.Add(new WktText(value));
            return true;
        }

        if (IsNumber(wkt[index]) && double.TryParse(NumberToken(wkt, index), NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
        {
            items.Add(new WktNumber(number));
            index += NumberToken(wkt, index).Length;
            return true;
        }

        error = $"'{wkt[index]}' at position {index} is not a WKT keyword, name, number or separator.";
        return false;
    }

    private static bool TryParseText(string wkt, int start, out string value, out int end)
    {
        var quote = wkt[start];
        var builder = new StringBuilder();
        var index = start + 1;
        while (index < wkt.Length)
        {
            if (wkt[index] == quote)
            {
                value = builder.ToString();
                end = index + 1;
                return true;
            }

            builder.Append(wkt[index]);
            index++;
        }

        value = string.Empty;
        end = index;
        return false;
    }

    private static bool IsNumber(char character) => char.IsDigit(character) || character is '-' or '+' or '.';

    /// <summary>The longest prefix of <paramref name="wkt"/> from <paramref name="start"/> that reads as a number.</summary>
    private static ReadOnlySpan<char> NumberToken(string wkt, int start)
    {
        var end = start;
        while (end < wkt.Length && (char.IsDigit(wkt[end]) || wkt[end] is '.' or 'e' or 'E' or '+' or '-'))
        {
            end++;
        }

        return wkt.AsSpan(start, end - start);
    }

    private static int SkipWhitespace(string wkt, int index)
    {
        while (index < wkt.Length && char.IsWhiteSpace(wkt[index]))
        {
            index++;
        }

        return index;
    }
}
