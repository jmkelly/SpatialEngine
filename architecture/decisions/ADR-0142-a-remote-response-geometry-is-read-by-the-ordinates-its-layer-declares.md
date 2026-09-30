---
status: accepted
date: 2026-09-30
deciders: maintainer + agent
---

# ADR-0142: A remote response geometry is read by the ordinates its layer declares

## Context

The Esri geometry codec reads a third ordinate only when the geometry object
carries `hasZ` or `hasM` (spec §10). ADR-0085 made writer and reader agree on
that rule, because for the **array** forms (`points`, `paths`, `rings`) a
three-element coordinate is genuinely ambiguous: nothing in the payload says
whether the third number is a Z or an M.

That rule is right for a payload this engine wrote and wrong — silently — for a
payload somebody else wrote. The 52-endpoint recorded corpus
(`tests/fixtures/arcgis/captured/`, 316 layers, 18 layers declaring `hasZ: true`)
has **no flagged response geometry anywhere**: a `hasZ: true` layer answers
with `{"points": [[x, y]]}` or `{"x": …, "y": …, "z": …}` and states nothing of
its own. The layer resource is the only place the layout is declared. The
corpus does not show a 3-ordinate array — the captures did not ask for Z — but
it does settle the flag question the bead asked about: **a real FeatureServer
does not repeat `hasZ` on the response geometry**, so the per-geometry flag
cannot be the only source of the answer.

The point form is worse than merely under-informed. `{"x": 13.4, "y": 52.5,
"z": 34.5}` *names* its ordinate: the property is called `z`, so there is
nothing to disambiguate and nothing for a flag to add. Requiring one dropped
the elevation of every remote point.

The coupling ADR-0084 stated as a consequence — "`hasZ: true` is a claim the
store must keep backing" — was therefore not actually held end to end, and
ADR-0091 is what made it matter: it proved the layout from the remote layer
resource *and* sends `returnZ=true` for a `hasZ` layer, so the store now asks
the remote for elevations the codec would then read back as absent. The
end-to-end test for that claim
(`ArcGisRestStoreTests.Scan_keeps_the_ordinates_the_layer_declares`) had to
hand the response a `hasZ` flag the real service does not send, which made the
test prove the flag's existence rather than the store's claim.

## Decision

**A response geometry is read by the ordinate it names, and a response
geometry that names none is read by the layout its dataset declares.**

### 1. A point's `z`/`m` property is the flag

`DecodePoint` reads `z` and `m` when the property is present and numeric, with
no flag required. The point form is the one shape where the ordinate is named
rather than positional, so the property is self-describing; a remote that omits
the flag does not lose its elevation for sending one, and a geometry that
carries both a flag and the property is read the same either way.

### 2. The dataset's declared layout is the fallback for the array forms

`EsriGeometryCodec.Decode` and `EsriFeatureCodec.Decode` take the dataset's
declared `CoordinateLayout` — the `DatasetDescription.GeometryLayout` ADR-0084
added and ADR-0091 fills from the remote layer resource — and fall back to it
**only when the geometry object states neither flag**. A geometry that states
either flag keeps its own answer, because the per-geometry statement is the
more specific one and ADR-0085's writer/reader agreement is worth nothing if
the reader overrules the writer.

A two-dimensional declaration resolves to "no Z, no M", which is what an
unflagged geometry already meant, so a 2D dataset's reads are untouched.

`CoordinateLayout` is a `Spatial.Core` type, so nothing crosses a wall: the
parameter is the same core value the dataset description already carries, and
the codec still sees no store or provider shape. It is a trailing optional
parameter, so every existing call site — the serve path's
`EsriValueParser`, the Geometry and Image services — is unchanged and still
reads a flagged geometry exactly as before.

### 3. The ArcGIS REST store passes its description's layout

`ArcGisRestMapper.ReadFeatures` threads `description.GeometryLayout` into the
decode, which is the one place in the engine that has both a remote response
and a declaration of what that remote's layer carries.

## Consequences

- A proxied 3D layer's elevations survive the round trip. Before, ADR-0091's
  store asked the remote for Z and the codec dropped it on the way in.
- An M-declaring remote's measures are no longer read as elevations: without
  the flag, a three-element array defaulted to Z, so a `[x, y, m]` became
  `[x, y, z]`. The declared layout is the tie-break that ADR-0084's
  schema-proof rule already named.
- The engine still cannot read an ambiguous array from a service that declares
  nothing about its layout. That case keeps the old default (Z), which is the
  spec's own convention for a three-ordinate coordinate.
- A remote that declares a layout it does not honour is believed — the same
  trust ADR-0091 places in a remote's schema, in the same direction: a wrong
  declaration produces a mislabelled ordinate, never a lost geometry.
- The serve path is unchanged; a client's own query geometry is read by its
  flags, or by the property it names.

## References

- ADR-0035 (GeoServices boundary adapter — the pure mapper)
- ADR-0081 (the honesty rule applied to advertised flags)
- ADR-0084 (the dataset declares its coordinate layout)
- ADR-0085 (the reader's own rule for a three-ordinate Esri array)
- ADR-0091 (the ArcGIS REST store proves and asks for its declared ordinates)
- `research/compat/feature-service.md` §1–2
- `tests/fixtures/arcgis/captured/` (the recorded corpus: no flagged response
  geometry on any of the 18 `hasZ: true` layers)
