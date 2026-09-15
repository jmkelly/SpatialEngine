namespace Spatial.Architecture.Tests;

/// <summary>
/// N3 naming cleanup (T-099, ADR-0033): the job/worker model is gone, so the
/// last contract-side "job" (<c>IDemoJobs</c>) becomes <c>IDemoWork</c> and
/// worker-boundary comments become in-process language. Deliberately kept:
/// user-facing reject messages that truthfully state there is *no* job
/// model (offline/packaging/GPServer), and ADR history.
/// </summary>
public sealed class NamingN3Tests
{
    private static readonly Lazy<string> Root = new(RepositoryScanner.FindRepositoryRoot);

    /// <summary>
    /// The worker-era contract names are gone from live sources. The guards
    /// themselves are excluded from the scan.
    /// </summary>
    [Theory]
    [InlineData("IDemoJobs")]
    [InlineData("worker boundary")]
    [InlineData("worker reads")]
    [InlineData("sleep job")]
    [InlineData("long-running job")]
    [InlineData("spatial.demo.sleep@1")]
    [InlineData("spatial.crs.describe@1")]
    [InlineData("spatial.coordinate.transform@1")]
    [InlineData("spatial.geometry.*@1")]
    [InlineData("spatial.catalogue.list@1")]
    [InlineData("spatial.dataset.describe@1")]
    [InlineData("spatial.dataset.create@1")]
    [InlineData("spatial.feature.scan@1")]
    [InlineData("spatial.transaction.*@1")]
    public void No_live_source_uses_worker_job_names(string stale)
    {
        var violations = LiveFiles("*.cs", "*.md", "*.ts")
            .Where(path => File.ReadAllText(path).Contains(stale, StringComparison.Ordinal))
            .Select(path => Path.GetRelativePath(Root.Value, path))
            .ToList();

        Assert.Empty(violations);
    }

    /// <summary>
    /// The renamed demo-work interface exists with its cancellable sleep verb.
    /// </summary>
    [Fact]
    public void Demo_work_interface_exists()
    {
        var path = Path.Combine(Root.Value, "src", "Spatial.Contracts", "IDataStores.cs");
        var text = File.ReadAllText(path);

        Assert.Contains("IDemoWork", text, StringComparison.Ordinal);
        Assert.Contains("SleepAsync", text, StringComparison.Ordinal);
    }

    private static IEnumerable<string> LiveFiles(params string[] patterns)
    {
        return patterns.SelectMany(pattern => Directory.EnumerateFiles(Root.Value, pattern, SearchOption.AllDirectories))
            .Where(path => !IsExcluded(path));
    }

    private static bool IsExcluded(string path)
    {
        foreach (var guard in new[] { "NamingN1Tests.cs", "NamingN2Tests.cs", "NamingN3Tests.cs" })
        {
            if (string.Equals(Path.GetFileName(path), guard, StringComparison.Ordinal))
            {
                return true;
            }
        }

        var relative = Path.GetRelativePath(Root.Value, path);
        var segments = relative.Split(Path.DirectorySeparatorChar);
        return segments.Any(segment => segment is "bin" or "obj" or ".git" or "node_modules" or "StrykerOutput")
            || relative.StartsWith(
                "architecture" + Path.DirectorySeparatorChar + "decisions", StringComparison.Ordinal);
    }
}
