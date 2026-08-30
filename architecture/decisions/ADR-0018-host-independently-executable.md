# ADR-0018: Spatial.Host remains independently executable

Status: Accepted

## Context

The host serves browsers, automation and desktop apps alike. If it assumed a
desktop shell, every other client would inherit that coupling.

## Decision

`Spatial.Host` is an independently executable .NET process with its own API,
health endpoints and lifecycle. It never depends on Tauri or any UI. The
host's API surface is identical for every client.

## Consequences

- Browser, CLI, automation and desktop clients share one public API.
- The desktop shell merely launches, supervises or points at the host.
- Host smoke tests run with no shell present (see tests/integration).