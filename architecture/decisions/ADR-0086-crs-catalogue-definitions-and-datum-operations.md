---
status: accepted
date: 2026-09-28
deciders: maintainer + agent
amended-by: ADR-0153
summary: The CRS catalogue is vendored EPSG WKT read by a reader of our own (ProjNet's cannot read WKT2, and reads the widely published `Mercator_1SP` spelling of 3857 as a plain Mercator); a definition carries the datum's shift, while the accuracy and area of use of that shift live in a separate registered-operation table, because WKT states neither.
---

# ADR-0086: The CRS catalogue is WKT-defined, and a datum's operation data is not part of its definition

## Context

Two changes to the ProjNet CRS catalogue (ADR-0027) could not both land.
One replaced the hand-written parameter list with a WKT definition path: the
vendored EPSG WKT2:2015 documents are read by `ProjWkt`, a reader of the
engine's own, into the definitions the programmatic builder already consumed.
The other replaced `findTransformations`'s restatement of the catalogue with a
real transformation graph — area of use, candidate generation, ranking and
published Helmert parameters — and needed each datum to carry three things: its
shift to WGS 84, **the accuracy of that shift in metres**, and **the area it is
used over**.

They are mutually incompatible as written. The transformation graph read the
last two off the catalogue's rows:

```csharp
GeographicEntry(..., double[] ToWgs84, double AccuracyToWgs84Metres,
                string AreaOfUseName, double[] AreaOfUseBounds)
```

and the WKT path deletes those records, because its whole claim is that the
catalogue stops being a hand-written parameter list. Restoring them would put
a back-translated C# record back on top of the WKT it came from, so the datum's
numbers would live in two places and be able to disagree.

The question is therefore not "which merge won" but **what a definition is** —
and the honest answer decides the rest. A WKT2 `GEOGCRS` document carries the
datum, the ellipsoid, the projection and the projection's parameters. It
carries no accuracy and no usage: those are attributes of the *coordinate
operation* EPSG registers against a datum, and they appear in a WKT2 document
only inside a `BOUNDCRS`/`COORDINATEOPERATION` node, not in the CRS the
catalogue serves. So the two branches were not expressing the same fact in two
dialects; one of them was asking a definition for something a definition does
not hold.

## Decision

1. **The catalogue lives in `Spatial.Transformations.ProjNet`, as vendored EPSG
   WKT read by a reader of our own.** Not in ProjNet: ProjNet 2.1's own WKT
   reader cannot read WKT2 at all (`'PROJCRS' is not recognized`), and where it
   can read WKT1 it takes the projection class from the document, so the
   `Mercator_1SP` spelling of EPSG:3857 that Google, OSRM, GeoServer and
   EPSG:900913 all publish becomes a plain Mercator — 33,931 m too far south at
   Berlin's latitude. The catalogue's own reader (`ProjWkt`) accepts both the
   WKT1 and WKT2 dialects, intercepts the identifiable Pseudo-Mercator
   spellings (by projection name, CRS name, or EPSG 3857/3785/900913/102100/
   102113 authority) and routes them to the known-good programmatic
   construction. Construction itself is unchanged and stays on the one builder
   the catalogue has always used.
2. **A definition is WKT text read into a `CrsDefinition`** — a geodetic
   definition (datum, ellipsoid, the datum's `TOWGS84` shift) or a projected
   one (a geodetic base, a ProjNet projection class, its parameters). A
   hand-written row, a generated family member and a definition that exists only
   as WKT are the same thing after this point, and adding a definition is
   adding a document. The UTM families are one WKT template with two
   substituted numbers.
3. **A definition owns the datum's *shift*; the registered operation owns the
   datum's *quality and extent*.** The shift is a construction input ProjNet
   needs, and it is in the WKT (`TOWGS84`, a node WKT1 defined and the WKT2 CRS
   documents omit, appended where it applies). The accuracy in metres and the
   area of use are the registry's `helmert_transformation` row, so they live in
   a curated operation table beside the WKT
   (`EpsgDatumOperations`) and are joined to a definition by the datum's EPSG
   name — the identifier the vendored `DATUM` node actually carries. A datum
   with no published operation contributes no node: a gap the graph reports,
   never an accuracy the engine invented.
4. **The join is at the datum, and the pivot is the catalogue's own WGS 84.**
   `ProjEpsgCatalog.TryGetDatum` resolves a projected definition through its
   geodetic base, so a CRS and its geographic parent are one node. The graph's
   WGS 84 pivot is read from the catalogue like any other datum, so the shift
   the graph composes is the shift the engine applies and the two cannot drift.
5. **EPSG:3857 stays correct because the definition model is the intercepted
   one, and the graph never constructs anything.** The pseudo-Mercator
   workaround moved from a refusal to a code path in the reader; the
   transformation graph reads a definition and joins an operation, and builds
   no coordinate system at all. A definition that the reader could not build
   safely would have been rejected at read time, not served.
6. **ADR numbers are allocated from `architecture/decisions/` on `main`.** A
   branch takes the next free number when it starts and re-numbers on rebase if
   `main` took it in the meantime; `AdrNumberingTests` enforces the result
   (uniqueness, title/file agreement, and that every bare `ADR-NNNN` reference
   in the tree resolves to exactly one record). There is no separate authority
   to appoint — the tests are it.

## Consequences

- The catalogue has one shape. A new CRS is a new WKT document; a new datum
  operation is a new row. Neither is a new hand-transcribed parameter list, and
  the two cannot silently disagree because there is only one place each number
  is written.
- The transformation graph (ADR-0087) keeps everything it earned: area of use,
  ranking, published parameters, symmetry, and the `project` acceptance of the
  operation it applies. What it loses is nothing — its accuracy and extent
  columns move to the table that actually owns them.
- A reader who wants to know how accurate a datum shift is now looks at
  `EpsgDatumOperations`, not at a CRS row. That is the correct place to look,
  and the ADR is the reason.
- The table is curated data, so it carries a licence note like the WKT does and
  a test that every geodetic definition the catalogue serves has a row — a
  datum added to the WKT without an operation fails
  `EpsgDatumOperationsTests` rather than falling out of the graph silently.
- Grid shifts (NTv2/NADCON/OSTN15) remain the follow-on. Under this model they
  are not a new column but a new *kind* of row, which is the point of keeping
  the definition and the operation apart.
