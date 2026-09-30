---
status: accepted
date: 2026-09-26
deciders: maintainer + agent
summary: The host integration suite starts its own PostGIS container and configures the store from it, so no `SPATIAL_POSTGIS_CONNECTION` is needed to run the tests.
---

# ADR-0072: The host integration suite supplies its own PostGIS

## Context

`tests/integration/Spatial.Host.Tests` boots the real host in process
(`WebApplicationFactory<Program>`). The host's PostGIS store takes its
connection string from configuration (`Spatial:Postgis:ConnectionString` or
`SPATIAL_POSTGIS_CONNECTION`, ADR-0033), so a plain `dotnet test` starts a
host with no store and every start logs

> The PostGIS store is not configured; requests to the 'postgis' store will
> fail with store.unavailable.

That warning is correct — the operator configured nothing — but it is the
state a contributor's machine is in every day, and it hides a real gap: the
host's typed data API and its GeoServices projection are never exercised
against a real PostGIS database, because the store's own suite
(`tests/integration/Spatial.PostGIS.Tests`) stops at the provider. ADR-0028
already fixed the provider half with Testcontainers, an explicit skip reason
when no Docker daemon is reachable, and one pinned image. The host half has
no such fixture, so the only way to see a configured host today is to
export a connection string by hand.

## Decision

**1. One PostGIS container per test process, started lazily.**
`PostgisTestDatabase` starts the pinned `postgis/postgis:16-3.4` image at
most once per test process and exposes its connection string, its
availability, and the reason it is absent. It is started on first use, so a
suite that never touches PostGIS never waits for it, and Testcontainers'
reaper removes the container when the test process exits. Without a
reachable Docker daemon the container is absent, `SkipReason` explains why,
and the tests that need the database skip through `Skip.If` — the same
honest-degradation rule the provider suite follows.

**2. The container reaches the host as ordinary configuration.**
`PostgisHostFactory` (the suite's default factory) sets
`Spatial:Postgis:ConnectionString` from the container. Test classes that
need extra host settings (an admin token, a private map file) derive from
it and call the base first, so there is one place that wires the store.
`PostgisHostTests` then drives catalogue, describe, create, write, scan,
bbox and attribute query, transaction commit/rollback, `not.found`,
cancellation, and the same dataset served through an Esri FeatureServer —
all against the container, with no environment variable anywhere.

**3. The unconfigured store keeps its own host.** `store.unavailable`
remains a contract (ADR-0033), not an accident, so the assertions that
catalogue and transaction routes answer 503 without a store keep booting a
plain `WebApplicationFactory<Program>`; that host logs the warning on
purpose.

## Consequences

- `dotnet test` needs no `SPATIAL_POSTGIS_CONNECTION`: the host suite is a
  second covered path to the PostGIS store, from the client SDK and the
  GeoServices boundary down to the database.
- The host suite needs Docker for its full value, and says so: with no
  daemon the containerised tests skip with a reason and everything else
  passes against the unconfigured store.
- The shared container is process-wide, so containerised tests create and
  drop their own tables; the provider suite keeps its own per-class fixture
  and its seeded data. Both pin the same image, so one local image serves
  both.
- Test-only change: no contract, host, SDK or logging behaviour moves, so
  the `store.unavailable` warning still fires for any real deployment that
  forgets the setting.

## Alternatives

- **Set `SPATIAL_POSTGIS_CONNECTION` process-wide from the fixture.** It
  would configure every host in the assembly, including the many ad-hoc
  factories, and remove the warning everywhere — but the host falls back to
  that variable whenever configuration is blank, so the `store.unavailable`
  contract could no longer be tested in the same process, and a leaked
  variable would make a developer's own run order-dependent. Rejected.
- **Compose PostGIS in the Aspire AppHost (ADR-0034).** Right for
  `dotnet run`, wrong for `dotnet test`: the suite must not require the
  development stack to be up. Worth doing separately for the local
  development profile.
- **Drop or downgrade the startup warning.** Silences the symptom and hides
  a genuine deployment misconfiguration. Rejected.

## References

- ADR-0028 (PostGIS provider contracts, its containerised suite), ADR-0033
  (in-process interfaces, the unconfigured-store contract), ADR-0034 (Aspire
  local development)
- `tests/integration/Spatial.Host.Tests/PostgisTestDatabase.cs`,
  `PostgisHostFactory.cs`, `PostgisHostTests.cs`
