# ADR-0015: Browser delivery is completed before desktop packaging

Status: Superseded by ADR-0039 (desktop packaging abandoned).

## Context

Desktop shells obscure webview variation, packaging and lifecycle problems
under a layer of native complexity. The web client is the thing being proven.

## Decision

Milestone 1 is the browser-hosted workbench, complete with exit criteria and
Playwright tests, before any Tauri work begins. The workbench must not depend
on Tauri code or APIs.

## Consequences

- The browser milestone de-risks the architecture before native packaging.
- Tauri later wraps the unchanged React assets (ADR-0016/0017).
- Browser delivery remains independently supported after desktop shipping.