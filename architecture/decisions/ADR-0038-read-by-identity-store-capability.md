---
status: proposed
date: 2026-09-13
deciders: maintainer + agent
---

# ADR-0038: Read-by-identity is an additive store capability

## Context

ADR-0037 delivered GeoServices Feature Service editing and recorded one
accepted cost in its consequences: "Update is read-modify-write, so a partial
update scans the dataset to merge unchanged fields. A store-level read-by-id
is a future optimisation, not a contract change."

The facade's merge step (`FeatureService.IndexAsync`) and its per-object
delete/existence check both call `IFeatureStore.ScanAsync` and build an
`OBJECTID → Feature` map over the *whole* dataset, even when the request names
one feature. Over PostGIS that is a full `SELECT` per edit batch — the
dominant cost of an edit against a large layer, and the reason the
`GeoServices` edit path cannot scale.

The tension is the usual one (ADR-0036): not every store can fetch by
identity. `ArcGisRestStore` reaches a remote `/query` and the demo store is
procedural; forcing a read-by-id verb onto `IFeatureStore` would make
read-only implementations carry a method they cannot honour. ADR-0037
therefore left the optimisation as a future additive capability.

## Decision

**1. Read-by-identity is a separate, additive SDK interface.**
`Spatial.PluginSdk.Providers.IFeatureLookup` (alongside `IFeatureStore`, not
extending it) exposes one batch read:

```csharp
Task<IReadOnlyList<Feature>> GetAsync(
    string dataset, IReadOnlyList<FeatureId> ids, CancellationToken cancellationToken = default);
```

The batch shape is deliberate: an edit resolves several identities at once
(an `updateFeatures`/`deleteFeatures` payload, or the `where`-matched delete
set), so one targeted statement replaces the scan rather than N round trips.
A miss is simply absent from the result — not an error — which is exactly the
facade's `not.found` / HTTP 400 semantics when the caller then fails to merge
a named feature. Core-typed only: identity crosses as `FeatureId`, never as a
provider or protocol concept.

**2. PostGIS implements it with one targeted statement.**
`PostgisStore` implements `IFeatureLookup`; `PostgisQueries.SelectByIdentity`
builds `SELECT <columns> FROM <table> WHERE (<identity> = @p…) OR (…)` from
discovered identifiers only, with one bound parameter per identity value.
A single requested identity carries `LIMIT 1`; a batch has no limit because
the primary-key predicate already matches at most one row per tuple. Tables
without a primary key return an empty result (there is nothing durable to
address).

**3. The facade prefers the lookup and falls back to the scan.**
`FeatureService.ResolveAsync` uses `IFeatureLookup` when the resolved
`IFeatureStore` instance implements it, and otherwise runs the existing
`IndexAsync` scan. Behaviour is unchanged: the same features merge into the
same partial update, and a named-but-missing identity still yields the same
`invalid.arguments` / HTTP 400 result.

**4. Composition mirrors the other granular faces.**
`Spatial.Host` registers the capability keyed `"postgis"` (resolving the
canonical `PostgisStore`), like `IFeatureEditStore` (ADR-0037). The writable
in-memory store used by the host HTTP tests implements the interface so the
lookup path runs without Docker; a wrapper without it pins the scan fallback.

## Consequences

- `Spatial.Core` and the existing `IFeatureStore` are unchanged. Adding the
  capability touches the SDK, the facade, `PostgisStore`,
  `PostgisQueries`, the host registration and the tests.
- A PostGIS partial update or per-object delete now issues one
  identity-targeted read instead of a full-dataset scan. The read is one
  `Describe` plus one indexed `SELECT`, and can use the primary-key index.
- ADR-0037's "future optimisation" note is now delivered; its remaining
  consequence (a serial-identity add must supply the identity) is unaffected.
- Read-only stores (`DemoStore`, `ArcGisRestStore`) stay unchanged and the
  facade's `query` path still scans, because an arbitrary `where`/bbox cannot
  be expressed as identity lookups.
- The capability is additive: a store that never implements it keeps working
  through the fallback, and no public version bump is needed.

## References

- ADR-0037 (feature editing, whose read-by-id consequence this closes),
  ADR-0036 (granular capability interfaces), ADR-0033 (in-process services)
- `architecture/geoservices-implementation-plan.md` §5 (S3)
- `architecture/distilled/contracts.md`, `architecture/distilled/runtime.md`
