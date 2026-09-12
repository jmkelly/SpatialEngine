---
status: accepted
date: 2026-09-12
deciders: maintainer + agent
---

# ADR-0039: Desktop (Tauri) packaging is abandoned

## Context

ADR-0015–0017 and ADR-0019 planned a Tauri 2 desktop shell that would package
the unchanged browser workbench and optionally supervise `Spatial.Host` as a
sidecar. That work never started. The delivered product is the independently
executable .NET host (ADR-0018) with the browser-hosted React + MapLibre
workbench as its client, and the active direction is Esri GeoServices REST
compatibility (ADR-0035) rather than a second delivery shell.

Keeping the desktop plan on the books is not free: it anchors four ADRs, a
pair of principles, a deployment profile and an architecture guard for a
client that does not exist, and it implies a packaging commitment the engine
does not make.

## Decision

Desktop packaging is abandoned. The engine ships as the independently
executable host; the browser workbench is its client. No Tauri/Rust shell,
sidecar supervision or desktop-only adapter is built.

- ADR-0015 (browser before desktop), ADR-0016 (Tauri is the shell),
  ADR-0017 (Tauri contains no spatial logic) and ADR-0019 (Tauri sidecar)
  are superseded.
- The `Web_clients_do_not_depend_on_tauri` architecture guard is removed;
  there is no shell to guard against. Clients still depend only on the public
  typed HTTP routes.
- ADR-0018 (host independently executable) and the browser/client invariants
  are unaffected.

A future desktop client is not precluded, but it is a new decision that needs
its own ADR and a demonstrated need.

## Consequences

- No Rust or Tauri code, toolchain or CI profile enters the repository.
- The host stays packaging-agnostic and independently executable; nothing
  about the typed API, canonical codecs or SDKs changes.
- Principles 3–5 and 18–20 are restated in terms of the host/client boundary
  they always described rather than naming a specific shell.

## References

- ADR-0015, ADR-0016, ADR-0017, ADR-0019 (superseded)
- ADR-0018 (host independently executable)
- ADR-0033 (in-process interfaces), ADR-0035 (GeoServices boundary)
