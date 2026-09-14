---
status: accepted
date: 2026-09-14
deciders: maintainer + agent
---

# ADR-0067: Guid identity columns serve uniqueIds in canonical form

## Context

ADR-0056 §4 landed `uniqueIds` / `returnUniqueIdsOnly` (spec §9.1.4, 11.5+)
for string-ID databases only: `EsriUniqueIdScheme` models a dataset with
exactly one identity column of kind `AttributeKind.String`, and every other
layer — integer, ordinal, and guid-identity — rejects both params by name.
The ADR closed with the measured-demand rule: guid-identity columns stay on
the honest-reject path "until a string-form demand is measured", because
guid formatting rules were unmeasured. T-058 is that demand: guid-keyed
datasets exist behind the facade today, and their clients get a reject for
a param the engine can honestly serve.

The constraints are the AGENTS.md hard walls: no SDK change (the scheme is
adapter-internal beside `EsriObjectIdScheme`), no Core change (guid values
are structural core values), long-running work stays a cancellable `Task`,
failures stay typed `EsriInteropException` codes. The only question is the
wire form of a guid: it must be documented, deterministic, and
round-trippable through the engine's string `FeatureId`.

## Decision

**A dataset with exactly one identity column of kind `AttributeKind.Guid`
serves `uniqueIds` / `returnUniqueIdsOnly` with the canonical lowercase
`D` form (`xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx`); layers with no
string-or-guid identity keep the honest reject-by-name. No SDK, Core or
provider change.**

### 1. Canonical form

`EsriUniqueIdScheme.CanonicalForm` renders a guid with `Guid.ToString("D")`
— lowercase hex, hyphenated, 36 chars. This is the same text the stores
already render a boxed `Guid` with (`PostgisDiagnostics.FeatureIdentity`
calls `ToString()` on the key value), so the served id round-trips through
`FeatureId`: `Guid.Parse(new FeatureId(uniqueId).Value).ToString("D")`
equals the served `uniqueId`. `Guid.Parse` accepts the wider family
(`N`, `B`, `P`, `X`, any case) on the way in, but the facade only ever
emits canonical `D` — and requested ids match the served ids exactly
(ordinal), the same exact-match rule string-ID layers already have.

### 2. Served surface (symmetric with string-ID layers)

- `For` accepts a single identity column of kind `String` **or** `Guid`;
  `TryResolve` emits the string value (still non-empty) or the canonical
  guid form, and is false for a null/absent value (a nullable guid column
  with no value).
- `Resolve` keeps its typed `serverError` failure for the valueless case;
  `ResolveFor` and the `returnUniqueIdsOnly` writer keep their typed
  `invalid.arguments` reject-by-name for layers with no string-or-guid
  model (integer, ordinal, composite-identity), now pointing at `objectIds`.
- Layer metadata advertises the guid column as `uniqueIdField` with
  `isSystemMaintained: false` — the engine's guid identity is
  client-visible data, never a system-maintained GlobalID.

### 3. Tests

Served-behaviour tests at the response level: scheme acceptance, canonical
emission, `FeatureId` round-trip, `uniqueIds` filtering and
`returnUniqueIdsOnly` on a guid layer, `uniqueIdField` advertisement, the
null-guid server failure on both params, and cancellation over
`QueryAsync`. The integer-layer reject tests stand unchanged — the reject
is still the honest answer where there is no model.

## Consequences

- Guid-keyed datasets are queryable by their durable keys through the S3
  11.5+ params; integer and ordinal layers keep their exact current
  behaviour, including the reject text (updated to name the guid model).
- Behaviour lands with scheme + tests + this ADR together; no contract,
  SDK, provider, legend, attachment or perf surface is touched.

## Alternatives

- **Uppercase or braced (`B`) canonical form**: closer to some Esri
  GlobalID renderings, but diverges from the stores' existing `ToString()`
  text and breaks the `FeatureId` round-trip identity. Rejected.
- **Case-insensitive / multi-format request matching**: friendlier to
  clients sending `N`/`B`/uppercase, but a second matching rule beside the
  string layers' ordinal rule, unmeasured. Rejected; the canonical form is
  documented and exact.
- **Keep the honest reject**: zero change, but leaves guid-keyed datasets
  without durable-key queries now that the demand is measured. Rejected by
  the task.
