# Esri-docs parity corpus (offline replay fixtures)

Verbatim-shaped request/response pairs transcribed from the Esri REST API
docs and `sampleserver6` for the **GeometryServer** slice (T-064). Later
slices reuse this skeleton: FeatureServer/query (T-069), MapServer (T-070),
ImageServer + edge-cases + strict-vs-lenient policy (T-071).

- Layout: `geometryserver/<operation>-<shape>.json` and
  `featureserver/<operation>-<facet>.json`, each indexed by its own
  `manifest.json` (the corpus loops).
- Each case keeps the doc-verbatim `request` (method + path + query string),
  the doc-verbatim `esriResponse`, the `expect` subset our host must satisfy,
  and `knownDeltas` where the engine honestly differs. The replay suite
  (`EsriDocsReplayTests`) replays `request.query` verbatim against our host
  and applies a semantic JSON diff: numbers compare with tolerance `1e-6`
  (scaled: `|a-b| <= tol * max(1,|a|,|b|)`), geometry coordinates likewise.
- `esriResponse` values are transcribed from the docs/sampleserver6 and
  lightly trimmed where the docs elide (never renormalised to our engine):
  they pin what Esri said, `expect` pins what we answer.

## ProjNet-vs-Esri-PE note

`project` replays compare within the scaled `1e-6` tolerance. Our engine
reprojects through ProjNet over the curated WKID catalogue while Esri uses
the PE engine with datum tables we deliberately do not ship (ADR-0009,
ADR-0035): same-CRS-family projects agree to the tolerance, datum steps stay
an honest reject via `findTransformations`. If a future slice tightens
project assertions, keep the scaled tolerance — never exact string equality.

## FeatureServer/query slice (T-069)

Ten cases over the fixed 8-city demo snapshot (FeatureServer layer 0):
`where`, `objectIds`, envelope `geometry` + `spatialRel`, `outSR`
reprojection, `returnGeometry`, `orderByFields`, `resultOffset` /
`resultRecordCount` paging, `outStatistics`, and `validateSQL` accept +
reject. The wire shape (feature sets, `exceededTransferLimit`,
`isValidSQL`/`validationErrors`) is Esri's; the rows are ours — the
fixtures say so in `knownDeltas` rather than copying Esri sample rows.
Partial pages pin `features` + `exceededTransferLimit`, never the opaque
`resultPaginationToken`. No FeatureServer behaviour was changed for this
slice.

## MapServer slice (T-070)

Eight cases over a runtime `world` map published on demo.cities (layer 0)
by the replay suite itself (the default test host serves no MapServer):
`root`, `layers`, single `layer` (fields + `drawingInfo`), `legend`,
`find` (`searchText=Ber`), `identify` (doc-pattern point probe with pixel
tolerance) and `export` as JSON envelope plus `f=image`. The wire shape is
Esri's; rows, styles and renders are ours. Images are never byte-compared:
the legend swatch `imageData` is excluded from the semantic diff and gated
structurally (valid PNG at the pinned 20x20 swatch size), and the export
image gets a structural PNG gate (media type, raster headers, IHDR
dimensions, non-trivial body) — renderers are never pixel-equal across
engines, so there is no Esri reference image to hash. The legend swatch
`url` is a per-process `HashCode` token, hence nondeterministic across
restarts: pinned non-empty only, with follow-up T-085 for a stable hash.
No MapServer behaviour was changed for this slice.

## Honest deltas (no parity chasing in this slice)

- `areasAndLengths` / `lengths`: the docs name the inputs `polygons` /
  `polylines`; our Geometry Service requires `geometries` (like the other
  verbs). The fixtures replay the doc-verbatim query and pin the honest
  `400`/`invalid.arguments` reject instead of aliasing new params here.
  Follow-up: `eng/tasks add` alias task (filed by T-064).
- `buffer` vertices differ from Esri (NTS quadrant segmentation vs PE);
  the replay pins structure + area/length semantics, not vertex equality.
- `generalize` is Douglas-Peucker and `simplify` is topological repair —
  never the same verb (GeometryService docs); the fixtures pin that split.
