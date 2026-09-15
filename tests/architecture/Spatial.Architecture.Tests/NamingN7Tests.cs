namespace Spatial.Architecture.Tests;

/// <summary>
/// N7 naming cleanup (T-103): tile scheme vs tile serving. The tiling scheme
/// lives in <c>Spatial.Tiling.*</c>; the host's cache/render orchestration
/// lives in <c>Spatial.Host.TileServing</c> (not <c>Tiling</c>). The raster
/// vocabulary — Raster (contract types), Skia (vector render), Vips/imagery
/// (raster engine) — is written down in the distilled rendering doc.
/// </summary>
public sealed class NamingN7Tests
{
    private static readonly Lazy<string> Root = new(RepositoryScanner.FindRepositoryRoot);

    /// <summary>
    /// The old host tiling namespace is gone. The guards themselves are
    /// excluded from the scan.
    /// </summary>
    [Fact]
    public void No_live_source_uses_the_old_tiling_namespace()
    {
        var violations = LiveFiles("*.cs", "*.csproj", "*.md")
            .Where(path => File.ReadAllText(path).Contains("Host.Tiling", StringComparison.Ordinal)
                || File.ReadAllText(path).Contains("Host/Tiling", StringComparison.Ordinal))
            .Select(path => Path.GetRelativePath(Root.Value, path))
            .ToList();

        Assert.Empty(violations);
    }

    /// <summary>
    /// The serving namespace exists and the vocabulary box is documented.
    /// </summary>
    [Fact]
    public void Serving_namespace_and_vocabulary_exist()
    {
        Assert.True(Directory.Exists(Path.Combine(Root.Value, "src", "Spatial.Host", "TileServing")));

        var rendering = Path.Combine(Root.Value, "architecture", "distilled", "rendering.md");
        var text = File.ReadAllText(rendering);
        Assert.Contains("TileServing", text, StringComparison.Ordinal);
        Assert.Contains("scheme", text, StringComparison.Ordinal);
    }

    private static IEnumerable<string> LiveFiles(params string[] patterns)
    {
        return patterns.SelectMany(pattern => Directory.EnumerateFiles(Root.Value, pattern, SearchOption.AllDirectories))
            .Where(path => !IsExcluded(path));
    }

    private static bool IsExcluded(string path)
    {
        foreach (var guard in new[]
                 {
                     "NamingN1Tests.cs", "NamingN2Tests.cs", "NamingN3Tests.cs",
                     "NamingN4Tests.cs", "NamingN5Tests.cs", "NamingN6Tests.cs",
                     "NamingN7Tests.cs",
                 })
        {
            if (string.Equals(Path.GetFileName(path), guard, StringComparison.Ordinal))
            {
                return true;
            }
        }

        var relative = Path.GetRelativePath(Root.Value, path);
        var segments = relative.Split(Path.DirectorySeparatorChar);

        if (string.Equals(relative, "CHANGELOG.md", StringComparison.Ordinal)
            || string.Equals(relative, "HANDOFF.md", StringComparison.Ordinal)
            || relative.EndsWith("-queue.md", StringComparison.Ordinal)
            || string.Equals(relative, Path.Combine("architecture", "distilled", "README.md"), StringComparison.Ordinal))
        {
            return true;
        }

        return segments.Any(segment => segment is "bin" or "obj" or ".git" or "node_modules" or "StrykerOutput")
            || relative.StartsWith(
                "architecture" + Path.DirectorySeparatorChar + "decisions", StringComparison.Ordinal);
    }
}
