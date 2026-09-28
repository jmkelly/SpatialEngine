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

        Assert.Equal(3.0, node.AccuracyMetres, 9);
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
