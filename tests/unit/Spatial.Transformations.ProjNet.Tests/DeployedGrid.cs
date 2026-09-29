namespace Spatial.Transformations.ProjNet.Tests;

/// <summary>
/// Deploys a datum-shift bundle into a temporary directory, so a test can put
/// a host in the state an operator would (ADR-0105 §licence: a grid is
/// deployed, never vendored) and take the directory away again afterwards.
/// <para>
/// The bundle bytes are built by <see cref="Ntv2Fixture"/> rather than
/// fetched, because every published grid is third-party data and a test that
/// depended on fetching one would be a test that depended on the network.
/// </para>
/// </summary>
internal sealed class DeployedGrid : IDisposable
{
    private readonly List<string> _directories = [];

    /// <summary>
    /// A directory holding an OSTN-shaped bundle whose shift is constant, so
    /// the expected answer is the arithmetic the test writes down rather than
    /// a remembered number. The block is given by the caller so a test can
    /// place London inside it or well outside it.
    /// </summary>
    public string Constant(
        double south = 49.5,
        double north = 52.5,
        double west = -9.0,
        double east = 1.0,
        float latitudeSeconds = 1.0f,
        float longitudeSeconds = 2.0f)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"spatialengine-grid-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        _directories.Add(directory);
        File.WriteAllBytes(
            Path.Combine(directory, EpsgGridShiftOperations.OsgbBundle),
            Ntv2Fixture.ToBytes(Ntv2Fixture.Constant(
                "OSTN15",
                south,
                north,
                west,
                east,
                0.25,
                0.25,
                new Ntv2Fixture.Shift(latitudeSeconds, longitudeSeconds, 0.05f, 0.05f))));
        return directory;
    }

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
}
