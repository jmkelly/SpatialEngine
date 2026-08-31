# @spatial/client — TypeScript SDK

The TypeScript SDK for the Spatial Engine public host API (plan §12, Epic H).
Talked to by the browser workbench (Phase 10); browser and Node compatible,
zero runtime dependencies, no Tauri.

## Layout

- `src/wire.ts` — the shared inline value codec (mirror of the .NET
  `Spatial.PluginSdk.Codec.ValueCodec`): scalars, `$i64`, `$bytes`,
  `$geometry`, `$crs`, `$resource`.
- `src/generated-types.ts` — **generated** wire types, emitted from the host's
  OpenAPI description by `scripts/generate.mjs` (do not edit by hand).
- `src/feature-batch.ts` — the canonical "SFBAT" v1 decoder (ADR-0020),
  byte-for-byte aligned with the .NET `FeatureBatchCodec` (a .NET-produced
  reference vector is pinned in the tests).
- `src/client.ts` — the fetch-based `SpatialClient`: capabilities,
  invocations, jobs (poll/cancel/events/wait), resources and streams
  (line-delimited JSON of codec-encoded items), plugins.
- `test/` — `node:test` suites; `test/e2e.test.ts` runs against a live host
  (set `SPATIAL_HOST_URL`), driven by `eng/e2e-web.sh`.

## Commands (node >= 22.6)

```bash
npm ci
npm test                # typecheck + generated-types drift check + unit tests
npm run generate        # regenerate src/generated-types.ts from the snapshot
npm run test:e2e        # against a running host (SPATIAL_HOST_URL)
```

## Generation

`scripts/generate.mjs` emits `src/generated-types.ts` from
`scripts/openapi.snapshot.json` (a committed capture of the host's
`/openapi/v1.json`). `npm test` regenerates into a temp file and fails on
drift; `eng/e2e-web.sh` refreshes the snapshot from a live host. This is the
"generate and test the TypeScript SDK" loop of plan §12.