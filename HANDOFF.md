# Handoff — Spatial Engine

> Written for the next agent taking over. Read `AGENTS.md`, then
> `architecture/decisions/ADR-0033-in-process-interfaces.md`. **The worker
> plugin model is retired** — services are in-process interfaces composed by
> DI; ADRs 0002/0003/0006–0008/0013/0022–0028/0030/0031 are superseded
> history. Phase 11 (Tauri 2 Desktop Packaging) is next.

## Repository state

- **Branch:** main. The quality-gate queue files (if any) are gitignored —
  do not commit them.
- **Model:** `Spatial.Core` (values, zero deps) → `Spatial.PluginSdk`
  (interfaces: `IGeometryOperations`, `ICrsDirectory`,
  `ICoordinateTransforms`, `IDataCatalogue`, `IFeatureStore`,
  `ITransactionStore`, `IDemoJobs`, `SpatialException`, DTOs, HTTP shapes)
  → implementations (`NtsGeometryOperations`, `ProjNetTransforms`,
  `DemoStore`, `PostgisStore`) → `Spatial.Host` (typed routes, keyed DI
  `"demo"`/`"postgis"`, `PostgisOptions`).
- **Tests:** `eng/verify.sh` is the gate. Counts (post-refactor):
  Core 308, NTS 28, ProjNet 61, Demo 15, PostGIS 130 unit, Client 9,
  Host 14, Architecture 8, PostGIS integration 16 (skip without Docker).
  TS SDK 7+1 skip; workbench unit 22; Playwright 5.
- **Web:** `eng/e2e-web.sh` (real host + TS SDK) and
  `eng/workbench-e2e.sh` (real host + built app + Playwright chromium, 5
  specs) are green.

## Hard-won gotchas (read before touching this code)

- **STJ cannot bind the core structs.** `FeatureSchema` (ctor param
  `fields` vs property `Fields`) and `FieldDefinition` (readonly struct)
  silently deserialize to defaults. The explicit
  `FeatureSchemaConverter`/`FieldDefinitionConverter` in
  `Spatial.PluginSdk.Http` are load-bearing — do not remove them.
- **NaN never survives JSON.** `JSON.stringify(NaN)` is `null` (TS side);
  the .NET `HostApiJson` allows named floating-point literals, so the .NET
  client can send NaN but browsers cannot. Invalid-number tests should use
  `quadrantSegments: 0` or bad Base64 instead.
- **`DatasetDescription.Schema` must stay concrete `FeatureSchema`.**
  STJ cannot deserialize `IFeatureSchema`; the interface-typed version
  broke the catalogue round trip.
- **MapLibre must not be queried via readPixels in headless CI.**
  Selection stays coordinate-based (`click-match.ts`); the suite fires
  `map.fire("click", …)` at lng/lat `(0,0)`.
- **MapLibre's worker file must be pinned**: `public/maplibre-gl-worker.mjs`
  + `setWorkerUrl("/…")` in `MapScreen.tsx`.
- **`Spatial:WebRoot` static middleware must run BEFORE `app.UseRouting()`**.
- **`dotnet run` orphans its apphost**: `eng/*e2e.sh` kill both the wrapper
  and `Spatial.Host --urls` and refuse a busy port.
- **React effects capture stale state**: the map's mount-once effect calls
  the CURRENT `selectFeature` via `actionsRef`.
- **`npm ci` in the workbench re-creates the SDK `file:` dep** — regenerate
  with `npm install` when the SDK changes and the lock drifts.
- **Style/quality traps still apply** (CA1859/CA1826/CA1068/CA1305/CA1861,
  `Results<…>` typed results, format before commit).

## Next up — Phase 11: Tauri 2 Desktop Packaging

From the plan (§16 Phase 11, Milestone 2): thin Tauri shell packaging the
**unchanged** workbench `dist`, optional `Spatial.Host` sidecar supervision,
remote-host mode, a narrow desktop file adapter, Windows-first tests.

1. Build with `npm run build` in `apps/workbench-web` and ship `dist/` as
   `Spatial:WebRoot` static content. The host's `GET /` serves the app when
   WebRoot is set — the desktop window just points at it.
2. Host sidecar: `Spatial.Host` is framework-dependent; ship the runtime or
   use `dotnet publish` framework-dependent + `--urls http://127.0.0.1:<port>`.
   Remote-host mode = `VITE_SPATIAL_HOST_URL` pointing at a remote host URL.
3. PostGIS in the sidecar needs `Spatial:Postgis:ConnectionString` (or
   `SPATIAL_POSTGIS_CONNECTION`); the demo store needs nothing.
4. PostGIS container tests still skip without Docker — the workbench's real
   dataset browser runs against the demo store in CI.
5. ADR-0016/0017/0019 rules stand: the shell contains no spatial logic, no
   `@tauri-apps/*` deps may enter `apps/workbench-web` (architecture test
   scans every `package.json` under apps/ and clients/).
