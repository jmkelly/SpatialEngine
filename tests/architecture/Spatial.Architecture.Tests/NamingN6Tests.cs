namespace Spatial.Architecture.Tests;

/// <summary>
/// N6 naming cleanup (T-102): two spellings, one rule. Engine dataset
/// listings are <c>Catalogue</c> (British: <c>IDataCatalogue</c>,
/// <c>IRasterCatalogue</c>, <c>/api/catalogue</c>); Esri-protocol catalog
/// concepts keep Esri's <c>Catalog</c> (<c>GeoServicesCatalog</c>, raster
/// catalog items). The ladder — Catalogue (datasets in one store) vs
/// Registry (stores/maps across the engine) — is documented in the
/// distilled contract catalog.
/// </summary>
public sealed class NamingN6Tests
{
    private static readonly Lazy<string> Root = new(RepositoryScanner.FindRepositoryRoot);

    /// <summary>
    /// The demo dataset listing uses the listing spelling. The guards
    /// themselves are excluded from the scan.
    /// </summary>
    [Fact]
    public void No_live_source_uses_the_american_listing_name()
    {
        var violations = LiveFiles("*.cs", "*.md")
            .Where(path => System.Text.RegularExpressions.Regex.IsMatch(
                File.ReadAllText(path), @"DemoDatasetCatalog(?!ue)"))
            .Select(path => Path.GetRelativePath(Root.Value, path))
            .ToList();

        Assert.Empty(violations);
    }

    /// <summary>
    /// Both spellings exist deliberately on their own side of the rule.
    /// </summary>
    [Fact]
    public void Both_spellings_exist_on_their_own_side()
    {
        var sdk = Path.Combine(Root.Value, "src", "Spatial.PluginSdk", "IDataStores.cs");
        Assert.Contains("IDataCatalogue", File.ReadAllText(sdk), StringComparison.Ordinal);

        var catalog = Path.Combine(Root.Value, "src", "Spatial.Adapter.GeoServices", "GeoServicesCatalog.cs");
        Assert.True(File.Exists(catalog));
    }

    /// <summary>
    /// The ladder and the spelling rule are written down where implementors
    /// look: the distilled contract catalog.
    /// </summary>
    [Fact]
    public void Ladder_and_spelling_rule_are_documented()
    {
        var path = Path.Combine(Root.Value, "architecture", "distilled", "contracts.md");
        var text = File.ReadAllText(path);

        Assert.Contains("`Catalogue` = datasets in one store", text, StringComparison.Ordinal);
        Assert.Contains("stores and maps across the engine", text, StringComparison.Ordinal);
        Assert.Contains("Esri", text, StringComparison.Ordinal);
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
