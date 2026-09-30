using Microsoft.AspNetCore.Mvc.Testing;

namespace Spatial.Host.Tests;

/// <summary>
/// The suite's base host factory: a <see cref="WebApplicationFactory{TEntryPoint}"/>
/// whose clients wait long enough to measure the host rather than the
/// machine (SpatialEngine-c5f).
///
/// </summary>
/// <remarks>
/// The host under test runs in process, so a request has no socket, no
/// listener and no accept queue in front of it — the only thing that can make
/// one slow is the work the host is doing and how much else the box is
/// doing. The framework's default client timeout is 100 seconds, and under a
/// parallel agent swarm that is a real budget: the suite went red with
/// <c>TaskCanceledException</c> ("The client aborted the request") raised while
/// copying the body of a response that was about to arrive, at 1 m 40 s to
/// 2 m 12 s, with no assertion failing anywhere. Every fixture in the suite
/// derives from this class (or from <see cref="PostgisHostFactory"/>, which
/// derives from it) so that one class of flake is gone, and
/// <c>TestHostClientTimeoutTests</c> fails if a new factory opts back out.
/// The timeout stays finite: a request that genuinely hangs is a defect, and
/// five minutes is long enough that it is one worth naming.
/// </remarks>
public class SpatialHostFactory : WebApplicationFactory<Program>
{
    /// <summary>How long a client waits for the in-process host.</summary>
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromMinutes(5);

    /// <inheritdoc />
    protected override void ConfigureClient(HttpClient client)
    {
        base.ConfigureClient(client);

        client.Timeout = RequestTimeout;
    }
}
