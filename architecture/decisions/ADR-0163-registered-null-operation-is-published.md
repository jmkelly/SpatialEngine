---
status: accepted
date: 2026-10-08
deciders: maintainer + agent
summary: A registered datum operation that moves nothing is published as a candidate carrying its published accuracy, so ETRS89, NAD83 and NZGD2000 against WGS 84 answer with the operation EPSG registers rather than with nothing (amends 0087, 0111).
amends: ADR-0087, ADR-0111
---

# ADR-0163: A registered null operation is an operation, and it is published

## Context

ADR-0087 §2 ends: "A pair whose composed shift is the identity (ETRS89
against NAD83) yields nothing, because there is no operation to publish." The
graph honoured that, so `findTransformations` for 4326→4167, 4326→4258 and
4326→4269 came back `[]`.

The premise is a reading of the registry, and the registry says otherwise. EPSG
registers each of these datums against WGS 84 as an *operation* with the method
EPSG:9603 "Geocentric translations (geog2D domain)", parameters tx = ty = tz = 0
and no rotation or scale — and a published accuracy:

| Datum | EPSG operation | Method | Accuracy | Extent |
| --- | --- | --- | --- | --- |
| ETRS89 (1149) | 1149 "ETRS89 to WGS 84 (1)" | 9603, all parameters zero | 1.0 m | 4755 Europe |
| NAD83 (1188) | 1188 "NAD83 to WGS 84 (1)" | 9603, all parameters zero | 4.0 m | 1325 North America |
| RGF93 v1 (1671) | 1671 "RGF93 v1 to WGS 84 (1)" | 9603, all parameters zero | 1.0 m | 1096 France |
| NZGD2000 (1565) | 1565 "NZGD2000 to WGS 84 (1)" | 9603, all parameters zero | 1.0 m | 1175 New Zealand |

A null shift with a stated accuracy is not a missing operation. It is the
registry's way of saying that the two realizations agree to within that
accuracy — and that is precisely the information a client cannot get any other
way. NAD83's is four metres: over CONUS a client that has been told "no
transformation needed" has been told the coordinates are interchangeable, and
the registry says they are four metres apart.

So the rule and the data disagreed, and the rule won, silently. Three things
followed. The accuracy column of the operation table was stated for rows the
graph never read — every accuracy a definition carries was dead weight, and no
test could tell the difference between a datum the registry states and one the
engine invented. `project` over one of these pairs applied a shift the search
had never published and refused by name any `datumTransformation` the client
had been given, because there was none. And a search that returns `[]` for two
CRSs a client asked about is indistinguishable from a server that does not know
them.

ADR-0111 recorded the consequence in its own words — "NZGD2000 still serves no
transformation through the service, because its vendored WKT carries no
`TOWGS84` and the graph composes shifts out of definitions" — which is a
misdiagnosis: the WKT is not missing a node, and the graph is not missing
parameters. The registry records zero parameters. The vendored WKT is right,
and the graph was right to have no node to build a shift from; what was wrong
was the conclusion drawn from it.

## Decision

**Publish the registered null operation as a candidate carrying its published
accuracy.** A pair of distinct datums whose composed shift is the identity
comes back with exactly one candidate — the direct registered operation, one
step, the composed parameters (which are zero), the method EPSG publishes for
it, `approximate: false`, and the accuracy the two datums' registered
operations state between them, combined in quadrature exactly as two shifted
datums' accuracies are. It is valid over the intersection of the two datums'
areas of use, so `extentOfInterest` filters it like any other candidate, and it
is symmetric like any other candidate. This amends ADR-0087 §2's identity rule.

Three cases still publish nothing, and they are the ones with nothing behind
them:

- **A datum against itself.** There is no registered operation between a datum
  and itself; "no transformation needed" is the whole and honest answer, and
  4326→3857 keeps coming back empty.
- **A pair whose legs are both the unregistered pivot**, so no leg states an
  accuracy. The catalogued datums all have a row in the operation table
  (ADR-0086), so this is the conservative direction: a datum whose accuracy
  the registry does not state contributes a node and no operation.
- **A null pair that shares no ground.** ETRS89 against NAD83 is still `[]` —
  not because the shift is the identity, but because the operation is valid
  nowhere and ADR-0087 §4 drops it. The reasons are now distinguishable, and
  the tests say which one applies.

The null operation is published **alone**. The concatenated form would have no
steps at all, both legs being null, and the reduced form would restate the
same three zero translations over a *wider* area of use at the same accuracy —
three names for a shift that moves nothing is three claims about the same
ground, and the widest is the least true. A deployed grid bundle changes
nothing about this: the grid candidate is published alongside, ranked ahead by
its own accuracy, and the registered figure is still published for the ground
the grid does not cover (ADR-0105, ADR-0107).

## Alternatives

- **Keep publishing nothing, and document "no transformation needed" as the
  answer.** Rejected on the evidence: the registry publishes an operation with
  an accuracy for every one of these pairs, so the honest answer is not "none"
  but "one that costs a metre, or four". It also leaves the operation table's
  accuracy column stated for rows nothing reads, which is the defect the bead
  was opened for.
- **Publish the null operation as a `geographic offset` or a no-op step with
  no parameters.** Rejected: the registry names a method (EPSG:9603
  geocentric translations) and parameters, all zero, and a client reading a
  step with no parameters at all cannot tell it from an adapter that failed to
  fill them in.
- **Publish all three shapes — direct, concatenated and reduced — for
  consistency with a shifted pair.** Rejected: they are the same null shift,
  and the concatenated one has no steps. Consistency in shape is worth less
  than a listing that says one true thing three times.
- **Publish it only when the accuracies differ from the pivot's.** Rejected:
  that is the same rule stated in a way that reads as a special case.

## Not decided

Whether a datum pair whose composed shift is *nearly* null — within a
micrometre, the tolerance `HelmertAlgebra.IsNull` uses — should be published
as null or rounded to it. The catalogue holds no such pair today, and the
threshold is a judgement about parameter significance rather than about the
registry.

Whether the Esri-facing method string should carry the accuracy, as it does
for a demoted Ordnance Survey Helmert (ADR-0105 §fallback). The accuracy is
published in its own field, so this is presentational; it was left alone
rather than made special for null operations.

## Consequences

- `findTransformations` answers for three datums it previously refused, and
  says what the answer costs. A client over Europe is told that ETRS89 and WGS
  84 agree to a metre, and a client over CONUS that they agree to four.
- `project` accepts the name of the operation it applies for these pairs, so a
  client can round-trip the operation name the search gave it — which it
  previously could not, because there was no name to give.
- The published parameters are checkable from outside for these pairs too: the
  numbers are the registry's, and a client reading them can verify they are
  zero against the EPSG record the row cites.
- ADR-0111's consequence bullet is corrected by this record. New Zealand now
  serves the operation EPSG:1565 registers, over both halves of extent 1175;
  the reason was never a missing `TOWGS84` node.
- The cost is a longer listing and a narrower contract in one place: a client
  that treated an empty result as "the datums are interchangeable" now has to
  read `accuracy` to learn they are not, and a client that compares operation
  *names* between two runs of the same pair sees a new one where there was
  none. Both are the correct direction — a name and a figure the registry
  published, rather than silence the registry does not.
- `project` over these pairs still takes the geocentric round trip (source
  ellipsoid → geocentric → target ellipsoid), which moves a point by
  centimetres rather than by nothing. The registered accuracy is a metre or
  four, so this is well inside it, but a client wanting bit-identity would need
  a path that skips the conversion — which is not a registry operation and is
  not in scope here.
- Four rows in `EpsgDatumOperations` now have a reader. RGF93 v1 was already in
  the same class as the other three and is published the same way; the table's
  test pins the null parameters of all of them.

## References

- `src/Spatial.Transformations.ProjNet/DatumTransformationGraph.cs` —
  `IsRegisteredNullPair`, `NullOperation`, and the null branch of
  `Candidates`.
- `src/Spatial.Transformations.ProjNet/EpsgDatumOperations.cs` — the four rows
  whose registered operation is null, and the comment that said the pair
  publishes nothing.
- `tests/unit/Spatial.Transformations.ProjNet.Tests/NullDatumOperationTests.cs`
  — the decision, pinned.
- `tests/unit/Spatial.Adapter.GeoServices.Tests/GeometryServiceTests.cs` —
  `Find_transformations_publishes_the_registered_null_operation_for_a_datum_that_realises_WGS84_without_moving`,
  the Esri answer for 4326→4258 and 4326→4269.
- ADR-0087 §2 (amended), §4 (unchanged), §6 (unchanged); ADR-0086 §3 (the
  operation table is the only place an accuracy is stated); ADR-0105, ADR-0107
  (grid ranking and demotion); ADR-0111 Consequences (amended).
- Bead SpatialEngine-6nr. Found while closing SpatialEngine-jbj, whose premise
  was false; SpatialEngine-392 carries the WKID-4167 mapping gap this record
  does not fix.

## Measurements

| Datum pair | Candidates before | Candidates after | Accuracy published |
| --- | --- | --- | --- |
| 4326 → 4258 (ETRS89) | 0 | 1 | 1.0 m (EPSG:1149) |
| 4326 → 4269 (NAD83) | 0 | 1 | 4.0 m (EPSG:1188) |
| 4326 → 4167 (NZGD2000) | 0 | 1 | 1.0 m (EPSG:1565) |
| 4326 → 4171 (RGF93 v1) | 0 | 1 | 1.0 m (EPSG:1671) |
| 4258 → 26910 (ETRS89 → NAD83 UTM 10N) | 0 | 0 | — no shared area of use |
| 4326 → 3857 (WGS 84 → Web Mercator) | 0 | 0 | — the same datum |

Accuracies read from the EPSG Geodetic Parameter Dataset release carried by
PROJ 9.8.1 (EPSG v12.029), the release every row in `EpsgDatumOperations` was
transcribed from.
