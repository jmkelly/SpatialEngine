# Spatial.Stores.SqlServer

The SQL Server provider (`sqlserver@N`): Microsoft.Data.SqlClient, WKB
interchange, SRID discovered from the data then the provider metadata. Two
records bind it: **ADR-0073** (the provider, its interchange and its
containerised tests) and **ADR-0092** (a created dataset carries its own
indexes, and an index that cannot be created rolls the create back). Route by
task: `architecture/principles.md`.

## Never

- No SQL from a client-supplied string: identifiers come from a validated
  allowlist, values from parameters. `sp_executesql` with a concatenated
  predicate is the failure this package is audited for.
- No pushdown that changes the answer (ADR-0074); what T-SQL cannot state
  exactly is decided in managed code.
- No dialect assumed to be PostGIS: geometry, aggregates and collations are
  the ones this provider has measured (ADR-0133, ADR-0136, ADR-0137).
- No index creation outside the create/ingest transaction (ADR-0092).
- No `Spatial.Core` type gaining a `Microsoft.Data.SqlClient` one, and no
  client type crossing a contract (ADR-0005).

## Commands

- `dotnet test tests/unit/Spatial.Stores.SqlServer.Tests` — unit tier, no
  container.
- `dotnet test tests/integration/Spatial.SqlServer.Tests` — containerised; a
  run reporting mostly skips is a red lane, not a green one.
- `eng/verify.sh --fast` — the lane a change here is handed off on.
