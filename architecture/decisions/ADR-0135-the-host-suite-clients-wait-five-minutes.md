---
status: accepted
date: 2026-09-30
deciders: maintainer + agent
summary: The host integration suite's clients wait **five minutes, not the framework's 100 seconds**: the host runs in process, so a client that gives up at 100 s is timing out on machine load, not on a defect — that is what made `Spatial.Host.Tests` red (10/779, then 2/786) with a `TaskCanceledException` on a response copy and no assertion failing. `SpatialHostFactory` sets `Timeout` in `ConfigureClient` (the hook `WithWebHostBuilder` forwards, and `ClientOptions` cannot be set from a derived factory in .NET 10), every fixture derives from it or from `PostgisHostFactory`, the timeout stays finite so a hung request is still a signal, and `TestHostClientTimeoutTests` fails naming any fixture that opts back out. A real-network client (`EsriLiveRefreshTests`, 20 s) is deliberately not covered.
---

# ADR-0135: The host integration suite's clients wait five minutes, not one hundred seconds

## Context

`tests/integration/Spatial.Host.Tests` boots the real host in process through
`WebApplicationFactory<Program>`. Every fixture in it therefore talks to the
host over `TestServer`: no socket, no listener, no accept queue, no DNS, no
TLS. The framework's default client timeout for a factory that configures
nothing is 100 seconds, and that number is a wall-clock budget on *the box*,
not on the host.

On a swarm host running a dozen or more agents at once, it is a budget the
box can blow. `SpatialEngine-c5f` recorded it twice: 10 of 779 tests on
2026-09-29, and 2 of 786 on 2026-09-30. Every failure had the same shape —
`TaskCanceledException`, "The client aborted the request", raised while
copying the HTTP response body, at 1 m 40 s, 1 m 56 s, 2 m 02 s, 2 m 12 s,
**with no assertion failing anywhere**. They were requests that were about to
succeed. The suite is 22 minutes of a 25-minute gate on its own, so each one
also added two minutes of wall clock to a run that could not merge the bead
behind it (SpatialEngine-u2x.44 was merged, pushed, and stuck open behind this).

The bead's first hypothesis — a PostGIS container per test rather than per
collection — was already answered by ADR-0072: `PostgisTestDatabase` starts
one container per test process, and the PostGIS suite ran 110 green in the
same lane. So the contention was not the container. It was the timeout being
sized for a network client in a suite that has no network.

## Decision

**The suite's host clients get one timeout, set in one place: five minutes.**

`SpatialHostFactory` is the base factory for the suite. It derives from
`WebApplicationFactory<Program>` and overrides `ConfigureClient`, the hook
`CreateDefaultClient` calls for every client it hands out, setting
`Timeout = SpatialHostFactory.RequestTimeout` (5 minutes). Every fixture in
the suite derives from `SpatialHostFactory` or from `PostgisHostFactory`,
which now derives from it, so the timeout is a property of the suite rather
than a decision each of ~40 factories has to remember.

Two details are load-bearing:

- **`ConfigureClient`, not `ClientOptions`.** In .NET 10
  `WebApplicationFactory.ClientOptions` has a private setter, and
  `WithWebHostBuilder` returns a *delegating* factory that forwards
  `ConfigureClient` to the original. Overriding the hook therefore survives
  `WithWebHostBuilder`, which a per-instance `ClientOptions` mutation would
  not. `TestHostClientTimeoutTests` asserts that survival, because three
  fixtures depend on it.
- **The timeout stays finite.** A request that genuinely hangs is a defect,
  and five minutes is long enough that a hung one is still worth naming. The
  test asserts the value is inside a 1–15 minute band, so somebody raising it
  to `Timeout.InfiniteTimeSpan` — which turns a red test into a suite that
  never finishes — has to say so in a failing test.

**The suite polices its own fixtures.**
`TestHostClientTimeoutTests.Every_host_factory_in_the_suite_shares_one_client_timeout`
walks the assembly's concrete `WebApplicationFactory<Program>` subclasses and
fails, naming them, on any that gets its timeout from the framework. A new
fixture written as `: WebApplicationFactory<Program>` is therefore a test
failure rather than a latent reintroduction of the flake.

**A real-network client is not covered by this.** `EsriLiveRefreshTests`
talks to a live ArcGIS service with its own 20-second timeout, and stays at
it: that one is measuring a remote server over TCP, where a slow answer is
information.

## Consequences

- The load flake is gone as a class. A test that fails on a `TaskCanceledException`
  raised while copying a response body, with no assertion failing, is now
  naming machine load rather than a host defect, and a request that needs
  more than five minutes under this load is a real signal.
- Genuinely hung requests surface five minutes in rather than one minute
  forty. That is the cost, and it is paid only when something is actually
  wrong.
- The change is wide in files and narrow in behaviour: ~60 lines across the
  suite's fixtures, all of it a base class and a type name. No product code,
  no contract, no wire format and no assertion changed.
- The 100-second default is still right for a client that crosses a network,
  and the suite keeps one client that does (`EsriLiveRefreshTests`).

## References

- ADR-0072 (the suite's own PostGIS container, per process, not per test),
  ADR-0028, ADR-0033, ADR-0118 and ADR-0134 (the lanes this flake was
  costing), `tests/integration/Spatial.Host.Tests/SpatialHostFactory.cs`,
  `TestHostClientTimeoutTests.cs`
- SpatialEngine-c5f (this record's bead); the coordinator tick of 2026-09-30
  and SpatialEngine-u2x.44, the bead whose close this was blocking
