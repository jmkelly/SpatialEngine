# Handoff — Spatial Engine

> Written for the next agent taking over. Read `AGENTS.md`, then
> `architecture/implementation-plan.md` (the source of truth). **Phase 10
> (Browser Workbench) is complete** — Milestone 1 done; Phase 11 (Tauri 2
> Desktop Packaging) is next.

## Repository state

- **Branch:** `main`. Working tree clean at handoff. The quality-gate queue
  files (`crap-queue.md`, `coverage-queue.md`, `metrics-queue.md`,
  `warnings-queue.md`, `*.report.json`, `coverage-history.csv`,
  `stryker-queue.md`) are gitignored and report the ALL-GREEN state — do
  not commit them.
- **Phase 10 commits** (in order):
  - `da26c8b` — host plugin-control API (`route-new-work`/`drain`/`rollback`
    on `/api/plugins/{id}`), static workbench serving (`Spatial:WebRoot`),
    `nts@2` provider variant, `demo@1` data provider, PluginPacker packages
    all four, .NET + TS SDK control methods, integration tests
    (`PluginReplacementTests`, `WorkbenchHostingTests`), ADR-0031,
    host-api.md update, OpenAPI snapshot refresh.
  - *(second commit, after the quality gates)* — the workbench itself
    (`apps/workbench-web`), the Playwright suite + `eng/workbench-e2e.sh`,
    and a real wire fix: JSON integral numbers now read as the declared
    int64 contract arguments (`CapabilityInvocation` int↔long widening —
    see gotchas below — with `ArgumentCastTests`), which also stabilized
    the Phase 9 job tests under parallel load.
  - *(third commit, the metrics-gate follow-up pass)* — ADR-0032: the
    geometry value model gains its contract faces (`IPoint`, `ILineString`,
    `IPolygon`, `IMultiPoint`, `IMultiLineString`, `IMultiPolygon`,
    `IGeometryParts`, `IGeometryFactory`) and `GeometryCodec` +
    `CanonicalFormatException` move to `Spatial.Core.Geometry.Codec` — the
    demo provider had pushed `Spatial.Core.Geometry` to Ca 9 / abstractness
    0.09 and re-triggered `architectural-rigidity`; the faces carry the hub
    (abstractness 0.31), the codec namespace is a leaf. Also the CRAP pass
    on `CapabilityInvocation.TryCast` (behavior-preserving extraction into
    `TryNumericCast`/`NumericConvert`, `ArgumentCastTests` still pin both
    directions) and the demo `DemoRunner` split into `DemoCatalogueHandler`/
    `DemoFeatureHandler` (+ `LikePattern`, catalogue pattern filtering
    tests). Docs updated together: `geometry-model.md`, `core-boundary.md`,
    `src/Spatial.Core/AGENTS.md`, plan ADR index.
- **Tests:** `eng/verify.sh` passes from a clean checkout, run six times in
  a row all-green (the load flake it used to show is fixed — see gotchas).
  Counts: Core 308, Runtime 207 (+6 arg-cast), Client 11, PluginHost 85,
  Provider.Demo 34 (new, +9 catalogue-pattern/feature-batch tests in the
  ADR-0032 pass), Provider.PostGIS 175 unit + 17 skipped
  container, Conformance 13 (+nts v2 matrix), Host 52 (+10 from Phase 10:
  7 replacement + 3 hosting), NTS 31, ProjNet 65, Architecture 8.
- **Web:** `apps/workbench-web` unit tests 22/22 pass and the build is
  clean; `clients/typescript` 14/14; `eng/workbench-e2e.sh` runs the real
  host + built app + Playwright chromium — 6/6 specs, five consecutive
  green runs.

## Completed — Phase 10 (Epic H: browser workbench + replacement demo)

- **The workbench** (`apps/workbench-web`, ADR-0031): React 19 + Vite +
  MapLibre GL over the Phase 9 TypeScript SDK, four screens — provider/
  capability catalogue, dataset map with coordinate-based selection and
  attribute inspection, generated capability forms with job progress and
  result preview/persistence (browser localStorage), runtime health with
  plugin replacement controls. The host serves the built app from
  `Spatial:WebRoot` (same origin, no CORS, no Tauri).
- **Geometry adapter** (`src/sgeom.ts`): the SDK's raw canonical SGEOM
  bytes are decoded to GeoJSON in-browser, byte-for-byte mirroring the
  .NET `GeometryCodec` (header, nested nodes, CRS skip, little-endian
  doubles) — pinned by hand-built vectors AND the real .NET buffered-circle
  payload.
- **Selection is coordinate-based** (`src/click-match.ts`): click lng/lat →
  nearest feature by projection math; deliberately NOT pixel-query
  (`queryRenderedFeatures`/readPixels is fragile in software-rendered
  headless browsers — see gotchas).
- **Plugin replacement over HTTP** (`PluginReplacementTests`): both NTS
  versions activate side by side, `route-new-work` switches provenance to
  `nts@2` (resolution step `ActivePreferred`), `drain` stops `nts@1`
  without stopping the host, `rollback` reactivates it.
- **`demo@1`** (`Spatial.Provider.Demo`): read-only data-provider
  contracts over procedural datasets (110-point grid + 8 cities, EPSG:4326
  SGEOM), bbox queries, plus a long-running `spatial.demo.sleep@1` with
  progress — the Docker-free catalogue/map/progress vehicle. **The demo
  provider is NOT in the conformance matrix** (only its unit suite + the
  host e2e exercise it); adding it to `PostgisProviderConformance`-style
  fixtures would be a reasonable Phase 12+ add.
- **Playwright** (`tests/end-to-end-web` + `eng/workbench-e2e.sh`): six
  specs against the real host serving the real app — no Tauri, no Docker.

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

## Quality gates (Phase 10)

Re-verified after the ADR-0032 follow-up pass: all four gates green
(CRAP 0/2568, branches 82.3%, metrics 0 findings, warnings 0),
`eng/verify.sh` exit 0, workbench unit tests 22/22, `eng/workbench-e2e.sh`
6/6.

- **CRAP**: 0 of ≥2500 methods ≥ 10 (loop-verified).
- **Coverage**: authored branch ≥ 70% (loop-verified).
- **Metrics**: 0 findings (the new host endpoints stayed fan-out-small;
  `DemoRunner` split validation/emitters and then dispatch/handlers; Core
  fan-in carried by the ADR-0032 faces — abstractness 0.31).
- **Warnings**: 0.
- **Stryker (my call — SKIPPED)**: Phase 10 changed no `Spatial.Core`
  behavior (the audited project — every mutation run pins
  `configured[0]`), so the ~11-minute full run was skipped per the
  heuristic for non-Core phases. The ADR-0032 follow-up pass was reviewed
  with the same lens and also skipped: its `Spatial.Core` changes are
  structural only (additive interfaces with no bodies, a namespace move of
  unchanged codec code), and the `CapabilityInvocation`/demo changes are
  behavior-preserving refactors pinned by existing tests — no new mutation
  surface. The final repo-wide pass should still add the Phase 9/10
  assemblies (`Spatial.PluginSdk`, `Spatial.Host`, `Spatial.Client`,
  `Spatial.Provider.Demo`) to the Stryker matrix. Last full run on record:
  **94.44%** (stryker-queue.md of 30 Aug).

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