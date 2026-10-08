# Architecture Principles

This is the project's philosophy. The code is the documentation;
this file states the standing shape so a change can be judged against it.

## The twenty principles

1. Geometry is core. Spatial algorithms are not.
2. The engine is headless. Every UI is a client.
3. The browser workbench is the frontend.
4. Packaging is not architecture: the host ships standalone.
5. The .NET host runs independently of every client.
6. Contracts outlive implementations.
7. Plugins depend on contracts, never on other plugin implementations.
8. No plugin-specific geometry object crosses a capability boundary.
9. Core geometry values are immutable.
10. Data stores are providers, not the domain model.
11. Long-running operations are jobs and are always cancellable.
12. Plugin code is disposable. Persistent state is external.
13. Open formats and language-neutral protocols are preferred at boundaries.
14. Agents and human clients use the same public capabilities.
15. Optimised provider pushdown is optional and preserves contract semantics.
16. Every derived result records provenance.
17. The kernel remains small, stable and independently testable.
18. Add another language only where profiling or platform integration justifies it.
19. Do not introduce Native AOT until compatibility is demonstrated.
20. The host and every client are covered by the same conformance tests.

## How to change the architecture

- Add a capability → versioned contract, conformance fixtures, SDK updates.
- Keep the change small, tested, and consistent with the principles above.

Enforcement lives in tests/architecture/Spatial.Architecture.Tests.

<!-- orientation:begin -->

## Orientation

One line per closed bead: where the first hour went. Facts that cost time when
they are absent — a file, a subsystem, a trap, a dead search — never a rule an
agent would have followed anyway.

- Start a store-query bead from the measured baseline, not from reading the adapter: the cost was materialisation, not filtering, and the ratios are in `eng/spike-u2x-query-baseline/RESULTS.md`. (SpatialEngine-u2x.1)
- Do not hand-merge a changelog entry: `tools/changelog.py --range <prev>..HEAD --print` renders the release section from the `Task:` trailers and the commit narratives, and the bead queue is not in a CI clone but the commits are (SpatialEngine-v47)
- Ask what a doc-audit row is *for* before draining it: a finding a reader can be wrong about is fixed by an edit, and one that only an edit manufactured to move a counter could clear is a census — report it as a signal and leave the queue empty rather than churning a document (SpatialEngine-3kk)
- A `*.csproj` under `eng/` is in no lane's build: `tools/verify_scope.py` walks the reference graph from the solution's own project list, so an ungated harness rots out of compilation unread — `tools/spike_harnesses.py` builds each in every lane. (SpatialEngine-b60)
- Query by plan: `POST /api/features/query` takes the `FeatureQuery` plan as JSON under `plan` and answers a page; `bbox` and `filter` stay as sugar for the plan's box and `where`. (SpatialEngine-gd2)
- Bind a literal as its column's kind, in the one predicate grammar: a text parameter against a `uuid` column compiled to `uuid = @p0` and Postgres answered 42883. (SpatialEngine-u2x.8)
- Spend `maxAllowableOffset` and `quantizationParameters` through `IGeometryOperations.Generalize`, a deviation allowance; `Simplify`'s tolerance is a different verb and is not a fallback for it. (SpatialEngine-u2x.3)
- Advertise a capability flag only where behaviour proves it: a REST JS client gates on `supportsQuantization`, so the flag is what makes a served parameter reachable at all. (SpatialEngine-u2x.25)
- Treat a LineString under two positions as a value, not a segment: the zero-position one is the empty LineString and the one-position one is neither — and keep it out of the planar clip, which bounds extent. (SpatialEngine-a74.2)
- Compile the feature-match envelope onto the store's plan all-or-nothing, decided before the store is asked, and treat a null geometry in the box pre-filter as not selected — that is what every SQL back end answers. (SpatialEngine-u2x.11)
- Push identify's envelope box and find's null test only: a pushed `LIKE` is a subset of the served case-insensitive search (`searchText=ALP` returned nothing), so pushing it made the answer depend on the store. (SpatialEngine-u2x.12)
- Declare a relationship on the map's layer and check it against the live schemas at `PUT /api/maps`; `queryRelatedRecords` is the ordinary query path with the origin key as one ANDed equality term. (SpatialEngine-u2x.22)
- Read the declared source CRS in the decode (`crs` member, ND-GeoJSON first record, CSV `# crs=`), and stream-test a JSON reader: a span past the valid data reads pooled bytes, and a feature over the 64 KiB buffer reads as a truncated document. (SpatialEngine-u2x.23)
- Read a DE-9IM pattern by position — index 1 is position 2 — and union the masks: a pattern is one nine-character matrix, and the single mask `FT*******` disagreed with the reference on 23 of 225 ordered pairs. (SpatialEngine-u2x.35)
- Hold the discovered description per store, keyed by dataset, and treat the write methods as the invalidation set: a walk of N pages costs one discovery, and every write the store makes forgets what it could have changed. (SpatialEngine-u2x.41)
- State the byte order on every pushed string term (`COLLATE "C"` on Postgres, `Latin1_General_100_BIN2` on SQL Server): the database's own collation is a locale, and an all-lower-case fixture cannot see the difference. (SpatialEngine-u2x.43)
- Compile a pushed text predicate through the same `Ordered` helper the sort keys use, so a `WHERE` and an `ORDER BY` cannot disagree about the order. (SpatialEngine-u2x.48)
- Start the SQL Server suite's container once for the assembly (a collection fixture, a database per class) and read the skip reasons: a run that reports itself mostly skipped is twelve container starts losing a 60-second wait strategy, not a missing daemon. (SpatialEngine-qhz)
- Declare the order on the sidecar columns the store itself owns; a case-folding key cannot hold both codes at all, so restating the collation per statement cannot fix it. (SpatialEngine-u2x.57)
- Name a pushed row by the identity columns' positions in what was read, not by the columns appended for the mapper: the appended set named every pushed row of a keyed table by its ordinal. (SpatialEngine-u2x.55)
- Ask what a declined pushdown is protecting before making every face decline it: ADR-0097's rule is about the ordinal a feature is named by, and a count, a distinct set and a grouped reduction return values, so they push on a keyless layer where the feature read cannot. (SpatialEngine-xg5)
- Measure a store reduction through the store's reduction face (`IFeatureAggregateStore`, probed as `FeatureReductionFallback` does), not a `QueryAsync` plan read: the spike's `B`/`Bp` cells reduced a read in the adapter and so measured the read, not the face ADR-0184 pushes. (SpatialEngine-8dm)
- Read an absent `orderByFields` on a grouped `outStatistics` request as the plan asking for no order, not as an order the store cannot return: the `keys is null` branch of `StoreQueryPath.TryGroupOrder` is the route from the served statistics face to `IFeatureAggregateStore`. (SpatialEngine-d0q)
- Measure where an allocation is before naming the class that owns the feature: the 15 MB was two SQL stores' row mapping building a second feature per row, not `FeaturePlanExecutor` — decide the projection once per read, and order a page's window rather than the read. (SpatialEngine-yup)
- Apply each sort key as a then-key in `FeaturePlanExecutor.Order`; a fresh `OrderBy` per key made a composite order its last key's, and the SQL pushdowns were narrowed to the orders both sides agreed on. (SpatialEngine-u2x.54)
- Read CRS definitions through `ProjWkt`, our own WKT reader: ProjNet's path lost 33,931 m of northing on the widely published `Mercator_1SP` spelling of EPSG:3857. (SpatialEngine-u2x.28)
- Check ProjNet for the projection's convention before writing its arithmetic: `Hotine_Oblique_Mercator` and `Oblique_Mercator` are one oblique Mercator differing only in where the false offsets go, so variant A needed a map entry. (SpatialEngine-r4o)
- Key every ingested dataset (`Auto` or `Source`) and refuse `identity=none` by name on both ingest routes and the CLI; `CreateAsync` stays keyless on purpose, and each path says why they differ. (SpatialEngine-2cm)
- Read the grid exercise's environment rather than assuming it: a bundle under `SPATIALENGINE_GRID_DIR` and a `cs2cs` that opened it are two separate facts, and PROJ answers from a Helmert when the grid is missing, so a run printing neither is green for the wrong reason. (SpatialEngine-yt2)
- Write a grid fixture in the container's convention, not the engine's: a NADCON pair states every longitude positive west, so an east-positive fixture pins a reader against a file NADCON does not publish, and the sign defect it hid is one no rule catches. (SpatialEngine-90n)
- Start at `Spatial.Rendering.Skia/StyleExpression.cs` and `ExpressionReader.cs`: the tree is read, validated and interned there, and a number is never coerced into a colour. (SpatialEngine-u2x.19)
- Label placement is candidates, then priority, in `SymbolSceneBuilder`: point offsets or repeated positions along a line, `symbol-sort-key` beating document order across layers, and the face registry's fallback chain. (SpatialEngine-u2x.20)
- Write a fixture and its expected spatialRel verdict down once, in `SpatialRelationMatrix`: the Feature Service query path and the Geometry Service `relation` operation are then held to the same row. (SpatialEngine-dih)
- Derive each pair's matrix from nine single-cell questions rather than reading the table's `De9im` column; that read is what found two of three line-along-the-edge rows wrong. (SpatialEngine-imj)
<!-- orientation:end -->
