namespace Spatial.Architecture.Tests;

/// <summary>
/// The honest-degradation rule the containerised provider suites follow
/// (ADR-0010, ADR-0028, ADR-0072 §1): the container fixture records why the
/// container could not start, and every test that needs the container says so
/// through <c>Skip.If</c> rather than failing with a connection error — or,
/// worse, passing for the wrong reason on a host that happens to have the
/// image pulled.
///
/// <para>
/// The rule is a source rule, so the guard reads the suites' sources. It is
/// scoped to the two containerised provider suites, whose classes either take
/// the fixture or are pure planning (<c>PostgisIndexPlanTests</c>, which builds
/// statements without a database and uses plain <c>[Fact]</c>). The scope is
/// where the guard is exact: it reads a fact's own body, which is where these
/// suites put their guard, while the host suite guards some facts through a
/// helper call instead (<c>SkipUnlessLive</c>, <c>PublishAsync</c>) and is not
/// read here at all.
/// </para>
///
/// <para>
/// What it asks is whether the fact <em>needs the container</em>, not how many
/// guards a class happens to carry: a fact that reads the fixture has a
/// connection string to fail on and must consult the fixture's availability,
/// while a fact that never reaches for the fixture — the store-unavailable case,
/// which asserts a code from a store with no connection string at all — needs
/// no guard and is left alone. Reading availability rather than counting
/// guards is what makes that distinction expressible.
/// </para>
/// </summary>
public sealed class ContainerSkipGuardTests
{
    private static readonly string RepositoryRoot = RepositoryScanner.FindRepositoryRoot();

    /// <summary>
    /// The containerised suites, each with the fixture its classes take. A class
    /// that takes one of these fixtures is container-backed; the rule is
    /// otherwise the same for both.
    /// </summary>
    public static TheoryData<string, string> Suites => new()
    {
        { "Spatial.PostGIS.Tests", "IClassFixture<PostgisContainerFixture>" },
        { "Spatial.SqlServer.Tests", "IClassFixture<SqlServerContainerFixture>" },
    };

    [Theory]
    [MemberData(nameof(Suites))]
    public void Every_container_backed_fact_skips_without_a_docker_daemon(string suite, string fixture)
    {
        var unguarded = ContainerBackedClasses(suite, fixture)
            .SelectMany(Assertions)
            .ToList();

        Assert.Empty(unguarded);
    }

    private static IEnumerable<string> ContainerBackedClasses(string suite, string fixture) =>
        Directory
            .EnumerateFiles(Path.Combine(RepositoryRoot, "tests", "integration", suite), "*.cs", SearchOption.TopDirectoryOnly)
            .Where(path => File.ReadAllText(path).Contains(fixture, StringComparison.Ordinal));

    /// <summary>
    /// One message per <c>[SkippableFact]</c> that reads the fixture and never
    /// consults its availability. A fact that guards itself passes; a fact that
    /// opens with a context built from an empty connection string does not; a
    /// fact that never reads the fixture has no container to skip on and is
    /// not reported.
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
                if (body.Contains("_fixture.", StringComparison.Ordinal)
                    && !body.Contains("DockerAvailable", StringComparison.Ordinal))
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
