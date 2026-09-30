---
status: accepted
date: 2026-09-27
deciders: maintainer + agent
---

# ADR-0083: The tile cache key carries the data version

## Context

ADR-0046 made the tile cache key content-addressed by the *request*: `Version`
was a SHA-256 over the style document, the ordered layer/store/filter
descriptors, the imagery stack and the encoding options. Its consequences
section already named the gap: "Cache invalidation on *data* change needs a
dataset version; until stores expose one, operators invalidate with `DELETE
/api/render/cache` and the version folds the style and dataset references."

A rendered tile is derived from feature data, not from the request that
describes it. With the request alone in the key, nothing moves when a feature
is written, added, updated or deleted, so after `POST /api/features/write`, an
Esri edit, or an ingest, every cached tile for that map is stale until an
operator flushes the cache by hand. A write path that does not invalidate its
own derived cache is a correctness bug wearing a performance feature's
clothes — and `DELETE /api/render/cache` is a global, all-layers hammer, so it
is also the wrong tool.

ADR-0046 anticipated the fix shape: "a store-level version stamp can replace
the descriptor-derived version when stores expose one". This ADR takes that
option, and folds the stamp into the existing version rather than replacing
it, so the request descriptors keep describing the request.

## Decision

**1. A store may report a per-dataset content version.**

`Spatial.Contracts` gains `IVersionedFeatureStore` with one cancellable
operation, `GetContentVersionAsync(dataset)`, returning an opaque token that
changes whenever that dataset's feature content changes. The token is not a
data value and not an existence check: callers fold it into a key and never
parse it, and an unknown dataset reports the unversioned token so a missing
dataset still fails at read time with the error it fails with today.

The face is additive and optional. `ContentVersions.OfAsync` resolves a
version for any `IFeatureStore` — the versioned store's token, or
`ContentVersions.Unversioned` for every other store — so the key path has one
code path rather than a per-store branch, and a read-only store (demo, ArcGIS
REST) keeps serving exactly as before. `ContentVersions.FoldAsync` folds a
request's `(store, dataset)` list into the single token the key carries.

**2. The write paths bump the version, the key reads it.**

In the in-memory store (`ADR-0042`), a per-dataset counter in the catalogue
moves on every mutation: `IFeatureStore.WriteAsync`, `IDatasetIngest` /
`IDataCatalogue.CreateAsync`, and every `IFeatureEditStore` add, update and
delete that changed at least one feature (a partially successful batch still
moves it; an empty write, or a batch where every feature failed, does not).
A transaction snapshots the counter with the data and a rollback restores both,
so a rolled-back write leaves the cache valid for the state it restored.

Attachment writes do not move the version: nothing renders an attachment into a
tile, so attachment content is not an input to a tile.

**3. Every tile key folds the version, and the resolved version is reported.**

The raster key (`TileCacheKey`, ADR-0046) and the MVT key
(`VectorTileCacheKey`, ADR-0070) both take the request fingerprint *and* the
folded data version, on all four routes that build one: the neutral
`/api/render/tiles` single and batch routes, the map raster and map MVT routes,
the Esri `MapServer/tile` and `MapServer/vectorTile` routes, and the OGC API
Tiles vector-tile path. The version is computed before the cache lookup, so a
write between two tile requests always misses.

A tile response now carries `X-Tile-Version` beside the existing
`X-Tile-Cached`, and the client SDK's `SpatialClient.Tiles.RenderWithVersionAsync`
returns the image, the cache disposition and that version, so a delivered
client that caches tiles of its own can tell that what it holds is stale
without interpreting the token.

## Consequences

- An edit now invalidates exactly the tiles derived from the data it changed;
  `DELETE /api/render/cache` remains for a full flush and for the paths that
  change without a data write (a style save, a new renderer). Style saves
  already moved the key, because the map routes compose a map's persisted
  per-layer styles into the fingerprint on every request.
- A tile renders the whole map, so a write to one layer still invalidates that
  map's tiles, not only the affected layer's. That is the pre-existing
  composition granularity, unchanged by this ADR; per-layer composition is a
  separate, measured decision. **That decision is now made: ADR-0117 caches
  per-layer tiles and composites them at serve time.** Measured on a
  five-layer city basemap, a single-layer edit invalidated all 25 warm tiles
  of a one-viewport working set while one tile's pixels changed, and per-layer
  composition repays itself from about one to three tiles served per edit
  (ADR-0117, `eng/spike-u2x-tile-cache/RESULTS.md`).
- A store that reports no version (PostGIS, SQL Server, demo, ArcGIS REST)
  keeps its current behaviour, which means a write to a durable store from
  *this* process also still leaves its tiles stale until a flush. **That gap is
  now closed for the two durable providers: ADR-0129 gives the PostGIS and SQL
  Server stores a content version that is a row in the database, bumped in the
  write's own transaction, so a write invalidates the affected tiles on every
  host reading that database.** The demo and ArcGIS REST stores are still
  unversioned, correctly — nothing writes to them through the engine.
- The key is computed per request, so each tile request makes one extra cheap
  call per distinct layer. It is a dictionary read for the in-memory store and
  a constant for every store that does not report a version.
- `MemoryCatalogue` now snapshots one extra dictionary per open transaction, so
  a rollback restores content versions alongside the data.
- `OgcVectorTileService` takes an `IStoreRegistry` alongside its encoder, cache
  and schemes, because its key needs the same fold; its `Version(Map)` helper is
  unchanged, so an OGC tile and a neutral tile still key differently.

## Alternatives

- **Flush the cache on every write.** Correct and trivial, but global: it
  throws away every unrelated tile on every edit and still misses a write from
  another process. Rejected as the mechanism, kept as the manual escape hatch.
- **Hash the dataset content on every tile request.** Correct across processes,
  and far too expensive: a tile would read every feature it draws. Rejected.
- **A version per map or per store rather than per dataset.** Cheaper to
  resolve, but one edited dataset invalidates the tiles of every dataset beside
  it — the same over-invalidation the store key was meant to avoid. Rejected.
- **An in-process counter in the host, bumped by the write endpoints.** Would
  miss every write that does not travel through an HTTP route (ingest
  pipelines, seed, in-process callers) and would need a hook in every mutating
  endpoint. Putting the version on the store keeps the bump next to the
  mutation, in the one place that already owns it.

## See also

ADR-0046 (tile cache key), ADR-0070 (MVT over the same cache), ADR-0037
(feature editing), ADR-0042 (in-memory store).
