namespace Spatial.Architecture.Tests;

/// <summary>
/// N4 naming cleanup (T-100): <c>capability</c> is reserved for protocol wire
/// vocabulary (Esri layer capabilities strings, OGC <c>GetCapabilities</c>,
/// the neutral <c>/capabilities</c> routes). SDK/implementation comments
/// call optional store interfaces <c>faces</c>. Deliberately kept: every wire
/// use, ADR history, and the superseded-ADR register in the distilled README.
/// </summary>
public sealed class NamingN4Tests
{
    private static readonly Lazy<string> Root = new(RepositoryScanner.FindRepositoryRoot);

    /// <summary>
    /// No live source describes an SDK interface with capability language.
    /// The guards themselves are excluded from the scan.
    /// </summary>
    [Theory]
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
    public void No_live_source_calls_a_store_face_a_capability(string stale)
    {
        var violations = LiveFiles("*.cs", "*.md")
            .Where(path => File.ReadAllText(path).Contains(stale, StringComparison.Ordinal))
            .Select(path => Path.GetRelativePath(Root.Value, path))
            .ToList();

        Assert.Empty(violations);
    }

    private static IEnumerable<string> LiveFiles(params string[] patterns)
    {
        return patterns.SelectMany(pattern => Directory.EnumerateFiles(Root.Value, pattern, SearchOption.AllDirectories))
            .Where(path => !IsExcluded(path));
    }

    private static bool IsExcluded(string path)
    {
        foreach (var guard in new[] { "NamingN1Tests.cs", "NamingN2Tests.cs", "NamingN3Tests.cs", "NamingN4Tests.cs" })
        {
            if (string.Equals(Path.GetFileName(path), guard, StringComparison.Ordinal))
            {
                return true;
            }
        }

        var relative = Path.GetRelativePath(Root.Value, path);
        var segments = relative.Split(Path.DirectorySeparatorChar);

        // CHANGELOG and ADRs are immutable history; the distilled README's
        // remaining capability words are the superseded-ADR register and
        // protocol-flag lines, both deliberately kept.

        // CHANGELOG and ADRs are immutable history; wire-protocol references stay.
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
