# Worker Wire Protocol

Read when implementing, extending or debugging the process boundary between
the spatial host (supervisor) and isolated plugin workers. Part of Phase 5
(plan §16, Epic F); implements ADR-0006/0013/0020/0025.

## Transport and framing

- Every plugin worker runs as a separate executable process.
- The supervisor and the worker speak over the worker's **stdin/stdout**;
  stderr carries free-form diagnostics only.
- Each message is **one JSON object per line** (UTF-8, `\n`-terminated).
  Lines are bounded (4 MB default) — a larger line is a protocol error.
- Framing is versioned: every line carries `"protocol":"spatial.worker/1"`.

```json
{"protocol":"spatial.worker/1","type":"invoke","id":"3a2c1b4d…","payload":{…}}
```

- `type` — the message type (below). `id` correlates request/response pairs
  and identifies asynchronous messages (progress, cancel). `payload` is the
  type-specific body.
- Request/response pairs: `ping→pong`, `invoke→result`, and every
  `facility.*→facility.result` echo the same `id`. A late answer to an
  abandoned request (timeout/cancellation) is ignored by the requester.

## Messages

| type | direction | purpose |
| --- | --- | --- |
| `hello` | worker → supervisor | startup handshake: the validated package manifest + declared capability ids |
| `ping` / `pong` | supervisor ↔ worker | liveness probe for health checks |
| `invoke` | supervisor → worker | one capability invocation (id = the invoke id) |
| `progress` | worker → supervisor | a progress observation (`fraction`, `message`) for an invoke |
| `result` | worker → supervisor | the terminal outcome: `{"kind":"success","value":…}` or `{"kind":"failure","error":{…}}` |
| `cancel` | supervisor → worker | request cancellation of an invoke (id = the invoke id) |
| `close` / `closed` | supervisor ↔ worker | graceful drain: finish in-flight work, then `closed` + exit 0 |
| `error` | either | protocol-level error (malformed line, unknown type) |
| `facility.mint` | worker → supervisor | facility RPC: mint a runtime-owned resource handle |
| `facility.stream.create` | worker → supervisor | facility RPC: create a bounded stream |
| `facility.stream.write` | worker → supervisor | facility RPC: write items (backpressure through the host's bounded stream) |
| `facility.stream.complete` | worker → supervisor | facility RPC: complete a stream, optionally with an error |
| `facility.result` | supervisor → worker | the answer to any facility RPC (ok + handle, or structured error) |

`invoke` carries the capability id, the encoded arguments, the granted
permission names and the optional deadline (ISO-8601). The worker enforces
the deadline by cancelling the invocation; the supervisor enforces it too,
so a silent worker cannot hang a job past its deadline.

## Inline values

Argument and result values use the inline value codec — scalars plus tagged
values:

| Value | Wire form |
| --- | --- |
| `null` / `bool` / string | JSON primitive |
| int32 | JSON number |
| int64 | `{"$i64":"80"}` (decimal string — the only lossless JSON form) |
| double | JSON number |
| `byte[]` | `{"$bytes":"aGVsbG8="}` (base64) |
| `ResourceHandle` | `{"$resource":{"token","kind","owner","createdAt"}}` (opaque token) |
| `ProviderId` | its canonical `name@version` string |

Decoding maps JSON numbers to int32 when integral and in range, else int64,
else double. **Spatial values (geometries, feature batches) are rejected by
the codec with a structured error**: they cross the boundary as canonical
binary interchange (ADR-0020), which the operation/store plugins add in later
phases — the runtime never routes geometry through JSON between workers
(`architecture/interchange.md`).

## Resources and streams cross the boundary as facilities

The host owns resources and bounded streams (ADR-0022/0023). A worker never
creates state locally: `facility.mint` asks the host for a runtime-owned
handle and the host answers with the opaque token; `facility.stream.*` ask
the host to create the bounded stream, accept writes (the host's full buffer
makes the write wait — cross-process backpressure) and complete it. The
invoked capability returns the handle token as its result; the supervisor
resolves the token back to the host-side handle before the runtime's
streaming contract check. Cancellation of a long job flows host → worker as a
`cancel` message; host-side deadlines and caller cancellation both route
through it.

## The two ends

- **Worker host** (`Spatial.PluginHost.DotNet` executable): loads one package
  (manifest + assembly) in an isolated load context, validates the loaded
  provider against the manifest, answers `hello`, serves `invoke`/`cancel`/
  `ping`/`close`, and relays progress. Language-neutral: another language can
  implement the same protocol without .NET types crossing the boundary.
- **Supervisor** (`Spatial.PluginHost.DotNet` library surface): discovers and
  validates packages, spawns worker processes, performs the handshake,
  health-checks with `ping`, restarts crashed workers with backoff, routes
  invocations, and drains (`close`) with rollback metadata — see
  `architecture/plugin-lifecycle.md`.