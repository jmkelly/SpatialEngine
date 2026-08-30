# Deployment Profiles

Read when packaging, running or testing a delivery target. See
implementation-plan.md §2.5.

## Browser/server

React web assets · ASP.NET Core Spatial.Host · external PostGIS ·
out-of-process plugin workers.

## Local development

.NET Aspire composition · PostGIS container · Spatial.Host · React dev
server · plugin worker processes · browser.

## Desktop

Tauri 2 package · React production assets · self-contained Spatial.Host
sidecar **or** configured remote host · selected built-in plugins · optional
remote providers.

## Invariants

- One public API, contract set, TypeScript SDK and React application in every
  profile.
- The host is independently executable (ADR-0018); Tauri is optional
  packaging (ADR-0015/0016/0019).
- Every profile runs the same conformance tests.
- Browser tests (Playwright) never require Tauri; desktop tests verify
  packaging and lifecycle, not spatial behaviour.