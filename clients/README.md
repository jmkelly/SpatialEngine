# Clients

The public spatial engine client SDKs — the browser/web and automation
surfaces of the independently executable `Spatial.Host` (ADR-0033).

- `typescript/` — `@spatial/client`: the TypeScript SDK for React and other
  web clients (workbench). Fetch-based (browser and Node compatible), zero
  runtime dependencies, wire types generated from the host's OpenAPI
  description.
- `dotnet/Spatial.Client/` — the .NET SDK for automation and service clients:
  one typed method per route over `HttpClient`; geometries as core values
  (canonical SGEOM/SFBAT Base64 on the wire).

## Invariants

- Every client speaks only the public typed HTTP routes
  (`Spatial.PluginSdk.Http`).
- Geometry crosses as Base64 canonical bytes (ADR-0020); feature reads
  return decoded canonical batches.
- The TypeScript SDK's generated wire types are pinned to the OpenAPI
  snapshot (`clients/typescript/scripts/openapi.snapshot.json`) and tested
  for drift in `npm test`; refresh with `npm run generate` (or
  `eng/e2e-web.sh`, which refreshes from a live host).
