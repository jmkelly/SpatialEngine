using System.Reflection;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Spatial.Host.Tests;

/// <summary>
/// The host integration suite drives the real host in process, so a request
/// has no network, no listener and no queue in front of it: a client that
/// gives up at the framework's 100-second default is not measuring the host,
/// it is measuring how loaded the machine is. Under a parallel agent swarm
/// that turned into a red suite that was not a defect anywhere — a
/// <c>TaskCanceledException</c> ("The client aborted the request") raised
/// while copying the response body of a request that was about to succeed,
/// with no assertion failing (SpatialEngine-c5f).
///
/// So the suite gives every host factory's client a timeout of its own, set
/// in one place, and these tests are what stop a new factory from quietly
/// opting back out of it by deriving straight from
/// <see cref="WebApplicationFactory{TEntryPoint}"/>.
/// </summary>
public class TestHostClientTimeoutTests
{
    /// <summary>
    /// <see cref="WebApplicationFactoryClientOptions"/>' own default: the
    /// timeout a factory that configures nothing gets.
    /// </summary>
    private static readonly TimeSpan FrameworkDefault = TimeSpan.FromSeconds(100);

    [Fact]
    public void Every_host_factory_in_the_suite_shares_one_client_timeout()
    {
        var factories = typeof(TestHostClientTimeoutTests).Assembly
            .GetTypes()
            .Where(type => !type.IsAbstract && typeof(WebApplicationFactory<Program>).IsAssignableFrom(type))
            .ToList();

        Assert.NotEmpty(factories);

        var unconfigured = factories
            .Where(type => !OverridesTheClientTimeout(type))
            .Select(type => type.Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();

        Assert.True(
            unconfigured.Count == 0,
            "these factories hand out clients on the framework's 100-second default, which is how the "
            + "suite goes red under load instead of green: "
            + string.Join(", ", unconfigured));
    }

    [Fact]
    public void A_client_from_the_suite_runs_past_the_framework_default()
    {
        using var factory = new PostgisHostFactory();
        using var client = factory.CreateClient();

        Assert.True(
            client.Timeout > FrameworkDefault,
            $"a client that times out at {client.Timeout} is timing out on machine load, not on a defect.");
        Assert.InRange(client.Timeout, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(15));
    }

    [Fact]
    public void Deriving_a_factory_with_a_web_host_builder_keeps_the_timeout()
    {
        using var factory = new PostgisHostFactory();
        using var direct = factory.CreateClient();

        // WithWebHostBuilder returns a delegating factory around the original
        // rather than another of the same type, so the timeout has to survive
        // the hop or it is gone from every fixture that tweaks a setting.
        using var derived = factory.WithWebHostBuilder(builder => { }).CreateClient();

        Assert.Equal(direct.Timeout, derived.Timeout);
        Assert.True(derived.Timeout > FrameworkDefault);
    }

    /// <summary>
    /// Whether <paramref name="type"/> gets its client timeout from somewhere
    /// in this suite's own hierarchy, rather than straight from the framework.
    /// </summary>
    private static bool OverridesTheClientTimeout(Type type)
    {
        const BindingFlags Declared =
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

        for (var current = type; current is not null; current = current.BaseType)
        {
            if (current == typeof(WebApplicationFactory<Program>))
            {
                return false;
            }

            if (current.GetMethod("ConfigureClient", Declared) is not null)
            {
                return true;
            }
        }

        return false;
    }
}
