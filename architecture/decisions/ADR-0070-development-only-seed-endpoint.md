---
status: accepted
date: 2026-09-20
deciders: maintainer + agent
---

# ADR-0070: A development-only seed endpoint

## Context

The demo dataset (`eng/seed.sh` → `tools/seed/seed.mjs`) is a client of the
neutral admin API: it downloads public GeoJSON, ingests it through
`POST /api/ingest`, and publishes maps through `PUT /api/maps/{name}`
(ADR-0041 §5, ADR-0053 §4). Both routes are mutations, so they are mounted
only when an admin token is configured (ADR-0041 §6).

The Aspire DevHost composition (`src/Spatial.DevHost/Program.cs`) does not
configure a token, so the host behind `http://127.0.0.1:5201` serves reads
only. Seeding it today means restarting the whole Aspire stack with a token
— and the restart wipes the `memory` store the seed targets, so the restart
is both disruptive and self-defeating. The map registry survives (it is
file-persisted), which leaves the worst state: advertised services whose
datasets are gone.

## Decision

**1. `POST /api/seed` runs a seed document server-side, Development only.**

The endpoint accepts one JSON document that mirrors the seed manifest
(`tools/seed/manifest.mjs`), so the manifest stays the single source of
truth and the seed tool can POST it verbatim:

```csharp
public sealed record SeedSource(
    string Id, string Url, string Format, int Srid,
    int? SourceSrid = null, string? Identity = null, string? IdentityField = null);

public sealed record SeedLayerStyle(
    string? Color = null, double? Opacity = null, double? LineWidth = null,
    double? Radius = null, bool? Visible = null);

public sealed record SeedMapLayer(
    string Dataset, string? Name = null, string Geometry = "mixed",
    SeedLayerStyle? Style = null, string Kind = "feature");

public sealed record SeedMap(
    string Name, IReadOnlyList<string> Services, IReadOnlyList<SeedMapLayer> Layers,
    string? Description = null, string? Copyright = null);

public sealed record SeedRequest(
    IReadOnlyList<SeedSource> Sources, IReadOnlyList<SeedMap> Maps,
    string Store = "memory", bool Force = false,
    IReadOnlyList<string>? Only = null);

public sealed record SeedFailure(string Target, string Code, string Message);

public sealed record SeedResponse(
    string Store, int Ingested, int Reused, int Published,
    IReadOnlyList<SeedFailure> Failures);
```

Per source the host downloads the URL, decodes it with `DatasetDecoder`,
reprojects `sourceSrid` → `srid` through `ICoordinateTransforms`, and loads
it atomically through `IDatasetIngest` — the same pipeline as
`POST /api/ingest`, with the same `MaxBytes`/`MaxFeatures`/format caps.
Existing datasets are reused unless `force` is set, in which case ingest is
re-attempted and stores report per-item `invalid.arguments` for datasets
they still hold (the same semantics as `seed.mjs --force`, which has no
replace path to call). Per map the host lowers
the compact draw recipe to the persisted MapLibre fragment (the same lowering
`seed.mjs` and the workbench composer perform), preserves published layer
ids, and stores the map through `IMapRegistry`, which keeps its
dangling-layer validation. Failures are per item, not fatal: the response is
always 200 with a `failures` list, mirroring the seed tool's summary. A
malformed document is `invalid.arguments`; the request's cancellation token
flows through the whole run (ADR-0033: cancellable `Task`s, no job model).

**2. ADR-0041 §6 ("no server-side fetch") is amended with a Development-only
exception.**

The `{"url": …}` remote-input form stays rejected everywhere else, and
`/api/seed` is **not mounted outside Development** (unknown routes answer
404, as today). Inside Development it requires the admin token when one is
configured and is open when none is — the DevHost binds loopback by default,
so the exposure is the local developer. Rationale for the exception rather
than a token-only rule: the endpoint exists precisely for hosts that were
started without a token; gating it on a token would reproduce the restart it
removes.

**3. The seed tool prefers the endpoint and falls back to the legacy drive.**

`seed.mjs` POSTs the manifest to `/api/seed` first; on 404/405 (hosts
without the endpoint) it falls back to the current download → ingest →
publish drive. `eng/seed.sh` is unchanged.

## Consequences

- Seeding the Aspire host needs no restart and no token: after this change
  ships, `eng/seed.sh` (or a plain POST) seeds 5201 in place.
- One code path ingests: the endpoint reuses the ingest pipeline, the
  transform service, the registry validation and the ingest caps. The only
  new logic is the download (a bounded `HttpClient` fetch) and the compact
  style lowering, which is the third copy of that recipe (seed tool,
  composer, host) — kept identical and noted here.
- Production attack surface is unchanged: the route does not exist outside
  Development, and no new package is added (`HttpClient` is framework).
- The contract (`SeedRequest`/`SeedResponse` in `Spatial.Contracts`, core
  types only), the .NET and TypeScript SDK methods, the endpoint tests
  (success, per-item failure, cancellation, prod-unmounted, auth), the seed
  tool and the distilled `host-and-clients.md` land together (AGENTS.md).

## Alternatives

- **Set a dev admin token in the DevHost composition.** ADR-clean and one
  line, but it still needs a stack restart to take effect and leaves the
  next token-less host unseedable. Rejected as the whole fix; the endpoint
  also serves hosts started any other way without a token.
- **Token-gate the endpoint unconditionally.** Reproduces the restart the
  endpoint removes. Rejected.
- **Mount the endpoint in production behind the token.** Widens a
  server-side-fetch surface to deployed hosts for no product need.
  Rejected.

## References

- ADR-0041 (neutral ingest, §6 token gating and no-fetch rule), ADR-0053
  (maps), ADR-0047 (persisted style), ADR-0034 (DevHost), ADR-0018 (host runs
  standalone)
- `tools/seed/seed.mjs`, `tools/seed/manifest.mjs`, `eng/seed.sh`
