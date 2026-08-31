# Clients

The public spatial engine client SDKs — the browser/web and automation
surfaces of the independently executable `Spatial.Host` (plan §12, Epic H).

- `typescript/` — `@spatial/client`: the TypeScript SDK for React and other
  web clients (Milestone 1 workbench). Fetch-based (browser and Node
  compatible), zero runtime dependencies, wire types generated from the
  host's OpenAPI description. The workbench depends on this — never on Tauri
  (frontend-boundary.md).
- `dotnet/Spatial.Client/` — the .NET SDK for automation and service clients:
  typed methods over `HttpClient` with the same versioned contracts and the
  shared value codec.

## Invariants

- Every client speaks only the public HTTP contracts
  (`spatial.host/api/...`, `Spatial.PluginSdk.Http`).
- Inline values use the SDK's value codec (`Spatial.PluginSdk.Codec` /
  `client/src/wire.ts`); feature data crosses as canonical binary streams.
- `Web_clients_do_not_depend_on_tauri` (tests/architecture) enforces the
  no-Tauri rule on every client package manifest.
- The TypeScript SDK's generated wire types are pinned to the OpenAPI
  snapshot (`clients/typescript/scripts/openapi.snapshot.json`) and tested
  for drift in `npm test`; refresh with `npm run generate` (or
  `eng/e2e-web.sh`, which refreshes from a live host).