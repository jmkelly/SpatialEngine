# ADR-0016: Tauri 2 is the first desktop shell

Status: Superseded by ADR-0039 (desktop packaging abandoned).

## Context

After the browser milestone, packaging the same web client natively requires
a small, modern shell with narrow native integration.

## Decision

Tauri 2 is the desktop shell: window and lifecycle, packaging of React
production assets, optional launch/supervision of the .NET spatial host
sidecar, native file picker adapter, install/update integration and clean
shutdown. Windows WebView2 is the first target; other platforms need explicit
compatibility tests before support is claimed.

## Consequences

- Rust stays confined to the thin shell until a measured use case justifies more.
- Desktop conformance tests mirror browser conformance tests (ADR-0017/0020).