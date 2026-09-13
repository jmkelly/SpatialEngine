---
status: accepted
date: 2026-09-15
deciders: maintainer + agent
---

# ADR-0047: Layer style is persisted on the publication

## Context

The workbench composer (`architecture/map-composer-plan.md`) lets a user
order datasets and style each one, but per-layer style is **authoring state
held only in browser memory**: `POST /api/publications` carries ordered
layers and stable ids, and a reload discards every colour, opacity, width and
radius. The composer's own non-goal said so explicitly — style persistence
"needs the MapServer render model (M2/M4) and its own ADR".

That gap is now the first step toward in-depth styling. Two decisions already
fix the shape anything must conform to:

- **ADR-0041**: a `Publication` is the neutral, ordered projection of
  datasets onto a protocol surface, and its `PublicationLayer` list already
  owns draw order and stable `LayerId`s.
- **ADR-0044**: the engine's canonical style document is the **MapLibre
  style spec** (a documented subset), and the renderer already compiles it
  (`StyleCompiler`). A Map service's `drawingInfo` (map-service-plan M4) is a
  projection of that same dialect, not a second style model.

So the question is only **where** the style lives and **in what form**. A
separate style store would duplicate the publication's draw order and its
stable ids; a bespoke neutral style record would be a second style model to
keep in sync with the compiler. Neither is justified.

## Decision

**Layer style is a field on `PublicationLayer`, persisted with the
publication, expressed in the engine's MapLibre style dialect.**

```csharp
public sealed record PublicationLayer(
    string Dataset,
    int LayerId,
    string? Name = null,
    string? Style = null);
```

1. **Form.** `Style` is a JSON **array of MapLibre style-layer objects** (the
   ADR-0044 subset: `background`/`fill`/`line`/`circle`, flat paint, filters,
   zoom windows). It carries only the draw recipe (`type`, `layout`, `paint`,
   optionally `filter`/`minzoom`/`maxzoom`); the **host injects** `id` and
   `source-layer: <dataset>` into each element when it assembles a
   `{"layers":[…]}` document for the renderer. The dataset is never duplicated
   inside the style text, so the layer's `Dataset` stays the single source of
   truth. `null`/empty means "no explicit style — renderer defaults".

2. **Persistence.** The registry already serializes the whole `Publication`
   record to its versioned JSON file (`Spatial.Provider.Publications`,
   ADR-0041 §2), so adding the field makes it durable with no new storage
   path. Config-declared layers gain a matching `Style` so the declared and
   runtime paths behave identically.

3. **Validation.** A non-empty `Style` must parse as a JSON array whose every
   element is an object; anything else is a typed `invalid.arguments` naming
   the layer. Unsupported *paint* stays the renderer's concern
   (`StyleCompiler` already rejects what it cannot draw).

4. **Clients.** `Spatial.PluginSdk` gains only a `string?`; no renderer,
   JSON or protocol type crosses the contract (ADR-0005). The workbench
   composer writes the fragment on publish and reads it back on load; the
   .NET/TS SDKs expose it as the wire field the host already serves.

This is an **additive** contract change in the ADR-0036/0037/0038 style: an
absent `Style` is the old behaviour, so existing publications load unchanged.

## Consequences

- The composer round-trips what the user authored: publishing then loading a
  publication restores each layer's style instead of resetting it.
- There is exactly one style dialect (MapLibre, ADR-0044) and one draw order
  (the publication's layer list) — no second model to drift.
- A future publication-render route and the MapServer `drawingInfo`
  projection (M4) can honour the stored style without a client; neither is
  built here, so nothing yet renders a publication server-side.
- The OpenAPI snapshot and generated TypeScript types are refreshed with the
  contract, and the composer's `toPublication`/`fromPublication` mapping is
  updated with it (contract, SDK, test and ADR land together, per AGENTS.md).
- In-depth authoring beyond the current five controls (class filters, dash,
  zoom windows) needs no new storage decision — the dialect already supports
  it; only the composer UI has to grow.

## Alternatives

- **One MapLibre document on the `Publication`** (not per layer): matches the
  render request byte-for-byte, but duplicates draw order (the layer list and
  the document order) and makes per-layer editing a whole-document rewrite.
  Rejected on drift risk.
- **A bespoke neutral style record** (fill colour, stroke, radius, opacity):
  smallest composer round-trip, but a second style model beside the compiler
  that must be lowered and can never express filters, dashes or zoom windows
  — the opposite of the in-depth goal. Rejected.
- **A sidecar style store keyed by service + layer id**: keeps the
  publication contract untouched, but splits one resource across two
  persistence mechanisms and re-derives ordering. Rejected.
- **No persistence; client keeps authoring state**: the status quo this ADR
  exists to end.
