---
status: accepted
date: 2026-09-14
deciders: maintainer + agent
---

# ADR-0059: Map/Image offline and async surface is rejected by name, not served

## Context

The compatibility reviews leave one Map/Image block unscored
(`research/compat/map-service.md` §1, rows T-F; `research/compat/tiles.md`
§1–3, rows T-M/T-K):

1. `exportTiles` + `estimateExportTileSize` (S4, map **and** image
   variants): offline `.tpk`/`.vtpk` packaging. Our tiles are
   live-rendered per scheme only; no packaging route exists, and T-040
   records the honesty flag (`exportTilesAllowed:false`, ADR-0058).
2. The WMTS triple (S4 `wmts-*-map-service/`, OGC 07-057r7): base,
   `WMTSCapabilities.xml`, tile. Zero WMTS routes exist in tree.
3. KML (`generateKml`, `kml-image`, S4): no KML surface anywhere in tree.
4. Async jobs (`.../MapServer/jobs`, one job, its results and inputs, S4):
   no job model exists by architecture.

The standing constraint is ADR-0033: capabilities are in-process service
interfaces resolved by DI; long-running work is a cancellable `Task`,
and the jobs/resources/streams infrastructure was deliberately deleted
(ADRs 0008/0024 stay superseded). Serving any of the four items honestly
would mean building a packaging pipeline **and** an observable job model —
a new architecture, not a new route. The repo's standing rule (T-024
audit) is loud errors over silent narrowing, and the T-015 precedent is
reject-by-name (`EsriFeatureQuery.RejectUnsupported`,
`EsriEditRequest.RejectUnsupported`).

T-048 (tiles parity: LOD byte-proof vs G1, live-tile work, vector-tiles
decision) is not started. This ADR sets the scope split it builds on, so
T-048 never re-decides what is closed here.

## Decision

**All four items are documented non-goals, each rejected by name with a
typed `invalid.arguments` (HTTP 400) Esri envelope that points at the live
alternative. No packaging route, no WMTS serving, no KML surface and no job
model are built — closing this with a job model is explicitly out of scope.**

1. **`exportTiles` / `estimateExportTileSize`, map + image variants.**
   `.../MapServer/exportTiles`, `.../MapServer/estimateExportTileSize`,
   `.../ImageServer/exportTiles`,
   `.../ImageServer/estimateExportTileSize` resolve their service (an
   unknown service stays a typed `not.found`) and then reject: packaging
   needs a job model the host does not have, and there is no package to
   size. The root keeps advertising `exportTilesAllowed:false` (ADR-0058);
   the live alternatives are `tile/{z}/{y}/{x}` (per tile) and
   `export` / `exportImage` (per image).
2. **WMTS.** `.../MapServer/WMTS` and `.../MapServer/WMTS/{*rest}` (which
   covers `WMTS/1.0.0/WMTSCapabilities.xml` and the WMTS tile remainder)
   reject: no WMTS endpoint is served; live tiles come from
   `tile/{z}/{y}/{x}`. WMTS serving stays out of scope — see the T-048
   split below.
3. **KML.** `.../MapServer/generateKml` and `.../MapServer/kml/{*rest}`
   (which covers `kml/mapImage.kmz`) reject: no KML surface is served
   anywhere.
4. **Async jobs.** `.../MapServer/jobs` and `.../MapServer/jobs/{*rest}`
   (one job, its results, its inputs) reject: long-running work runs as
   cancellable tasks, not observable jobs — there is nothing to poll
   (ADR-0033). Clients await the synchronous `export` / tile response.

Every handler checks the `CancellationToken` before resolving, so
cancellation still maps to the 499 envelope. The implementation is one
adapter file (`MapOfflineRejects.cs`); Core, the SDK, the providers and
the neutral tile routes are untouched, and no package is added.

## Scope split with T-048 (tiles parity)

T-048 builds on this scope instead of re-deciding it:

- **Closed here (T-048 does not reopen):** tile export/packaging,
  `estimateExportTileSize`, WMTS serving, the async job model. If measured
  client demand ever reopens any of them, that is a new task with its own
  ADR — it must reconcile with ADR-0033 first.
- **Owned by T-048:** LOD byte-proof of the live `tile/{z}/{y}/{x}`
  against the G1 cached grid (envelope-equality, `tileInfo` replay),
  whatever live-tile parity that proof demands, and the vector-tiles
  (`.vtpk`/MVT) decision, which is **not** pre-decided here: vector tiles
  are a render-format question, while this ADR closes only the
  packaging/WMTS/jobs questions.

## Consequences

- An offline-capable client (ArcGIS Earth, Explorer, a `.tpk` workflow)
  gets an actionable 400 naming the missing operation and the live
  alternative, instead of a framework 404 with no Esri envelope — and the
  root's `exportTilesAllowed:false` already told it not to ask.
- The WMTS/KML/async probes of a generic capabilities crawler fail loudly
  per operation, so conformance tooling records honest non-goals rather
  than ambiguous absences.
- T-048 starts from a closed scope: live tiles + `tileInfo` honesty only.

## Alternatives

- **Implement packaging + a job model to close this:** would serve real
  `.tpk` downloads, but contradicts ADR-0033 (jobs deleted on purpose) and
  invents a packaging pipeline with no requesting client trace. Rejected;
  the task body forbids it explicitly.
- **Serve a static WMTS capabilities document over the live scheme:**
  cheap to write, but a capabilities document advertises a tile-access
  contract (KVP + RESTful + dimensions) the host does not implement, so
  conformant WMTS clients would break on first tile. Rejected; a reject
  that names the live tile route is more honest than a half-served
  standard.
- **Leave the routes unmounted (framework 404):** zero code, but the 404
  carries no Esri envelope and names nothing — indistinguishable from a
  typo'd URL. Rejected; the T-024 rule is loud errors over silent
  narrowing.
