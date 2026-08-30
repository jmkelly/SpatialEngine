# Apps

Frontend applications:

- `workbench-web/` — React + TypeScript + MapLibre browser workbench,
  Milestone 1 (Phase 10). Runs in a normal browser; no Tauri dependencies.
- `workbench-desktop-tauri/` — thin Tauri 2 shell packaging the unchanged
  workbench, Milestone 2 (Phase 11). Contains no spatial logic.

Architecture tests forbid `@tauri-apps/*` dependencies in web clients.