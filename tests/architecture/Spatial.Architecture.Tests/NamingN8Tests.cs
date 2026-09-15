namespace Spatial.Architecture.Tests;

/// <summary>
/// N8 naming cleanup (T-104): the <c>Provider.*</c> bucket mixed keyed stores
/// with the map registry. Stores live in <c>Spatial.Stores.*</c>, the map
/// registry in <c>Spatial.Maps</c>. Test projects move with their
/// implementation. ADRs keep the old names as history.
/// </summary>
public sealed class NamingN8Tests
{
    private static readonly Lazy<string> Root = new(RepositoryScanner.FindRepositoryRoot);

    /// <summary>
    /// No live project, source, doc or solution file still names the old
    /// bucket. The guards themselves are excluded from the scan.
    /// </summary>
    [Fact]
    public void No_live_file_names_the_provider_bucket()
    {
        var violations = LiveFiles("*.cs", "*.csproj", "*.slnx", "*.md", "*.props", "*.targets", "*.json", "*.sh")
            .Where(path => File.ReadAllText(path).Contains("Spatial.Provider.", StringComparison.Ordinal))
            .Select(path => Path.GetRelativePath(Root.Value, path))
            .ToList();

        Assert.Empty(violations);
    }

    /// <summary>
    /// The new homes exist with matching namespaces.
    /// </summary>
    [Theory]
    [InlineData("src/Spatial.Stores.Demo", "Spatial.Stores.Demo")]
    [InlineData("src/Spatial.Stores.Memory", "Spatial.Stores.Memory")]
    [InlineData("src/Spatial.Stores.PostGIS", "Spatial.Stores.PostGIS")]
    [InlineData("src/Spatial.Stores.ArcGisRest", "Spatial.Stores.ArcGisRest")]
    [InlineData("src/Spatial.Maps", "Spatial.Maps")]
    public void New_homes_exist(string directory, string @namespace)
    {
        var dir = Path.Combine(Root.Value, directory);
        Assert.True(Directory.Exists(dir));

        var sources = Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .ToList();
        Assert.NotEmpty(sources);
        Assert.All(sources, path => Assert.Contains(
            $"namespace {@namespace}", File.ReadAllText(path), StringComparison.Ordinal));
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
