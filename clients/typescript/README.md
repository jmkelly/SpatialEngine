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

## Commands (node >= 22.6)

```bash
npm ci
npm test                # typecheck + generated-types drift check + unit tests
npm run generate        # regenerate src/generated-types.ts from the snapshot
npm run test:e2e        # against a running host (SPATIAL_HOST_URL)
```
