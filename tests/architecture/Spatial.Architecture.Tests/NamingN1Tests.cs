namespace Spatial.Architecture.Tests;

/// <summary>
/// N1 naming cleanup (T-097, ADR-0053): publications were replaced by maps.
/// The empty Publications provider projects are deleted, no live reference
/// to them remains, and the non-migration comments and docs
/// that still presented publications as current now say maps. Migration-path
/// code (<c>LegacyPath</c> readers, <c>LegacyPublication*</c> types) belongs
/// to N2 and is excluded from every assertion here.
/// </summary>
public sealed class NamingN1Tests
{
    private static readonly Lazy<string> Root = new(RepositoryScanner.FindRepositoryRoot);

    /// <summary>
    /// The empty Publications projects (only bin/obj, never in the solution)
    /// are deleted from src and tests.
    /// </summary>
    [Fact]
    public void Empty_Publications_project_directories_are_deleted()
    {
        var stale = new[]
        {
            Path.Combine("src", "Spatial.Provider.Publications"),
            Path.Combine("tests", "unit", "Spatial.Provider.Publications.Tests"),
        }
        .Where(relative => Directory.Exists(Path.Combine(Root.Value, relative)))
        .ToList();

        Assert.Empty(stale);
    }

    /// <summary>
    /// No live file still names the deleted project. ADRs under
    /// architecture/decisions are immutable history and excluded; generated
    /// output (bin/obj, StrykerOutput) is excluded too.
    /// </summary>
    [Fact]
    public void No_live_reference_to_Spatial_Provider_Publications()
    {
        var violations = LiveFiles("*.cs", "*.csproj", "*.slnx", "*.props", "*.targets", "*.md", "*.json", "*.sh")
            .Where(path => File.ReadAllText(path).Contains("Spatial.Provider.Publications", StringComparison.Ordinal))
            .Select(path => Path.GetRelativePath(Root.Value, path))
            .ToList();

        Assert.Empty(violations);
    }

    /// <summary>
    /// The non-migration comments that said "publications" now say maps.
    /// Each stale string is pinned so a reintroduction fails loudly.
    /// </summary>
    [Theory]
    [InlineData("to enable publications and ingest")]
    [InlineData("the registry's publications plus")]
    [InlineData("Declared services are also publications")]
    [InlineData("POST /api/publications/{name}/render")]
    [InlineData("each `PublicationLayer.Style`")]
    [InlineData("remain as deprecated aliases")]
    public void Non_migration_publication_comments_say_maps(string stale)
    {
        var violations = LiveFiles("*.cs", "*.md")
            .Where(path => File.ReadAllText(path).Contains(stale, StringComparison.Ordinal))
            .Select(path => Path.GetRelativePath(Root.Value, path))
            .ToList();

        Assert.Empty(violations);
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
        // The guard pins the stale strings as literals, so it never scans itself.
        if (string.Equals(Path.GetFileName(path), "NamingN1Tests.cs", StringComparison.Ordinal))
        {
            return true;
        }

        var relative = Path.GetRelativePath(Root.Value, path);
        var segments = relative.Split(Path.DirectorySeparatorChar);
        // CHANGELOG.md records released versions; its past-tense route notes are history, like the ADRs.
        if (string.Equals(relative, "CHANGELOG.md", StringComparison.Ordinal))
        {
            return true;
        }

        return segments.Any(segment => segment is "bin" or "obj" or ".git" or "node_modules" or "StrykerOutput")
            || relative.StartsWith(
                "architecture" + Path.DirectorySeparatorChar + "decisions", StringComparison.Ordinal);
    }
}
