---
status: accepted
date: 2026-10-01
deciders: maintainer + agent
summary: **The two-minute in-process request is CPU starvation, and the suite's own ~15 s cold start is what it starves**: neither named test touches the shared PostGIS container or re-issues DDL — `GeoServicesRelationshipsTests.Relate_moves_the_key_and_the_read_follows_it` runs against the in-memory store and `EsriVectorTileRouteTests.The_esri_route_serves_a_vector_tile_then_its_cached_bytes` against the procedural demo catalogue, so of ADR-0135's three candidates only the third survives measurement. What the suite does do is boot a host per test class, and the **first** host boot in a test process costs 13–15 s of CPU-bound work while every later boot costs 0.6 s; xunit charges that one cost to whichever tests happen to boot first, which is why six unrelated tests carry a ~15 s (30 s under the suite's own 12-way parallelism) floor. The five minutes ADR-0135 budgets stay, and the fix is the suite's shape — a per-class host fixture, a bounded collection parallelism, or a cross-process warm start — which is **not** decided here and is tracked as its own bead. The measurement is re-runnable with `tools/slowest_tests.py`.
amends: ADR-0135
---

# ADR-0154: The two-minute in-process request is CPU starvation, not store contention

## Context

ADR-0135 gave the host integration suite's clients five minutes instead of the
framework's 100 seconds, and said plainly that it sized the budget without
explaining the cause. `SpatialEngine-ou1` inherited the question: two tests,
`GeoServicesRelationshipsTests.Relate_moves_the_key_and_the_read_follows_it`
and `EsriVectorTileRouteTests.The_esri_route_serves_a_vector_tile_then_its_cached_bytes`,
had each taken about two minutes for **one** request against an in-process
`TestServer` — no socket, no listener, no accept queue — and three candidate
causes were on the table: (a) tests writing the same shared PostGIS tables and
blocking on locks, (b) the write path re-issuing DDL or a full re-projection per
test, (c) plain CPU starvation from the concurrent agent swarm.

The suite is 795 tests in one process against one shared PostGIS container, so
(a) was the plausible story. It is also the story that would have been most
expensive to believe for long, because it points at the container, and the
container is the one component every lane can see.

## Decision

**Record the cause as CPU starvation, keep ADR-0135's five minutes, and leave
the suite's shape to a separate record.**

Three findings, each measured, and the first two are exclusions:

- **The named tests cannot be waiting on the store.** The relationships test
  ingests four small GeoJSON datasets into the in-memory store; the vector-tile
  test reads the procedural demo catalogue. Neither opens a PostGIS
  connection. Across the whole suite the PostGIS- and SQL-store-backed tests are
  3.9 % of the summed test time, and their slowest is 13 s — an order of
  magnitude short of the observed tail.
- **Neither is a tier-0 or DDL cost.** The same test in a Release build costs
  what it costs in Debug, and the cost is inside `CreateClient()` — before any
  request is sent. There is no DDL on the path.
- **Duration is a smooth function of machine load, with no floor.** The two
  named tests, run alone in one process, cost 2.4 s and 3.2 s on an idle box;
  5.7 s and 7.7 s with twelve competing processes; 9.9 s and 13.5 s with
  thirty-six; 17.9 s and 28.1 s with seventy-two. A row lock or a DDL wait
  produces a step and a floor — a floor the two tests do not have. This is
  candidate (c), and the two-minute observation is a point further along the
  same curve.

**The suite is a load generator, and the reason is structural.** The first
`WebApplicationFactory` boot in a test process costs 13–15 s; the second, third
and fourth cost 0.56 s, 0.59 s and 0.65 s. One process-wide cost — building the
host's DI graph, its endpoint table and its configuration off a cold page
cache. xunit parallelises by collection, so a dozen classes reach
`CreateClient()` at once, the one cost is paid concurrently rather than once,
and it lands on whichever dozen tests booted first as a ~15 s floor, which
becomes ~30 s under the suite's own parallelism and minutes when three or four
lanes are each running this suite at the same time. The whole run asks for
6.8 of the box's 12 cores and 480 MB.

**What is not decided here is the fix.** The levers are a per-class or
per-assembly host fixture, a bounded collection parallelism, a cross-process
warm start, and a ReadyToRun publication of the host under test — and the
measurement says the cheap JIT knobs are not among them (below). Choosing
between them is a different record, and it needs the experiment ADR-0135's
bead already asked for: which one removes the timeouts.

## Alternatives

- **Raise the client timeout to fifteen minutes.** Rejected: it treats a
  load-dependent wall clock as if it were a budget the host controls, and it
  makes a genuinely hung request take a quarter of an hour to be named.
- **Give each test class its own PostGIS container**, which is the shape
  `SpatialEngine-c5f` first hypothesised. Rejected on measurement: the
  PostGIS-backed tests are 3.9 % of the run and none of the named tests touch
  the container. It would buy isolation the suite does not need at the cost ADR-0072
  already reasoned about.
- **Serialise the suite** (`maxParallelThreads: 1`) to remove the contention.
  Rejected as a decision without a measurement, and it is the wrong shape
  anyway: it trades a 2 m 23 s run for a run whose length is the summed 24 m
  43 s, on a box where the lanes, not the suite, are the scarce resource.
- **Set `DOTNET_TieredCompilation=0` or `DOTNET_TieredPGO=0`** on the test
  host. Rejected on measurement: the cold start is 14.9 s and 11.6 s
  respectively, against 13.3 s with the defaults. The cold start is not tiering.
- **Do nothing and say so only in the bead.** Rejected: ADR-0135 explicitly
  left the cause unexplained and pointed at this bead, and the next agent to
  meet a two-minute test would otherwise re-derive it from the container.

## Not decided

How to change the suite's shape. The candidate levers, none of them measured
against each other yet:

- **One host per class rather than per test.** 76 classes boot a host today;
  the classes that already use `IClassFixture` cost one boot each. This is the
  smallest change with the largest share of the win, and it is a real
  behaviour change: a shared host is shared state, and the per-test temp
  directories and injected settings these fixtures rely on are exactly what
  makes them per-test.
- **Bounding xunit's collection parallelism** to something below `nproc`, which
  directly caps how many cold starts overlap. It trades wall clock for
  predictability, and the trade needs a number.
- **A cross-process warm start** — a single shared host process that several
  test processes attach to — and **ReadyToRun publication** of the host under
  test, which `DOTNET_ReadyToRun=1` cannot supply on its own because the
  assemblies are not published R2R.

What would settle it: the same load curve, re-measured with each lever in
place, and the answer to which of them removes the timeouts on a loaded box.

## Consequences

- ADR-0135's five minutes stands, and it is now a number with a curve behind
  it rather than a guess: a test that needs more than five minutes under this
  load is a real signal, and on an idle box the suite's own worst test is 31 s.
- A `TaskCanceledException` on a response copy, with no assertion failing, is
  named as machine load by mechanism rather than by inference. Nobody has to
  re-open the PostGIS container to explain it again.
- The suite is knowingly a 6.8-core load for as long as it runs, and four
  concurrent lanes are knowingly running it four times over. That is the cost
  of the current shape, and it is now written down rather than discovered.
- The measurement is re-runnable: `tools/slowest_tests.py` reads the trx files
  a lane already writes and ranks the slowest tests, so the next question about
  this suite starts from the same numbers instead of a throwaway parser.
- Nothing about the host's behaviour changed. No contract moved, no test was
  relaxed, and the fix is explicitly somebody else's bead.

## References

- ADR-0135 — the five-minute client timeout this record explains.
- ADR-0072 — the one PostGIS container per test process, which the measurement
  clears of the two named failures.
- ADR-0109 — the scoped verification lane whose merge gate this suite sits in.
- `tests/integration/Spatial.Host.Tests/` — the suite measured; 795 results,
  76 test classes, 118 sites constructing a factory.
- `tools/slowest_tests.py`, `tools/test_slowest_tests.py` — the reader, added
  so the table below can be re-derived.
- Beads: SpatialEngine-ou1 (this measurement), SpatialEngine-c5f (ADR-0135 and
  the fix experiment), SpatialEngine-u2x.44 (the bead the timeouts blocked).

## Measurements

Host: 12 cores, `load average` ≤ 4 for the unlad runs, Linux 6.15, .NET SDK
10.0.400, `tests/integration/Spatial.Host.Tests` in Debug unless stated.
`tools/slowest_tests.py <trx>` on the output of
`dotnet test --logger "trx;LogFileName=…" --results-directory …`.

**The whole suite, unloaded: 793 passed, 2 skipped, 2 m 23 s wall, 24 m 43 s
summed** — a parallelism factor of 10.4, with the `testhost` process itself
measured at 378 % rising to 680 % CPU and 480 MB RSS. So the suite occupies
6.8 of the box's 12 cores for its whole run.

Distribution over the 795 results: p50 1.42 s, p75 3.09 s, p90 3.87 s, p95
4.22 s, p99 7.65 s, max 31.43 s. 446 results over one second, 22 over five,
7 over ten. The slowest 20 are 18.3 % of the summed time.

**The slowest 20** (Debug, unloaded) are led by six tests between 29.3 s and
31.4 s — `RenderEndpointsTests.Render_returns_a_png_for_a_symbol_layer`,
`GeoServicesOrderByTests.Order_by_population_defaults_to_ascending`,
`GeoServicesSpatialRelTests.Overlaps_and_crosses_are_false_for_point_vs_polygon`,
`GeoServicesInSrTests.A_3857_envelope_needs_its_in_sr_to_match`,
`GeoServicesTests.The_geometry_server_resource_is_served` and
`GeoServicesPagingTests.A_query_all_features_loop_terminates_with_the_full_set`
— whose per-class siblings each cost under 0.1 s. Per class, the largest totals
are `OgcEndpointTests` 2 m 16 s over 53 tests, `GeoServicesImageTests` 1 m 29 s
over 28 and `AdminEndpointTests` 1 m 18 s over 35.

**Those six are the cold start, not their own work.** Run alone in one process
they cost 15.02 s, 15.15 s, 15.16 s, 15.40 s, 16.12 s and 23.41 s — a floor of
15.0 s across five unrelated classes, which no per-test work explains. A
throwaway probe (added, run, removed; not part of the suite) timed the boot
directly:

| Step (one process, one test) | Wall |
| --- | --- |
| `new PostgisHostFactory().CreateClient()` | 13 293 ms |
| first `GET /health/ready` | 919 ms |
| second `GET /health/ready` | 5 ms |
| first `GET …/GeometryServer?f=json` | 24 ms |
| first `GET …/demo/FeatureServer/0/query` | 147 ms |
| second host in the same process, `CreateClient` + request | 651 ms |
| third host, same | 593 ms |
| fourth host, same | 562 ms |

The cost is in the boot, it is paid once per **process**, and every later boot
is 0.6 s. The probe's own data is the exclusion for candidates (a) and (b):
the 13 s is spent before a single byte is requested.

**Debug is not the reason.** The same four tests in a Release build:
`RenderEndpointsTests` 15.31 s (against 15.85 s in Debug),
`GeoServicesTests.The_geometry_server_resource_is_served` 14.50 s,
`EsriVectorTileRouteTests.The_esri_route_serves_a_vector_tile_then_its_cached_bytes`
3.99 s, `GeoServicesRelationshipsTests.Relate_moves_the_key_and_the_read_follows_it`
2.82 s. The JIT knobs do not move the cold start either:
`DOTNET_TieredCompilation=0` 14 918 ms, `DOTNET_TieredPGO=0` 11 624 ms,
`DOTNET_ReadyToRun=1` 12 396 ms, against 13 293 ms for the defaults.

**Load is the reason.** The two named tests, run alone, with N competing
spinning processes on the box:

| Spinners | `Relate_moves_the_key…` | `The_esri_route_serves_a_vector_tile…` |
| --- | --- | --- |
| 0 | 2.43 s | 3.21 s |
| 12 | 5.72 s | 7.65 s |
| 36 | 9.94 s | 13.45 s |
| 72 | 17.94 s | 28.10 s |

Smooth, monotone, no floor. In the full suite at 12-way internal parallelism
they cost 5.28 s and 6.35 s; on a box running four swarm lanes they cost
1 m 56 s and 2 m 2 s. Same curve, further along.

**The store is not the reason.** Classes touching PostGIS or SQL Server —
`PostgisHostTests`, `PostgisTileDataVersionTests`, `StoreTransactionTests` and
the SQL Server classes — are 20 tests, 57.4 s of the 1 483 s summed
(3.9 %), and their slowest is 13.03 s. `PostgisHostTests`' seven tests average
2.92 s each, which is the process cold start divided seven ways rather than a
lock.
