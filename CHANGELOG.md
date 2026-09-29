# Changelog

All notable changes to Spatial Engine are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and the project uses
[Semantic Versioning](https://semver.org/spec/v2.0.0.html).

The product version is single-sourced in `Directory.Build.props`; update it and
this file together, then tag the release (`RELEASING.md`).

## [Unreleased]

### Changed

- **The tile cache holds per-layer tiles, composited at serve time**
  (ADR-0117, SpatialEngine-u2x.21.2): a write to one layer now invalidates
  that layer's tiles for that map rather than every tile of the map, which
  closes the composition half of the decision ADR-0083 left open and measured.
  Measured on a five-layer city basemap over a one-viewport working set, a
  single-layer edit invalidated all 25 warm tiles while one tile's pixels
  actually changed — 25× over-invalidation — and per-layer composition repays
  itself from about one to three tiles served per edit, saving 10% of tile CPU
  at a realistic profile. The per-layer entries are PNGs, the representation
  the whole-map entries already cache: compositing from decoded raw buffers is
  faster still but needs 23.7× the bytes, which would overrun the cache's byte
  bound by 16×. The standing cost is a composite on every tile response,
  including warm hits, so a host serving under about one to three tiles per
  edit is slower than before — a measured trade, not an oversight. Style saves
  are unaffected: they already moved the key per layer and were never
  over-invalidated. The spike and its reading are
  `eng/spike-u2x-tile-cache/RESULTS.md`.

### Added

- **A store read is a page, a position and a "one more"** (ADR-0116,
  SpatialEngine-u2x.10): a large-layer query is read with a `LIMIT`/`OFFSET`
  instead of being materialised and paged on the way in, a page says outright
  whether more remains (`FeatureQueryPage.HasMore`), and the Esri
  `resultPaginationToken` on a store-answered query *is* the store's own
  continuation rather than an adapter-minted index into a match set that was
  thrown away. A plan that restricts nothing is now read whole only when its
  order is not a total order the dialect can reproduce, which is the one reason
  the page genuinely has no position; a store with no pushdown still pages, and
  the contract now says in words what such a store does and does not promise
  (its answer is one page; it cannot promise to read fewer rows than it holds).
  Paging still terminates with the exact total in stable `OBJECTID` order and no
  duplicates, on both the token and the `resultOffset` workflow.

- **An area of use is a set of rectangles, so an extent across the
  antimeridian is not an empty one** (ADR-0111, SpatialEngine-u2x.32): EPSG
  writes a wrapped extent as a west bound in the east and an east bound in the
  west — extent 1175 "New Zealand" is 160.6E to 171.2W — and read as one
  rectangle that is the empty box, which the graph reads as "valid nowhere", so
  New Zealand left the transformation graph entirely rather than the part of it
  that does not overlap; clipped at 180 the node survived and left the
  registered ground west of the antimeridian uncovered. `CrsAreaOfUse` is now a
  name and a list of `CrsAreaOfUseBox`, the wrapped extent is the two
  rectangles it is with their registered bounds, and emptiness is the absence of
  boxes. The graph's intersection and union are rectangle algebra over that set
  and merge nothing — joining the halves would fabricate the very rectangle this
  removes — so a wrapped operand can no longer widen a composition it takes
  part in. **Caller-visible changes:** `CrsAreaOfUse` carries `Boxes` instead
  of `XMin`/`YMin`/`XMax`/`YMax`; a `findTransformations` response publishes
  `areaOfUse` as a list of envelopes (`areaOfUse[0]` is the one a client was
  reading); a concatenated operation publishes the two extents rather than
  their bounding box; and an `extentOfInterest` across the seam is split at
  the antimeridian instead of being sorted into one interval. The area's name
  stays a short label and the registry's verbatim extent name stays with the
  row, because half the areas in a listing are composed ones that name no
  extent. NZGD2000 still serves no transformation through the service — its
  vendored WKT carries no `TOWGS84` — which is a gap in the *parameters* of
  EPSG:1565 and is not fixed here.


- **A deployed datum shift grid is applied, per coordinate, over the classic
  Helmert** (ADR-0107, SpatialEngine-7at): `project` and the coordinate
  transform path now use a bundle an operator has deployed wherever that bundle
  covers the coordinate, and the classic Helmert everywhere else, in the same
  request — so a geometry straddling a grid's block edge is shifted by both
  operations. This closes the gap ADR-0105 §applied left open and priced into
  every grid method string, and with it the last break of the ADR-0087 §6
  invariant that the first ranked candidate is the path the engine applies; the
  grid candidate's method text now says how the two are chosen instead of
  confessing that the verb does not apply it. The composition is
  *source → the source's own geographic coordinates → datum shift → WGS 84 →
  the target's own → target*, built on shift-free copies of the source and
  target systems (the same definitions with their WGS 84 conversion zeroed), so
  the outer legs are pure projection maths and no leg re-applies a shift —
  the re-application ADR-0105 measured, 113 m at London on a same-datum leg. The
  fallback leg is still ProjNet's own maths asked in the direction the engine
  means, so no seven-parameter Helmert is re-implemented: with the grid out of
  the way the answer is bit-for-bit what it always was, and the 0.1 m London
  control point still holds against the PROJ 9 reference the repo pins. A pair
  no bundle serves takes no plan at all and runs the path it always ran. Grids
  stay configured rather than embedded (ADR-0105); the change is in
  `Spatial.Transformations.ProjNet` and its method strings, with no contract,
  SDK or workbench change. Not measured, and stated in the ADR: agreement with
  a *published* bundle such as OSTN15, which this machine has no PROJ to check
  against.

- **A created dataset carries its own indexes** (ADR-0092, SpatialEngine-0zp):
  `CreateAsync` and ingest on PostGIS and SQL Server now create the GiST (resp.
  spatial) index on the dataset's primary geometry column and a btree on every
  attribute column a pushed-down filter may name, inside the same transaction
  as the table. A dataset that has been created is queryable with no
  out-of-band DDL and no change to `eng/seed.sh` — the measurement behind this
  (eng/spike-u2x-query-baseline/RESULTS.md, finding 7) had the bbox+where
  pushdown as a Seq Scan on a 34,135-row table, and an index scan is 73 → 44 ms
  (europe box) and 170 → 109 ms (global box). The DDL is a pure function of the
  schema, unit-tested without a database and asserted through the planner's own
  plan against the real containers. An index that cannot be created rolls the
  create back rather than leaving an unindexed table. `PostgisOptions.CreateIndexes`
  and `SqlServerOptions.CreateIndexes` (default on) are the operator's opt-out
  for a bulk load. SQL Server's two limits are detected in advance rather than
  discovered as a failed statement: a table with no clustered primary key is
  not gridded (followed up as SpatialEngine-9vg) and an `nvarchar(max)` text
  column cannot be a key.

- **Albers Equal Area, Lambert Azimuthal Equal Area, Polar Stereographic
  (variant A) and Hotine Oblique Mercator (variant B) join the WKT method
  map, and the catalogue serves the Conus Albers** (ADR-0027,
  SpatialEngine-u2x.26). The WKT reader resolved only four projection
  methods, and every one of them is now a claim measured against PROJ 9.8.1
  rather than an assumption: each candidate was projected forward and inverse
  at points inside its area of use, on its own ellipsoid, and compared with
  PROJ's own coordinates for a named EPSG definition. Four more methods agree
  to a micrometre and are resolved, with the outcome for every candidate
  recorded in a table on the reader. **ProjNet's Albers agrees with PROJ to
  a micrometre over the whole conus** — the open question that kept EPSG:5070
  out of the catalogue is resolved, and EPSG:5070 is served. Three methods
  diverge and stay out as named failures: Polar Stereographic (variant B)
  has no latitude-of-standard-parallel parameter (527 km of northing),
  Hotine Oblique Mercator (variant A) applies the false offsets at the
  projection centre where PROJ applies them at the natural origin (2,047 km),
  and Krovak computes the right magnitudes in the south-oriented axis
  convention the catalogue does not serve. Each divergence is sized in a
  test, so closing one has to move a number, and each repair that would need
  a decision the bead did not authorise is recorded as its own bead
  (SpatialEngine-r4o, SpatialEngine-ufn, SpatialEngine-g2m). Measuring the
  variant B Hotine also found that EPSG spells parameter 8813 two ways --
  "Azimuth of initial line" on the Swiss grids and "Azimuth at projection
  centre" on the Borneo one -- and only the first was read, so EPSG:29873
  was unreadable; both spellings are read now, and the control points
  include a definition whose azimuth and skew angle are not ProjNet's
  defaults, so the test can tell a reader that reads them from one that
  ignores them.

- **`to-color`, `at-interpolate` and `cubic-bezier` in the MapLibre style
  dialect** (ADR-0088, SpatialEngine-ymh): the three interpolation constructs
  ADR-0076 named as unserved. `["to-color", value]` is the one coercion the
  dialect performs and it is asked for by name — a number clamped to [0, 1]
  and written to all three channels, or a colour name — so the MapLibre idiom
  of ramping a number into a colour (`… 5, ["to-color", 0], 10,
  ["to-color", 1]`) now compiles; a bare number on a colour property is still
  rejected. `at-interpolate` samples an existing ramp at a literal stop, which
  is what makes a ramp reusable as a `let`-bound scale.
  `["cubic-bezier", x1, y1, x2, y2]` is the CSS easing, solved as the
  ordinate of the cubic Bezier at the parameter where the abscissa is the
  linear progress (Newton with a bisection guard, no per-feature table), with
  the two abscissas required to lie in [0, 1] and trailing values defaulting
  to zero. A malformed easing, a non-numeric `at` and an unreadable
  `to-color` operand are each a typed `invalid.arguments` naming the
  operator.
- **Query `distance`/`units` band, `returnCentroid` and `returnZ`/`returnM`**
  (ADR-0085, SpatialEngine-u2x.16): the three §7.1 query rejects that were
  really engine verbs are served. `distance` is a band from the query
  geometry applied as a buffer in the layer CRS, with the unit code resolved
  from the same curated table and projected/geographic rule the Geometry
  Service uses, so it composes with every exact `spatialRel`.
  `returnCentroid` is a new `IGeometryMeasures.Centroid` verb (the area
  centroid, not the envelope middle) written beside each feature's geometry.
  `returnZ`/`returnM` select the output ordinates.
- **The Feature Server layer advertises the Z/M its data actually carries**
  (ADR-0084, SpatialEngine-fhf): `hasZ`/`hasM` are on the layer resource, so a
  client can preflight whether a dataset carries elevations or measures before
  it asks for them. They are a description of the data, not a capability, and
  ArcGIS clients branch on them — a client told a 2D layer has Z will send Z
  in query geometry and edit payloads — so they are emitted only where the store
  proves the ordinate. `DatasetDescription` now carries the layout its store
  declares (`geometryLayout`, default `xy`), PostGIS reads it from the
  geometry column's declared type (`geometry(PointZ,4326)` → `xyz`), and a
  two-dimensional or unconstrained column advertises neither key rather than
  `false`. SQL Server (whose spatial types have no Z/M) and the ArcGIS REST
  provider report `xy` until they can prove more.
- **The ArcGIS REST store carries the remote layer's `hasZ`/`hasM`**
  (ADR-0091, SpatialEngine-fhf.2): the provider dropped the booleans off the
  Feature Server layer resource, so a proxied 3D layer was described as 2D and
  the layer metadata advertised nothing. The remote's own declaration is the
  same kind of proof ADR-0084 accepts from a declared column type — schema,
  not a sample — and only a JSON `true` counts, so a layer that declares
  nothing still lands on the honest `xy` default. The query path sends
  `returnZ`/`returnM` for exactly the ordinates the description declares,
  because a Feature Server returns the extra ordinates only when asked, and an
  advertised flag the read path cannot back is the broken round trip ADR-0084
  names.
- **`findTransformations` is a search, and datum transformations are values**
  (ADR-0087, SpatialEngine-u2x.15, landed on ADR-0086): the operation used to
  be a reformulation of the catalogue — same datum `[]`, different datum one
  forward composite, `extentOfInterest` rejected *by name* because "the
  catalogue has no area-of-use model" — with the parameters welded to the
  adapter, so there was one possible answer and nothing outside the adapter
  could check it. A new `Spatial.Contracts.TransformationSearch` namespace
  carries `CrsTransformation`, `CrsTransformationStep`, `HelmertParameters`,
  `CrsAreaOfUse` and `CrsTransformationQuery` (records of strings, doubles
  and booleans, no packages), and `ICrsDirectory` gains `FindTransformations`.
  The graph is composed from the catalogue's own datum definitions, so it is a
  hub and spokes rather than a table of hand-written pairs: between two datums
  it offers the **direct** composed Helmert (the path the engine applies), the
  **concatenated** path through the WGS 84 pivot, and the same shift **reduced
  to three translations**, each carrying its steps, the seven EPSG-9606
  parameters it applies, its area of use and a stated accuracy. Accuracies
  combine in quadrature and the reduced form adds the first-order bound on what
  its dropped rotations cost (14.5 m measured against 41.9 m stated). Area of
  use follows the EPSG rule for the shape of the operation, so an empty
  intersection drops the direct candidate outright and `extentOfInterest`
  *filters* what is left instead of refusing — it is read in the source CRS's
  own coordinates and reprojected onto the geographic boxes the catalogue
  records. The search is symmetric: a reversed request returns the same
  operations with `transformForward: false`, and the default is every ranked
  candidate rather than one (`numOfResults` and the ArcGIS REST JS
  `numTransformations` both slice it). `project` now accepts a
  `datumTransformation` that names the operation it applies and refuses any
  other by naming that one; `vertical=false` is accepted and `vertical=true`
  stays refused. The published parameters are the engine's: a control point
  applies the returned Helmert to the London point by hand and lands within a
  millimetre of the PROJ 9 (OSTN15) reference. **Caller-visible changes:** a
  `findTransformations` response is a ranked array of candidates with
  `name`/`geoTransforms`/`accuracy`/`approximate`/`areaOfUse` (and `helmert` per
  step) instead of a one-element array, an unknown CRS is `invalid.arguments`
  rather than `[]`, and `project` no longer refuses `datumTransformation`
  outright.

- **The CRS catalogue's model is decided: WKT definitions, and datum quality
  data that is not part of a definition** (ADR-0086, SpatialEngine-u2x.28):
  the WKT path and the transformation graph were mutually incompatible because
  the graph read a datum's *accuracy* and *area of use* off the hand-written
  catalogue rows, which the WKT path deletes — and a WKT2 `GEOGCRS` genuinely
  carries neither, because both are attributes of the registered coordinate
  operation, not of the CRS. A definition therefore owns the datum's
  `TOWGS84` shift (a construction input ProjNet needs), and a new curated
  `EpsgDatumOperations` table beside the vendored WKT owns the accuracy in
  metres and the registered extent, joined to a definition by the datum's EPSG
  name. A datum with no published operation contributes no graph node rather
  than an invented accuracy, and a test fails if a geodetic definition is
  added to the WKT without a row. EPSG:3857 is unaffected: the pseudo-Mercator
  interception lives in the reader, and the graph builds no coordinate systems
  at all. The ADR also settles ADR numbering: the next free number on `main`,
  with `AdrNumberingTests` as the enforcement.
- **A large upload can be staged, resumed and then loaded** (ADR-0090,
  SpatialEngine-u2x.27): an upload that failed at 90% no longer starts over.
  `POST /api/uploads` opens (or re-opens) a staged upload, `PUT
  /api/uploads/{id}?offset=` appends a chunk whose first byte belongs at that
  offset, and `GET /api/uploads/{id}` answers with how many bytes have landed —
  the offset to resume from. `POST /api/ingest?upload=<id>` then loads the
  staged bytes. The chunks are **bytes**, not features, so the load is exactly
  the transaction it always was: a malformed row at the end of a resumed upload
  still fails the whole load and leaves no dataset, but the staged bytes survive
  the failure for a retry. A partial upload can never be mistaken for a
  complete one — `complete` is true only when a declared total is reached, and
  the ingest refuses anything else by name and offset; the staging is discarded
  only after the load commits, and an un-ingested upload is pruned after
  `Spatial:Uploads:MaxAgeHours` (24). An append addressed past the staged
  length is rejected *naming the offset to resume from*, an append behind it is
  accepted only when the re-sent bytes are identical (the lost-acknowledgement
  case), a refused append changes nothing, and a declared SHA-256 that does not
  match faults the upload rather than loading bytes nobody declared. The staged
  document is bounded by the same `Spatial:Ingest:MaxBytes` as a single-request
  upload. The .NET and TypeScript SDKs gain the staging verbs and a driver
  (`ResumableIngest.UploadAsync`, `ingestResumable`) that resumes from the
  host's offset, retries a chunk that failed in transit, and ingests only once
  every byte has landed. The Esri `/arcgis/admin/uploads` projection is
  unchanged: the neutral staging is the domain model, and a chunked Esri
  projection onto it is later work. The feature-cap stream wrapper duplicated
  between the two upload paths is now the one `IngestPageCap` helper, so both
  count the same way and answer with the same message.

### Changed

- **The MapServer and per-feature read surfaces push down, and the adapter
  keeps only what it must** (ADR-0112, SpatialEngine-u2x.12): the last five
  whole-layer reads in the GeoServices adapter moved onto faces the tree
  already had. MapServer `identify` reads a plan carrying the query geometry's
  envelope, and the tolerance-buffered intersection test plus the `layerDefs`
  and temporal filters stay the answer, so a layer whose `OBJECTID` is the scan
  ordinal — the one whose `layerDefs` clause is numbered off that ordinal —
  keeps its scan untouched. `find` reads a plan carrying the one restriction
  the predicate vocabulary can state without changing the answer (a searched
  field is not null) and keeps the case-insensitive comparison of the text
  itself: `LIKE` is case-sensitive on some back ends and not on others while
  the served search is case-insensitive on all of them, so a pushed pattern
  would drop matches rather than pre-filter them. `generateRenderer` asks the
  aggregate face for the minimum and maximum behind the class breaks and the
  distinct set behind the unique values, and keeps the quantisation
  (ADR-0055) and the scan for a `where` no store can read. The advertised
  layer extent is now a geometry-only projection rather than a whole-row read
  (the reduction it wants is an envelope statistic the aggregate vocabulary
  does not have yet). The per-feature (`FeatureServer/<layer>/<objectId>`)
  resource and the attachment targets resolve through the store's identity
  face, one targeted read for a request naming several ids, keyed by the
  `OBJECTID` each row carries rather than by the id the lookup was asked with
  — so a store whose `Feature.Id` is not the identity column (a
  source-identity ingest numbers features as it reads them) misses and the
  scan decides, rather than the wrong feature being served under the right
  object id. Every response is byte-identical to the pre-change output,
  including the `layerDefs` and `dynamicLayers` paths the compatibility
  reference pins, and the `not.found`, `invalid.arguments` and
  `serverError` refusals are unchanged.

### Fixed

- **A vector tile whose bounds collapse on one axis is rejected instead of
  encoding saturated infinities** (ADR-0101, SpatialEngine-a74.1): the MVT
  encoder places a vertex by dividing by the tile's extents, and a tile with no
  width or no height — a well-formed `Envelope(5, -9, 5, 9)`, which is not
  empty — divided by zero, saturated to `long.MaxValue` and went out as
  `09 0080400A00FF3F`: a `200`, a correct media type and a line of astronomical
  length on the client. Only `Bounds.IsEmpty` was checked. A tile now needs
  extent on both axes and is rejected as `invalid.arguments` before a layer is
  read, which also covers inverted and non-finite bounds. The frame is not
  narrowed to a centre line instead, because that would encode geometry at a
  position the caller never asked for. Ordinary tiles are byte-identical: a
  vertical line over `Envelope(0, -10, 10, 10)` still encodes as
  `098020E63C0A00CB39`.
- **Four datum operations now carry the numbers EPSG publishes**
  (SpatialEngine-u2x.28.1): the curated operation table behind ADR-0086 was
  reconciled against the EPSG Geodetic Parameter Dataset as carried by PROJ
  9.8.1 — EPSG v12.029, a citation a reader can reproduce rather than one
  they must take on trust — and four of its six rows did not match the
  registry. NAD83 stated 2 m where
  EPSG:1188 "NAD83 to WGS 84 (1)" states 4 m — the "accuracy 2m in each axis"
  in that record's remarks is a note on how the parameters were derived, not
  the accuracy of the operation, and a search between NAD83 and WGS 84
  reported it. OSGB36 stated 3 m where EPSG:1314 "OSGB36 to WGS 84 (6)" — the
  operation whose seven parameters are exactly the vendored `TOWGS84` node —
  states 2 m. ETRS89's area of use reached 32.88N-40.18E, which is no extent
  the registry publishes; EPSG:1149 is registered over extent 4755 at
  33.26N-38.01E. NZGD2000's area of use (166.36E-178.52E, 46.64S-34.1S) was not
  traceable to the registry at all; EPSG:1565 is registered over extent 1175,
  160.6E to 171.2W and 55.95S to 25.88S. Every row now names the EPSG
  operation and extent record it was read from, so a value can be checked
  against the registry and a row that drifts says where it drifted from, and
  tests pin the reconciled numbers rather than only proving coverage. Every
  bound is the one the registry publishes, at the precision it publishes it —
  ETRS89's eastern bound is 38.01E, not a rounded 38.0E. The one exception is
  New Zealand's eastern bound: EPSG's extent
  crosses the antimeridian and an area of use is a box, so it is the
  registered extent clipped at 180E, which keeps the datum in the graph
  (SpatialEngine-u2x.32).
- **A resumed upload is no longer refused for "the chunk does not fit"** (ADR-0090
  §3, SpatialEngine-u2x.27): the staging bounded an incoming chunk against the
  bytes already staged rather than against the offset the chunk was addressed
  at. A client resuming from the offset the host itself had reported — after a
  lost acknowledgement, or after an append interrupted once its bytes had
  reached the staged file — was answering a question the resume protocol exists
  to make answerable, and was turned away by a cap check that the overlapping
  bytes were never going to breach. The bound is now measured from the chunk's
  own offset, so the re-sent overlap counts for what it is and the bytes an
  append actually adds are still held to the declared total and to
  `Spatial:Ingest:MaxBytes`.

- **The Feature Server layer advertises the capability flags its query surface
  earns** (ADR-0081, SpatialEngine-u2x.25): the layer resource now carries
  `supportsQuantization` — at the top level, where the ArcGIS REST JS gate
  reads it, and inside `advancedQueryCapabilities`, where the S3 layer
  reference pins it — so a tiled web-map client finally sends the
  `quantizationParameters` the engine has served since ADR-0079 instead of
  seeing a false gate and asking for full precision. Alongside it,
  `advancedQueryCapabilities.supportsPaginationOnAggregatedQueries` is true,
  because an `outStatistics` response pages and reports
  `exceededTransferLimit` with a `resultPaginationToken`. The rest of the flag
  family stays absent rather than advertised `false`: each unserved flag
  (`supportsTrueCurve`, `supportsLod`, `supportsQueryWithDistance`,
  `supportsQueryWithDatumTransformation`, `supportsQueryAnalytic`,
  `supportsQuantizationEditMode`, `supportsValidateSQL`, `supportsCalculate`,
  …) names behaviour the facade rejects by name, and a key a client can read
  is a claim. A table — a dataset with no geometry field — omits the per-layer
  quantization flag, having no coordinates to quantize.

- **Deeper label placement: candidates, priority, line placement, font faces
  and a marker set** (ADR-0080, SpatialEngine-u2x.20): a label is no longer
  offered a single position. Each feature generates ordered candidates — a
  point offers its position and the four anchor offsets around it, and
  `symbol-placement: line` offers a candidate every `symbol-spacing` pixels
  along the projected line, aligned to the local direction, then the same
  candidates again in reverse — and the greedy first-fit takes the first that
  places anything, so a label that loses its first choice usually still draws.
  Which label wins is now a style priority (`symbol-sort-key`), decided in a
  placement pass of its own across all symbol layers — the order is
  `symbol-sort-key`, then the style's document order, then the existing
  identity/envelope-centre tie-break, which keeps it a total order — while the
  pixels still composite in document order. `symbol-allow-overlap` (inheriting
  `text-`/`icon-allow-overlap`) and `symbol-ignore-placement` bypass the
  collision test. `text-font` stops being a style error: the bundle grows to
  the four digest-pinned Noto Sans 2.003 faces, each name in the list resolves
  through a documented fallback chain (family and weight/style, else the
  nearest bundled weight, else the next name), and a family the engine has
  never heard of renders in the default face instead of failing the render.
  `text-transform`, `text-letter-spacing`, `text-line-height` and
  `text-rotate` are applied to the shaped string, and the sprite registry
  gains the circle, square, diamond, triangle and ring markers beside
  `default-marker`. Covered by the new placement, font and sprite suites and a
  second committed golden render (`symbols-line.png`); the existing
  `symbols.png` golden is byte-identical, because a style whose labels do not
  collide takes the same candidate and draws the same pixels it always did.

- **A deviation allowance is its own geometry verb** (ADR-0079,
  SpatialEngine-u2x.3): the Feature Service `query` now honours
  `maxAllowableOffset` instead of accepting it and returning full precision,
  and serves `quantizationParameters` instead of rejecting it by name. Both
  go through the new `IGeometryOperations.Generalize`, which states how far
  the answer may be from the true geometry rather than how coarsely the
  algorithm should thin it: every returned vertex is a vertex of the input,
  every input vertex stays within the allowance, and an allowance too wide to
  spend without changing a feature's geometry kind returns the input
  unchanged — so an offset of zero is byte-identical to full precision.
  `quantizationParameters` snaps x, y, z and m to the view grid anchored on
  the request's extent, then spends the rest of the budget on the same verb;
  an unservable `mode` or `originPosition` is still rejected by name.

- **A WKT definition path for the built-in CRS catalogue** (ADR-0027,
  SpatialEngine-u2x.17): every CRS the ProjNet provider serves is now
  defined as EPSG WKT and read by a reader of the engine's own, so the
  catalogue is no longer a hand-written parameter list. `ProjWkt` reads both
  the OGC WKT1 and the WKT2 dialect and hands what it reads to the one
  programmatic builder the catalogue already used — the UTM families are one
  WKT template with two substituted numbers, and the hand-written rows, the
  generated zones and the definitions that exist only as WKT are
  indistinguishable to callers. The Pseudo-Mercator workaround survives as
  code rather than as a refusal: the reader intercepts the identifiable
  spellings (the projection names, the CRS names, the EPSG
  3857/3785/900913/102100/102113 authorities) before construction and routes
  them to the known-good programmatic path, which matters because EPSG:3857
  is very widely published with the projection named `Mercator_1SP`, and
  ProjNet's own WKT reader reads that spelling as a plain Mercator —
  **33,931 m** too far south at Berlin's latitude, measured by test rather
  than asserted in a comment. Two definitions that are new to the served set
  come in with the path, both read as WKT and both verified against something
  other than the library that reads them: **EPSG:3395** (WGS 84 / World
  Mercator) against EPSG Guidance Note 7-2's method 9804, and **EPSG:2193**
  (NZGD2000 / New Zealand Transverse Mercator) against the projection's own
  analytics — its central meridian, scale factor, false easting, false
  northing and latitude of origin, and the meridian-arc series. **EPSG:3857
  and the other fourteen codes the catalogue already served are
  byte-identical**, pinned to the last bit, the catalogue is still built once
  and lazily, and the reader resolves only the projection methods it has
  been checked for, so a definition is never served with coordinates that are
  quietly wrong.

- **Ground-distance buffering** (ADR-0075, SpatialEngine-u2x.14): the
  GeoServices `buffer` operation now serves a linear `unit` against a
  geographic buffer CRS — the commonest request there is — through a new
  `IGeodesicBuffering` contract verb implemented as
  `ProjNetGeodesicBuffering`. It reprojects onto a local transverse Mercator
  sized by the work, buffers, and projects back: within **0.05% relative of
  the geodesic** for a working radius up to 300 km, and a typed
  `invalid.arguments` failure naming the radius and the remedy past that
  instead of a shape it cannot stand behind. `geodesic=true` is served on
  that path and refused by name against an angular unit, no unit or a
  projected `bufferSR`; `unionResults=true` dissolves the per-input buffers
  into one geometry. The projected-`bufferSR` planar path is unchanged and
  remains the exact answer inside a valid zone.
- **UTM zone families in the built-in CRS catalogue** (ADR-0027,
  SpatialEngine-u2x.6): the catalogue now *generates* the projected families
  it used to enumerate by hand — the UTM grid over all sixty zones in both
  hemispheres (EPSG 32601-32660 and 32701-32760) plus the ETRS89 and NAD83
  UTM bands over their own datums — from one Transverse Mercator parameter
  template. `Describe` and `Transform` therefore succeed for any UTM zone
  instead of failing with `invalid.arguments` outside the seven zones that
  were typed out. A generated zone is byte-identical to the hand-written row
  it replaces (the served coordinates of all fifteen pre-existing codes are
  pinned to the last bit), the catalogue is still built once with each CRS
  built on first use, and the ProjNet Pseudo-Mercator workaround is
  untouched.
- **Relationships are declared, traversed and written**
  (ADR-0077, SpatialEngine-u2x.22): a map layer now declares how its records
  relate to another of the map's layers over two key columns
  (`LayerRelationship`: one-to-one, one-to-many, or many-to-many through a
  join dataset), validated structurally when the map is stored and against
  the live schemas where the declaration happens. The declaring layer
  advertises the relationship in its `relationships` metadata, the Feature
  Service serves `queryRelatedRecords` over it (the related layer's own
  `where`, `outFields`, `geometry`/`spatialRel`, `time` and `outSR` all
  apply), and `relate`/`unrelate` move the same key behind the existing
  admin-token edit gate, one result per origin/related pair. The
  `geoservices-compatibility.md` §7.1 non-goal that listed
  `queryRelatedRecords` (and, wrongly, attachments) as absent is now
  corrected.


- **SQL Server store provider** (ADR-0073, T-113): `Spatial.Stores.SqlServer`
  implements the catalogue, feature, lookup, transaction, editing, ingest and
  attachment faces on Microsoft.Data.SqlClient, wired into the host under the
  store key `sqlserver` (`Spatial:SqlServer:ConnectionString` or
  `SPATIAL_SQLSERVER_CONNECTION`). Geometry crosses as OGC WKB, a dataset's CRS
  is discovered from its data and then from a provider-owned `spatial_datasets`
  sidecar, only XY geometries are written (Z/M is refused, not flattened), and
  the store-backed matrix is proven against a real SQL Server container with
  Testcontainers alongside a DB-free unit suite.

### Changed

- **`deleteFeatures?where=` is served by the store, not by a facade-side
  scan** (ADR-0074 §7, ADR-0097 §1, SpatialEngine-7rq): the write path's last
  full read. The clause compiled to the one core predicate vocabulary and is
  handed to the store as the query plan's `Where`, so a delete by where reads
  the matching features instead of scanning and materialising the whole
  dataset, and deletes exactly the rows that read returned — no identity round
  trip over features the read already resolved. The resolver is the one the
  read paths already use, so a clause naming the synthetic `OBJECTID` is
  rewritten onto the layer's identity column and keeps its meaning, and a layer
  whose object id is the scan ordinal keeps the clause in the facade (such a
  layer is not editable anyway). The per-position `deleteResults` are
  unchanged; nothing in the request, response or capability flags moved.
- **The feature query answers `Intersects` with the OGC intersect patterns,
  not by building the intersection** (ADR-0036, SpatialEngine-51k):
  `spatialRel=esriSpatialRelIntersects` was exact but materialised
  `Intersection(feature, query)` for every candidate the envelope pre-filter
  admitted, on a path that runs per feature — an overlay per row to answer a
  question the DE-9IM pattern table already answers. It now reads the four OGC
  intersect patterns (`T********`, `*T*******`, `***T*****`, `****T****` —
  the interiors meet, either interior reaches the other's boundary, or the
  boundaries meet) out of the one `SpatialRelationPredicates` table the other
  verbs read, behind the same envelope pre-filter and with no `Intersection`
  call on the query path. The patterns are tried as four short-circuiting
  `Relate` calls because a DE-9IM pattern is a single nine-character matrix
  and the grammar takes no `|` alternation. The Geometry Service `relation`
  reads the same table entry, so both endpoints still answer one way; the
  answers are unchanged, since a non-empty intersection is exactly this
  union.
- **Geometry Service `relation` serves the named Touches, Overlaps and
  Crosses relations** (ADR-0036, SpatialEngine-zpz): `relation=esriSpatialRelTouches
  |esriSpatialRelOverlaps|esriSpatialRelCrosses` used to be rejected by name,
  even though the feature query path has answered them exactly since
  SpatialEngine-u2x.2 — so the same verb had two answers depending on which
  endpoint served it. They now read the one dimension-aware DE-9IM pattern
  table the query path reads (`SpatialRelationPredicates`), with the left
  geometry in the feature's role and the right in the query's; no pattern
  string is restated, and an unrecognised relation name is still a named
  `invalid.arguments`.
- **The map root advertises the time relation it actually applies**
  (ADR-0100, SpatialEngine-oas): `supportsTimeRelation` is `false`, and
  `esriTimeRelationContains`/`esriTimeRelationWithin` are typed
  `invalid.arguments` on MapServer `export` and `identify` instead of being
  accepted and served as overlaps. The flag said the service distinguishes
  the relations and it did not — a client that branched on it got a
  confidently wrong temporal window. `esriTimeRelationOverlaps` (the default)
  is still accepted, because that is the relation the engine's rule — "any
  date value inside the window" — is. Serving the other two needs a feature
  temporal extent to compare the window against, which the engine does not
  model; that is a model decision, filed as its own follow-up rather than
  smuggled in here.
- **The store query surface does the shaping, not the adapter**
  (ADR-0098, SpatialEngine-u2x.9 + SpatialEngine-u2x.9.1): `outFields` becomes
  the store's projection, `orderByFields` the store's ordering,
  `resultOffset`/`resultRecordCount` its page start and cap, and
  `returnCountOnly`, `returnDistinctValues` and `outStatistics` become the
  store's own count, distinct set and grouped aggregate over a new optional
  `IFeatureAggregateStore` face. A read answers a `FeatureQueryPage` — the
  batches, a continuation cursor, and the total when the store computed it
  cheaply — so a `returnCountOnly` on a large PostGIS table is a `COUNT(*)`
  rather than a materialised scan, and an `outStatistics` response is a
  `GROUP BY`. A plan is validated once, against the dataset's schema, at the
  boundary that received it, so an unknown `outFields` entry or a negative cap
  is still the same typed `invalid.arguments`.
- **The Esri `where` clause rides in the store's plan** (ADR-0098, ADR-0097,
  SpatialEngine-u2x.9.1): a served `where` now compiles to the plan's
  `Predicate` and is answered by the store rather than by the adapter, but
  only on a layer whose `OBJECTID` is store-derived — a layer whose `OBJECTID`
  is the scan ordinal keeps the clause per-feature, so a filtered feature never
  comes back with an id that depends on the query. The same rule now applies
  *inside* the PostGIS and SQL Server stores: a dataset with no identity column
  names its features by the ordinal of the read, so a `WHERE` that reached SQL
  would renumber them, and such a dataset keeps the restriction in the caller.
  No served `OBJECTID` value changes.
- **The reference is one piece of code** (ADR-0098, SpatialEngine-u2x.9.1): the
  in-memory reference executor and the reference predicate evaluator live
  together in `Spatial.Querying`, so the in-memory store, the demo store, the
  ArcGIS REST store and every store that evaluates a plan in memory share one
  definition of the plan's semantics instead of one per provider. A store that
  pushes the restriction into its own dialect finishes the plan with the shared
  executor, and the pushdown-equals-reference suite — now run against PostGIS
  *and* SQL Server, and extended to plans that carry a predicate beside the
  order, the cap and the box — holds every provider to it, values and feature
  identities alike.
- **One predicate grammar across the engine** (ADR-0074, ADR-0097,
  SpatialEngine-u2x.8): the store filter is a core-typed `Predicate` tree
  instead of a string, and there is one grammar instead of three. The
  PostGIS and SQL Server `*Filter{Lexer,Parser,Sql}` trios — two copies of
  one SQL-flavoured mini-language, ~800 lines each — are gone, replaced by
  one parsed-at-the-boundary tree and two thin per-provider compilers that
  keep the parameterisation guarantee. The Esri `where` grammar now
  *parses* to that tree and hands it to the store instead of evaluating
  features (`EsriFilterLogic` is retired), and the in-memory store evaluates
  the whole vocabulary for the first time rather than refusing filters as
  "bbox queries only". The `filter` query parameter keeps its published
  syntax, and the demo catalogue filters for the first time too. A shared
  conformance fixture (one dataset, one row set, 29 cases) is answered by
  memory, PostGIS and SQL Server alike, and an unknown column is still a
  typed `invalid.arguments` on every one of them.
- **A pushdown is allowed only where it is identity-preserving**
  (ADR-0097, SpatialEngine-u2x.8): an attribute clause reaches a store only
  when the layer's `OBJECTID` is store-derived. A layer whose `OBJECTID` is
  the scan ordinal (ADR-0037) keeps the clause as a residual per-feature
  match, because a store returning only the matching rows renumbers that
  key — so the same feature would come back with an id that depends on the
  query, breaking `objectIds`, `returnIdsOnly`, paging and the edit
  round-trip.
- **A filter literal binds as its column's kind** (ADR-0097,
  SpatialEngine-u2x.8): a guid-formatted string binds as a `Guid`, and a
  number against a date-time column binds as the instant it already is. A
  string literal against a `uuid` column used to compile to `uuid = @p0`
  with a text parameter, which is not an operator Postgres has, so the
  query failed at execution as `store.unavailable` for a filter that should
  have matched rows. A pair that means nothing to the reference evaluator
  (`uuid = 5`, `bit < true`, `LIKE` on a non-text column) now answers the
  same constant the evaluator does, rather than coercing or failing.
- **The Esri geometry writer states its `hasZ`/`hasM` flags**
  (ADR-0085, SpatialEngine-u2x.16): a three-ordinate Esri coordinate array is
  Z when only `hasZ` is set and M when only `hasM` is set — the codec's own
  read rule — so a 3D geometry was being written in a form the same codec
  would read back with the wrong ordinate. Checked as the bead asked: the
  canonical binary codec round-trips `Xyzm` exactly, so the loss was Esri
  codec depth, not the engine model.

- **Geometry Service `simplify` is generalization again** (ADR-0036, §7.0.5):
  it now calls `IGeometryOperations.Simplify` (Douglas-Peucker) with the
  tolerance the request carries — `deviation`, or mutually exclusively
  `value` — the same engine verb `generalize` already used under its
  `maxDeviation` name. It used to call `IGeometryProcessing.Repair`
  (topological MakeValid) and ignore both parameters, so a valid geometry
  came back whole and a self-intersecting one was silently repaired instead
  of thinned. A request with neither tolerance, with both, or with a
  negative one is now a named `invalid.arguments` failure. Topological
  repair keeps its engine verb and its home: no Esri operation names it, so
  nothing in the facade maps to it.

- **Feature Service `spatialRel` is exact DE-9IM, not envelope arithmetic**
  (ADR-0036, SpatialEngine-u2x.2): `Contains`, `Within`, `Touches`,
  `Overlaps` and `Crosses` on the Feature/Map query and Image Service
  catalog paths are now intersection patterns over
  `IGeometryRelations.Relate`, with the envelope tests kept as the
  pre-filter. The interior results are unchanged; the boundary cases the
  approximation documented as approximate now follow OGC semantics, so three
  answers change: a containee lying *on* the container's boundary is no
  longer `Contains`, a point or line on a feature's boundary is now
  `Touches`, and a line crossing a feature is no longer `Touches`.
  `Intersects` stays the non-empty intersection and
  `esriSpatialRelEnvelopeIntersects` stays the envelope test. The Geometry
  Service `relation` operation, which already mapped the same verbs to the
  same patterns, is unchanged.

### Fixed

- **A write now invalidates the tiles derived from it** (ADR-0083,
  SpatialEngine-u2x.21): the tile cache key fingerprinted the *request*, so
  nothing in it moved when a feature was written, edited or ingested, and
  every cached tile of an edited map stayed stale until someone called
  `DELETE /api/render/cache` by hand. A store may now report a per-dataset
  content version (`IVersionedFeatureStore`), the in-memory store bumps it on
  every write, edit and ingest, and every tile key — the neutral raster single
  and batch routes, the map raster and MVT routes, and the Esri
  `MapServer/tile` and `MapServer/vectorTile` routes — folds it in. Tiles
  report the version they were rendered at in `X-Tile-Version`, which
  `SpatialClient.Tiles.RenderWithVersionAsync` returns. A store that reports no
  version (PostGIS, SQL Server, demo, ArcGIS REST) behaves exactly as before.
- **Fused-cache MapServer root advertises the tile scheme reference**
  (ADR-0048): a tiled MapServer root now serves its spatial reference,
  `initialExtent`/`fullExtent` and units in the tiling SR (layer extents
  reprojected server-side, Web-Mercator inputs clamped to validity) instead
  of the data CRS. Advertising 4326 extents alongside a 3857 `tileInfo`
  made QGIS derive Null-Island tile indices for a real canvas — every tile
  request 200, every tile blank. Untiled roots still advertise the data CRS.
- **MapServer tile/root diagnostic logging**: one `Debug` event per tile
  resolves the requested address to its rendered geography and scheme, and
  the root logs its advertised SR/extents versus the tile-scheme SR, so a
  client fetching the wrong tiles is distinguishable from the server
  rendering the wrong geography.


## [0.3.0] - 2026-09-15

### Added

- **Feature attachments end to end** (ADR-0065/0066, T-060/T-061/T-088):
  the additive `IFeatureAttachmentStore` SDK capability with provider-owned
  bytes, served `hasAttachments`/`queryAttachments`/add/update/delete plus
  the single-attachment bytes resource (the OpenAPI snapshot gains
  `.../attachments/{attachmentId}` GET), with PostGIS sidecar persistence.
- **Guid identity columns** (ADR-0067, T-058): `uniqueIds` for guid-identity
  columns are served in canonical form.
- **ImageServer authored metadata XML** (ADR-0068, T-056): the authored
  ISO/FGDC document is served verbatim as `application/xml` at both the
  service level and the per-item level.
- **Esri compatibility projections** (ADR-0054–0059, T-071): ImageServer
  legend/find/statistics/histograms/attribute-table/thumbnail/metadata,
  MapServer legend/queryDomains/queryLegends/generateRenderer, modern
  Feature query params, percentile statistics with capability-flag honesty,
  Map export time/dynamicLayers/layerOption/cached-root honesty, and
  Image capability flags with named rejects; offline/async surface stays
  rejected by name (ADR-0060). Proven by the Esri-docs replay suite D.
- **Feature write-model extensions** (ADR-0061): service query,
  generateRenderer reuse, validateSQL, aggregation honesty, attachments.
- **Workbench**: service Parity page (T-072), Geometry playground + Image
  tab (T-073), Chaos simulation toggles proving typed error mapping
  (T-074).
- **Performance suites**: suite C throughput smoke and suite D full-job
  baselines with a nightly job (T-077/T-078/T-086), OpenLayers proof at
  vectorCount 12 (T-090).
- **Interop test infrastructure**: live Esri fixture refresh script
  (T-065) with the GeometryServer root fixture drift pinned (T-093).
- **Documented non-goals**: vector tiles and OGC API Tiles (ADR-0062);
  BenchmarkDotNet for the benchmark suite (ADR-0063).

### Changed

- **Quality loop cleared to green again** (ADR-0069): high-complexity
  methods split into cohesive engines (`FeatureStatisticsEngine`,
  `FeatureSpatialMatcher`, `FeatureProjection`, `FeatureResponseWriter`,
  `FeatureQueryHandlers`, `FeatureAttachmentHandlers`,
  `GeoServicesResolution`, `ImageFileHandlers`, `ImageLegendBuilder`,
  `WmsGmlWriter`) with real unit tests; CRAP 0 of ~2600 methods,
  authored branch coverage 83.2% (floor 70%), Core.Tests mutation 91.2%
  (break 80). The two namespace `architectural-rigidity` diagnoses are
  advisory (shared-codec coupling prescribed by ADR-0035; the tool offers
  no per-diagnosis lever), so `failOn.severity` returns to `high`, and the
  structural architecture suite is waived out of the mutation gate.
- **Esri admin projection deltas aligned** (T-062): uploads MaxFeatures
  cap plus publish merge semantics; demo catalogue summaries cached
  (T-096); WorldCities snapshot loads lazily (T-095); CRS lookups cached
  on the transform hot path (T-087); query burst-tail diagnosed with Esri
  writer bytes sent direct (T-092).
- **Deterministic legend swatch URLs** (T-085).
- **ADR-0059 item 5 amended** (T-082): `hasHistograms` for every
  real-valued band format.

### Fixed

- **Malformed multipart ingest** (T-094): rejected as a typed 400 naming
  the file part.
- **WFS GetFeature ids** (ADR-0064, T-084): ids are scoped per typeName,
  so unique ids hold across layers.

## [0.2.0] - 2026-09-14

### Added

- **WMS 1.1.1 capabilities dialect** (ADR-0053, T-045). `GetCapabilities`
  with `VERSION=1.1.x` serves the legacy dialect: DTD doctype, unqualified
  `WMS_Capabilities`, the SRS vocabulary and `LatLonBoundingBox` in lon/lat
  order with one `BoundingBox` per SRS. Absent or 1.3.x versions keep the
  1.3.0 dialect (what QGIS sends on add-layer); anything else is
  `InvalidParameterValue`. 1.1.1 `GetMap`/`GetFeatureInfo` KVP already
  worked; now 1.1.1 clients (GDAL, OWSLib) can negotiate from capabilities.
  `GetStyles`, `DescribeLayer` and `SLD`/`SLD_BODY` stay explicit
  `OperationNotSupported` rejects — the recorded client corpus carries no
  trace of them.

- **OGC failure diagnostics** (ADR-0045). The WMS/WFS adapter now logs one
  structured event per operation with the `request` operation and the merged
  request parameters, and logs a rejected operation at `Warning` with the
  mapped OGC `ServiceException` code, reason and HTTP status. A blank or
  400-rejected WMS layer in an interop client (for example QGIS) is now
  diagnosable from the log alone; previously only the path and status
  appeared.
- **Maps are the unit of authoring and exposure** (ADR-0053). `Map` replaces
  `Publication` across the engine: a named, ordered set of styled layers from
  one keyed store plus the set of services it exposes — `Feature`, `Map`,
  `Tiles`, `Wms`, `Wfs` and `Image`. A layer is owned by its map, so the same
  dataset can be styled differently in different maps. `IMapRegistry`
  (`Spatial.Provider.Maps`) persists maps to `maps.json` and reads a legacy
  `publications.json` once for migration; the GeoServices adapter serves only
  the services a map enables. New surfaces: map tiles at
  `GET /api/maps/{name}/tiles/{z}/{x}/{y}.{format}`, and OGC WMS 1.3.0 / WFS
  2.0.0 in the new `Spatial.Adapter.Ogc`. The neutral host API moves to
  `/api/maps` (deprecated `/api/publications` aliases remain for one
  release), and the workbench Composer becomes the Maps experience with
  service toggles and copyable endpoint URLs. The TypeScript and .NET SDKs,
  the seed tool and the OpenAPI snapshot move to the map contract (the CLI
  composes `Map` documents too).
- **COG and tiled GeoTIFF support** (ADR-0051, plan I4): the NetVips raster
  catalogue now reads a tiled/pyramidal GeoTIFF's structure (`tile-width`,
  `tile-height`, `n-subifds`) into `RasterInfo`'s block and pyramid fields,
  opens tiled rasters for random access, exports a downscale from the
  coarsest internal overview that still covers the output, and reports the
  ImageServer `minPixelSize`/`maxPixelSize` from the pyramid depth. A
  COG-style tiled+pyramidal file can be written through the concrete
  `VipsRasterCatalogue.WriteCogAsync` storage operation. No new dependency:
  libvips already covers the format, so the GDAL follow-up trigger is
  unchanged.
- **ImageServer catalog operations** (ADR-0051, plan I3): the GeoServices
  ImageServer now serves the full catalog `query` (the Feature Service safe
  `where` subset, `objectIds`, geometry, `outFields`, `orderByFields`,
  paging, ids/count/extent/distinct and `outSR`), the §8.2 Raster Image and
  §8.3 Thumbnail resources, and the §8.0.7 Download Rasters / §8.5 Raster
  File surface. `IRasterCatalogue` gains `ListFilesAsync`/`ReadFileAsync`
  over opaque provider-owned file ids (paths never cross); raw download is
  opt-in (`Spatial:GeoServices:AllowRasterDownload`), size/file-capped and
  range-capable. `Spatial:Raster` can now declare catalog attributes and
  items, so catalogs are configurable end-to-end rather than test-only.
- **Spatial CLI** (ADR-0052): a dependency-free console client of the public
  host API at `clients/dotnet/Spatial.Cli`. It adds datasets through the
  neutral ingest route, composes styled maps (FeatureServer, MapServer or
  ImageServer), stores the workspace as a versioned declarative
  `spatial.json`, and reports each map's GeoServices endpoint. Descriptive
  long flags, a `--json` envelope, `--dry-run` and stable exit codes make it
  script- and LLM-friendly; it publishes as one self-contained binary.
  Quality-gated through `tests/unit/Spatial.Cli.Tests`.
- **Map labels and sprite symbols** (ADR-0049): the Skia
  renderer's MapLibre subset gains `symbol` layers — `SkiaSharp.HarfBuzz`
  text shaping, a deterministic label placement/collision pass, and
  `Svg.Skia` sprite icons. Fonts are an embedded, pinned `NotoSans` resource
  (no system-font dependence) and the default marker sprite is embedded
  (ADR-0049). Unsupported symbol properties stay typed `invalid.arguments`.
- **ImageServer projection** (ADR-0051): a `PublicationKind.Image`
  publication is served as an ArcGIS ImageServer — root metadata (extent,
  pixel size, band count, pixel type, service data type, catalog
  `fields`/`objectIdField`), raster info, catalog item/listing, `identify` and
  `exportImage` (`f=image` bytes or JSON `href`, bbox/image SR, png/jpg/tiff,
  interpolation, compression, `pixelType`, `noData`). The raster boundary is
  provider-owned: `Spatial.PluginSdk` gains the core-typed `IRasterCatalogue`
  contract (no raster values, no third-party types), `Spatial.Imagery.Vips`
  implements it over the managed NetVips path behind `Spatial:Raster`
  configuration, and only encoded image bytes, core metadata and core
  geometry footprints cross the contract. GDAL is a measured-demand
  follow-up; raster analytics/functions remain non-goals. Architecture tests
  pin the SDK's package-free, core-typed surface.
- **Persisted per-layer style on publications** (ADR-0047):
  `PublicationLayer` gains an optional MapLibre style fragment (`string?`),
  validated as a JSON array of style-layer objects and persisted in
  `publications.json`. The workbench composer writes it on publish and reads
  it back on load, and `POST /api/publications/{name}/render` renders a
  publication server-side.
- **MapServer projection** (ADR-0048): a `PublicationKind.Map` publication is
  served as an ArcGIS MapServer — root, layer, `query`, `identify`, `find` and
  render — with the persisted style lowered to `drawingInfo`; covered by
  ArcGIS REST JS end-to-end tests.
- **Rich MapServer style metadata** (ADR-0050): the adapter projects the
  persisted MapLibre fragment onto `uniqueValue` and `classBreaks` renderers
  (equal-value and interval `filter` siblings), the single-field `esriTS`
  `labelingInfo` subset, and coded-value/range `domains` for the rendered
  field; the §4.7 image resource is mounted and returns a typed `not.found`
  (the engine has no picture symbols). All Esri types stay inside
  `Spatial.Adapter.GeoServices`; the ArcGIS REST JS e2e reads a rich layer.
- **Engine-side ingest reprojection**: `POST /api/ingest` accepts a
  `sourceSrid` and transforms every decoded page to the target SRID through
  the ProjNet `ICoordinateTransforms` service, so uploaded data in a curated
  CRS lands in a declared column CRS. The .NET and TypeScript SDKs pass the
  optional parameter through.
- **Seed tooling** (`eng/seed.sh`, `tools/seed/`): an on-demand script that
  fetches real public data (Natural Earth, USGS), ingests it — including the
  4326 → 3857 reprojection — and publishes a set of styled feature and map
  services through the neutral admin API, idempotently.
- **Structured logging to Seq** (ADR-0045): `Spatial.Host` logs through
  Serilog — console always, Seq when `Spatial:Logging:Seq:Url`
  (`SPATIAL_SEQ_URL`) is set — with one structured event per request
  (`UseSerilogRequestLogging`, server errors at `Warning`), a startup
  summary and actionable configuration warnings. The Aspire AppHost runs a
  Seq container (`Aspire.Hosting.Seq`) and injects its endpoint; the host
  still runs with no Seq (ADR-0018). Diagnostics carry configuration
  *state* only, never a connection string or token.
- **Map composer**: a workbench
  screen that composes engine datasets into an ordered, styled MapLibre
  preview — add catalogue datasets or upload GeoJSON/NDJSON/CSV inline,
  reorder layers by drag and drop, style them, and publish the composition
  as a neutral feature or map service through the existing
  `PUT /api/publications/{name}` and `POST /api/ingest` routes. Loads and
  deletes existing services, preserving their stable layer ids. Per-layer
  style is persisted with the publication as a MapLibre fragment (ADR-0047);
  the composer round-trips it on publish and load.
- **Tiles and a pluggable tile cache** (ADR-0046): the core-typed
  `ITileScheme`/`ITileCache` contracts in `Spatial.PluginSdk`
  (`TileCoordinate`, `TileLevel`, `TileCacheKey`) with the
  `Spatial.Tiling.WebMercator` (EPSG:3857 XYZ) scheme as the first
  implementation, a host-local in-memory LRU `ITileCache` (configurable byte
  and entry bounds), and `TileService` (cache-aware single tile plus an
  ordered, bounded-parallel batch). The host serves
  `POST /api/render/tiles/{z}/{x}/{y}.{format}`,
  `POST /api/render/tiles/batch`, `GET /api/render/tiles/capabilities` and
  `DELETE /api/render/cache`; the .NET client exposes
  `SpatialClient.Tiles.RenderAsync`/`CapabilitiesAsync` and the TypeScript
  client `renderTile`/`renderTiles`/`tileCapabilities`.
- **Raster rendering pipeline** (ADR-0044): the core-typed
  `IMapRenderer`/`IRasterOperations` contracts and DTOs in
  `Spatial.PluginSdk`, the `Spatial.Rendering.Skia` vector rasterizer
  (MapLibre-subset `background`/`fill`/`line`/`circle`, attribute filters,
  zoom windows, bbox pushdown, screen-space simplify/cull) and the
  `Spatial.Imagery.Vips` NetVips imagery pipeline
  (read/normalise/compose/encode). The host serves `POST /api/render` and
  `GET /api/render/capabilities`, configured by `Spatial:Rendering` and
  `Spatial:Imagery`; the .NET and TypeScript clients expose `RenderAsync` /
  `render`.
- **Ingest codec** (ADR-0041): `Spatial.Interop.Ingest` decodes GeoJSON,
  newline-delimited GeoJSON and CSV uploads into canonical `FeatureBatch`
  pages with inferred schemas (`DatasetDecoder.Decode`). Core-only; no host
  wiring yet.
- **Ingest and publication SDK contracts** (ADR-0041):
  `IPublicationRegistry` with the core-typed
  `Publication`/`PublicationKind`/`PublicationLayer` runtime service registry,
  and the additive `IDatasetIngest`
  (`IngestRequest`/`IngestOutcome`/`IngestIdentity`) atomic bulk
  create-and-load capability. Contracts only — implementations, the host
  admin API and the Esri admin projection land in later phases (ADR-0041).

### Changed

- **Scale-aware WMS DPI** (ADR-0053, T-045). The QGIS `dpiMode=7` triple
  (`DPI`, then `MAP_RESOLUTION`, then `FORMAT_OPTIONS` dpi:N) now drives
  rendering instead of being accepted and ignored: `MapRenderRequest`
  gains a `Dpi` member (default 96, the CSS reference pixel style sizes are
  defined at) and the Skia pipeline scales paint sizes linearly with it
  while the output frame keeps the requested size. A malformed `DPI` or
  `MAP_RESOLUTION` is `InvalidParameterValue`; 96 dpi renders byte-identical
  to before, so existing QGIS traffic is unaffected.

- **Quality loop cleared to green** (ADR-0040): the raster/MapServer/
  ImageServer work plus pre-existing baseline debt were paid down in one
  pass — high-complexity methods split into cohesive services
  (`MapStyleProjection`, `ImageService`, `VipsRasterCatalogue`,
  `MapRenderEngine`, the GeoServices endpoint groups and `VipsEncoder`),
  `Program.cs` de-top-levelled off the CRAP `Program.<Main>$` entry, and
  real unit tests added. `crap4dotnet` CRAP, authored branch coverage,
  `.dependably` metrics and the warnings gate are all green.
- **Viewport bbox pushdown direction fixed** (ADR-0046):
  `GeometryPipeline.TransformEnvelope` transformed viewport bounds the wrong
  way (`dataset CRS → viewport CRS` instead of `viewport CRS → dataset CRS`),
  which only surfaced for a world-covering Web-Mercator tile against a
  geographic dataset. The direction is corrected and pinned by a unit test.
- **Quality metrics gate recalibrated** (ADR-0040): `.dependably` now uses
  published thresholds (cyclomatic ≤ 15, cognitive ≤ 15, nesting ≤ 4, MI
  ≥ 20, in-repo coupling ≤ 40), disables the raw LCOM4 rule (it is
  meaningless for stateless types and gates through the tool's guard-aware
  diagnoses) and sets `failOn` to `moderate`. The metrics gate drops from
  26 high findings to 3 high + 1 moderate, all genuine.
- **Research code excluded from the metrics gate**: `.dependably` ignores
  `**/research/**`, so research probes are never measured as product code.

### Removed

- **Deprecated `/api/publications` aliases** (ADR-0053 §4, T-079): the
  pre-0.2.0 aliases (`GET /api/publications[/{name}]`,
  `PUT`/`DELETE /api/publications/{name}`,
  `POST /api/publications/{name}/render`) are gone and answer 404;
  `/api/maps[/{name}]` and `/api/maps/{name}/render` are canonical. The
  `publications.json` → `maps.json` migration shim is unchanged.
- **Rendering research spike** (`research/rendering/spike/RenderSpike`): the
  throwaway vertical slice is deleted now that its findings are promoted into
  ADR-0044 and the production `Spatial.Rendering.Skia` /
  `Spatial.Imagery.Vips` projects. Its measured results remain recorded in
  `research/rendering/README.md`. Because `crap4dotnet` globs every `.csproj`
  under the solution directory (ignoring solution membership), the spike had
  produced 21 of the 22 CRAP gate findings; only the known
  `PostgisEwkb.WritePoint` coverage-matching artifact remains (see HANDOFF.md).

### Fixed

- **QGIS Feature Service layer 400 ("unknown field 'OBJECTID'")**: the
  adapter's `orderByFields` validation resolved fields against the dataset
  schema only, but `OBJECTID` is synthetic (the identity column or scan
  ordinal, ADR-0037) and is the field clients order by for stable paging.
  `orderByFields=OBJECTID` now compiles to the resolved object id, and the
  `where` grammar resolves the same synthetic field (`EsriSyntheticField`),
  so `where=OBJECTID ...` works too — including `where`-based edits. The
  FeatureServer now renders in QGIS.
- **QGIS WMS returned "nothing to show" (HTTP 400)**: the WMS capabilities
  advertised each DCP `Get` URI with `?service=WMS&request=...` embedded.
  QGIS honours that URI and appends the operation parameters, so `service`
  and `request` arrived twice (`SERVICE=WMS,WMS`) and the adapter rejected
  the request. DCP endpoints are now the bare service URL (`...?`), and a
  parameter repeated with an identical value is collapsed, so an already
  added layer works without re-fetching capabilities.
- **WMS GetFeatureInfo missed the feature under the click**: the search was
  widened by only half a pixel, so clicking anywhere inside a rendered point
  marker (but not within half a pixel of its coordinate) returned an empty
  `FeatureCollection` and clients such as QGIS reported "no feature at the
  location". The tolerance now includes the layer's persisted marker radius
  (plus its stroke and the clicked pixel), so a click inside the drawn symbol
  identifies the point feature.
- **Composer crashed when served over plain HTTP**: adding an existing
  service (or any composer layer) called `crypto.randomUUID`, which browsers
  only expose in secure contexts, so on a LAN HTTP origin it threw
  `crypto.randomUUID is not a function`. Identifiers now go through a shared
  `newId` helper that falls back to `crypto.getRandomValues` when the native
  helper is absent.

## [0.1.0] - 2026-09-12

First tagged release: the independently executable .NET 10 host, the browser
React + MapLibre workbench, both SDKs, the PostGIS store, and the Esri
GeoServices REST serve/consume/edit boundaries.

### Added

- **Core and contracts** — `Spatial.Core` spatial value model (coordinates,
  geometries, CRS identity, features, SGEOM/SFBAT codecs) with no
  dependencies; `Spatial.PluginSdk` interfaces over core types only.
- **In-process services** (ADR-0033) composed by DI in `Spatial.Host`:
  geometry operations on NetTopologySuite (ADR-0005/0036), CRS description and
  transformation on ProjNet, the Docker-free demo store, and the PostGIS store
  (catalogue, dataset, scan/query/write, transactions, editing).
- **Host HTTP API** — typed routes (`/api/geometry/*`, `/api/crs/describe`,
  `/api/coordinates/transform`, `/api/catalogue`, `/api/datasets`,
  `/api/features/*`, `/api/transactions/*`, `/api/demo/sleep`), structured
  `SpatialException` codes, health endpoints and an OpenAPI document.
- **SDKs** — `clients/typescript` (`@spatial/client`, generated wire types with
  drift checking) and `clients/dotnet/Spatial.Client`.
- **Browser workbench** — React 19 + TypeScript + MapLibre served by the host
  from `Spatial:WebRoot`; catalogue, map rendering and selection, typed
  operations, result preview with browser-side persistence and cancellation.
- **Esri GeoServices REST boundary** (ADR-0035/0036/0037/0038) — serving
  (`Spatial.Adapter.GeoServices`: catalog, Geometry Service, FeatureServer
  query and gated editing) and consuming (`Spatial.Provider.ArcGisRest`), on
  the shared `Spatial.Interop.Esri` codec and filter grammar. The compatibility
  claim is proven against the official ArcGIS REST JS client.
- **Quality gates and delivery** — `eng/verify.sh` (format · build · tests),
  the two end-to-end scripts, `eng/*` build helpers, the container image
  (`Dockerfile`), and the CI workflow.
