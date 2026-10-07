namespace Spatial.SqlServer.Tests;

/// <summary>
/// The structural half of the container-skip invariant (ADR-0193): the fixture
/// refuses a connection string when the container is not available, so a fact
/// that forgets <c>Skip.If</c> fails with a message naming the guard it forgot
/// rather than connecting to an empty string — or, worse, passing having
/// tested nothing. The source-reading backstop in
/// <c>Spatial.Architecture.Tests</c> stays, but it no longer carries the
/// invariant alone.
///
/// <para>
/// These facts need no Docker: an uninitialized <see cref="SqlServerContainerFixture"/>
/// is exactly the degraded shape every unguarded fact would meet, so the
/// refusal is asserted without a container.
/// </para>
/// </summary>
public sealed class SqlServerFixtureRefusalTests
{
    [Fact]
    public void The_database_fixture_refuses_a_connection_string_when_the_container_never_became_available()
    {
        var container = new SqlServerContainerFixture();
        var fixture = new SqlServerDatabaseFixture(container);

        Assert.False(fixture.DockerAvailable);

        var exception = Assert.Throws<InvalidOperationException>(() => _ = fixture.ConnectionString);

        Assert.Contains("DockerAvailable", exception.Message);
        Assert.Contains("Skip.If", exception.Message);
    }
}
