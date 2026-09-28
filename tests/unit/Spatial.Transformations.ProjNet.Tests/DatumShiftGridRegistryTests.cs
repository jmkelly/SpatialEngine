using Spatial.Transformations.ProjNet.Grids;

namespace Spatial.Transformations.ProjNet.Tests;

/// <summary>
/// The grid registry: which configured directories it reads, in what order,
/// and what it does when the file behind an operation is absent or unreadable.
/// A grid bundle is third-party data the host may or may not have been given
/// (ADR-0105), so every one of these answers has to be a graceful "not
/// deployed" rather than a failure to start.
/// </summary>
public sealed class DatumShiftGridRegistryTests : IDisposable
{
    private const string OrdnanceSurvey = "OSGB36";
    private const string NorthAmerican = "NAD83";

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

    /// <summary>A fresh directory holding one grid bundle for the Ordnance Survey datum.</summary>
    private string DeployBundle(string name = EpsgGridShiftOperations.OsgbBundle, string subGrid = "OSTN15")
    {
        var directory = NewDirectory();
        var grid = Ntv2Fixture.Constant(subGrid, 49.75, 50.75, -9.0, 0.0, 0.25, 0.25, new(1.0f, 2.0f, 0.05f, 0.05f));
        File.WriteAllBytes(Path.Combine(directory, name), Ntv2Fixture.ToBytes(grid));
        return directory;
    }

    private string NewDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"spatialengine-grids-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        _directories.Add(directory);
        return directory;
    }

    [Fact]
    public void A_host_with_no_configured_grid_directory_serves_no_grid()
    {
        var registry = DatumShiftGridRegistry.Empty;

        Assert.Empty(registry.Directories);
        Assert.False(registry.TryGet(OrdnanceSurvey, out var grid));
        Assert.Null(grid);
    }

    [Fact]
    public void A_deployed_bundle_is_found_for_the_datum_it_serves()
    {
        var registry = DatumShiftGridRegistry.Load([DeployBundle()]);

        Assert.True(registry.TryGet(OrdnanceSurvey, out var grid));
        Assert.Equal("OSTN15", grid!.Name);
        Assert.Equal(EpsgGridShiftOperations.OsgbBundle, grid.FileName);
        Assert.Equal(0.05, grid.AccuracyMetres, 6);
    }

    [Fact]
    public void A_bundle_is_read_when_the_grid_is_asked_for_and_not_before()
    {
        var directory = NewDirectory();
        var registry = DatumShiftGridRegistry.Load([directory]);
        Assert.False(registry.TryGet(OrdnanceSurvey, out _));

        // The file arrives after the host started: a grid is deployed by
        // dropping a bundle into a configured directory, and a host that
        // scanned once at start-up would need a restart to see it.
        var grid = Ntv2Fixture.Constant("OSTN15", 49.75, 50.75, -9.0, 0.0, 0.25, 0.25, new(1.0f, 2.0f, 0.05f, 0.05f));
        File.WriteAllBytes(Path.Combine(directory, EpsgGridShiftOperations.OsgbBundle), Ntv2Fixture.ToBytes(grid));

        Assert.True(registry.TryGet(OrdnanceSurvey, out var found));
        Assert.Equal("OSTN15", found!.Name);
    }

    [Fact]
    public void A_bundle_is_read_from_disk_once_and_then_served_from_the_cache()
    {
        var directory = DeployBundle();
        var registry = DatumShiftGridRegistry.Load([directory]);
        Assert.True(registry.TryGet(OrdnanceSurvey, out var first));

        // Rewriting the file with different shifts must not change what the
        // registry answers: the cache exists so a transform on the hot path
        // does not re-read and re-parse a multi-megabyte bundle per request.
        var replacement = Ntv2Fixture.Constant("OSTN15", 49.75, 50.75, -9.0, 0.0, 0.25, 0.25, new(9.0f, 9.0f, 0.05f, 0.05f));
        File.WriteAllBytes(Path.Combine(directory, EpsgGridShiftOperations.OsgbBundle), Ntv2Fixture.ToBytes(replacement));

        Assert.True(registry.TryGet(OrdnanceSurvey, out var second));
        Assert.True(registry.TryGet(OrdnanceSurvey, out var third));
        Assert.Equal(1.0, ShiftOf(first!, -0.1276, 50.0), 9);
        Assert.Equal(1.0, ShiftOf(second!, -0.1276, 50.0), 9);
        Assert.Equal(1.0, ShiftOf(third!, -0.1276, 50.0), 9);
    }

    [Fact]
    public void A_bundle_in_an_earlier_directory_wins_over_a_later_one()
    {
        var preferred = NewDirectory();
        var fallback = NewDirectory();
        Write(preferred, 1.0f);
        Write(fallback, 5.0f);

        var registry = DatumShiftGridRegistry.Load([preferred, fallback]);
        Assert.True(registry.TryGet(OrdnanceSurvey, out var grid));
        Assert.Equal(1.0, ShiftOf(grid!, -0.1276, 50.0), 6);

        // The documented order is a priority, not a set: reversing it reverses
        // which bundle is deployed, which is the whole point of an operator
        // being able to shadow a shipped default.
        var reversed = DatumShiftGridRegistry.Load([fallback, preferred]);
        Assert.True(reversed.TryGet(OrdnanceSurvey, out var shadowed));
        Assert.Equal(5.0, ShiftOf(shadowed!, -0.1276, 50.0), 6);

        void Write(string directory, float shift) =>
            File.WriteAllBytes(
                Path.Combine(directory, EpsgGridShiftOperations.OsgbBundle),
                Ntv2Fixture.ToBytes(Ntv2Fixture.Constant("OSTN15", 49.75, 50.75, -9.0, 0.0, 0.25, 0.25, new(shift, shift, 0.05f, 0.05f))));
    }

    [Fact]
    public void A_directory_that_does_not_exist_is_skipped_rather_than_failing()
    {
        var registry = DatumShiftGridRegistry.Load(["/does/not/exist", DeployBundle()]);

        Assert.True(registry.TryGet(OrdnanceSurvey, out _));
        Assert.Empty(registry.Failures);
    }

    [Fact]
    public void A_bundle_that_is_not_a_grid_is_reported_and_the_host_still_starts()
    {
        var directory = NewDirectory();
        File.WriteAllText(Path.Combine(directory, EpsgGridShiftOperations.OsgbBundle), "not a grid at all");

        var registry = DatumShiftGridRegistry.Load([directory]);

        Assert.False(registry.TryGet(OrdnanceSurvey, out _));
        var failure = Assert.Single(registry.Failures);
        Assert.Equal(EpsgGridShiftOperations.OsgbBundle, failure.FileName);
        Assert.NotEmpty(failure.Reason);
    }

    [Fact]
    public void An_unreadable_bundle_does_not_stop_a_later_directory_from_serving()
    {
        var broken = NewDirectory();
        File.WriteAllText(Path.Combine(broken, EpsgGridShiftOperations.OsgbBundle), "not a grid at all");
        var working = DeployBundle();

        var registry = DatumShiftGridRegistry.Load([broken, working]);

        Assert.True(registry.TryGet(OrdnanceSurvey, out var grid));
        Assert.Equal(1.0, ShiftOf(grid!, -0.1276, 50.0), 6);
        Assert.Single(registry.Failures);
    }

    [Fact]
    public void A_datum_no_bundle_serves_reports_no_grid_and_no_failure()
    {
        var registry = DatumShiftGridRegistry.Load([DeployBundle()]);

        Assert.False(registry.TryGet(NorthAmerican, out _));
        Assert.Empty(registry.Failures);
    }

    [Fact]
    public void The_configured_directories_are_reported_as_configured()
    {
        var directory = NewDirectory();
        var registry = DatumShiftGridRegistry.Load([directory, "/does/not/exist"]);

        // Both are reported, including the one that does not exist: an operator
        // who mistyped a path needs to see it echoed back, not silently absent.
        Assert.Equal([directory, "/does/not/exist"], registry.Directories);
    }

    /// <summary>The latitude shift a grid applies, in seconds of arc.</summary>
    private static double ShiftOf(DatumShiftGrid grid, double longitude, double latitude)
    {
        Assert.True(grid.TryShiftForward(longitude, latitude, out var shiftedLatitude, out _));
        return (shiftedLatitude - latitude) * 3600.0;
    }
}
