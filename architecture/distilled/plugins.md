# Plugins: Packages, Manifest, Wire Protocol, Lifecycle (distilled)

Implements ADR-0002/0006/0013/0025.

## Plugin boundaries, in order of preference

1. **Isolated worker process** (default): separate executable, language-neutral
   protocol, crash isolation, independent restart, side-by-side versions.
2. **In-process collectible ALC**: only controlled, latency-sensitive, trusted
   components; not a security boundary.
3. **WebAssembly**: later (Phase 12), after the worker model is established.

## Package layout

Immutable directory: `manifest.json` (schema v1) + loadable payload.
Discovery = scanning a packages root for `manifest.json`; side-by-side
versions are sibling directories. Replacement = new package next to the old,
never an edit in place.

Manifest essentials: `schemaVersion:1`; `id` = provider id `name@version`;
`runtime` hint (`dotnet` first, `wasm` future); `assembly` (+ optional
`assemblyType`, else auto-discover a single public `ICapabilityProvider`);
`capabilities` ≥ 1, each carrying id/purpose/input+output schema names/
≥1 error variants/optional permissions/traits/optional examples.

- Traits: `cancellable`, `streaming`, `long-running`, `side-effects`;
  `long-running` must also declare `cancellable`.
- The manifest carries contract **identity**. Before activation, the worker
  host compares the loaded provider's actual surface (ids, purposes, schema
  names, error codes, permissions, traits, example names) against the
  manifest — prose may differ, identity must not. Divergence refuses activation.

## Wire protocol (worker stdin/stdout)

- One JSON object per line, UTF-8, `\n`; lines bounded (4 MB default); every
  line carries `"protocol":"spatial.worker/1"`. stderr = free-form diagnostics.
- Envelope: `{"protocol":"spatial.worker/1","type":…,"id":…,"payload":…}`;
  `id` correlates request/response.

| type | direction | purpose |
| --- | --- | --- |
| `hello` | w → s | handshake: validated manifest + capability ids |
| `ping`/`pong` | both | liveness |
| `invoke` | s → w | capability id, encoded args, granted permissions, optional ISO-8601 deadline |
| `progress` | w → s | `{fraction, message}` observation |
| `result` | w → s | `{"kind":"success","value":…}` or `{"kind":"failure","error":{…}}` |
| `cancel` | s → w | cancel an invoke (id = invoke id) |
| `close`/`closed` | both | graceful drain, then exit 0 |
| `error` | both | malformed line / unknown type |
| `facility.mint` / `facility.stream.create|write|complete` / `facility.result` | w ↔ s | runtime-owned resource/stream RPCs |

- Both ends enforce the deadline (a silent worker cannot hang a job past it).
  Late answers to abandoned requests are ignored.
- **Inline value codec** = `Spatial.PluginSdk.Codec.ValueCodec` (ADR-0030,
  shared with the HTTP API): JSON scalars; int64 as `{"$i64":"…"}` decimal
  string; bytes as `{"$bytes":"base64"}`; geometry as
  `{"$geometry":"base64"}` (SGEOM); CRS descriptions as `{"$crs":…}`;
  handles as `{"$resource":{token,kind,owner,createdAt}}`. Bare JSON objects
  for spatial values are rejected; feature batches cross **only as streams**.
- Workers never own state: resources/streams are created host-side through
  facility RPCs; draining reclaims them via `ResourceRegistry.DisposeOwnerAsync`
  before the process stops.

## Lifecycle states

```
Discovered → Validated → Starting → Healthy → Active
                                          → Draining → Stopped
                                          → Failed (restarts exhausted)
```

## Supervision (`Spatial.PluginHost.DotNet`)

- **Activation**: spawn worker host apphost with `--package <dir>`; handshake
  (`hello` vs manifest); register provider proxy in `CapabilityRegistry`;
  mark `Active`. Duplicate provider id refused.
- **Health**: periodic ping; failure threshold → unhealthy (new work skips) + restart.
- **Restart**: exponential backoff (100 ms doubling, capped), default 3 attempts; exhaustion → `Failed` + unhealthy. Host never restarts.
- **Replacement sequence**: validate new package → start beside old → health +
  compatibility checks → route new work (active preference) → drain old
  (wait in-flight, reclaim resources, `close`) → stop → keep rollback metadata
  (old package still running, can be routed back).
- Replacement never requires a host or UI restart. Plugin code is disposable;
  persistent state is external. Plugins receive no ambient authority.
