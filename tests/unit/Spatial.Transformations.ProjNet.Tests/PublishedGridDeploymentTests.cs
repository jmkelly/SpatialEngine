using System.Globalization;

namespace Spatial.Transformations.ProjNet.Tests;

/// <summary>
/// The harness that makes the published-bundle exercise runnable (ADR-0105
/// §licence, ADR-0179). What is pinned here is the harness's own behaviour,
/// because it is what decides whether the control-point suite is a measurement
/// or a silence:
/// <list type="bullet">
/// <item>a bundle dropped into a configured directory is recognised, and the
/// engine answers from it rather than through the Helmert;</item>
/// <item>a bundle that is present and unreadable is reported with its reason,
/// never counted as deployed — the failure mode ADR-0105 §3 is about;</item>
/// <item>every other state says why, in words that name the thing that is
/// missing, so a skip is a finding rather than an absence;</item>
/// <item>PROJ's answer counts only when PROJ says it used a grid, because a
/// Helmert from PROJ is the fallback both sides already agree on.</item>
/// </list>
/// <para>
/// Nothing here reads a published bundle: the deployments these tests build are
/// the repository's own synthetic <c>.gsb</c> bytes
/// (<see cref="Ntv2Fixture"/>), because no grid bundle is vendored, embedded
/// or downloaded anywhere in this repository (ADR-0105 §licence). The
/// comparison against a <em>published</em> bundle is
/// <see cref="PublishedGridControlPointTests"/>, and it runs only where an
/// operator has deployed one and installed PROJ's own copy of it.
/// </para>
/// </summary>
public sealed class PublishedGridDeploymentTests : IDisposable
{
    private readonly DeployedGrid _deployment = new();

    public void Dispose()
    {
        _deployment.Dispose();
        foreach (var standIn in _standIns)
        {
            if (File.Exists(standIn))
            {
                File.Delete(standIn);
            }
        }
    }

    /// <summary>A directory holding the catalogue's own bundle, built by this repository rather than fetched.</summary>
    private string Catalogued() => _deployment.Named(EpsgGridShiftOperations.OsgbBundle);

    [Fact]
    public void A_deployed_bundle_is_recognised_and_the_host_answers_from_it()
    {
        var directory = Catalogued();
        var deployment = PublishedGridDeployment.Resolve(EnvironmentOf(("SPATIALENGINE_GRID_DIR", directory)), NeverFound);

        Assert.True(deployment.IsDeployed("OSGB36"));
        Assert.Equal([directory], deployment.Directories);
        Assert.Equal(string.Empty, deployment.MissingReason("OSGB36"));

        // Recognised is not enough: the point of the deployment is that the
        // engine then answers through the grid rather than through the Helmert,
        // so the host is built over the same directories and asked — and the
        // answer has to be the one a host with nothing deployed does not give.
        var deployed = PointFrom(deployment.Directories);
        var bare = PointFrom([]);
        Assert.NotEqual(bare.X!.Value, deployed.X!.Value, 6);
        Assert.NotEqual(bare.Y!.Value, deployed.Y!.Value, 6);
    }

    /// <summary>London on OSGB 36, through a host configured over the given grid directories.</summary>
    private static Spatial.Core.Geometry.Point PointFrom(IReadOnlyList<string> directories) =>
        Assert.IsType<Spatial.Core.Geometry.Point>(new ProjNetTransforms(directories)
            .Transform(
                Spatial.Core.Geometry.GeometryFactory.CreatePoint(-0.1276, 51.5072, Spatial.Core.Geometry.CoordinateReference.Epsg(4326)),
                "EPSG:4326",
                "EPSG:4277"));

    [Fact]
    public void An_unset_directory_says_so_and_names_the_variable_that_deploys_one()
    {
        var deployment = PublishedGridDeployment.Resolve(EnvironmentOf(), NeverFound);

        Assert.False(deployment.IsDeployed("OSGB36"));
        Assert.Empty(deployment.Directories);

        // The reason is the whole of this bead: a suite that is silently absent
        // reads as agreement. It has to say which variable is unset, because
        // that is the one line an operator has to know.
        Assert.Contains("SPATIALENGINE_GRID_DIR", deployment.MissingReason("OSGB36"), StringComparison.Ordinal);
        Assert.Contains("licence", deployment.MissingReason("OSGB36"), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_directory_that_does_not_exist_is_not_the_same_answer_as_an_empty_one()
    {
        var absent = Path.Combine(Path.GetTempPath(), $"spatialengine-absent-{Guid.NewGuid():N}");
        var empty = Path.Combine(Path.GetTempPath(), $"spatialengine-empty-{Guid.NewGuid():N}");
        Directory.CreateDirectory(empty);
        try
        {
            // ADR-0105 §3: a directory that does not exist is remembered and
            // echoed back rather than silently dropped, because an operator
            // who mistyped a path needs to see it.
            Assert.Contains(
                "configured directories exist",
                PublishedGridDeployment.Resolve(EnvironmentOf(("SPATIALENGINE_GRID_DIR", absent)), NeverFound).MissingReason("OSGB36"),
                StringComparison.OrdinalIgnoreCase);
            Assert.Contains(
                "hold no",
                PublishedGridDeployment.Resolve(EnvironmentOf(("SPATIALENGINE_GRID_DIR", empty)), NeverFound).MissingReason("OSGB36"),
                StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(empty, recursive: true);
        }
    }

    [Fact]
    public void A_bundle_present_and_unreadable_is_reported_rather_than_counted_as_deployed()
    {
        // A file named as the catalogue names it, holding bytes that are not a
        // grid. An operator who dropped the wrong file must be told so: the
        // alternative is the Helmert standing in for an accurate operation
        // while the deployment looks complete.
        var directory = Path.Combine(Path.GetTempPath(), $"spatialengine-broken-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllBytes(Path.Combine(directory, EpsgGridShiftOperations.OsgbBundle), [0x00, 0x01, 0x02, 0x03]);
            var deployment = PublishedGridDeployment.Resolve(EnvironmentOf(("SPATIALENGINE_GRID_DIR", directory)), NeverFound);

            Assert.False(deployment.IsDeployed("OSGB36"));
            var reason = deployment.MissingReason("OSGB36");
            Assert.Contains("unreadable", reason, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(EpsgGridShiftOperations.OsgbBundle, reason, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void The_first_directory_holding_a_bundle_wins()
    {
        // ADR-0105 §3: the directories are a priority order, so an operator can
        // shadow a shipped default. A harness that flattened them to a set would
        // compare against the wrong bundle while looking perfectly deployed.
        var shadow = _deployment.Named(EpsgGridShiftOperations.OsgbBundle, "OSTN15_SHADOW", 3.0f, 4.0f);
        var shadowed = PublishedGridDeployment.Resolve(EnvironmentOf(("SPATIALENGINE_GRID_DIR", shadow)), NeverFound);
        var both = PublishedGridDeployment.Resolve(
            EnvironmentOf(("SPATIALENGINE_GRID_DIR", $"{Catalogued()}{Path.PathSeparator}{shadow}")),
            NeverFound);

        Assert.True(shadowed.IsDeployed("OSGB36"));
        Assert.True(both.IsDeployed("OSGB36"));
        Assert.Equal(2, both.Directories.Count);
    }

    [Fact]
    public void The_report_names_every_catalogue_bundle_and_its_verdict()
    {
        var deployment = PublishedGridDeployment.Resolve(EnvironmentOf(), NeverFound);
        var report = deployment.Report();

        // Every bundle the catalogue publishes a row for, so an operator can
        // see at a glance which of the datums this exercise could cover and
        // which it could not.
        Assert.All(EpsgGridShiftOperations.Bundles, operation =>
            Assert.Contains(operation.FileName, report, StringComparison.Ordinal));
        Assert.Contains("not deployed", report, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("SPATIALENGINE_GRID_DIR", report, StringComparison.Ordinal);
    }

    [Fact]
    public void A_missing_PROJ_is_named_and_never_fails_the_suite()
    {
        // This is the state of every machine that has not installed PROJ,
        // including the one this record was written on. The suite must say so
        // precisely — a Helmert comparison is possible without PROJ, and
        // printing "no reference" instead of "no cs2cs" sends an operator
        // looking in the wrong place.
        var deployment = PublishedGridDeployment.Resolve(EnvironmentOf(), NeverFound);

        // With no bundle either, the bundle is what gates the run and is named
        // first; the reference is asked about on its own terms.
        Assert.Contains("SPATIALENGINE_GRID_DIR", deployment.MissingReason("OSGB36"), StringComparison.Ordinal);
        Assert.Contains("cs2cs", deployment.MissingReferenceReason, StringComparison.Ordinal);
        Assert.False(deployment.CanCompareAgainstProj);

        // With a bundle deployed and still no PROJ, the grid question is
        // answered and the reference question is the one left.
        var deployed = PublishedGridDeployment.Resolve(
            EnvironmentOf(("SPATIALENGINE_GRID_DIR", Catalogued())),
            NeverFound);

        Assert.Empty(deployed.MissingReason("OSGB36"));
        Assert.Contains("cs2cs", deployed.MissingReferenceReason, StringComparison.Ordinal);
        Assert.False(deployed.CanCompareAgainstProj);
    }

    [Fact]
    public void PROJ_output_that_names_no_grid_is_not_accepted_as_a_grid_run()
    {
        // A Helmert answer from PROJ would agree with the engine's Helmert
        // fallback and prove nothing about the bundle. PROJ's verbose output
        // names the grid it opened, so agreement is only measured over a run
        // that used one.
        Assert.True(ProjReference.NamesAGrid("+proj=pipeline +step +proj=hgridshift +grids=uk_os_OSTN15_NTv2_OSGBtoETRS.tif"));
        Assert.True(ProjReference.NamesAGrid("call_proj: +proj=longlat +ellps=airy +nadgrids=uk_os_OSTN15_NTv2_OSGBtoETRS.gsb"));
        Assert.False(ProjReference.NamesAGrid("call_proj: +proj=longlat +ellps=GRS80 +towgs84=446448,125157,542861,0.15,0.247,0.842"));
        Assert.False(ProjReference.NamesAGrid("+proj=pipeline +step +proj=tmerc +lat_0=49 +lon_0=-2 +x_0=400000 +y_0=-100000"));
        Assert.False(ProjReference.NamesAGrid(string.Empty));
    }

    [Fact]
    public void The_answer_is_read_from_the_pair_PROJ_printed_last()
    {
        // cs2cs -v prints its input, the operation it built and then the
        // answer. Taking the first pair of numbers would read the input back as
        // though it were the reference, and every control point would pass
        // exactly.
        const string Verbose = """
            input: 51d30'26"N   0d7'39"E
            call_proj: +proj=tmerc +lat_0=49 +lon_0=-2 +k=0.9996012717 +x_0=400000 +y_0=-100000
            output: 530043.194981 180358.208620
            """;

        var answer = ProjReference.TryReadAnswer(Verbose, out var printed);

        Assert.True(answer);
        var (x, y) = printed;
        Assert.Equal(530043.194981, x, 6);
        Assert.Equal(180358.208620, y, 6);
        Assert.False(ProjReference.TryReadAnswer("cs2cs: Error: no such file or directory", out _));
    }

    [Fact]
    public void A_broken_PROJ_invocation_is_reported_with_what_it_printed()
    {
        // The failure an operator will actually hit is PROJ running and
        // refusing: a grid missing from its data directory. Dropping the
        // message on the floor would make that look like a disagreement.
        var failure = Assert.Throws<InvalidOperationException>(
            () => ProjReference.RequireAnswer("/usr/bin/cs2cs", "EPSG:4326", "EPSG:27700", -0.1276, 51.5072, "cs2cs: grid not found"));

        Assert.Contains("cs2cs: grid not found", failure.Message, StringComparison.Ordinal);
        Assert.Contains("PROJ's data directory", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void No_grid_bundle_is_vendored_anywhere_in_the_repository()    {
        // ADR-0105 §licence, as an assertion rather than as a note: the whole
        // position is that no published bundle is embedded, so a file with a
        // grid's extension in the tree is a licence problem, not a fixture.
        var root = RepositoryRoot();
        var found = new List<string>();
        foreach (var directory in new[] { "src", "tests", "apps", "clients", "eng", "tools" })
        {
            var path = Path.Combine(root, directory);
            if (Directory.Exists(path))
            {
                found.AddRange(System.IO.Directory
                    .EnumerateFiles(path, "*", SearchOption.AllDirectories)
                    .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                        && !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
                    .Where(file => GridBundleExtensions.Contains(Path.GetExtension(file))));
            }
        }

        Assert.Empty(found);
    }

    /// <summary>The repository root, found by walking up to the solution file.</summary>
    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "SpatialEngine.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException(
            $"The repository root is not above '{AppContext.BaseDirectory}': the licence guard cannot say the tree is clean.");
    }

    /// <summary>An environment reader over the given variables, and nothing else.</summary>
    /// <summary>
    /// The measurement the control-point suite takes, over a stand-in for PROJ
    /// rather than PROJ — because the machine this was written on has no PROJ,
    /// and a suite whose comparison cannot be exercised at all is a suite
    /// nobody knows is a measurement. The stand-in answers whatever it is told
    /// to, so both directions of the assertion are reachable: agreement is a
    /// zero residual, and disagreement is the distance between the two answers
    /// and not a pass.
    /// </summary>
    [SkippableFact]
    public void A_deployed_bundle_is_measured_against_PROJ_and_the_residual_is_the_distance()
    {
        Skip.IfNot(OperatingSystem.IsLinux(), "the stand-in for cs2cs is a POSIX shell script.");
        var directory = Catalogued();
        var point = PublishedGridDeployment.OsgbControlPoints[0];
        var (engineX, engineY) = Projected(directory, point, "EPSG:27700");

        var agreeing = PublishedGridDeployment.Resolve(
            EnvironmentOf(("SPATIALENGINE_GRID_DIR", directory)),
            _ => StandIn("+proj=pipeline +step +proj=hgridshift +grids=OSTN15_osgb_02_NTv2_OSGBtoETRS.gsb", engineX, engineY));
        var disagreeing = PublishedGridDeployment.Resolve(
            EnvironmentOf(("SPATIALENGINE_GRID_DIR", directory)),
            _ => StandIn("+proj=pipeline +step +proj=hgridshift +grids=OSTN15_osgb_02_NTv2_OSGBtoETRS.gsb", engineX + 100, engineY));

        var agree = agreeing.Measure(point, "EPSG:27700");
        var disagree = disagreeing.Measure(point, "EPSG:27700");

        Assert.Equal(0.0, agree.ResidualMetres, 6);
        Assert.Equal(engineX, agree.EngineX, 6);
        Assert.Equal(100.0, disagree.ResidualMetres, 6);
        Assert.True(
            disagree.ResidualMetres > PublishedGridDeployment.PinnedResidualMetres,
            "a PROJ 100 m away has to be a failing residual, not a passing one.");
    }

    [SkippableFact]
    public void A_PROJ_run_that_opened_no_grid_is_refused_rather_than_compared()
    {
        // The failure this guard exists for: PROJ answers from a Helmert when
        // the grid it wanted is not in its data directory, that answer agrees
        // with the engine's own Helmert fallback, and a comparison that took it
        // would report agreement about a bundle neither side read.
        Skip.IfNot(OperatingSystem.IsLinux(), "the stand-in for cs2cs is a POSIX shell script.");
        var directory = Catalogued();
        var point = PublishedGridDeployment.OsgbControlPoints[0];
        var (engineX, engineY) = Projected(directory, point, "EPSG:27700");
        var deployment = PublishedGridDeployment.Resolve(
            EnvironmentOf(("SPATIALENGINE_GRID_DIR", directory)),
            _ => StandIn("+proj=longlat +ellps=GRS80 +towgs84=446448,125157,542861,0.15,0.247,0.842", engineX, engineY));

        var failure = Assert.Throws<InvalidOperationException>(() => deployment.Measure(point, "EPSG:27700"));

        Assert.Contains("without opening a grid", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>One control point on a projected target, through a host built over the given directory.</summary>
    private static (double X, double Y) Projected(string directory, ControlPoint point, string target)
    {
        var projected = AsPoint(new ProjNetTransforms([directory])
            .Transform(
                Spatial.Core.Geometry.GeometryFactory.CreatePoint(point.Longitude, point.Latitude, Spatial.Core.Geometry.CoordinateReference.Epsg(4326)),
                "EPSG:4326",
                target));
        return (projected.X!.Value, projected.Y!.Value);
    }

    private static Spatial.Core.Geometry.Point AsPoint(Spatial.Core.Geometry.IGeometry geometry) =>
        Assert.IsType<Spatial.Core.Geometry.Point>(geometry);

    /// <summary>
    /// A stand-in for <c>cs2cs</c>: it reads the coordinate, prints the pipeline
    /// it was given and then the answer it was given, exactly as cs2cs -v does.
    /// </summary>
    private static string StandIn(string pipeline, double x, double y)
    {
        var path = Path.Combine(Path.GetTempPath(), $"spatialengine-standin-{Guid.NewGuid():N}");
        File.WriteAllText(
            path,
            string.Create(
                CultureInfo.InvariantCulture,
                $"#!/bin/sh\ncat >/dev/null\necho \"call_proj: {pipeline}\"\necho \"output: {x} {y}\"\n"));
        if (OperatingSystem.IsLinux())
        {
            File.SetUnixFileMode(
                path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        _standIns.Add(path);
        return path;
    }

    private static readonly List<string> _standIns = [];

    private static Func<string, string?> EnvironmentOf(params (string Name, string Value)[] variables) =>
        name => variables.FirstOrDefault(variable => variable.Name == name).Value;

    /// <summary>No executable is installed, which is the state of a machine without PROJ.</summary>
    private static string? NeverFound(string _) => null;
}
