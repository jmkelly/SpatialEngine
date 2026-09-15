namespace Spatial.Architecture.Tests;

/// <summary>
/// Naming regression net (T-108, consolidates the N1–N11 sweeps): retired
/// vocabulary from the worker-plugin era (ADR-0033), the publication era
/// (ADR-0041/ADR-0053) and the pre-rename project buckets must not return.
/// <c>capability</c> stays reserved for protocol wire vocabulary (Esri layer
/// strings, OGC <c>GetCapabilities</c>, neutral <c>/capabilities</c> routes);
/// store interfaces are <c>faces</c>. ADRs, CHANGELOG and the distilled
/// superseded-register keep history.
/// </summary>
public sealed class NamingTests
{
    private static readonly Lazy<string> Root = new(RepositoryScanner.FindRepositoryRoot);

    /// <summary>
    /// No live file uses a retired name. This guard itself is excluded from
    /// the scan.
    /// </summary>
    [Theory]
    [InlineData("Spatial.PluginSdk")]
    [InlineData("Spatial.Provider.")]
    [InlineData("Spatial.Interop")]
    [InlineData("Spatial.AppHost")]
    [InlineData("LegacyPublication")]
    [InlineData("IDemoJobs")]
    [InlineData("worker boundary")]
    [InlineData("worker reads")]
    [InlineData("sleep job")]
    [InlineData("long-running job")]
    [InlineData("MapService.Feature")]
    [InlineData("MapService.Map")]
    [InlineData("MapService.Image")]
    [InlineData("MapService.Tiles")]
    [InlineData("MapService.Wms")]
    [InlineData("MapService.Wfs")]
    [InlineData("read-by-identity capability")]
    [InlineData("create-and-load capability")]
    [InlineData("additive capability")]
    [InlineData("additive capabilities")]
    [InlineData("capability faces")]
    [InlineData("capability interfaces")]
    [InlineData("store capabilities")]
    [InlineData("keyed capabilities")]
    [InlineData("editing capability")]
    [InlineData("ingest capability")]
    [InlineData("attachment capability")]
    [InlineData("blob capability")]
    [InlineData("transaction capability")]
    [InlineData("capability contract")]
    [InlineData("capability errors")]
    [InlineData("granular capability")]
    [InlineData("plugin capability")]
    [InlineData("scheme capability discovery")]
    [InlineData("capability description")]
    [InlineData("read capabilities")]
    [InlineData("its capabilities")]
    [InlineData("to enable publications and ingest")]
    [InlineData("the registry's publications plus")]
    [InlineData("Declared services are also publications")]
    [InlineData("legacy publication file")]
    [InlineData("publications file")]
    [InlineData("publications document")]
    [InlineData("publication record")]
    [InlineData("publication layer")]
    [InlineData("each `PublicationLayer.Style`")]
    [InlineData("POST /api/publications/{name}/render")]
    [InlineData("remain as deprecated aliases")]
    [InlineData("spatial.demo.sleep@1")]
    [InlineData("spatial.crs.describe@1")]
    [InlineData("spatial.coordinate.transform@1")]
    [InlineData("spatial.geometry.*@1")]
    [InlineData("spatial.catalogue.list@1")]
    [InlineData("spatial.dataset.describe@1")]
    [InlineData("spatial.dataset.create@1")]
    [InlineData("spatial.feature.scan@1")]
    [InlineData("spatial.transaction.*@1")]
    public void No_live_file_uses_a_retired_name(string stale)
    {
        var violations = LiveFiles("*.cs", "*.csproj", "*.slnx", "*.md", "*.props", "*.targets", "*.json", "*.sh", "*.py", "*.ts")
            .Where(path => File.ReadAllText(path).Contains(stale, StringComparison.Ordinal))
            .Select(path => Path.GetRelativePath(Root.Value, path))
            .ToList();

        Assert.Empty(violations);
    }

    /// <summary>
    /// The service-kind rename is code-only: member names are server names
    /// while the JSON wire keys stay pinned, so persisted files and clients
    /// keep working.
    /// </summary>
    [Fact]
    public void Service_kind_pins_its_wire_keys()
    {
        var path = Path.Combine(Root.Value, "src", "Spatial.Contracts", "Providers", "IMapRegistry.cs");
        var text = File.ReadAllText(path);

        Assert.Contains("enum MapServiceKind", text, StringComparison.Ordinal);
        Assert.Contains("FeatureServer", text, StringComparison.Ordinal);
        Assert.Contains("MapServer", text, StringComparison.Ordinal);
        foreach (var key in new[] { "\"feature\"", "\"map\"", "\"tiles\"", "\"wms\"", "\"wfs\"", "\"image\"" })
        {
            Assert.Contains(key, text, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// Declared-config parsing still accepts the legacy names, and the
    /// legacy migration file keeps its wire key, so existing configuration
    /// and files keep working.
    /// </summary>
    [Fact]
    public void Legacy_names_and_keys_are_preserved()
    {
        var registry = File.ReadAllText(Path.Combine(Root.Value, "src", "Spatial.Maps", "MapRegistry.cs"));
        Assert.Contains("\"feature\"", registry, StringComparison.Ordinal);
        Assert.Contains("\"map\"", registry, StringComparison.Ordinal);
        Assert.Contains("\"publications\"", registry, StringComparison.Ordinal);
    }

    /// <summary>
    /// The ladder and spelling rule are written down where implementors look.
    /// </summary>
    [Fact]
    public void Ladder_spelling_and_vocabulary_are_documented()
    {
        var contracts = File.ReadAllText(Path.Combine(Root.Value, "architecture", "distilled", "contracts.md"));
        Assert.Contains("`Catalogue` = datasets in one store", contracts, StringComparison.Ordinal);
        Assert.Contains("stores and maps across the engine", contracts, StringComparison.Ordinal);

        var rendering = File.ReadAllText(Path.Combine(Root.Value, "architecture", "distilled", "rendering.md"));
        Assert.Contains("TileServing", rendering, StringComparison.Ordinal);
    }

    /// <summary>
    /// The distilled docs keep exactly one publications history line: the
    /// removal note for the pre-ADR-0053 aliases.
    /// </summary>
    [Fact]
    public void Distilled_keeps_a_single_publications_history_line()
    {
        var hits = LiveFiles("*.md")
            .Where(path => Path.GetRelativePath(Root.Value, path).StartsWith(
                Path.Combine("architecture", "distilled"), StringComparison.Ordinal))
            .SelectMany(path => File.ReadAllLines(path)
                .Select((line, index) => (Path: path, Line: line, Number: index + 1))
                .Where(entry => entry.Line.Contains("/api/publications", StringComparison.Ordinal))
                .Select(entry => $"{Path.GetRelativePath(Root.Value, entry.Path)}:{entry.Number}: {entry.Line.Trim()}"))
            .ToList();

        Assert.Single(hits);
        Assert.Contains("were removed in 0.2.0", hits[0], StringComparison.Ordinal);
    }

    private static IEnumerable<string> LiveFiles(params string[] patterns)
    {
        return patterns.SelectMany(pattern => Directory.EnumerateFiles(Root.Value, pattern, SearchOption.AllDirectories))
            .Where(path => !IsExcluded(path));
    }

    private static bool IsExcluded(string path)
    {
        if (string.Equals(Path.GetFileName(path), "NamingTests.cs", StringComparison.Ordinal))
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
