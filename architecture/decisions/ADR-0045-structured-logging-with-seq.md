---
status: accepted
date: 2026-09-15
deciders: maintainer + agent
---

# ADR-0045: Structured logging to Seq (Serilog) with an Aspire-hosted Seq server

## Context

ADR-0034 composes the local development profile with Aspire: PostGIS,
`Spatial.Host` and the Vite workbench. The host produces standard
`Microsoft.Extensions.Logging` output (console only), so request, startup and
failure logs are not searchable across a session — the Aspire dashboard shows
process stdout, not a queryable structured event stream.

Seq is the maintainer's chosen log server. It ingests structured events over
its HTTP API and stores them for text/structured queries, and its own client
guidance is Serilog. The host already depends on the framework logging
abstractions; the decision is which sink/config package to add and how the
development profile runs the Seq server.

Numbering: ADR-0044 is the newest record, so this decision takes 0045.

## Decision

**Serilog as the host's logging provider, Seq as the structured sink, and an
Aspire Seq container for local development.**

- **Packages** (pinned in `Directory.Packages.props`):
  `Serilog.AspNetCore` and `Serilog.Sinks.Seq` for `Spatial.Host`;
  `Aspire.Hosting.Seq` for `Spatial.AppHost`.
- **Host wiring** (`Spatial.Host`, `LoggingSetup`): Serilog replaces the
  default providers, honours the existing `Logging:LogLevel` section
  (`Logging:LogLevel:Default`, `Logging:LogLevel:Microsoft.AspNetCore`),
  enriches from the log context and stamps `service.name = Spatial.Host`.
  The console sink is always on; the Seq sink is added **only when a server
  URL is configured**, so the host still runs with no Seq and no external
  services (ADR-0018).
- **Configuration**: `Spatial:Logging:Seq:Url` (env `SPATIAL_SEQ_URL`) and
  `Spatial:Logging:Seq:ApiKey` (env `SPATIAL_SEQ_API_KEY`). Secrets never
  live in the repository; the API key is an ordinary configuration value.
- **Request logging**: `UseSerilogRequestLogging` logs one structured event
  per HTTP request (method, path, status, elapsed) and raises the level to
  `Warning` for server errors, instead of a log line per framework event.
- **OGC request diagnostics**: the WMS/WFS adapter logs one structured event
  per operation carrying the `request` operation and the merged request
  parameters, and raises a rejected operation to `Warning` with the mapped
  OGC `ServiceException` code, reason and HTTP status. OGC protocol
  parameters are the only request data this adds and they carry no secrets,
  so a blank or rejected interop client (for example QGIS) is diagnosable
  from the log alone.
- **Startup logging**: one summary event records the profile, the host
  version, whether the PostGIS store is configured (never the connection
  string), whether admin routes are enabled and whether the Seq sink is
  active. Missing optional configuration logs an actionable warning.
- **Aspire composition** (ADR-0034 extends, does not change): `AddSeq("seq")`
  starts the Seq container, and its `http` endpoint is injected into the host
  as `SPATIAL_SEQ_URL` with `WithEnvironment`. The AppHost links no engine
  project and owns no logging logic.

Boundaries:

- The Serilog packages are allowlisted for `Spatial.Host` and
  `Aspire.Hosting.Seq` for `Spatial.AppHost` in the architecture guard, with
  this ADR — never silently.
- `Spatial.Core` and `Spatial.PluginSdk` stay free of logging packages; no
  implementation project gains a Serilog reference. Implementations keep
  using `Microsoft.Extensions.Logging.Abstractions` if and when they log.
- Logging never weakens the redaction contract: no connection string, token
  or request body segment is logged. Messages carry configuration *state*
  (configured/unconfigured), not configuration *values*. The OGC adapter's
  own diagnostics are the narrow exception: they log the OGC protocol request
  parameters (never the admin API's query token), which carry no secrets by
  contract.

## Consequences

- Local development gains a Seq UI with a searchable event stream; the
  Aspire dashboard still shows process output.
- Production (browser/server profile) is unchanged: with no
  `Spatial:Logging:Seq:Url`, the host writes to console exactly as before.
- A new configuration surface exists (`Spatial:Logging:Seq:*`); the
  `host-and-clients` digest and `CHANGELOG.md` are updated with this ADR.
- Serilog is a host-only dependency. The SDK, core values and every
  implementation remain logging-framework neutral.
