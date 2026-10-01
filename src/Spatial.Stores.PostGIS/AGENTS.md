# Spatial.Stores.PostGIS

The PostGIS provider (`postgis@N`): Npgsql, raw parameterised SQL, spatial
indexes created with the dataset. Two records bind it: **ADR-0074** (the
feature-query plan is the contract a pushdown answers) and **ADR-0092** (a
created dataset carries its own indexes, and an index that cannot be created
rolls the create back). Route by task: `architecture/distilled/plugins.md`.

## Never

- No SQL from a client-supplied string. Identifiers (table, column, index,
  layer) are quoted from a validated allowlist; values are parameters.
- No pushdown that changes the answer: what the store cannot express exactly
  is read back and decided in managed code (ADR-0074).
- No index creation outside the create/ingest transaction, and no
  `CreateIndexes = false` default — it is the operator's opt-out (ADR-0092).
- No `Spatial.Core` type gains an Npgsql one, and no Npgsql type crosses a
  contract (ADR-0005).
- No uncommitted state: every write path is one transaction over the whole
  page set, and a failure rolls the whole thing back.

## Commands

- `dotnet test tests/unit/Spatial.Stores.PostGIS.Tests` — unit tier, no Docker.
- `dotnet test tests/integration/Spatial.PostGIS.Tests` — needs the container
  the suite supplies (ADR-0072); a run that skips most of it is a red lane, not
  a green one.
- `eng/verify.sh --fast` — the lane a change here is handed off on.
