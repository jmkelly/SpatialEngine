---
status: accepted
date: 2026-09-14
deciders: maintainer + agent
---

# ADR-0057: Feature percentile statistics and capability-flag honesty

## Context

The compatibility matrix (`research/compat/feature-service.md` §1–2,
follow-up T-B) records two gaps against the live S3 (Query, Feature Service
layer) reference and the `feature-layer0.DamageAssessment.json` ground
truth (10.91, ~75 layer keys):

- The facade serves `count/sum/min/max/avg/stddev/var` outStatistics
  (`EsriFeatureQuery.cs`) but not the S3 percentile type; the live layer
  advertises `advancedQueryCapabilities.supportsPercentileStatistics: true`.
- The live layer carries `supportsExceedsLimitStatistics: true` (top level)
  and `advancedQueryCapabilities.supportsCountDistinct: true`, which we do
  not emit; `supportsDefaultSR` (proved by the T-036 `defaultSR` behaviour,
  ADR-0056) and `supportsFullTextSearch` (S2 layer resource, 11.5, under
  `advancedQueryCapabilities` with the `fullTextSearchableFields` list) are
  not advertised at all, so clients cannot tell honest support from honest
  rejection.

Two constraints pin the answer:

- **ADR-0001 / principle 1:** spatial algorithms live in implementation
  projects; `Spatial.Core` stays structural. Percentile aggregation is an
  adapter-side computation over already-materialised matches — no Core
  change.
- **ADR-0005 / principle 8:** Esri wire types stay inside
  `Spatial.Adapter.GeoServices`. No `Spatial.PluginSdk` change, no new
  package, no provider change.

## Decision

**The adapter serves the S3 percentile type and COUNT DISTINCT, and the
layer advertises exactly the five flags with values proved by behaviour
tests (the T-019 pattern). The `text` full-text parameter stays honestly
rejected.**

### 1. `percentile_cont` / `percentile_disc` (S3 percentile type)

`EsriOutStatistic` gains the optional `PercentileValue` (fraction) and
`PercentileDescending` rank order. The request shape follows S3 exactly:

- `statisticType` is `percentile_cont` or `percentile_disc`
  (case-insensitive, like the existing table);
- `statisticParameters.value` is required, a number in `[0, 1]`
  (0.9 is the ninetieth percentile);
- `statisticParameters.orderBy` is optional `ASC` (default) or `DESC`;
- the input field must be numeric (`Int64`/`Double`), like
  `sum`/`avg`/`stddev`/`var`;
- percentile statistics cannot combine with `having` (S3: "Percentile
  statistic Types cannot be used with the having Clause parameter") — a
  typed `invalid.arguments` failure naming `having`;
- `statisticParameters` on any other statistic type is rejected by name
  (nothing would honour it).

Semantics mirror the S3 examples: discrete returns the dataset value at
rank `ceil(fraction × n)` in the requested order (1..10 at 0.9 ascending
is 9, descending is 2); continuous linearly interpolates at rank
`fraction × (n − 1)`. Empty sets yield `null`, like the other statistics.
The result field is `esriFieldTypeDouble` (as the live percentile example
returns). Grouping, ordering by the alias and paging apply unchanged.

### 2. COUNT DISTINCT (`returnCountOnly` + `returnDistinctValues`)

The S3 count-distinct shape is the only exception to the mutually
exclusive result shapes: it returns `{"count": n}` over the deduplicated
projection, sharing the `DistinctRows` helper with `returnDistinctValues`
so both agree on what "distinct" means. Every other shape clash stays
rejected, and the token workflow still treats the shape as unpaged
(`resultPaginationToken` is rejected with it, as with the other
count/extent/ids shapes).

### 3. Capability flags (S2 layer-resource placement)

- `advancedQueryCapabilities.supportsCountDistinct: true` and
  `supportsPercentileStatistics: true` — placed as on the live layer,
  proved by the served behaviours above.
- Top-level `supportsExceedsLimitStatistics: true` — placed as on the
  live layer, proved by paged statistics responses carrying
  `exceededTransferLimit` (and the next-page token).
- Top-level `supportsDefaultSR: true` — a facade-defined honesty flag
  (S2/S3 define no such key), proved by the T-036 `defaultSR` behaviour
  it names on both sides of the request.
- `advancedQueryCapabilities.supportsFullTextSearch: false` with
  `fullTextSearchableFields: []` — the S2 11.5 placement; explicit
  `false` (not absent) so the surrounding `true` flags cannot imply
  support, and the searchable-fields list is present-but-empty because
  the engine builds no full-text indexes. The `text` parameter stays
  rejected by name, pointing at `where … LIKE`.

## Consequences

- ArcGIS clients can request percentile statistics and COUNT DISTINCT and
  can branch on all five flags without probing; full-text clients get an
  immediate honest 400 plus caps that agree with it.
- Behaviour lands with parse-level tests (percentile shape, value range,
  order, having conflict, non-percentile parameters, shape exclusivity),
  response-level tests over the demo cities (interpolated vs dataset
  values, distinct counts, exceeded-limit statistics pages), live-key
  replay tests against the ground-truth layer JSON, failure tests and a
  cancellation test — plus this ADR. No SDK, Core or provider change was
  needed.
- `EsriOutStatistic` gains two optional members (existing three-argument
  constructions are untouched); `EsriLayer` gains two top-level flags and
  `EsriAdvancedQueryCapabilities` gains four members, all required so
  future call sites must choose honest values.

## Alternatives

- **Percentile value in 0–100**: rejected; S3's examples and prose use the
  0–1 fraction ("the ninetieth percentile (value 0.9)").
- **PostgreSQL-style `PERCENTILE_CONT(fraction) WITHIN GROUP` errors for
  edge fractions**: rejected; fractions clamp to the dataset ends
  (0 → first, 1 → last), total order preserved.
- **`supportsFullTextSearch` absent instead of false**: rejected; absence
  on a 10.91-shaped layer is ambiguous next to six `true` siblings —
  explicit `false` cannot be misread.
- **Emitting `fullTextSearchCapabilities` too**: rejected; with no
  supported full-text property there is nothing truthful to list, and the
  empty searchable-fields list already carries the signal.
