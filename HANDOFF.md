# Handoff — Spatial Engine

> Written for the next agent taking over. Read `AGENTS.md`, then
> `architecture/implementation-plan.md` (the source of truth). **Phase 10
> (Browser Workbench) is complete** — Milestone 1 done; Phase 11 (Tauri 2
> Desktop Packaging) is next.

Current state, repository layout and contracts live in `README.md` and
`architecture/distilled/`; phase-by-phase history lives in git. This file
keeps only the hard-won gotchas and the next steps.

## Hard-won gotchas (read before touching this code)

- **JSON number width is a wire bug factory.** `ValueCodec` decodes JSON
  integral numbers as **int32** (int first), so contract args declared
  int64 (`TryGetArgument<long>`) failed over HTTP/worker wire and providers
  silently fell back to defaults — the fixture sleep tests raced a silent
  300 ms default and produced random "job completed instead of cancelled"
  flakes under parallel test load. Fixed in
  `CapabilityInvocation.TryCast` (int→long widening, long→int range-checked
  narrowing, `ArgumentCastTests`). **When a provider reads a JSON number
  argument, remember int and long are distinct wire types; the TS SDK sends
  `{$i64: "…"}` for int64.** Do not regress the cast widening.
- **MapLibre must not be queried via readPixels in headless CI.** Under
  `--disable-gpu` (or after a few WebGL contexts in one Chromium), feature
  queries can stall/return empty for seconds and clicks "miss". The
  workbench selects by click coordinates (deterministic); the Playwright
  suite fires `map.fire("click", {point, lngLat, …})` at lng/lat `(0,0)`
  instead of synthesizing mouse bytes. The `window.__spatialMap` test seam
  is intentional (documented in `MapScreen.tsx`).
- **MapLibre's worker file must be pinned**: the bundle computes
  `maplibre-gl-worker.mjs` relative to itself, which a static SPA build
  never emits — `public/maplibre-gl-worker.mjs` + `setWorkerUrl("/…")` in
  `MapScreen.tsx`. Without it the map never finishes loading (`loaded()`
  stays false forever, `queryRenderedFeatures` returns nothing).
- **Polygon rings / multi-parts are nested geometry nodes** in SGEOM
  (layout/type/CRS byte then body), not flat data — the first decoder draft
  got this wrong and the real buffered polygon "truncated" at byte 561.
- **`Spatial:WebRoot` static middleware must run BEFORE `app.UseRouting()`**
  — ASP.NET's static-file middleware refuses a path routing already matched
  (the `/` identity endpoint shadows `index.html` otherwise).
- **`dotnet run` orphans its apphost**: killing the `dotnet run` wrapper
  leaves `Spatial.Host --urls …` (the actual server) alive on the port —
  stale hosts answer later runs with old plugin routing. `eng/workbench-e2e.sh`
  kills both (`pkill -f 'Spatial.Host --urls'`) and refuses a busy port.
- **React effects capture stale state**: the map's mount-once effect must
  call the CURRENT `selectFeature` (via `actionsRef`), not the initial
  closure — otherwise clicks look up the empty initial dataset forever.
- **Zombie e2e knowledge**: `npm ci` in the workbench re-creates the SDK
  `file:` dep — regenerate with `npm install` when the SDK changes and the
  lock drifts. The workbench lockfiles and `public/` worker files are
  committed.
- **Style/quality traps still apply** (CA1859/CA1826/CA1068, LoggerMessage
  delegates, `Results<…>` typed results, format before commit).

## Next up — Phase 11: Tauri 2 Desktop Packaging

From the plan (§16 Phase 11, Milestone 2): thin Tauri shell packaging the
**unchanged** workbench `dist`, optional `Spatial.Host` sidecar supervision,
remote-host mode, a narrow desktop file adapter, Windows-first tests.

1. **The Phase 10 workbench is the exact asset Tauri will package**: build
   with `npm run build` in `apps/workbench-web` and ship `dist/` as
   `Spatial:WebRoot` static content (ADR-0031 already describes that
   contract). The host's `GET /` serves the app when WebRoot is set — the
   desktop window just points at it.
2. **Host sidecar**: `Spatial.Host` is a framework-dependent app; ship the
   runtime or use `dotnet publish` framework-dependent + `--urls
   http://127.0.0.1:<port>`. Remote-host mode = point the workbench at a
   `VITE_SPATIAL_HOST_URL`/runtime-config host URL (the SDK takes a base
   URL; the workbench reads `import.meta.env.VITE_SPATIAL_HOST_URL`).
3. **The plugin packages are immutable directories** under
   `Spatial:PackagesRoot` — the sidecar needs `nts@1/nts@2/demo@1`
   (`eng/tools/PluginPacker`) plus optionally `postgis@1` with
   `SPATIAL_POSTGIS_CONNECTION` in `Spatial:WorkerEnvironment`.
4. **PostGIS container tests still skip without Docker** — the workbench's
   real dataset browser runs against `demo@1` in CI; the PostGIS-backed
   flow is ready but needs a live database (see `PostgisIntegrationTests`).
5. **ADR-0016/0017/0019 rules stand**: the shell contains no spatial logic,
   no `@tauri-apps/*` deps may enter `apps/workbench-web` (architecture
   test scans every `package.json` under apps/ and clients/).
