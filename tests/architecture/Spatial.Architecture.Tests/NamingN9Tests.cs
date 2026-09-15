namespace Spatial.Architecture.Tests;

/// <summary>
/// N9 naming cleanup (T-105): <c>Interop</c> hid two different roles. The
/// shared wire codecs are <c>Spatial.Esri.Codec</c> (Esri JSON shapes) and
/// <c>Spatial.Ingest.Codec</c> (upload format decoders); the server boundary
/// stays <c>Spatial.Adapter.GeoServices</c> and the consuming store stays
/// <c>Spatial.Stores.ArcGisRest</c>, clarified in their project headers. ADRs
/// keep the old names as history.
/// </summary>
public sealed class NamingN9Tests
{
    private static readonly Lazy<string> Root = new(RepositoryScanner.FindRepositoryRoot);

    /// <summary>
    /// No live project, source, doc or solution file still names the Interop
    /// bucket. The guards themselves are excluded from the scan.
    /// </summary>
    [Fact]
    public void No_live_file_names_the_interop_bucket()
    {
        var violations = LiveFiles("*.cs", "*.csproj", "*.slnx", "*.md", "*.props", "*.targets", "*.json", "*.sh", "*.py")
            .Where(path => File.ReadAllText(path).Contains("Spatial.Interop", StringComparison.Ordinal))
            .Select(path => Path.GetRelativePath(Root.Value, path))
            .ToList();

        Assert.Empty(violations);
    }

    /// <summary>
    /// The codec homes exist with matching namespaces.
    /// </summary>
    [Theory]
    [InlineData("src/Spatial.Esri.Codec", "Spatial.Esri.Codec")]
    [InlineData("src/Spatial.Ingest.Codec", "Spatial.Ingest.Codec")]
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
