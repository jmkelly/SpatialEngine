# Plugin Manifest

Read when writing, validating or loading a plugin package. Part of Phase 5
(plan §16, Epic F "language-neutral worker protocol" + ".NET worker SDK");
implements ADR-0006/0013. The schema is the *packaging* contract: it declares
what a plugin package is and what it serves, in a language-neutral JSON
document every host and worker can validate before anything runs.

## Package layout

A plugin package is an immutable directory:

```text
plugin-dir/
  manifest.json      # this schema, version 1
  <assembly>         # the loadable payload for the "dotnet" runtime hint
  ...dependencies    # self-contained copies for the worker's load context
```

The supervisor discovers packages by looking for `manifest.json` in a
directory (side-by-side versions are sibling package directories). Packages
are immutable: a replacement is a *new* package next to the old one, never an
edit in place.

## Schema (version 1)

```json
{
  "schemaVersion": 1,
  "id": "fixture@1",
  "displayName": "Spatial fixture plugin",
  "runtime": "dotnet",
  "assembly": "Spatial.Plugin.Fixtures.dll",
  "assemblyType": "Spatial.Plugin.Fixtures.FixtureProviderV1",
  "capabilities": [
    {
      "id": "spatial.fixture.peek@1",
      "purpose": "Returns the provider id that served the invocation.",
      "inputSchema": "none",
      "outputSchema": "scalar",
      "errors": [ { "code": "invalid.arguments", "description": "An argument is missing or of the wrong kind." } ],
      "permissions": [ "spatial.fixture.read" ],
      "traits": [ "cancellable", "streaming" ],
      "examples": [ { "name": "three points", "description": "Counts a batch of three features." } ]
    }
  ]
}
```

| Field | Required | Rule |
| --- | --- | --- |
| `schemaVersion` | yes | `1` (the only version this host understands). |
| `id` | yes | Provider id `name@version` (dotted lowercase name, positive version) — the identity the package activates. |
| `displayName` | yes (defaults to `id`) | Human-readable, non-empty. |
| `runtime` | yes | Runtime hint; `dotnet` is the first supported hint. |
| `assembly` | yes (for `dotnet`) | Loadable assembly file name inside the package. |
| `assemblyType` | no (for `dotnet`) | Full CLR type name implementing `ICapabilityProvider`; when absent the worker auto-discovers a single public provider in the assembly. |
| `capabilities` | yes, ≥ 1 | The capability contracts served, one entry per capability id (no duplicates). |

Each capability entry carries the contract *identity* from plan §9:

- `id` — versioned capability id (`dotted.name@version`).
- `purpose` — non-empty.
- `inputSchema` / `outputSchema` — non-empty interchange shape names.
- `errors` — ≥ 1 error variant; each has a valid dotted `code` and a `description`.
- `permissions` — optional; each a valid permission name.
- `traits` — optional; known names `cancellable`, `streaming`, `long-running`,
  `side-effects`. A `long-running` capability must also declare `cancellable`
  (ADR-0008).
- `examples` — optional; each a named, described conformance example.

The manifest carries the contract *identity*; the exact schema shapes
(feature columns, binary interchange) and example bodies live with the
implementation and are checked at activation (see Compatibility).

## Validation and compatibility

- **Schema validation** (`PluginManifestValidator`): every rule above, with
  actionable per-field problems. The loader refuses a package that fails it.
- **Activation compatibility** (`ManifestCompatibility`): before a package
  goes live, the worker host compares the loaded provider's actual contract
  surface (provider id, capability ids, purposes, schema names, error codes,
  permissions, traits, example names) against the manifest. Prose may differ;
  contract identity must not. An out-of-date manifest can never accidentally
  route traffic to a changed contract.

## Runtime hints

| Hint | Meaning | Loadable payload |
| --- | --- | --- |
| `dotnet` | .NET worker (Phase 5) | `assembly` (+ `assemblyType`), loaded in an isolated load context by the .NET worker host |
| (future) `wasm` | WebAssembly component (Phase 12) | WIT-defined component |

Hints are versioned with the schema; adding a hint is a schema extension
(requires an ADR), never a silent acceptance.