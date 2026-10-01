using System.Globalization;
using Xunit.Abstractions;

namespace Spatial.Transformations.ProjNet.Tests;

/// <summary>
/// The control-point suite ADR-0105 §licence filed and ADR-0179 builds: the
/// engine's answer for a <em>published</em> datum shift bundle, compared
/// against PROJ reading the same bundle, with the residual pinned inside a
/// metre.
/// <para>
/// It runs where an operator has made the two things it compares: a bundle
/// deployed into <c>SPATIALENGINE_GRID_DIR</c>, and PROJ installed with its own
/// copy of that bundle in its data directory. Where either is absent it skips,
/// and it says which — that is the whole contract of this file, because a suite
/// that is silently absent is read as agreement.
/// </para>
/// <para>
/// It is a deployment exercise and not a CI gate (ADR-0179). No bundle is
/// fetched, embedded or vendored anywhere to make it run: the licence position
/// is the operator's, and a gate that depended on a third-party download would
/// decide it on their behalf.
/// </para>
/// </summary>
public sealed class PublishedGridControlPointTests
{
    private readonly ITestOutputHelper _output;

    public PublishedGridControlPointTests(ITestOutputHelper output) => _output = output;

    /// <summary>
    /// Whatever the state of the machine, the suite says what that state is.
    /// This never skips: it is the one case that is always runnable, and it is
    /// what makes a run in which everything else skipped legible rather than
    /// green.
    /// </summary>
    [Fact]
    public void The_suite_states_its_deployment_in_the_test_output_whatever_the_state()
    {
        var report = PublishedGridDeployment.FromEnvironment().Report();

        // Both channels, deliberately: the trx carries it for a machine reading
        // the results, and the console carries it for an operator who ran the
        // suite and is looking at a line of dots.
        _output.WriteLine(report);
        Console.WriteLine(report);

        Assert.All(EpsgGridShiftOperations.Bundles, operation =>
            Assert.Contains(operation.FileName, report, StringComparison.Ordinal));
        Assert.Contains("SPATIALENGINE_GRID_DIR", report, StringComparison.Ordinal);
        Assert.Contains(
            PublishedGridDeployment.PinnedResidualMetres.ToString("0.###", CultureInfo.InvariantCulture),
            report,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// Great Britain: the Ordnance Survey bundle the catalogue names, over the
    /// national grid a client actually asks for, at three points spread across
    /// the country so a grid read wrongly at one end cannot pass on the others.
    /// </summary>
    [SkippableFact]
    public void The_Ordnance_Survey_bundle_agrees_with_PROJ_in_Great_Britain()
    {
        var deployment = PublishedGridDeployment.FromEnvironment();

        foreach (var point in PublishedGridDeployment.OsgbControlPoints)
        {
            AssertAgrees(deployment.Measure(point, "EPSG:27700"), deployment);
        }
    }

    /// <summary>
    /// North America: the NAD 83 bundle the catalogue names, over UTM zone 10N.
    /// A different agency, a different licence and a different container from
    /// the one above, which is the point of having both rather than the one
    /// that is easiest.
    /// </summary>
    [SkippableFact]
    public void The_NAD83_bundle_agrees_with_PROJ_in_North_America()
    {
        var deployment = PublishedGridDeployment.FromEnvironment();

        foreach (var point in PublishedGridDeployment.Nad83ControlPoints)
        {
            AssertAgrees(deployment.Measure(point, "EPSG:26910"), deployment);
        }
    }

    /// <summary>
    /// One control point's residual against the pin, with a message that says
    /// which datum, which point and which bundle — because a residual of four
    /// metres means a different thing for a mis-deployed bundle than for a
    /// reader that got the format wrong, and the operator is the one who can
    /// tell those apart.
    /// </summary>
    private void AssertAgrees(Agreement agreement, PublishedGridDeployment deployment)
    {
        _output.WriteLine(agreement.Report());

        Assert.True(
            agreement.ResidualMetres <= PublishedGridDeployment.PinnedResidualMetres,
            $"{agreement.Report()} — beyond the {PublishedGridDeployment.PinnedResidualMetres} m a published "
            + $"bundle is held to. The bundle deployed under SPATIALENGINE_GRID_DIR is "
            + $"{deployment.BundleFor(DatumOf(agreement.Target))}, and PROJ read its own copy from its data "
            + "directory; check that the two are the same bundle before reading the residual as a defect.");
    }

    /// <summary>The graph token whose bundle serves a projected target, for the failure message.</summary>
    private static string DatumOf(string target) =>
        target == "EPSG:26910" ? "NAD83" : "OSGB36";
}
