using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Spatial.Host.Tests;

/// <summary>
/// One host per test class, and the per-test dataset identity that makes
/// sharing it honest (ADR-0160).
/// </summary>
/// <remarks>
/// <para>A warm <see cref="WebApplicationFactory{TEntryPoint}"/> boot costs
/// 0.55 s (ADR-0154) and this suite constructs one per test at 184 sites, so
/// the per-test host is a hundred seconds of a summed run spent rebuilding a
/// host the class could have shared. The obstacle is not the host, it is the
/// identity: a class fixture is a rename plus an audit of every test that
/// asserts on a catalog count or a republish (ADR-0155).</para>
///
/// <para>This class is the other half of that trade, and it is deliberately
/// narrow. The host is per class; the <b>map name is per test</b>, handed out
/// by <see cref="NextMapName"/> and never reused, so a test publishes under a
/// name no other test in the class has published under and a test that kept a
/// shared name is red rather than quietly sharing state. The maps file is the
/// class's own temporary directory and is deleted with it, so nothing a test
/// published is visible to another class.</para>
///
/// <para>What this does <em>not</em> give a test is a private store: a
/// singleton store, cache or ingestion in the host is still shared by the
/// class, and a test that mutates one needs per-test identity of its own kind
/// (a keyed dataset, a uniquely named service) or a class of its own. That is
/// the audit ADR-0160 records, and it is why the register in
/// <see cref="SharedHostPolicy"/> is a list and not a sweep.</para>
/// </remarks>
public abstract class ClassHostFixture : IAsyncLifetime
{
    private readonly HashSet<string> _issued = new(StringComparer.Ordinal);
    private readonly string _directory;
    private int _issuedCount;
    private FixtureFactory? _factory;
    private HttpClient? _client;

    /// <summary>Creates the class's temporary directory for the maps file.</summary>
    protected ClassHostFixture(string prefix) =>
        _directory = Directory.CreateTempSubdirectory(prefix + "-").FullName;

    /// <summary>
    /// The host's own configuration: the settings every test in the class
    /// shares, and therefore the reason a test needing a different setting
    /// cannot join this class.
    /// </summary>
    protected abstract void ConfigureHost(IWebHostBuilder builder);

    /// <summary>The class's one host, booted on first use.</summary>
    public HttpClient Client => _client ??= Factory.CreateClient();

    /// <summary>The maps file this class's host persists to.</summary>
    protected string MapsPath => Path.Combine(_directory, "maps.json");

    /// <summary>Every map name this class has handed out, in issue order.</summary>
    public IReadOnlyCollection<string> IssuedNames => _issued;

    /// <summary>
    /// A map name no other test in this class has been given: the dataset
    /// identity a per-test host used to provide for free. The name is a legal
    /// map name — a flat identifier, no folders — because the registry refuses
    /// anything else.
    /// </summary>
    public string NextMapName(string label)
    {
        var name = $"{label}_{_issuedCount++ + 1:D2}";
        if (!_issued.Add(name))
        {
            throw new InvalidOperationException($"map name '{name}' was already issued in this class.");
        }

        return name;
    }

    /// <inheritdoc />
    public Task InitializeAsync() => Task.CompletedTask;

    /// <inheritdoc />
    public Task DisposeAsync()
    {
        _client?.Dispose();
        _factory?.Dispose();
        _factory = null;
        _client = null;

        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
            // The class never booted a host, so it never wrote the file.
        }

        return Task.CompletedTask;
    }

    private SpatialHostFactory Factory => _factory ??= new FixtureFactory(this);

    private sealed class FixtureFactory(ClassHostFixture owner) : SpatialHostFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder) => owner.ConfigureHost(builder);
    }
}
