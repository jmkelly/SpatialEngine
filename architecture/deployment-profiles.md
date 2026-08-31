# Deployment Profiles

Read when packaging, running or testing a delivery target. See
implementation-plan.md §2.5.

## Browser/server

React web assets · ASP.NET Core Spatial.Host · external PostGIS ·
out-of-process plugin workers.

## Local development

.NET Aspire composition · PostGIS container · Spatial.Host · React dev
server · plugin worker processes · browser.

## Desktop

Tauri 2 package · React production assets · self-contained Spatial.Host
sidecar **or** configured remote host · selected built-in plugins · optional
remote providers.

## Invariants

- One public API, contract set, TypeScript SDK and React application in every
  profile.
- The host is independently executable (ADR-0018); Tauri is optional
  packaging (ADR-0015/0016/0019).
- Every profile runs the same conformance tests.
- Browser tests (Playwright) never require Tauri; desktop tests verify
  packaging and lifecycle, not spatial behaviour.

## Running the host (Phase 9)

`Spatial.Host` is an ASP.NET Core minimal API (JIT-compiled, ADR-0012).
Start it with the packages it should activate:

```bash
Spatial__PackagesRoot=/path/to/plugin/packages dotnet run --project src/Spatial.Host
```

- `Spatial:PackagesRoot` — directory of immutable plugin packages
  (`eng/tools/PluginPacker` assembles them); empty = no plugins.
- `Spatial:Preferences` — configured provider preferences (resolution step 3).
- `Spatial:WorkerEnvironment` — host-managed worker launch environment
  (provider secrets, ADR-0028).

The web E2E (`eng/e2e-web.sh`) packs the NTS worker, runs the real host
process and drives it from the TypeScript SDK — the Phase 9 "host runs
independently" proof. Client SDKs: `clients/dotnet/Spatial.Client` (.NET)
and `clients/typescript` (`@spatial/client`); the API contract is documented
in `architecture/host-api.md` (ADR-0030).