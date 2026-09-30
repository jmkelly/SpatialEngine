---
status: accepted
date: 2026-09-30
deciders: maintainer + agent
summary: **A read-by-identity needs a declared identity column**: a dataset that declares none has no durable feature key, so `IFeatureLookup.GetAsync` is refused on it with `invalid.arguments` naming the dataset, by memory, PostGIS and SQL Server alike — not answered with an empty result (which claims every requested identity is absent) and not answered from the scan ordinal (ADR-0037's `OBJECTID`, which a write renumbers). A miss stays an absence; this is a typed failure, because a layer that cannot name its features and a feature that is not there are different answers. The adapter does **not** fall back to the `OBJECTID`: a restricted read would renumber it (ADR-0097), so `FeatureMatchPushdown`, `FeatureAttachmentTargets` and `FeatureEditEngine` keep declining to push and keep falling back to the scan. Giving every dataset an identity column at create/ingest is a *separate* change to ingest's contract, not a lookup fix (amends 0038).
---

# ADR-0140: A read-by-identity needs a declared identity column

## Context

`IFeatureLookup` (ADR-0038) is the engine's per-feature read face: an edit, an
attachment lookup or a relationship traversal names the identities it wants and
the store answers with exactly those features. The measurement spike
`SpatialEngine-u2x.1` (`eng/spike-u2x-query-baseline/RESULTS.md`, finding 6)
asked for it on a 34,135-row layer and could not measure it, because the face
is not answerable on a dataset that declares no identity column — and it does
not say so.

- **PostGIS and SQL Server returned an empty list.** `PostgisFeatures.ByIdentityAsync`
  and `SqlServerFeatures.ByIdentityAsync` had an explicit
  `if (description.IdColumns.Count == 0) return [];` (ADR-0038 §2, "tables
  without a primary key return an empty result"). A caller asking for one
  feature of such a layer received a successful answer asserting that the
  feature is absent. There was no miss-per-id either: one wrong answer covered
  every requested identity.
- **The in-memory store answered from a number that is not a key.** Its
  `GetAsync` filtered on `Feature.Id`, which on a `None` ingest (ADR-0041) is
  the decoder's read ordinal — the same kind of number ADR-0037 uses as the
  `OBJECTID` of a layer with no integer identity column. It is not durable: a
  client holding `4` from one request cannot address that feature after any
  write renumbers the layer. So the two families of store disagreed about the
  same layer, and the one that answered was the one whose answer was not
  durable.

The question this record answers is the one the bead left open: give a dataset
without an identity column one at create/ingest time, refuse the read, or let
the adapter fall back to the Esri `OBJECTID`.

## Decision

**1. A dataset that declares no identity column has no durable feature key, so
`IFeatureLookup.GetAsync` is refused on it with `invalid.arguments`, naming the
dataset.** The three stores agree: `MemoryStore`, `PostgisFeatures` and
`SqlServerFeatures` all refuse, and the contract says so. The refusal is about
the *dataset*, so it does not depend on the identities asked for — an empty id
list on an unkeyed dataset is refused too, rather than answered with the empty
set, which would again read as "nothing matches".

**2. A miss and a refusal are different answers, and the shapes say which is
which.** A miss stays an absence from the result (ADR-0038); this is a typed
failure. The previous behaviour collapsed the two, which is the actual defect:
a client could not tell a layer that does not have the requested feature from a
layer that cannot name its features at all.

**3. The adapter does not fall back to the Esri `OBJECTID`.** It already does
not, and this record says why rather than leaving it to be re-derived: on a
layer with no integer identity column the `OBJECTID` *is* the whole-dataset
scan ordinal (ADR-0037), so it is not a durable key, and any restricted read
that returned only the matching rows would renumber it (ADR-0097). The
facade's rules stand unchanged — `FeatureMatchPushdown.Compile` declines to
push anything down such a layer, `FeatureAttachmentTargets.LookupFor` and
`FeatureEditEngine.EditsAsync` decline to ask a lookup, and the
read-modify-write paths fall back to a full scan. The spike could not measure
the face on the layer it had because the face genuinely does not exist there,
which is the honest answer rather than a number.

**4. Giving every dataset an identity column at create/ingest time is *not*
adopted here.** It is a real alternative and it is the one that would make the
face universally available, but it is a different change at a different layer:
it changes what ingest writes, what `DescribeAsync` reports, what the served
layer advertises as editable (`MemoryDataset.Editable` is `IdColumns.Count > 0`,
ADR-0037's gate) and every dataset's stored schema. It is filed as its own bead
rather than smuggled in behind a lookup fix.

## Consequences

- **A read-by-identity on an unkeyed dataset fails loudly.** Anything calling
  `GetAsync` for such a layer now gets `invalid.arguments` instead of an empty
  answer. Every in-repo caller already gated on a declared integer identity
  column (`FeatureAttachmentTargets.LookupFor`, `FeatureEditEngine.EditsAsync`),
  so the served surfaces answer identically; the change is visible to a
  direct caller such as the spike.
- **`MemoryStore.CreateAsync` builds a dataset with no identity column**, so
  such a dataset is no longer lookup-able, and `Write_round_trips_through_create_and_scan`
  asserts the declaration rather than the lookup. The stored features still
  carry the identities the caller wrote; they are simply not a key the store
  will answer a read-by-identity from, because it cannot tell a caller's
  durable-looking id from the decoder's read ordinal — both are
  `IdColumns`-empty datasets.
- **`NotFound` is not used for this.** "There is no such feature" is a claim
  about a dataset that can name its features; on one that cannot, the claim is
  unfounded. `invalid.arguments` names the request as the thing that cannot be
  served.
- **The spike's finding 6 is answered, not deferred:** a dataset without an
  identity column makes `IFeatureLookup` unreachable by decision rather than by
  accident, and the layer it was measured on says why.

## Alternatives

- **Keep returning an empty result.** The status quo, and the reason a
  client-visible per-feature read is unavailable on such a layer without
  anyone being able to say why. Rejected: it makes an unanswerable question
  look answered.
- **Answer from the scan ordinal (the Esri `OBJECTID`).** What the in-memory
  store did, and what the adapter's read paths deliberately refuse: the number
  is not durable across a write (ADR-0037) and is not stable under a
  restricted read (ADR-0097). Rejected for the store face, where no such
  rule exists to keep it honest.
- **`store.unavailable` instead of `invalid.arguments`.** The store is
  available; the dataset is under-keyed, and the caller's request is well
  formed. `store.unavailable` is reserved for a store that cannot serve at all
  (not configured, not reachable).
- **Add the identity column to the ingest/create path.** Rejected here, filed
  as a separate bead: it is the change that would remove the restriction, and
  it belongs to ingest's contract rather than to a lookup fix.

## References

- ADR-0038 (read-by-identity; its "empty result for a table without a primary
  key" is what this record replaces), ADR-0037 (the synthetic `OBJECTID` and
  the integer-identity gate on editing), ADR-0041 (ingest identity modes),
  ADR-0042 (the in-memory provider), ADR-0097 (the identity rule for pushdown),
  ADR-0119 (`Feature.Id` is the identity column's value), ADR-0074 (the plan
  read, which is not the same face).
- `src/Spatial.Contracts/Providers/IFeatureLookup.cs`,
  `src/Spatial.Stores.Memory/MemoryStore.cs` (`GetAsync`),
  `src/Spatial.Stores.PostGIS/PostgisFeatures.cs` (`ByIdentityAsync`),
  `src/Spatial.Stores.SqlServer/SqlServerFeatures.cs` (`ByIdentityAsync`),
  `src/Spatial.Adapter.GeoServices/FeatureMatchPushdown.cs`,
  `src/Spatial.Adapter.GeoServices/FeatureAttachmentTargets.cs`,
  `tests/unit/Spatial.Stores.Memory.Tests/MemoryStoreTests.cs`,
  `tests/integration/Spatial.PostGIS.Tests/PostgisIntegrationTests.cs` and
  `tests/integration/Spatial.SqlServer.Tests/SqlServerIntegrationTests.cs`
  (each store's `Lookup_by_identity_of_a_table_without_a_primary_key_is_refused`).