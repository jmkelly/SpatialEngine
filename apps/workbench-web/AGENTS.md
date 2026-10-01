# workbench-web

The browser workbench: React + TypeScript + MapLibre against the independently
executable host. It is a **delivered client**, not a second engine: it talks to
the host only through the generated TypeScript SDK, and the host serves the
built bundle (ADR-0014; the desktop shell was abandoned in ADR-0039).

## Rules

- One channel to the host: `src/api.ts` over `@spatial/client`. No fetch of a
  host path from a component, and no spatial algorithm here — the two geometry
  files (`src/sgeom.ts`, `src/map-geometry.ts`) are wire adapters, not
  computation.
- The wire types are generated. Regenerate rather than hand-edit, and keep the
  snapshot honest: `eng/e2e-web.sh` rewrites it from a live host.
- A capability a layer advertises (`hasZ`, `hasM`, `supportsQuantization`) is
  what the UI may offer; a parameter the host refuses by name is a UI bug, not
  a server fallback to write.

## Commands

- `npm test` — typecheck, then `node --test test/*.test.ts`.
- `npm run build` — `tsc --noEmit` then `vite build`.
- `eng/workbench-e2e.sh` — real host + this client.

<!-- orientation:begin -->

## Orientation

One line per closed bead: where the first hour went (`SpatialEngine-rzq`).

- Regenerate the SDK types instead of editing them:
  `clients/typescript/src/generated-types.ts` comes from the OpenAPI snapshot,
  and CI's e2e job rewrites that snapshot from the live host and fails on any
  diff. (SpatialEngine-tte)
- Send the plan under `plan` to `/api/features/query`; `bbox` and `filter` are
  sugar for the plan's `boundingBox` and `where`, and a request that sends both
  spellings of one member must have them agree. (SpatialEngine-gd2)
- Offer no `identity=none` on ingest: every ingested dataset is keyed (`Auto`
  or `Source`) and the host refuses the value by name, with a message naming
  what to use instead. (SpatialEngine-2cm)
- Drive the extra ordinates off `hasZ`/`hasM` from the layer resource, never
  off an array's length: the flag is named, not positional, so a remote that
  omits it must still keep its elevation. (SpatialEngine-fhf)
<!-- orientation:end -->