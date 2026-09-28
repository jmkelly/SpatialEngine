# @spatial/client — TypeScript SDK

The TypeScript SDK for the Spatial Engine typed host API (ADR-0033).
Talked to by the browser workbench; browser and Node compatible,
zero runtime dependencies.

## Layout

- `src/generated-types.ts` — **generated** wire types, emitted from the host's
  OpenAPI description by `scripts/generate.mjs` (do not edit by hand).
- `src/feature-batch.ts` — the canonical "SFBAT" v1 decoder (ADR-0020),
  byte-for-byte aligned with the .NET `FeatureBatchCodec` (a .NET-produced
  reference vector is pinned in the tests).
- `src/client.ts` — the fetch-based `SpatialClient`: one method per typed
  route (geometry, transforms, catalogue, features, transactions, demo
  sleep, health). Geometries cross as Base64 canonical SGEOM bytes;
  feature batches as Base64 canonical SFBAT bytes.
- `test/` — `node:test` suites; `test/e2e.test.ts` and
  `test/geoservices-e2e.test.ts` run against a live host (set
  `SPATIAL_HOST_URL`), driven by `eng/e2e-web.sh`. The latter is the
  real-client proof: it drives the GeoServices boundary with the official
  Esri `@esri/arcgis-rest-feature-service` / `arcgis-rest-request` libraries.

## Authentication

Phase 1 local login uses the host's opaque bearer flow (ADR-0071):

```ts
const client = new SpatialClient("http://localhost:5201");
await client.login("alice", process.env.SPATIAL_PASSWORD!);
const identity = await client.me();
// putMap/deleteMap/ingest automatically use the in-memory bearer.
await client.logout();
```

`setToken()` is available for applications that manage the token themselves.
The browser workbench only persists the token when the user opts in.

## Resumable uploads (ADR-0090)

A document too large for one request is staged, resumed and then loaded:

```ts
await client.ingestResumable(
  new Blob([bytes]),
  { dataset: "public.parks", srid: 4326, chunkSize: 8 * 1024 * 1024 },
  adminToken,
);
```

`ingestResumable` asks the host how many bytes it holds, appends from there,
retries a chunk that failed in transit from the offset the host reports, and
ingests only once every byte has landed. The staging verbs
(`startUpload`/`getUpload`/`appendUpload`/`deleteUpload`/`ingestUpload`) are
there when a caller wants to drive the protocol itself.

## Commands (node >= 22.6)

```bash
npm ci
npm test                # typecheck + generated-types drift check + unit tests
npm run generate        # regenerate src/generated-types.ts from the snapshot
npm run test:e2e        # against a running host (SPATIAL_HOST_URL)
```
