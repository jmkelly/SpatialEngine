---
status: accepted
date: 2026-10-02
deciders: maintainer + agent
summary: **Adopt the shared-host lever for classes whose dataset identity can stay per test, under one rule: the host is the class's and the map name is the test's.** `ClassHostFixture` hands each test a map name it never reuses, `OgcEndpointTests` is the first class converted, and `SharedHostPolicy` + `TestHostShapeTests` hold the register and read compiled IL for a `new` a class fixture's name would hide. The lever is worth its audit: the class summed **86.9 s → 11.3 s** (7.7×) in a back-to-back pair at load 25–26, and 45.6 s → 10.7 s unloaded. The audit's other half is the failure mode list — per-test injected settings and a shared-name republish are what block a class, and the republish is not hypothetical: `MapRegistry.PutAsync` assigns layer ids from `max + 1`, so a name two tests share renumbers the other's layers.
amends: ADR-0155
---

# ADR-0160: One host per class, and a per-test map name to keep the dataset private

## Context

ADR-0154 measured the host integration suite's tail and named three levers for
it. ADR-0155 took one — a cap of four collection threads, which took 37 % of
the CPU out of the run — and declined one host per class on an extrapolation:
184 factory sites at a measured 0.55 s warm boot is about 112 s of a 752 s
summed run, under a sixth of the win, against an audit of every test's private
identity that had not been started.

The extrapolation was the wrong end of the trade to argue about, because it
prices the lever in the suite's smallest unit rather than in the class that
carries it. `OgcEndpointTests` is the class ADR-0155 named as the cheap
instance, and it is the largest per-class total in ADR-0154's table: 53 tests,
2 m 16 s, and 47 of them publishing a map called `world`. ADR-0155 said a class
fixture there "is not a fixture swap, it is a rename plus an audit of every
test that asserts on a catalog count or a republish", and that is the two
questions this record answers:

- **Can one host hold N datasets cheaply?** The map registry is a singleton over
  a JSON file, so the cost of the Nth map is a file rewrite, not a host.
- **Does any test's assertion depend on its own dataset being the only one?** In
  `OgcEndpointTests`, no: nothing in the class reads a listing, a count, or
  another test's map. Every test publishes, then reads its own name back.

## Decision

**Share one host per class, and keep the dataset identity per test: the map
name is handed out by the fixture, is unique within the class, and is never
reused.** `ClassHostFixture` is the shape — one `WebApplicationFactory`, one
class-scoped temporary directory for the maps file, `NextMapName(label)`
issuing `label_01`, `label_02`, … — and `OgcEndpointTests` is converted onto it,
with each test's `/ogc/{name}/…` URL built from the name its own test was
issued.

Three things hold that in place rather than in a comment:

- **`SharedHostPolicy.Converted` is the register** of converted classes, and
  `WhyNotOneHostPerClass` is the read: the class must take an
  `IClassFixture<T>` deriving from `ClassHostFixture`, and must construct no
  factory of its own. That second half is read out of the **compiled IL** of
  the class's own methods and constructors, so a `new SomeFactory()` in a test
  body, in a field initialiser or in a private helper is named — a per-test
  host wearing a class fixture's name is the shape ADR-0154 measured, and it is
  invisible to a reviewer.
- **`TestHostShapeTests`** fails the run when a registered class is not actually
  one host, and when the register names a class the assembly does not have.
- **`OgcEndpointTests.A_test_publishes_under_a_map_name_no_other_test_holds`**
  is the identity model as a test: a name the class has never issued is
  `not.found` before the test publishes it and `OK` after, whatever order the
  class's tests ran in. A test that kept a shared name is red rather than
  quietly sharing state.

The class-scoped maps file is deleted with the class, so nothing one class
published is visible to another. The name is a flat identifier
(`[A-Za-z_][A-Za-z0-9_]*`, folders refused by the registry) — the first
conversion attempt used `world-01` and all 53 publishes came back
`invalid.arguments`, which is the validator earning its place.

## Alternatives

- **One map per class, published once by the fixture**, so the URLs stay
  `world` and the diff is ten lines rather than 243. Rejected, and not on
  taste: it is the shape the audit was asked about. A republish assigns layer
  ids from `max + 1` (`MapRegistry.PutAsync`), so a name two tests share
  renumbers the other's layers under it, and a class whose tests publish
  different layer sets under one name — `OgcEndpointTests` has two — has no
  per-test dataset at all. The URLs are the audit, and they are what makes the
  identity model checkable: a site that forgot to rename returns 404 and fails.
- **Convert every class that constructs a factory.** Rejected as scope. The
  conversion is mechanical only where a test's *settings* are the class's;
  `AdminEndpointTests` passes `maxBytes`, `maxFeatures` and `skipMalformed` per
  test, which a shared host cannot honour, and the rest of the suite has not
  been audited for a shared store, a shared cache or a shared ingestion. The
  register is a list because that is what is true.
- **Leave the lever alone**, as ADR-0155 did. Rejected on the measurement
  below: the class is 7.7× cheaper, and the extrapolation that declined it
  counted 184 warm boots spread over a summed run that is dominated by
  everything else, which says nothing about the class where the boots are.
- **A cross-process warm start** or ReadyToRun publication. Unchanged and
  unmeasured; ADR-0155 declined both and this record does not revisit them. The
  cap has already taken the off-box cost they attack.

## Not decided

- **Which of the remaining classes convert.** A class is convertible when its
  factory settings are the class's and its dataset identity can be made
  per-test. The two shapes that block it are per-test injected settings and a
  test that mutates a shared singleton (a store, an ingestion, a cache); the
  second needs identity of its own kind — a keyed dataset rather than a map
  name — and the per-class audit that finds them is not done.
- **Whether a per-test map name is the right identity, or a per-test service
  name.** Both are flat identifiers resolving through the same singleton, and
  the GeoServices classes publish services rather than maps; the same rule may
  read better there as a per-test service id.
- **Whether the register should be an attribute on the class** rather than a
  list in a policy class. A list is a better error message and an explicit
  order of conversion; an attribute is closer to the class and cannot be
  forgotten into a stale list.

## Consequences

- `OgcEndpointTests` costs a seventh of what it did: 86.9 s summed to 11.3 s in
  a back-to-back pair at load 25–26, 45.6 s to 10.7 s unloaded, with the same
  53 assertions green plus one more. The class was the largest per-class total
  in the suite.
- A class in this shape is no longer isolated in its *store*, only in its
  *map name*. That is a real narrowing and the class's doc comment says so; a
  test that relied on the whole host being private is a test that must give its
  dataset a name of its own or move to a class of its own.
- The suite now has two shapes for the same idea — eighteen classes already
  used `IClassFixture<T>` of their own, and a register that says which of them
  are the audited, uniform kind. A reader has to know which register to read.
- The gate reads IL, so a contributor who finds it opaque will find it
  unreadable rather than skippable, and the failure message names the factory
  type and the class. It is the suite's third structural gate of this kind
  (with `TestParallelismTests` and `TestHostClientTimeoutTests`) and it is
  order-independent, which a boot counter would not be.
- No host behaviour changed, no contract moved, and no assertion was relaxed.
  The one thing that would have been invisible and is now a test is a test
  sharing a map name with another.

## References

- ADR-0155 — the cap this record's lever sat beside, and the record it amends.
- ADR-0154 — the cold-start measurement the lever is made of, and the
  `tools/slowest_tests.py` reader used for the numbers below.
- ADR-0053 — the map registry whose singleton and persistence make N datasets
  on one host cheap.
- ADR-0041 — append-only, never-renumbered layer ids, which is what a republish
  of a shared name breaks.
- ADR-0134, ADR-0109 — the merge lane this suite's shape is measured through.
- `tests/integration/Spatial.Host.Tests/ClassHostFixture.cs` — the shape.
- `tests/integration/Spatial.Host.Tests/SharedHostPolicy.cs` — the register
  and the IL read.
- `tests/integration/Spatial.Host.Tests/TestHostShapeTests.cs` — the gate.
- `tests/integration/Spatial.Host.Tests/OgcEndpointTests.cs` — the conversion.
- `src/Spatial.Maps/MapRegistry.cs` — `PutAsync`'s `max + 1` layer ids.
- Beads: SpatialEngine-7k0 (this record and the conversion), SpatialEngine-cd2
  (ADR-0155), SpatialEngine-ou1 (ADR-0154).

## Measurements

Host: 12 cores, .NET SDK 10.0.400, `tests/integration/Spatial.Host.Tests` in
Debug, one Testcontainers PostGIS container, 2026-10-02. `dotnet test
--filter … --logger "trx;LogFileName=…" --results-directory …`, read with
`tools/slowest_tests.py`; summed is the trx duration of every result, wall is
what `dotnet test` reported. The box is shared with other lanes, so the load
average is given for every run and the load-bearing pair is the back-to-back
one.

**The class alone, one process, back to back at load 25 → 26 → 44** (the box
was busier through the second run, so the conversion is measured in the
direction that flatters it):

| Config | Wall | Summed | n | Per test |
| --- | --- | --- | --- | --- |
| host per test | 1 m 26 s | 86.88 s | 53 | 1.64 s |
| **host per class** | **0 m 11 s** | **11.32 s** | 54 | 0.21 s |

**The same pair unloaded** (`load average` 2.8 for the baseline, low for the
conversion), from a warm page cache:

| Config | Wall | Summed | n |
| --- | --- | --- | --- |
| host per test | 0 m 45 s | 45.56 s | 53 |
| **host per class** | **0 m 11 s** | **10.67 s** | 54 |

−77 % unloaded, −87 % at load 25, and the per-test cost falls from 0.88 s to
0.19 s: what is left is the request, not the host. The extra test is the
identity test, which is why `n` is 54.

**In the whole suite**, the class's own total, cap 4 throughout:

| Config | `OgcEndpointTests` summed | Whole suite wall | Whole suite summed | p90 | max |
| --- | --- | --- | --- | --- | --- |
| host per test | 2 m 05.5 s | 6 m 12 s | 1279 s | 4.75 s | 28.1 s |
| **host per class** | **0 m 07.4 s** | **2 m 52 s** | **644 s** | **1.65 s** | 18.7 s |

The two whole-suite rows were taken at load 8.4 and load 28.4 respectively —
the *baseline* was the unloaded one, so the suite-level columns flatter the
conversion and should not be read as its size; the class column and the
back-to-back pair are the measurement. 803 results, 2 skipped, none failed in
either run. The whole suite still passes with the conversion, which is the
point of converting one class: the other 75 are untouched and green.
