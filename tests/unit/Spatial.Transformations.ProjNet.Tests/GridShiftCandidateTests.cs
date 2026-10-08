using Spatial.Contracts.TransformationSearch;
using Spatial.Transformations.ProjNet.Grids;

namespace Spatial.Transformations.ProjNet.Tests;

/// <summary>
/// The grid-backed candidate between two datums (ADR-0105): the grid stands
/// in for the Helmert leg of whichever datum it serves, a grid that lands
/// beside the pivot continues along that datum's own path, and a grid that
/// lands on a datum the catalogue does not serve is not published at all.
/// Exercised on synthetic datum nodes so both legs can be gridded at once,
/// which the served catalogue cannot supply.
/// </summary>
public sealed class GridShiftCandidateTests : IDisposable
{
    private readonly List<string> _directories = [];

    public void Dispose()
    {
        foreach (var directory in _directories)
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    private string NewDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"spatialengine-candidate-{Guid.NewGuid():N}");
        System.IO.Directory.CreateDirectory(directory);
        _directories.Add(directory);
        return directory;
    }

    private static Ntv2Fixture.Shift Shift(float latitudeSeconds, float longitudeSeconds, float accuracy) =>
        new(latitudeSeconds, longitudeSeconds, accuracy, accuracy);

    private static DatumNode Shifted(string name, double translation, double accuracy) =>
        new(
            name,
            name,
            new HelmertParameters(translation, 0, 0, 0, 0, 0, 0),
            accuracy,
            CrsAreaOfUse.One(name, -10.0, 40.0, 10.0, 60.0));

    private string Ntv2Bundle(string fileName, string gridName, float accuracy)
    {
        var directory = NewDirectory();
        File.WriteAllBytes(
            Path.Combine(directory, fileName),
            Ntv2Fixture.ToBytes(Ntv2Fixture.Constant(gridName, 49.0, 51.0, -9.0, 1.0, 1.0, 1.0, Shift(1.0f, 2.0f, accuracy))));
        return directory;
    }

    private static void NadconPair(string directory, string latitudeFile, string longitudeFile, string gridName)
    {
        var latitudes = NadconFixture.Constant(gridName, 49.0, 51.0, -9.0, 1.0, 1.0, 1.0, 1.0f);
        var longitudes = NadconFixture.Constant(gridName, 49.0, 51.0, -9.0, 1.0, 1.0, 1.0, 3.0f);
        File.WriteAllBytes(Path.Combine(directory, latitudeFile), NadconFixture.ToBytes(latitudes, longitude: false));
        File.WriteAllBytes(Path.Combine(directory, longitudeFile), NadconFixture.ToBytes(longitudes, longitude: true));
    }

    [Fact]
    public void Grids_on_both_legs_concatenate_through_the_pivot()
    {
        var first = Ntv2Bundle("alpha.gsb", "ALPHA", 0.05f);
        var second = Ntv2Bundle("beta.gsb", "BETA", 0.07f);
        var registry = DatumShiftGridRegistry.Load(
            [first, second],
            [
                new EpsgGridShiftOperations.GridShiftOperation("Alpha", "alpha.gsb", 4326, "test"),
                new EpsgGridShiftOperations.GridShiftOperation("Beta", "beta.gsb", 4326, "test"),
            ]);

        var candidate = Assert.Single(
            DatumTransformationGraph.Search(Shifted("Alpha", 100.0, 2.0), Shifted("Beta", 200.0, 3.0), registry),
            candidate => candidate.Steps.Any(step => step.GridShift is not null));

        Assert.Equal("Alpha_To_Beta_Grid_ALPHA", candidate.Name);
        Assert.Equal(2, candidate.Steps.Count);
        Assert.All(candidate.Steps, step => Assert.NotNull(step.GridShift));
        Assert.Contains("alpha.gsb", candidate.Method, StringComparison.Ordinal);
        Assert.Contains("beta.gsb", candidate.Method, StringComparison.Ordinal);
        Assert.Contains("concatenated through WGS 84", candidate.Method, StringComparison.Ordinal);

        var expected = Math.Sqrt(((double)0.05f * (double)0.05f) + ((double)0.07f * (double)0.07f));
        Assert.Equal(expected, candidate.AccuracyMetres, 9);
        Assert.False(candidate.Approximate, "no leg of this candidate is the Helmert approximation.");
    }

    [Fact]
    public void Grids_in_different_standards_are_each_named()
    {
        var directory = NewDirectory();
        File.WriteAllBytes(
            Path.Combine(directory, "alpha.gsb"),
            Ntv2Fixture.ToBytes(Ntv2Fixture.Constant("ALPHA", 49.0, 51.0, -9.0, 1.0, 1.0, 1.0, Shift(1.0f, 2.0f, 0.05f))));
        NadconPair(directory, "beta.las", "beta.los", "BETA");
        var registry = DatumShiftGridRegistry.Load(
            [directory],
            [
                new EpsgGridShiftOperations.GridShiftOperation("Alpha", "alpha.gsb", 4326, "test"),
                new EpsgGridShiftOperations.GridShiftOperation("Beta", "beta.las", 4326, "test", "beta.los", 0.15),
            ]);

        var candidate = Assert.Single(
            DatumTransformationGraph.Search(Shifted("Alpha", 100.0, 2.0), Shifted("Beta", 200.0, 3.0), registry),
            candidate => candidate.Steps.Count(step => step.GridShift is not null) == 2);

        Assert.Contains("NTv2 and NADCON", candidate.Method, StringComparison.Ordinal);
    }

    [Fact]
    public void A_grid_landing_on_a_datum_the_catalogue_does_not_serve_is_not_published()
    {
        var directory = Ntv2Bundle("alpha.gsb", "ALPHA", 0.05f);
        var registry = DatumShiftGridRegistry.Load(
            [directory],
            [new EpsgGridShiftOperations.GridShiftOperation("Alpha", "alpha.gsb", 999999, "test")]);

        var candidates = DatumTransformationGraph.Search(Shifted("Alpha", 100.0, 2.0), Shifted("Beta", 200.0, 3.0), registry);

        Assert.DoesNotContain(candidates, candidate => candidate.Steps.Any(step => step.GridShift is not null));
        Assert.NotEmpty(candidates);
    }

    [Fact]
    public void A_grid_landing_on_an_unserved_datum_on_the_far_leg_is_not_published()
    {
        var directory = Ntv2Bundle("alpha.gsb", "ALPHA", 0.05f);
        File.WriteAllBytes(
            Path.Combine(directory, "beta.gsb"),
            Ntv2Fixture.ToBytes(Ntv2Fixture.Constant("BETA", 49.0, 51.0, -9.0, 1.0, 1.0, 1.0, Shift(1.0f, 2.0f, 0.05f))));
        var registry = DatumShiftGridRegistry.Load(
            [directory],
            [
                new EpsgGridShiftOperations.GridShiftOperation("Alpha", "alpha.gsb", 4326, "test"),
                new EpsgGridShiftOperations.GridShiftOperation("Beta", "beta.gsb", 999999, "test"),
            ]);

        var candidates = DatumTransformationGraph.Search(Shifted("Alpha", 100.0, 2.0), Shifted("Beta", 200.0, 3.0), registry);

        Assert.DoesNotContain(candidates, candidate => candidate.Steps.Any(step => step.GridShift is not null));
        Assert.NotEmpty(candidates);
    }

    [Fact]
    public void A_grid_landing_beside_the_pivot_continues_along_that_datums_path()
    {
        // NAD83 (EPSG:4269) is served but is not the pivot: a bundle landing
        // there continues from NAD83 to WGS 84 along the registered null
        // operation, and the far leg is still the Helmert it always was.
        var directory = Ntv2Bundle("alpha.gsb", "ALPHA", 0.05f);
        var registry = DatumShiftGridRegistry.Load(
            [directory],
            [new EpsgGridShiftOperations.GridShiftOperation("Alpha", "alpha.gsb", 4269, "test")]);

        var candidate = Assert.Single(
            DatumTransformationGraph.Search(Shifted("Alpha", 100.0, 2.0), Shifted("Beta", 200.0, 3.0), registry),
            candidate => candidate.Steps.Any(step => step.GridShift is not null));

        Assert.Equal(3, candidate.Steps.Count);
        Assert.NotNull(candidate.Steps[0].GridShift);
        Assert.All(candidate.Steps.Skip(1), step => Assert.Null(step.GridShift));
        Assert.True(candidate.Approximate, "the far leg is still the Helmert approximation.");
        Assert.True(candidate.AccuracyMetres > (double)0.05f, "the legs behind the grid still cost accuracy.");
    }
}
