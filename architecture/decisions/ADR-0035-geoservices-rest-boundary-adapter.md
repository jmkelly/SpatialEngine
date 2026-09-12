---
status: proposed
date: 2026-09-13
deciders: maintainer + agent
---

# ADR-0035: Esri GeoServices REST is a boundary adapter; ArcGIS REST is consumed as a provider

## Context

The repository holds the Esri GeoServices REST Specification v1.0
(`architecture/references/geoservices-rest-spec.pdf`) with a compatibility
review at `architecture/references/geoservices-compatibility.md`. The spec
is a REST *serving* contract — GET with `f=json`, Esri JSON geometries and
features, WKID spatial references. The engine is the opposite shape: a
headless, in-process, typed engine (ADR-0033) whose interchange is
canonical binary (ADR-0020) and whose CRS identity is EPSG-only (ADR-0009).

Two needs are in scope and are distinct:

1. **Serve** — Esri clients (ArcGIS JS 4.x, Collector, Pro) read engine
   data and geometry through GeoServices REST.
2. **Consume** — the engine ingests ArcGIS REST services as data sources
   (already anticipated as a provider in `implementation-plan.md` §5).

Serving collides with three standing decisions: ADR-0020's canonical-binary
interchange (the host contract says "feature data never crosses as JSON
geometry"); the query-security model (`where` is arbitrary SQL; the engine
never lets client text become SQL structure); and ADR-0009's EPSG-only CRS
identity (Esri WKIDs are not EPSG codes — the spec's example uses 102113,
EPSG is 3857). This ADR records where foreign wire formats are allowed to
live and what the engine will and will not guarantee.

## Decision

**1. Esri support is ordinary implementation projects; no core or SDK types.**

- `Spatial.Interop.Esri` — shared Esri wire codec: JSON geometry/feature
  shapes, `esriFieldType*` ↔ `AttributeKind`, WKID ↔ `EPSG:` mapping, Esri
  error model. References `Spatial.Core` only; no algorithms, no NTS.
- `Spatial.Adapter.GeoServices` — serving facade. Consumes the existing
  `Spatial.PluginSdk` interfaces and maps requests/results; owns the
  GeoServices URL grammar and `f=json`.
- `Spatial.Provider.ArcGisRest` — consuming provider implementing
  `IDataCatalogue` / `IFeatureStore` over ArcGIS REST.

`Spatial.Core` and `Spatial.PluginSdk` gain no Esri types. The three
projects depend on contracts, not on each other's implementations
(principle 7); the adapter and provider share only `Spatial.Interop.Esri`.

**2. Esri JSON is adapter-owned (clarifies ADR-0020).**

Canonical binary remains the engine's interchange between core, SDK and
implementations. Esri JSON may exist only inside `Spatial.Interop.Esri`
and at the adapter's HTTP boundary; it never enters core values or SDK
contracts. ADR-0020 is clarified, not repealed.

**3. Query safety and network safety are not negotiable.**

The facade never forwards `where` as SQL. It parses a supported subset
(AND / OR / parentheses / comparisons / `LIKE` / `IS [NOT] NULL` — the
closed grammar of the existing parameterised filter) and rejects anything
else with `invalid.arguments`. The spec's `{"url": ...}` remote-input form
is rejected (no server-side fetch). ArcGIS tokens, when needed, come from
host configuration only, never from request bodies.

**4. CRS by curated map, x-first.**

Esri `wkid` / `wkt` resolve through a curated WKID ↔ `EPSG:` map; unknown
codes are `invalid.arguments`. Coordinate order stays x-first, which
matches the spec's geometry arrays.

**5. Read-only first; editing is gated.**

Phase one serves the Geometry Service and a read-only Feature Service
(service/layer metadata and `query`). Editing
(`addFeatures` / `updateFeatures` / `deleteFeatures` / `applyEdits`)
requires update/delete verbs the store contracts do not have; it is a
later phase behind a follow-up ADR extending `IFeatureStore`. Map, Image,
GP and Geocode services stay out of scope (no renderer, raster or job
model — principles 1–2, plan §4.2).

**6. Verb expansion lives in the operations implementation.**

Geometry verbs the facade needs but the engine lacks (`generalize`,
`union`, `difference`, `densify`, `convexHull`, `offset`, area/length,
distance, relation, `simplify`-as-repair, …) are added to
`Spatial.PluginSdk` operation interfaces and implemented by
`Spatial.Operations.NetTopologySuite`. The adapter contains no spatial
algorithms (principle 1); it only maps protocol to verbs.

**7. Baseline v1.0, documented 10.x delta.**

The v1.0 specification is the compatibility baseline. The plan records the
10.x additions real clients expect (`resultOffset`/`resultRecordCount`,
`orderByFields`, `returnCountOnly`, `hasZ`/`hasM`, FeatureServer edits) so
the shape does not preclude them.

**8. The provider reuses the facade as its test fixture.**

`Spatial.Provider.ArcGisRest` converts Esri JSON to core values inside the
provider and pushes down the supported query subset. Facade conformance
fixtures double as the provider's stub server.

## Consequences

- Three new projects plus architecture-guard package allowlist entries
  (an HTTP client for the provider; ASP.NET Core minimal-API references for
  the adapter).
- The `where` subset, the WKID catalogue and the v1.0 baseline are
  documented limitations. Full SQL, arbitrary WKIDs and later spec versions
  are not supported until separately decided.
- New operation verbs and (later) update/delete follow the normal rule:
  interface + implementation + tests + ADR together.
- The engine host API, SDKs, workbench and canonical codecs are unchanged;
  the workbench keeps talking to the engine API, not the facade.
- The boundary is explicit: foreign protocols are adapter-owned edge code,
  never core or contract surface.

## References

- `architecture/references/geoservices-compatibility.md` — compatibility review
- `architecture/geoservices-implementation-plan.md` — delivery plan
- ADR-0001 (geometry is core), 0005 (no third-party types), 0009 (CRS
  identity), 0020 (canonical binary), 0033 (in-process interfaces)
