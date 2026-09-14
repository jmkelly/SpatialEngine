---
status: accepted
date: 2026-09-14
deciders: maintainer + agent
---

# ADR-0054: Feature modern query params are an adapter projection

## Context

The compatibility matrix (`research/compat/feature-service.md` §1, follow-up
T-A) records four Feature Service query params as missing against the live
S3 (Query, Feature Service layer) reference:

- `returnEnvelope` (new at 11.4): envelope geometries instead of full shapes.
- `resultPaginationToken` (S3 keyset paging workflow): token continuation
  instead of `resultOffset`.
- `defaultSR` (new at 11.3): request-wide spatial-reference shorthand.
- `uniqueIds` / `returnUniqueIdsOnly` (new at 11.5): string-ID databases.

Today the facade silently ignores all four: no `parameters.Get(...)` reads
them, paging is offset-only, and `EsriObjectIdScheme` is integer-identity
only. The T-024 silent-ignore audit requires that every served-allowlist
param the engine cannot honour be rejected by name — so each item either
gets a real engine-backed behaviour or an honest reject-by-name. None of
the four needs a new engine verb: envelopes are structural core values,
SR fallback is request plumbing, the token is an opaque cursor over the
already-materialised ordered match set, and string identity columns already
exist in `DatasetDescription.IdColumns` + `AttributeKind.String`.

Two constraints pin the answer:

- **ADR-0001 / principle 1:** spatial algorithms live in implementation
  projects; `Spatial.Core` stays structural. Envelope output uses the
  existing `IGeometry.Envelope` value — no new algorithm, no Core change.
- **ADR-0005 / principle 8:** Esri wire types stay inside
  `Spatial.Adapter.GeoServices` (+ the shared `Spatial.Interop.Esri`
  codec, which references Core only). `Spatial.PluginSdk` is untouched:
  no new interface, no new package.

## Decision

**The adapter serves all four params; items with no engine model are
rejected by name. No SDK, Core or provider change.**

### 1. `returnEnvelope` (11.4+)

`EsriFeatureQuery.ReturnEnvelope` (default false). Wherever the features
response (or the single-feature resource) writes a geometry, it writes the
geometry's `{xmin,ymin,xmax,ymax}` envelope in the feature's (possibly
`outSR`-reprojected) CRS instead. The writer lives in
`EsriFeatureCodec` (`EsriFeatureWriteOptions.ReturnEnvelope`, default
false — existing callers and the Image Service are unaffected). A feature
with no (or an empty) geometry writes `"geometry": null`, exactly as the
full-geometry form does. The flag is orthogonal to the result shapes: with
`returnGeometry=false` (or any geometry-less shape) there is nothing to
replace, so it is accepted and has no effect — the same standing rule as
`geometryPrecision` with `returnGeometry=false`.

### 2. `defaultSR` (11.3+)

`EsriFeatureQuery.DefaultSr` holds the raw parsed value; it is the fallback
behind the explicit params on both sides of the request:

- input: the query geometry's own `spatialReference` wins, then `inSR`,
  then `defaultSR`, then the layer SR;
- output: `outSR` wins, then `defaultSR`, then the layer SR
  (so `OutSr` is the *effective* output reference).

No capability flag is advertised here: `supportsDefaultSR` belongs to the
T-037 statistics/capability-honesty track, which owns `EsriLayerModel.cs`.

### 3. `resultPaginationToken` (S3 keyset workflow)

`ResultPagination` (adapter-internal) mints opaque base64url tokens
`{v:1, offset}`. A page that fills up returns `resultPaginationToken`
alongside `exceededTransferLimit: true`; the final page carries none. The
client repeats the identical query with the token instead of
`resultOffset`:

- `resultPaginationToken` + `resultOffset` is rejected (the token carries
  the position);
- the token is honoured on every paged shape (features, distinct values,
  statistics) and rejected with the unpaged shapes (`returnIdsOnly`,
  `returnCountOnly`, `returnExtentOnly`, `returnUniqueIdsOnly`);
- a malformed, forged or version-mismatched token is a typed
  `invalid.arguments` failure that tells the client to restart paging.

The engine materialises the deterministic ordered match set per request, so
an offset cursor into that set *is* the keyset continuation S3 describes,
with no server-side paging state. A token is only valid with the query that
minted it.

### 4. `uniqueIds` / `returnUniqueIdsOnly` (11.5+)

`EsriUniqueIdScheme` (beside `EsriObjectIdScheme`) models the string-ID
database: a dataset with exactly one identity column of kind
`AttributeKind.String` exposes that column's value under its field name.
`uniqueIds` filters on exact (ordinal) matches and composes with every
other predicate; `returnUniqueIdsOnly` is a result shape symmetric with
`returnIdsOnly` (`{uniqueIdFieldName, uniqueIds}`) and joins its mutual
exclusion set. Layers without a string identity column (integer layers,
ordinal layers, guid-identity layers) reject both params by name, pointing
at `objectIds`. Layer-metadata advertisement of the unique-id field is
**not** included: it touches `EsriLayerModel.cs`, which the unstarted T-037
owns — recorded as a follow-up below.

## Consequences

- ArcGIS clients can page with the S3 token workflow, request envelopes,
  use the `defaultSR` shorthand and query string-ID layers; integer layers
  keep their exact current behaviour.
- Behaviour lands with response-level replay tests (red first: each param
  was silently ignored), parse-level precedence/exclusivity tests,
  failure tests (bad token, token+offset, token+unpaged shape, shape
  conflicts, unique-ids on integer layers) and a cancellation test over
  `QueryAsync` — plus this ADR. No SDK or contract change was needed: the
  only public-surface touch is the optional `ReturnEnvelope` member on the
  interop `EsriFeatureWriteOptions` record.
- `WriteStatistics`/`WriteDistinctValues` gain an additive next-token
  argument and `FeatureQueryEngine.Matches` an optional unique-id argument;
  the statistics table itself is untouched for T-037.
- Follow-ups (filed on landing): layer-metadata `uniqueIdField`
  advertisement (needs `EsriLayerModel.cs` beside T-037); guid-identity
  columns stay on the honest-reject path until a string-form demand is
  measured.

## Alternatives

- **Server-side paging state for tokens**: true cursor stability across
  edits, but stateful sessions contradict the facade's stateless reads and
  the engine has no session model. Rejected.
- **True keyset tokens (last sort keys) instead of offset cursors**: equal
  behaviour on the materialised ordered set at higher token complexity.
  Rejected; the offset cursor is exact for snapshot reads.
- **Treat `defaultSR` as input-only**: narrower, but the task's "request-wide
  shorthand" reading and S3's 11.3 framing cover both sides. Rejected.
- **Map guid-identity columns to `uniqueIds`**: tempting, but S3's feature
  is string-ID databases and guid formatting rules are unmeasured. Rejected
  for now; the honest reject names the alternative.
- **Reject `returnEnvelope` with `returnGeometry=false`**: stricter, but
  precedent (`geometryPrecision`) accepts moot geometry modifiers. Rejected.
