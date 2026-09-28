using Spatial.Contracts.TransformationSearch;

namespace Spatial.Transformations.ProjNet.Tests;

/// <summary>
/// Where a datum's transformation data comes from (ADR-0086). A definition
/// read from WKT carries the datum's shift to WGS 84 and nothing else about
/// the operation; the accuracy that shift is stated at and the ground it is
/// valid over are attributes of the transformation EPSG registers against that
/// datum, and WKT states neither. These tests pin the split, because a
/// definition that grew an accuracy column would put the same datum's numbers
/// in two places and let them disagree.
/// </summary>
public sealed class EpsgDatumOperationsTests
{
    [Fact]
    public void The_datum_shift_is_the_one_the_WKT_definition_carries()
    {
        // The OSGB 36 definition's own TOWGS84 node, read from the vendored
        // WKT: the graph's node must be this, not a restatement of it.
        var definition = Geodetic(4277);
        var shift = definition.ToWgs84;

        Assert.True(EpsgDatumOperations.TryGetNode(definition, out var node));
        Assert.Equal(shift[0], node!.ToWgs84.Tx, 9);
        Assert.Equal(shift[1], node.ToWgs84.Ty, 9);
        Assert.Equal(shift[2], node.ToWgs84.Tz, 9);
        Assert.Equal(shift[3], node.ToWgs84.Rx, 9);
        Assert.Equal(shift[4], node.ToWgs84.Ry, 9);
        Assert.Equal(shift[5], node.ToWgs84.Rz, 9);
        Assert.Equal(shift[6], node.ToWgs84.ScalePpm, 9);

        // NAD83 carries a zero shift, so a node that restated one datum's
        // parameters for every datum would hand this one a shift it does not
        // have - and the search would offer operations that move nothing.
        var modern = Geodetic(4269);
        Assert.Equal([0, 0, 0, 0, 0, 0, 0], modern.ToWgs84);
        Assert.True(EpsgDatumOperations.TryGetNode(modern, out var nad83));
        Assert.True(HelmertAlgebra.IsNull(nad83!.ToWgs84));
    }

    [Fact]
    public void The_accuracy_and_the_area_of_use_are_the_registered_operation_s()
    {
        // Neither number is in the WKT - the definition carries no accuracy
        // and no usage - so they can only have come from the operation table.
        var node = NodeOf(4277);

        Assert.Equal(2.0, node.AccuracyMetres, 9);
        Assert.Equal("Great Britain", node.AreaOfUse.Name);
        Assert.Equal(-8.82, node.AreaOfUse.XMin, 6);
        Assert.Equal(60.94, node.AreaOfUse.YMax, 6);

        // The row itself, read the way the join reads it, is keyed on the
        // datum name the vendored DATUM node carries verbatim.
        var operation = EpsgDatumOperations.ReadForTest("Ordnance Survey of Great Britain 1936");
        Assert.NotNull(operation);
        Assert.Equal(6277, operation!.DatumCode);
        Assert.Equal("OSGB36", operation.GraphName);
    }

    // The rows below are the EPSG Geodetic Parameter Dataset v13.102, read from
    // its own coordinate-operation and extent records. Every number a row
    // carries is pinned here against the registry code it was read from, so a
    // number that drifts from what EPSG publishes fails instead of quietly
    // becoming the engine's opinion. The coverage test above proves every
    // definition has a row; this one proves the rows are right.
    [Theory]
    // EPSG:1262 "World" - the WGS 84 datum ensemble's own registered extent,
    // and the pivot rather than a published operation, so its accuracy is the
    // zero a datum already at the pivot has rather than a measured one.
    [InlineData("World Geodetic System 1984", 6326, "WGS84", 0.0, "World", -180.0, -90.0, 180.0, 90.0, 0, 1262)]
    // EPSG:1149 "ETRS89 to WGS 84 (1)" over extent 4755 "Europe - ETRF by
    // country". Registered west 16.1W, east 38.0E, south 33.26N, north
    // 84.73N.
    [InlineData("European Terrestrial Reference System 1989", 6258, "ETRS89", 1.0, "Europe", -16.1, 33.26, 38.0, 84.73, 1149, 4755)]
    // EPSG:1188 "NAD83 to WGS 84 (1)" over extent 1325. The registry states
    // 4.0 m; the "2m in each axis" in the row's own remarks is where the old
    // 2.0 came from, and it is a statement about the derivation, not the
    // published accuracy of the operation.
    [InlineData("North American Datum 1983", 6269, "NAD83", 4.0, "North America", -172.54, 23.81, -47.74, 86.46, 1188, 1325)]
    // EPSG:1314 "OSGB36 to WGS 84 (6)" over extent 1264 - the operation whose
    // seven parameters are exactly the vendored TOWGS84 node, so it is the one
    // this row describes, and it states 2.0 m.
    [InlineData("Ordnance Survey of Great Britain 1936", 6277, "OSGB36", 2.0, "Great Britain", -8.82, 49.79, 1.92, 60.94, 1314, 1264)]
    // EPSG:1671 "ETRS89-FRA [RGF93 v1] to WGS 84 (1)" over extent 1096
    // "France" - the operation registered for CRS EPSG:4171, which is the
    // vendored RGF93 v1 definition.
    [InlineData("Reseau Geodesique Francais 1993", 6171, "RGF93", 1.0, "France", -9.86, 41.15, 10.38, 51.56, 1671, 1096)]
    // EPSG:1565 "NZGD2000 to WGS 84 (1)" over extent 1175 "New Zealand":
    // south 55.95S, north 25.88S, and a longitude span the registry publishes
    // as 160.6E to 171.2W. That span crosses the antimeridian, which is a
    // wrapped extent rather than a box, and an area of use here is a box - so
    // the row carries the registered extent clipped at 180. See
    // The_new_zealand_extent_is_clipped_where_it_crosses_the_antimeridian.
    [InlineData("New Zealand Geodetic Datum 2000", 6167, "NZGD2000", 1.0, "New Zealand", 160.6, -55.95, 180.0, -25.88, 1565, 1175)]
    public void Every_row_states_the_numbers_the_registry_publishes(
        string datumName,
        int datumCode,
        string graphName,
        double accuracyMetres,
        string areaOfUseName,
        double xMin,
        double yMin,
        double xMax,
        double yMax,
        int operationCode,
        int extentCode)
    {
        var row = EpsgDatumOperations.ReadForTest(datumName);
        Assert.NotNull(row);

        // The registry code the numbers were read from travels with the row,
        // so a reader can check a value against EPSG without guessing which
        // record the curator had open, and a row that names the wrong record
        // is as wrong as one that carries the wrong number.
        Assert.Equal(datumCode, row!.DatumCode);
        Assert.Equal(operationCode, row.OperationCode);
        Assert.Equal(extentCode, row.ExtentCode);

        Assert.Equal(graphName, row.GraphName);
        Assert.Equal(accuracyMetres, row.AccuracyMetres, 9);
        Assert.Equal(areaOfUseName, row.AreaOfUseName);
        Assert.Equal(xMin, row.AreaOfUse.XMin, 9);
        Assert.Equal(yMin, row.AreaOfUse.YMin, 9);
        Assert.Equal(xMax, row.AreaOfUse.XMax, 9);
        Assert.Equal(yMax, row.AreaOfUse.YMax, 9);
    }

    [Fact]
    public void The_new_zealand_extent_is_clipped_where_it_crosses_the_antimeridian()
    {
        // EPSG:1175 registers New Zealand as 160.6E to 171.2W. An area of use
        // in this engine is a box that cannot wrap, so the row carries the
        // registered latitudes and the registered western bound, clipped at
        // the edge of the world rather than invented past it. The clip is the
        // documented behaviour, not a number nobody chose: a point inside the
        // registered extent west of the antimeridian (the Chathams) falls
        // outside the clipped box, which is the gap a wrapped-extent area of
        // use would close.
        var row = EpsgDatumOperations.ReadForTest("New Zealand Geodetic Datum 2000");
        Assert.NotNull(row);

        Assert.Equal(-55.95, row!.AreaOfUse.YMin, 9);
        Assert.Equal(-25.88, row.AreaOfUse.YMax, 9);
        Assert.Equal(160.6, row.AreaOfUse.XMin, 9);
        Assert.Equal(180.0, row.AreaOfUse.XMax, 9);

        // The clipped box is still a box: a rectangle that had been taken from
        // the registry verbatim would have been 160.6E to 171.2W, which reads
        // as empty and would drop New Zealand out of the graph as a datum no
        // operation covers.
        Assert.True(row.AreaOfUse.XMin <= row.AreaOfUse.XMax, "the clipped extent must not be an empty box.");
        Assert.True(row.AreaOfUse.YMin <= row.AreaOfUse.YMax, "the clipped extent must not be an empty box.");
    }

    [Fact]
    public void Every_geodetic_definition_the_catalogue_serves_has_a_published_operation()
    {
        // The join is by name, so a definition whose datum the table does not
        // name would silently fall out of the graph. A datum with no published
        // operation must be a loud gap at the catalogue's own boundary, not a
        // transformation served with a fabricated accuracy.
        foreach (var code in EpsgEpsgCodes.Geodetic)
        {
            var definition = Geodetic(code);
            Assert.True(
                EpsgDatumOperations.TryGetNode(definition, out _),
                $"EPSG:{code} ({definition.DatumName}) has no datum-to-WGS 84 operation, so it is missing from the transformation graph.");
        }

        Assert.Equal(
            EpsgEpsgCodes.Geodetic.Select(code => Geodetic(code).DatumName).Distinct().Count(),
            EpsgDatumOperations.DatumNames.Count());
    }

    [Fact]
    public void A_datum_with_no_published_operation_contributes_no_node()
    {
        var unknown = new GeodeticDefinition(
            "Unpublished",
            "Nobody Registered This Datum",
            "GRS 1980",
            6378137.0,
            298.257222101,
            [1.0, 2.0, 3.0, 0.0, 0.0, 0.0, 0.0]);

        Assert.False(EpsgDatumOperations.TryGetNode(unknown, out var node));
        Assert.Null(node);
    }

    [Fact]
    public void The_graph_pivot_is_the_catalogues_own_WGS84_definition()
    {
        // The pivot the graph composes through is read from the catalogue, so
        // the shift the graph publishes is the shift the engine applies. A
        // pivot restated as a literal in the graph would be a second place for
        // WGS 84's parameters to live.
        Assert.Equal(NodeOf(4326).ToWgs84, ProjEpsgCatalog.WorldDatum().ToWgs84);
        Assert.Equal("WGS84", ProjEpsgCatalog.WorldDatum().Code);
    }

    [Fact]
    public void A_projected_CRS_resolves_to_its_geodetic_base_s_datum()
    {
        // 27700 stands on OSGB 36 and 2193 on NZGD2000: the graph is keyed on
        // datums, so a projected definition resolves through the base the WKT
        // names.
        Assert.Equal(NodeOf(4277), ProjEpsgCatalog.TryGetDatum(27700, out var british) ? british : null);
        Assert.True(ProjEpsgCatalog.TryGetDatum(2193, out var newZealand));
        Assert.Equal("NZGD2000", newZealand!.Code);
        Assert.Equal("New Zealand", newZealand.AreaOfUse.Name);
    }

    [Fact]
    public void Web_Mercator_is_still_a_3857_the_engine_can_construct()
    {
        // The WKT path is the definition model and the graph is a second
        // consumer of it. Adding a consumer must not disturb what the first
        // one serves: 3857 still resolves through the intercepted
        // Pseudo-Mercator construction, on the pivot datum, and still projects
        // to the same metres it always did.
        var definition = (ProjectedDefinition)ProjEpsgCatalog.DefinitionOf(3857);
        Assert.Equal("Popular Visualisation Pseudo-Mercator", definition.ProjectionClass);
        Assert.Equal("World Geodetic System 1984", definition.Base.DatumName);
        Assert.True(ProjEpsgCatalog.TryGetDatum(3857, out var webMercator));
        Assert.Equal("WGS84", webMercator!.Code);
        Assert.Equal(0.0, webMercator.AccuracyMetres, 9);
        Assert.True(ProjEpsgCatalog.TryGet(3857, out var system));
        Assert.NotNull(system);
    }

    private static DatumNode NodeOf(int code) =>
        ProjEpsgCatalog.TryGetDatum(code, out var node) && node is not null
            ? node
            : throw new InvalidOperationException($"EPSG:{code} has no datum node.");

    private static GeodeticDefinition Geodetic(int code) =>
        ProjEpsgCatalog.DefinitionOf(code) as GeodeticDefinition
        ?? throw new InvalidOperationException($"EPSG:{code} is not a geodetic definition.");
}

/// <summary>
/// The geodetic codes the catalogue's own vendored WKT defines, read from the
/// definitions rather than restated, so a new definition cannot slip past the
/// coverage check above.
/// </summary>
internal static class EpsgEpsgCodes
{
    public static IEnumerable<int> Geodetic =>
        EpsgWktDefinitions.Geographic.Select(entry => entry.Code);
}
