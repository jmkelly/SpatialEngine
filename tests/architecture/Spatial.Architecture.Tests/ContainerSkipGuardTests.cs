namespace Spatial.Architecture.Tests;

/// <summary>
/// The honest-degradation rule the containerised provider suite follows
/// (ADR-0028, ADR-0072 §1): <c>PostgisContainerFixture</c> records why the
/// container could not start, and every test that needs the container says so
/// through <c>Skip.If</c> rather than failing with a connection error — or,
/// worse, passing for the wrong reason on a host that happens to have the
/// image pulled.
///
/// <para>
/// The rule is a source rule, so the guard reads the suite's sources: each
/// <c>[SkippableFact]</c> in a class that takes the fixture must guard itself,
/// as this project's classes do. It is scoped to the PostGIS provider suite,
/// whose classes either take the fixture or are pure planning
/// (<c>PostgisIndexPlanTests</c>, which builds statements without a database
/// and uses plain <c>[Fact]</c>). The scope is where the guard is exact: it
/// reads a fact's own body, which is where this suite puts its guard, while
/// the host suite guards some facts through a helper call instead.
/// </para>
/// </summary>
public sealed class ContainerSkipGuardTests
{
    private static readonly string SuiteRoot = Path.Combine(
        RepositoryScanner.FindRepositoryRoot(), "tests", "integration", "Spatial.PostGIS.Tests");

    [Fact]
    public void Every_container_backed_fact_in_the_postgis_suite_skips_without_a_docker_daemon()
    {
        var unguarded = ContainerBackedClasses()
            .SelectMany(Assertions)
            .ToList();

        Assert.Empty(unguarded);
    }

    private static IEnumerable<string> ContainerBackedClasses() =>
        Directory
            .EnumerateFiles(SuiteRoot, "*.cs", SearchOption.TopDirectoryOnly)
            .Where(path => File.ReadAllText(path).Contains("IClassFixture<PostgisContainerFixture>", StringComparison.Ordinal));

    /// <summary>
    /// One message per <c>[SkippableFact]</c> whose body never reaches for the
    /// fixture's availability. A fact that guards itself passes; a fact that
    /// opens with a context built from an empty connection string does not.
    /// </summary>
    private static IEnumerable<string> Assertions(string path)
    {
        var lines = File.ReadAllLines(path);
        for (var index = 0; index < lines.Length; index++)
        {
            if (lines[index].Contains("[SkippableFact]", StringComparison.Ordinal)
                || lines[index].Contains("[SkippableTheory]", StringComparison.Ordinal))
            {
                var declaration = index + 1;
                while (declaration < lines.Length && !lines[declaration].Contains("public ", StringComparison.Ordinal))
                {
                    declaration++;
                }

                if (declaration >= lines.Length)
                {
                    continue;
                }

                var body = Body(lines, declaration);
                if (!body.Contains("Skip.If", StringComparison.Ordinal))
                {
                    yield return $"{Path.GetFileName(path)}({index + 1}) {lines[declaration].Trim()}";
                }
            }
        }
    }

    /// <summary>The declaration's body, brace-counted from its first brace.</summary>
    private static string Body(string[] lines, int declaration)
    {
        var body = new List<string>();
        var depth = 0;
        var opened = false;
        for (var line = declaration; line < lines.Length; line++)
        {
            body.Add(lines[line]);
            foreach (var character in lines[line])
            {
                if (character is '{')
                {
                    depth++;
                    opened = true;
                }
                else if (character is '}')
                {
                    depth--;
                }
            }

            if (opened && depth == 0)
            {
                break;
            }
        }

        return string.Join('\n', body);
    }
}
