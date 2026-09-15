namespace Spatial.Architecture.Tests;

/// <summary>
/// N5 naming cleanup (T-101, ADR-0053): <c>MapService.Map</c> stutters, so
/// the enum becomes <c>MapServiceKind</c> with server names. The rename is
/// code-only: the JSON wire keys (<c>feature</c>, <c>map</c>, …) are pinned
/// and the declared-config names (<c>Feature</c>, <c>Map</c>, …) still parse,
/// so persisted <c>maps.json</c> files, snapshots and clients keep working.
/// </summary>
public sealed class NamingN5Tests
{
    private static readonly Lazy<string> Root = new(RepositoryScanner.FindRepositoryRoot);

    /// <summary>
    /// The old enum member accesses are gone from live sources. The guards
    /// themselves are excluded from the scan.
    /// </summary>
    [Theory]
    [InlineData("MapService.Feature")]
    [InlineData("MapService.Map")]
    [InlineData("MapService.Image")]
    [InlineData("MapService.Tiles")]
    [InlineData("MapService.Wms")]
    [InlineData("MapService.Wfs")]
    public void No_live_source_uses_the_old_enum_members(string stale)
    {
        var violations = LiveFiles("*.cs", "*.ts", "*.md")
            .Where(path => File.ReadAllText(path).Contains(stale, StringComparison.Ordinal))
            .Select(path => Path.GetRelativePath(Root.Value, path))
            .ToList();

        Assert.Empty(violations);
    }

    /// <summary>
    /// The renamed enum exists with server names, and the wire keys are
    /// pinned so the rename never leaks onto persisted files or the API.
    /// </summary>
    [Fact]
    public void Renamed_enum_pins_its_wire_keys()
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
    /// Declared-config parsing still accepts the legacy names, so existing
    /// configuration keeps working.
    /// </summary>
    [Fact]
    public void Declared_config_keeps_legacy_names()
    {
        var path = Path.Combine(Root.Value, "src", "Spatial.Maps", "MapRegistry.cs");
        var text = File.ReadAllText(path);

        Assert.Contains("FeatureServer", text, StringComparison.Ordinal);
        Assert.Contains("\"feature\"", text, StringComparison.Ordinal);
        Assert.Contains("\"map\"", text, StringComparison.Ordinal);
        Assert.Contains("\"image\"", text, StringComparison.Ordinal);
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
            || string.Equals(relative, "HANDOFF.md", StringComparison.Ordinal)
            || string.Equals(relative, Path.Combine("architecture", "distilled", "README.md"), StringComparison.Ordinal))
        {
            return true;
        }

        return segments.Any(segment => segment is "bin" or "obj" or ".git" or "node_modules" or "StrykerOutput")
            || relative.StartsWith(
                "architecture" + Path.DirectorySeparatorChar + "decisions", StringComparison.Ordinal);
    }
}
