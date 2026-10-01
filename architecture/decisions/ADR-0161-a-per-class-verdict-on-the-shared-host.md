---
status: accepted
date: 2026-10-02
deciders: maintainer + agent
summary: **Give every test class that boots a host a written verdict — convert, split or leave — and let the verdict be the gate: a class with no verdict fails the run.** Eight classes converted onto `ClassHostFixture` (their summed cost 107.6 s → 40.6 s), the ones already sharing a host were found to be twenty of the ninety-odd factory sites rather than a handful, and the two blockers ADR-0160 named account for every class that is left. `SharedHostPolicy.Audited` holds the verdicts with their evidence; `TestHostShapeTests` fails when a class boots a host nobody judged.
amends: ADR-0160
---

# ADR-0161: A per-class verdict on the shared host, and the eight classes it converts

## Context

ADR-0160 converted `OgcEndpointTests` onto `ClassHostFixture` and, in *Not
decided*, said what was left: "which of the remaining classes convert" is a
per-class audit that "is not done", and the two shapes that block a class are
per-test injected settings and a test that mutates a shared singleton needing
"identity of its own kind — a keyed dataset rather than a map name".

The population was not the one the record's context implies. A `using var
factory` count finds 147 sites across 24 classes, but that grep misses every
class that builds its factory in a constructor or a field initialiser.
Reading the compiled IL for `newobj` of a factory across every test class in
the assembly — the read `SharedHostPolicy` already had, narrowed to the types
xUnit will run — says that **35 classes** construct a factory in one of their
own methods, and after this record's eight conversions **26** do. Nineteen of
those 26 already build the host once, in a constructor or a field initialiser,
so for them the lever ADR-0160 declined is already pulled; **seven** still boot
a host per test and stay for the reasons below.

So the class ADR-0154 called "eighteen classes already use `IClassFixture` of
their own" and these nineteen are different sets again, and a grep for
`using var factory` sees neither. A class with no verdict is indistinguishable
from a class nobody looked at, which is how ADR-0160's audit stayed undone:
the omission was invisible.

## Decision

**Every test class in `Spatial.Host.Tests` that constructs a host factory gets
an entry in `SharedHostPolicy.Audited` carrying one of three verdicts —
`Convert`, `Split` or `Leave` — and the evidence that decided it, and
`TestHostShapeTests` fails the run when a class boots a host that has no
verdict.** The population is read out of the compiled IL, so a new class that
constructs a factory cannot join the suite unaudited.

`Convert` means exactly what ADR-0160 meant by it — one host per class, the
dataset identity per test — and eight classes take it:

`DiscoveryPageTests`, `FeatureQueryPlanTests`, `MapRenderTests`, `MapTileTests`,
`ParityCensusTests`, `PolarRenderTests`, `StoreTransactionTests`,
`TileDataVersionTests`.

Three of them needed no dataset change at all: `FeatureQueryPlanTests` and
`StoreTransactionTests` already seeded `public.plan_<guid>` and
`public.txn_<guid>` per test, and `MapTileTests`, `MapRenderTests`,
`DiscoveryPageTests` and `TileDataVersionTests` publish per test over a map
name the fixture now issues. Two needed the identity ADR-0160 deferred:
`PolarRenderTests` and `ParityCensusTests` ingest into the shared memory store,
so they take a **keyed dataset** from `ClassHostFixture.NextDatasetName` beside
the per-test map name.

`Split` — where the host's settings are per test and the class should be cut by
setting rather than fitted with a fixture — has one member:
`AuthEndpointTests`, whose `An_expired_token_is_rejected` injects
`Spatial:Auth:TokenLifetime`. That class's other four tests have the class's
settings and are convertible once it is cut; the split is the recorded next
step, not one taken here.

`Leave` is the other thirty-one audited entries, and each says which of the
two blockers it hit: injected per-test settings (`AdminEndpointTests`,
`GeoServicesEditTests`, `GeoServicesImageHonestyTests`,
`GeoServicesImageMetadataTests`, `GeoServicesImageMissingTests`,
`GeoServicesImageTests`, `SeedEndpointTests`, `RemoteStoreWiringTests`,
`ResumableUploadTests`, `WorkbenchHostingTests`), a shared singleton that a
test asserts on as a whole (`AdminEndpointTests`'s `/arcgis/admin/services`
listing, `ResumableUploadTests`' staged-upload listing), a class that is the
suite's own gate about factories (`TestHostClientTimeoutTests`,
`TestParallelismTests`), or already being one host for the class — nineteen
entries, and nothing to convert on them.

`SharedHostPolicy.Audited` holds **41** entries rather than 35 because six
classes that construct a factory in a method the read cannot resolve — a
generic method, or a test class nested in a generic one — are outside its
reach and were audited from their source instead: `CliEndToEndTests`,
`GeoServicesEditTests`, `GeoServicesImageHonestyTests`,
`GeoServicesImageMetadataTests`, `GeoServicesImageMissingTests`,
`TestParallelismTests`. That gap is named here rather than closed: a class
that reaches the read by neither route is still unaudited, and the gate cannot
see it.

## Alternatives

- **Convert every class that constructs a factory.** Rejected, and the audit is
  what says so per class rather than in general: a converted `GeoServicesImageTests`
  would have to serve eleven different raster sources from one host, and a
  converted `AdminEndpointTests` would assert its services listing against a
  registry the other thirty tests had filled.
- **Judge a class by its assertions rather than by its factory sites, and drop
  the register.** Rejected: the twenty-six classes that already share a host
  are the suite's largest remaining cost and are invisible to a grep for
  `using var factory`, so a rule keyed on source text would report the suite
  as healthier than it is.
- **Record the audit as a document instead of in code.** Rejected: ADR-0160's
  own lesson is that the register has to be a list a test reads, because a
  comment about a class's shape is true until the class changes.
- **Leave the lever where ADR-0160 put it.** Rejected on the measurement
  below: the eight converted classes cost 62 % less summed, on the same box, in
  a back-to-back pair.

## Not decided

- **The `Split` verdicts.** `AuthEndpointTests` is recorded as convertible once
  its lifetime test moves out; whether to make that move, and whether the same
  cut is worth making in `ResumableUploadTests` and `GeoServicesImageTests`
  rather than leaving them per test, is a separate piece of work with its own
  measurement.
- **Whether the twenty-six already-shared classes should move onto
  `ClassHostFixture`.** They boot one host per class by hand rather than through
  the fixture, so they have no per-test name from `NextMapName` and no class
  maps file. Converting them is a rename of a documented service plus a fixture
  per class, not a saving — the boots are already gone — and the cost of doing
  it is not obviously worth it.
- **Whether `GeoServicesSourceIdentityTests`'s `CountingStore` and the tile
  caches want a per-test key.** Both are read as shared counters, which is the
  shape ADR-0160 named and did not solve; a keyed cache is a different decision.

## Consequences

- The suite's per-class shape is now a table with evidence rather than a habit:
  a reviewer asking "why does this class boot a host per test?" gets an answer
  from the register, and a contributor adding a class gets a failing gate
  instead of an omission — as long as their class is one the IL read can
  resolve, which six of the forty-one are not.
- `SharedHostPolicy` grew from a nine-line list to the register of every class
  that boots a host. The evidence strings are prose in a test assembly, which
  is where ADR-0160 already put the register and one step further.
- The gate reads IL across every test class on every run, which is the same
  read `WhyNotOneHostPerClass` already did for nine classes. Broadening it
  found a real defect: `ResolveMethod` throws `BadImageFormatException` on a
  token from a generic method, so the read skipped generic methods and caught
  the format failure rather than failing a run over a class nobody claimed.
- Tests within a class run sequentially under xUnit's default collection
  model, which is what makes a shared host's mutable singletons safe between
  tests in a class and is what the whole conversion rests on. A class that
  opts into `[Collection]` sharing with another class would break that, and
  nothing in the gate would notice.
- No host behaviour changed, no contract moved, and no assertion was relaxed.
  The assertions that did move are the ones that named a map: they now name the
  name their own test was issued, which is the check ADR-0160 introduced.

## References

- ADR-0160 — the lever, the two blocking shapes, and the class this audit
  completes.
- ADR-0155 — the thread cap the runs below were taken under.
- ADR-0154 — the 0.55 s warm boot and the 184 factory sites this audit
  re-counted at 106.
- ADR-0053 — the map registry whose singleton makes N datasets on one host cheap.
- ADR-0041 — append-only layer ids, the reason a shared service name is a leave.
- `tests/integration/Spatial.Host.Tests/SharedHostPolicy.cs` — `Audited`,
  `Converted` and the IL read.
- `tests/integration/Spatial.Host.Tests/ClassHostFixture.cs` — the fixture and
  `NextDatasetName`.
- `tests/integration/Spatial.Host.Tests/TestHostShapeTests.cs` — the gate.
- Beads: SpatialEngine-6e2 (this record and the conversions),
  SpatialEngine-ej2 (the `AuthEndpointTests` split).

## Measurements

Host: 12 cores, .NET SDK 10.0.400, `tests/integration/Spatial.Host.Tests` in
Debug, one Testcontainers PostGIS container, 2026-10-02, cap 4 throughout
(ADR-0155). `dotnet test --logger "trx;LogFileName=…" --results-directory …`,
read with `tools/slowest_tests.py`; summed is the trx duration of every
result. **The pair is back to back on the same box** — the baseline run, then
the converted run, minutes apart — and the box was shared with other lanes
throughout, so the load average is given for both.

**Whole suite**

| Config | Wall | Summed | n | Load at start → end |
| --- | --- | --- | --- | --- |
| baseline (8 classes per test) | 4 m 18 s | 979.45 s | 817 | 12.6 → 17.3 |
| **8 classes per class** | **3 m 59 s** | **926.38 s** | 820 | 17.8 → 18.5 |

−5.4 % summed, −7 % wall, and three more results — the three audit gates. The
whole-suite columns understate the change, because the suite's tail is
`GeoServicesMapTests` (122 s) and `AdminEndpointTests` (77 s), both of which
the audit leaves alone on evidence.

**The eight converted classes, per class**

| Class | Before | After | n |
| --- | --- | --- | --- |
| `StoreTransactionTests` | 24.47 s | 2.88 s | 9 |
| `DiscoveryPageTests` | 23.39 s | 21.03 s | 4 |
| `FeatureQueryPlanTests` | 14.70 s | 1.04 s | 14 |
| `MapTileTests` | 10.56 s | 3.26 s | 7 |
| `TileDataVersionTests` | 8.49 s | 3.19 s | 7 |
| `ParityCensusTests` | 6.26 s | 1.67 s | 3 |
| `PolarRenderTests` | 5.83 s | 2.78 s | 3 |
| `MapRenderTests` | 4.39 s | 4.16 s | 4 |
| **total** | **108.09 s** | **40.01 s** | **51** |

−63 % summed on the classes the lever reached. `DiscoveryPageTests` and
`MapRenderTests` are the two that barely moved, and both are the same shape:
each test renders or lists a map, so the request dominates and the boot it
saved was already small next to it. Both are still correct conversions — the
boot is gone — and neither is worth arguing about on these numbers.

`OgcEndpointTests` went 3.55 s → 1.76 s between the same two runs, on a change
that did not touch it; that is the size of the run-to-run noise on this box,
and it is why the eight-class total above is read as a range rather than a
figure.
