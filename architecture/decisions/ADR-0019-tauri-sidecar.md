# ADR-0019: Tauri may bundle Spatial.Host as an optional sidecar

Status: Superseded by ADR-0039 (desktop packaging abandoned).

## Context

Desktop users should be able to run without configuring a remote host, while
other users connect to an existing deployment.

## Decision

Tauri may package a platform-specific, self-contained `Spatial.Host` and
launch it as a sidecar on an ephemeral or configured secure loopback
endpoint, waiting on the readiness endpoint and shutting the child down
cleanly. Remote-host mode remains fully supported, with no bundled process.

## Consequences

- Bundled and remote modes share one React client and one host API.
- Secrets are never embedded in the desktop package; the host manages them.
- Sidecar startup/readiness/shutdown are tested separately from host startup.