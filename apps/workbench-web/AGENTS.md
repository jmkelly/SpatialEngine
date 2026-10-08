# workbench-web

The browser workbench: React + TypeScript + MapLibre against the independently
executable host. It is a **delivered client**, not a second engine: it talks to
the host only through the generated TypeScript SDK, and the host serves the
built bundle (ADR-0014; the desktop shell was abandoned in ADR-0039). Route by
task: `architecture/principles.md`.

## Never

- No second channel to the host: `src/api.ts` over `@spatial/client`, and no
  `fetch` of a host path from a component.
- No spatial algorithm here. The two geometry files (`src/sgeom.ts`,
  `src/map-geometry.ts`) are wire adapters, not computation.
- No hand-edit of the generated wire types; regenerate, and keep the snapshot
  honest — `eng/e2e-web.sh` rewrites it from a live host.
- No capability the layer does not advertise: `hasZ`, `hasM` and
  `supportsQuantization` are what the UI may offer, and a parameter the host
  refuses by name is a UI bug, not a server fallback to write.

## Commands

- `npm test` — typecheck, then `node --test test/*.test.ts`.
- `npm run build` — `tsc --noEmit` then `vite build`.
- `eng/workbench-e2e.sh` — real host + this client.

