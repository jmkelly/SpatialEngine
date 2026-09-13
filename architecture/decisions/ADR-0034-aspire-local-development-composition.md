---
status: accepted
date: 2026-09-13
deciders: maintainer + agent
---

# ADR-0034: Aspire AppHost for local development composition

## Context

The plan's local development profile (§2.5) expects a .NET Aspire
composition: PostGIS container, `Spatial.Host`, the React development
server and the browser, in one launch. Today that profile is manual — a
developer runs a PostGIS container, exports `SPATIAL_POSTGIS_CONNECTION`,
starts `Spatial.Host` on a chosen port and starts the Vite dev server with
`VITE_DEV_HOST` pointed at it. Each step has its own default, so the wiring
drifts and is easy to get wrong.

The browser/server profile (host serving built assets) and the desktop
profile (Tauri) do not include Aspire; this is strictly a development-time
concern. The engine must stay free of orchestration code (ADR-0018 stands:
the host is independently executable).

## Decision

Add `src/Spatial.AppHost`, a .NET Aspire AppHost, as the local development
composition root:

- **Aspire 13.5.3** (`Aspire.AppHost.Sdk`, pinned in `global.json`
  `msbuild-sdks`; packages `Aspire.Hosting.AppHost`,
  `Aspire.Hosting.PostgreSQL`, `Aspire.Hosting.JavaScript` pinned in
  `Directory.Packages.props`). `AspireUseCliBundle=true` keeps the build
  warning-free; the app is launched with `dotnet run`.
- **PostGIS container** `postgis/postgis:16-3.4` — the same image the
  integration-test fixture uses — exposing a `spatial` database pinned as the
  container's bootstrap database (`POSTGRES_DB=spatial`). The image's init
  scripts load the `postgis` extension into `POSTGRES_DB` *before* the server
  accepts connections, so the extension is present when the host first
  connects; creating `spatial` through Aspire's `AddDatabase` instead would
  run after the server is ready and leave the host briefly pointing at a
  database without a `geometry` type. The integration fixture reaches the same
  state with an explicit `CREATE EXTENSION postgis`.
- **`Spatial.Host`** linked by `ProjectReference` and launched by Aspire
  with `SPATIAL_POSTGIS_CONNECTION` injected from the database resource
  (the setting `PostgisOptions` already reads; secrets never live in the
  repository).
- **Vite dev server** for `apps/workbench-web` via `AddViteApp`, with
  `VITE_DEV_HOST` set to the host's assigned endpoint so the existing
  `vite.config.ts` proxy forwards `/api`, `/health` and `/openapi`.

Boundaries:

- The AppHost references only `Spatial.Host`; it links no engine
  implementation, core or SDK project directly. It is never referenced by
  the engine or shipped in any non-development profile.
- Aspire hosting packages are allowlisted for `Spatial.AppHost` only, in the
  same architecture guard that allowlists `Npgsql`, `NetTopologySuite`,
  `ProjNET` and `Microsoft.AspNetCore.OpenApi`.
- The AppHost owns no spatial logic and no contracts; it is a composition of
  existing typed services.

## Consequences

- One command (`dotnet run --project src/Spatial.AppHost`) brings up
  PostGIS, the host and the workbench, with a dashboard for logs and
  endpoints. Docker and Node ≥ 22.6 are prerequisites for this profile.
- New project added to the solution and the architecture guard; a package
  allowlist entry is added with this ADR (never silently).
- The independently executable host is unchanged: `Spatial.Host` still runs
  with no Aspire and no desktop shell.
- Aspire is a development dependency. The browser/server and desktop
  profiles continue to use the host directly.
- Remote development over a private network (e.g. Tailscale) is opt-in
  via `SPATIAL_DEV_BIND` (usually `0.0.0.0`): the host and workbench
  endpoints go proxy-less on pinned ports (`:5199`, `:5273`) so the
  services are reachable directly at `http://<tailnet-host>:<port>` —
  the dashboard advertises loopback DCP-proxy URLs that a remote browser
  cannot use. `VITE_ALLOWED_HOSTS` allow-lists tailnet hostnames for the
  Vite dev server; the dashboard frontend itself is published with
  `ASPNETCORE_URLS=https://<tailnet-host>:18888`. Unset, everything binds
  loopback with Aspire-assigned ports as before.
- The composition pins `NODE_ENV=development` for the Vite dev server:
  Aspire launches JS apps with `NODE_ENV=production`, under which
  plugin-react omits the Fast Refresh preamble and the workbench loads
  blank (`$RefreshSig$ is not defined`).
