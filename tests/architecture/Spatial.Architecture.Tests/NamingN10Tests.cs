namespace Spatial.Architecture.Tests;

/// <summary>
/// N10 naming cleanup (T-106, ADR-0033): there are no plugins, so the
/// in-process service contracts move from <c>Spatial.PluginSdk</c> to
/// <c>Spatial.Contracts</c> (dir, assembly, namespaces, references, docs).
/// ADRs keep the old name as history; the architecture guard's project lists
/// move with the rename.
/// </summary>
public sealed class NamingN10Tests
{
    private static readonly Lazy<string> Root = new(RepositoryScanner.FindRepositoryRoot);

    /// <summary>
    /// No live project, source, doc or solution file still names the old
    /// assembly. The guards themselves are excluded from the scan.
    /// </summary>
    [Fact]
    public void No_live_file_names_the_old_contracts_assembly()
    {
        var violations = LiveFiles("*.cs", "*.csproj", "*.slnx", "*.md", "*.props", "*.targets", "*.json", "*.sh", "*.py")
            .Where(path => File.ReadAllText(path).Contains("Spatial.PluginSdk", StringComparison.Ordinal)
                || File.ReadAllText(path).Contains("PluginSdk_", StringComparison.Ordinal))
            .Select(path => Path.GetRelativePath(Root.Value, path))
            .ToList();

        Assert.Empty(violations);
    }

    /// <summary>
    /// The new home exists and the SDK agent doc moved with it.
    /// </summary>
    [Fact]
    public void New_home_exists()
    {
        var dir = Path.Combine(Root.Value, "src", "Spatial.Contracts");
        Assert.True(Directory.Exists(dir));
        Assert.True(File.Exists(Path.Combine(dir, "Spatial.Contracts.csproj")));
        Assert.True(File.Exists(Path.Combine(dir, "AGENTS.md")));

        var doc = File.ReadAllText(Path.Combine(dir, "AGENTS.md"));
        Assert.DoesNotContain("Spatial.PluginSdk", doc, StringComparison.Ordinal);
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
