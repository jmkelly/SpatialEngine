# Spatial.StoreConnections

The runtime store-connection registry: one settable connection string per
store key, persisted to a protected JSON file
(`Spatial:StoreConnections:Path`, default `./data/store-connections.json`).

## Never

- No secret on any status, log line or exception message: statuses carry
  configured/updated-at only, and the file is created owner-only on Unix.
- No packages and no references beyond Core and Contracts. Persistence is
  `System.Text.Json` over framework assemblies only.
- No asynchronous disk read on the lookup path: the stores resolve their
  connection per operation through the synchronous in-memory
  `TryGetConnectionString`, so lookups never touch the disk and mutations
  hold one mutex.
- No invented store names: keys normalise to lowercase and must be
  identifier-shaped (`[a-z][a-z0-9_]*`); which keys the host serves is the
  admin endpoint's decision, not this file's.
