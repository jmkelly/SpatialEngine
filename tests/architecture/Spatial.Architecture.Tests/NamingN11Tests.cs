namespace Spatial.Architecture.Tests;

/// <summary>
/// N11 naming cleanup (T-107, last): the Aspire orchestrator becomes
/// <c>Spatial.DevHost</c> — the product server stays <c>Spatial.Host</c> —
/// and the full banned-token sweep from N1–N10 is consolidated here as the
/// regression net: any reintroduction fails loudly. ADRs, CHANGELOG and the
/// distilled superseded-register keep history.
/// </summary>
public sealed class NamingN11Tests
{
    private static readonly Lazy<string> Root = new(RepositoryScanner.FindRepositoryRoot);

    /// <summary>
    /// No live file uses a retired name. The guards themselves are excluded
    /// from the scan.
    /// </summary>
    [Theory]
    [InlineData("Spatial.AppHost")]
    [InlineData("Spatial.PluginSdk")]
    [InlineData("Spatial.Provider.Publications")]
    [InlineData("Spatial.Provider.")]
    [InlineData("LegacyPublication")]
    [InlineData("IDemoJobs")]
    [InlineData("worker boundary")]
    [InlineData("MapService.Map")]
    [InlineData("MapService.Feature")]
    [InlineData("MapService.Image")]
    [InlineData("Spatial.Interop")]
    public void No_live_file_uses_a_retired_name(string stale)
    {
        var violations = LiveFiles("*.cs", "*.csproj", "*.slnx", "*.md", "*.props", "*.targets", "*.json", "*.sh", "*.py", "*.ts")
            .Where(path => File.ReadAllText(path).Contains(stale, StringComparison.Ordinal))
            .Select(path => Path.GetRelativePath(Root.Value, path))
            .ToList();

        Assert.Empty(violations);
    }

    /// <summary>
    /// The dev orchestrator home exists with its own namespace.
    /// </summary>
    [Fact]
    public void Dev_host_home_exists()
    {
        var dir = Path.Combine(Root.Value, "src", "Spatial.DevHost");
        Assert.True(Directory.Exists(dir));
        Assert.True(File.Exists(Path.Combine(dir, "Spatial.DevHost.csproj")));

        var program = File.ReadAllText(Path.Combine(dir, "Program.cs"));
        Assert.DoesNotContain("Spatial.DevHost", program, StringComparison.Ordinal);
    }

    private static IEnumerable<string> LiveFiles(params string[] patterns)
    {
        return patterns.SelectMany(pattern => Directory.EnumerateFiles(Root.Value, pattern, SearchOption.AllDirectories))
            .Where(path => !IsExcluded(path));
    }

    private static bool IsExcluded(string path)
    {
        var file = Path.GetFileName(path);
        if (file.StartsWith("NamingN", StringComparison.Ordinal) && file.EndsWith("Tests.cs", StringComparison.Ordinal))
        {
            return true;
        }

        var relative = Path.GetRelativePath(Root.Value, path);
        var segments = relative.Split(Path.DirectorySeparatorChar);

        if (string.Equals(relative, "CHANGELOG.md", StringComparison.Ordinal)
            || relative.EndsWith("-queue.md", StringComparison.Ordinal)
            || relative.EndsWith("-report.json", StringComparison.Ordinal)
            || string.Equals(relative, Path.Combine("architecture", "distilled", "README.md"), StringComparison.Ordinal))
        {
            return true;
        }

        return segments.Any(segment => segment is "bin" or "obj" or ".git" or "node_modules" or "StrykerOutput")
            || relative.StartsWith(
                "architecture" + Path.DirectorySeparatorChar + "decisions", StringComparison.Ordinal);
    }
}
