# Spatial.Transformations.ProjNet

The PROJ.NET adapter: the CRS catalogue reader, the coordinate transforms and
the datum-shift grid registry. Two records bind it: **ADR-0105** (grids are
*deployed*, not embedded; the classic Helmert is the stated fallback) and
**ADR-0087** (transformations are contract values with area of use and
accuracy, `findTransformations` a ranked, area-filtered search), with
**ADR-0179** on the published-bundle comparison being an operator-run exercise
rather than a CI gate. Route by task: `architecture/distilled/plugins.md`.

## Never

- No composing a shift with itself. The London case cost 113 m: a grid
  applied on both sides of a chain is applied once, and the graph, not the
  call site, owns that (ADR-0105, ADR-0087).
- No embedded grid bundle and no silent fallback to Helmert when a grid is
  missing — the missing grid is reported, and the fallback is stated in the
  result's provenance (ADR-0105).
- No ProjNet type on a contract: `CoordinateReference`, `CrsDescription` and
  `CrsTransformation` are core-typed and belong to `Spatial.Contracts`
  (ADR-0027, ADR-0005).
- No WKT2 read by ProjNet's own parser: the catalogue is vendored EPSG WKT
  read by a reader of ours, because ProjNet's cannot (ADR-0086).
- No axis-order convention invented here; the projected-axis rule is
  ADR-0170's and it does not bend for a CRS that reads oddly.

## Commands

- `dotnet test tests/unit/Spatial.Transformations.ProjNet.Tests` — the graph,
  the grid registry and the round-trip battery.
- `eng/grid-agreement.sh` — the published-bundle control points: deploy a bundle
  in `SPATIALENGINE_GRID_DIR`, install PROJ with its own copy of it, and the
  suite compares and reports.
- `dotnet test tests/conformance/Spatial.QueryConformance/Spatial.QueryConformance.csproj` — the query conformance cases the host and every client share.
- `eng/verify.sh --fast` — the lane a change here is handed off on.
