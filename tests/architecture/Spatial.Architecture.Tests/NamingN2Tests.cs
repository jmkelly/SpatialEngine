namespace Spatial.Architecture.Tests;

/// <summary>
/// N2 naming cleanup (T-098, ADR-0053): the migration path still spoke
/// "publication" after N1. Code identifiers are legacy-map names now; the
/// on-disk JSON shape (<c>publications</c>) is preserved for real legacy
/// files via <c>JsonPropertyName</c>. N1's <c>/api/publications</c> history
/// line and the <c>Spatial:Publications:Path</c> compat key are untouched.
/// </summary>
public sealed class NamingN2Tests
{
    private static readonly Lazy<string> Root = new(RepositoryScanner.FindRepositoryRoot);

    /// <summary>
    /// No live source file still declares the publication-era migration
    /// identifiers. The guard itself is excluded from the scan.
    /// </summary>
    [Theory]
    [InlineData("LegacyPublication")]
    [InlineData("legacy publication file")]
    [InlineData("publications file")]
    [InlineData("publications document")]
    [InlineData("publication record")]
    [InlineData("publication layer")]
    public void No_live_source_uses_publication_migration_names(string stale)
    {
        var violations = LiveFiles("*.cs")
            .Where(path => File.ReadAllText(path).Contains(stale, StringComparison.Ordinal))
            .Select(path => Path.GetRelativePath(Root.Value, path))
            .ToList();

        Assert.Empty(violations);
    }

    /// <summary>
    /// The legacy JSON wire key survives the rename: a real pre-ADR-0053
    /// file keyed <c>publications</c> must still deserialize.
    /// </summary>
    [Fact]
    public void Legacy_wire_key_is_preserved()
    {
        var path = Path.Combine(Root.Value, "src", "Spatial.Provider.Maps", "MapRegistry.cs");
        var text = File.ReadAllText(path);

        Assert.Contains("LegacyMapFile", text, StringComparison.Ordinal);
        Assert.Contains("\"publications\"", text, StringComparison.Ordinal);
    }

    private static IEnumerable<string> LiveFiles(params string[] patterns)
    {
        return patterns.SelectMany(pattern => Directory.EnumerateFiles(Root.Value, pattern, SearchOption.AllDirectories))
            .Where(path => !IsExcluded(path));
    }

    private static bool IsExcluded(string path)
    {
        foreach (var guard in new[] { "NamingN1Tests.cs", "NamingN2Tests.cs" })
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
