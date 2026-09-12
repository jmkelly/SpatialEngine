---
status: accepted
date: 2026-09-13
deciders: maintainer + agent
---

# ADR-0043: An unassigned identity lets uploads be created with a store-assigned key

## Context

ADR-0041 §3 gave ingested datasets a database-generated integer identity
(`IngestIdentity.Auto`) so uploaded layers are editable (ADR-0037) and
lookup-able (ADR-0038). ADR-0041 §Consequences recorded the remaining gap:
"an `IFeatureEditStore` 'omit identity on insert' mode" is required before
an uploaded auto-identity layer can advertise `create`.

The current editing path cannot express "let the store assign the key".
`FeatureId` is a non-empty string, `Feature` validates one attribute per
schema field and has no unassigned state, and both `PostgisEditStore` and
the facade treat a feature's identity as already known: the PostGIS insert
includes the identity column as a bound parameter (so an explicit `NULL`
hits the identity column's implicit `NOT NULL`) and the facade requires the
client-supplied `OBJECTID` for adds.

Three mechanisms were considered:

1. **A sentinel identity** — reserve a `FeatureId` value meaning "not yet
   assigned".
2. **An explicit mode on `AddAsync`** — a boolean/enum parameter saying the
   batch's identities are store-assigned.
3. **A sibling method** — a second add method without identities.

## Decision

**1. `FeatureId.Unassigned` is the reserved sentinel.**

`Spatial.Core.Features.FeatureId` gains one static value,
`FeatureId.Unassigned`, a well-known non-empty string outside the numeric
OBJECTID space (`"__unassigned__"`). It is a Core change that adds a value,
not a shape; `Feature` and every existing constructor are unchanged.

**2. `AddAsync` assigns when the feature's identity is unassigned.**

`IFeatureEditStore.AddAsync` needs no signature change: a feature whose
`Id` is `FeatureId.Unassigned` is inserted **without** its identity
column(s) and the store assigns them. When the store can return the
assigned identity (PostGIS `INSERT … RETURNING`, the in-memory provider's
counter) the `FeatureEditOutcome` carries it; otherwise the outcome reports
the unassigned sentinel and the caller re-reads. `UpdateAsync` and
`DeleteAsync` keep rejecting the sentinel (there is nothing to match).

**3. The foreign ingress path marks omitted OBJECTIDs unassigned.**

When an Esri `addFeatures`/`applyEdits` payload omits the object-id field,
the facade builds the feature with a placeholder attribute value (`0`) and
its `Id` set to `FeatureId.Unassigned`, instead of failing the request. The
store sees the sentinel and omits the identity column from the insert, so
the database assigns the key. (The placeholder is never persisted.) An
uploaded auto-identity layer therefore advertises `create` in its
`capabilities`.

**4. `None`-identity datasets stay non-editable.**

A data-only uploaded dataset (`IngestIdentity.None`) has no identity column,
so the facade continues to advertise it without editing capabilities and
the store rejects edits; nothing here changes that.

## Consequences

- Uploaded auto-identity layers now support client-omitted `OBJECTID` adds
  end-to-end: ingest → publish → `addFeatures`/`applyEdits` → assigned id.
- `PostgisEditStore` and the in-memory provider gain an insert variant that
  omits the identity column; `PostgisQueries` gains the omit-identity SQL.
- The facade's add parser distinguishes "OBJECTID supplied" from "assigned
  by the store" and the edit engine passes the sentinel through unchanged.
- `FeatureId.Unassigned` is reserved: a source identity literally equal to
  the sentinel string is rejected at ingest with `invalid.arguments`, so a
  real key can never be confused with the sentinel.
- The change is additive and needs no public version bump; `Spatial.Core`
  keeps its structural-only rule (one new value on an existing type).

## References

- ADR-0037 (feature editing), ADR-0038 (read-by-identity),
  ADR-0041 (ingest and publications are protocol-neutral)
- `architecture/publishing-and-ingest-plan.md` (P4-sub)
- `architecture/distilled/core.md`, `contracts.md`
