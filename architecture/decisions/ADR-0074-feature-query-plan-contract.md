---
status: accepted
date: 2026-09-28
deciders: maintainer + agent
---

# ADR-0074: The feature-query contract is a core-typed query plan, not a filter string

## Context

`IFeatureStore` (`src/Spatial.Contracts/IDataStores.cs:28-36`) has two reads:

```csharp
Task<IReadOnlyList<FeatureBatch>> ScanAsync(string dataset, CancellationToken cancellationToken = default);

Task<IReadOnlyList<FeatureBatch>> QueryAsync(
    string dataset, BoundingBox? bbox = null, string? filter = null, CancellationToken cancellationToken = default);
```

No projection, ordering, limit/offset, aggregate, distinct or count-only. The
`filter` parameter is a text mini-grammar, and it exists three times over:

- `PostgisFilterLexer` / `PostgisFilterParser` / `PostgisFilterSql` and the
  identically shaped `SqlServerFilter*` trio — roughly 800 lines per provider,
  two implementations of one language, differing only in how a bound literal
  is written.
- `MemoryStore` rejects the parameter outright (`MemoryStore.cs:85`): "The
  in-memory store supports bbox queries only; attribute filters are not
  supported." So the pushed-down path and the contract disagree about what a
  feature filter is.
- `Spatial.Esri.Codec/EsriFilterClause.cs` (796 lines plus 331 of logic) is a
  second, Esri-shaped grammar that does not produce a filter at all: it
  *evaluates features* through `Matches(IFeature)`.

The pushdown path is also unbounded. `PostgisFeatures.ReadBatchesAsync`
streams rows into 512-feature batches but materialises the entire match set
first, so a `LIMIT` a caller could have asked for is not asked for. And the
served protocol features above this contract — `orderByFields`,
`outStatistics`, `returnCountOnly`, `returnDistinctValues`, paging, `time`,
MapServer `identify`/`find` — are computed in the adapter over a materialised
scan; the GeoServices feature, match, identify and find paths do not even use
`QueryAsync` and full-`ScanAsync` the dataset instead.

Principle 15 says optimised provider pushdown is *optional* and *preserves
contract semantics*, and says nothing about what a store must do when its SQL
dialect cannot express part of a filter. The de facto answer is a typed
`invalid.arguments` from `PostgisFilterSql.TryBuild`, which reports the
server's dialect gap as the client's malformed request. The public surface
does have to keep accepting filter text: `GET /api/features/query` takes
`filter` and the .NET client sends it
(`clients/dotnet/Spatial.Client/SpatialDataClient.cs:62`).

## Decision

**1. The contract carries a query plan, not a filter string.**
`Spatial.Core.Features.Query.FeatureQuery` is the feature-read plan:

```csharp
public sealed record FeatureQuery(
    IReadOnlyList<FeatureId>? Ids = null,
    Predicate? Where = null,
    BoundingBox? BoundingBox = null,
    IReadOnlyList<string>? Projection = null,
    IReadOnlyList<OrderTerm>? Order = null,
    int? Limit = null,
    int? Offset = null,
    string? Cursor = null);
```

`FeatureQuery.All` is the unbounded read, so `ScanAsync(dataset)` is that plan
by another name. A plan that would change the *shape* of the answer (aggregate,
distinct, count-only) is deliberately not in it — see 5. Every member is a core
value: a field list over `FeatureSchema`, a predicate tree, a `BoundingBox`,
a `FeatureId` list, a string token (principle 8: no provider or protocol
vocabulary crosses). Plan members are validated against the dataset's schema
once, where the plan is built — an unknown projection or order field, a
malformed cursor or a negative limit is `invalid.arguments` there, not a
per-provider surprise later.

**2. One predicate vocabulary, not one filter language.**
`Predicate` is a small tree: `And`, `Or`, `Not`, `Compare` (field, operator,
literal), `IsNull`, `IsIn`, over `FieldRef` and `Literal`. The tree lives in
`Spatial.Core` next to `FeatureSchema` and `FeatureBatch` because it is a value
vocabulary, and Core stays structural: **no evaluation in Core.** Compiling a
plan to SQL is a store concern; evaluating one in memory is an implementation
concern (see consequences).

**3. The text grammar is a boundary format, parsed once, producing a plan.**
`GET /api/features/query?filter=` keeps the syntax clients already send — it
is a published surface and one breaking change at a time is the rule — but the
parser moves out of the providers into one place, and the per-provider
lexers, parsers and SQL builders are retired. Each provider compiles the
`Predicate` it is handed. The Esri `where` grammar and any OGC CQL subset
compile to the same `Predicate` (see 6), so there is one filter language in
the engine and the Esri text is a front-end syntax over it, not a second
language.

**4. Pushdown is per-conjunct, best-effort, and result-preserving.**
A provider pushes what its dialect can express and evaluates the residual
predicate itself over the rows it fetched. A plan the store can fully push
down is never fetched twice; a plan with one unexpressible conjunct is
correct, not rejected. So a valid plan is never refused for a dialect gap:
`invalid.arguments` stops meaning "this server's SQL cannot do that", and
principle 15's "optional but semantics-preserving" becomes a checkable rule —
the result of `QueryAsync(plan)` is the result of evaluating `plan` over the
whole dataset, in the same order, every time. `MemoryStore` implements the
whole vocabulary for the same reason: it is the reference evaluator.

**5. Paging is on the plan; a cursor is continuation of the same plan.**
The read face returns `FeatureQueryPage(Batches, NextCursor, TotalCount?)`.
`Limit` becomes a real row cap, so the unbounded materialisation is gone.
`NextCursor` is an opaque token the store issues; a token the store did not
issue is `invalid.arguments`. A cursor is only defined for a plan with a
deterministic total order, so the contract requires a store to append the
feature identity as a tie-break to any requested `Order` — without that,
`Offset` and cursors are not stable and the paging guarantees above are
unreachable. `TotalCount` is nullable and `null` means "not computed", never
zero: it is the cheap extra a provider may return and the caller may compute
instead, which is what makes `returnCountOnly` an optimisation rather than a
capability.

**6. Required reads and optional faces, split by what the store must compute.**
The required face keeps exactly two reads, `ScanAsync(dataset)` and
`QueryAsync(dataset, FeatureQuery)` returning a `FeatureQueryPage`; its answer
is always features. Everything that asks the store to *reduce* is additive, on
`IFeatureAggregateStore` — `CountAsync`, `DistinctAsync`, `AggregateAsync` —
which is the ADR-0033 optional-face pattern (`IFeatureLookup`, ADR-0038;
`IFeatureEditStore`, ADR-0037; `IFeatureAttachmentStore`, ADR-0065). A
store that does not implement it still answers `QueryAsync` correctly, and the
caller computes the reduction over the page. Making reduction required would
force the demo and ArcGIS REST stores to implement SQL aggregation or to
refuse, which is the capability dishonesty the rest of the repo rejects.

**7. The Esri `where` grammar stays in the Esri codec, and stops evaluating
features.**
`EsriFilterClause` parses the Esri text and *compiles* it to a `Predicate`
(plus `FeatureQuery.Ids` for `objectIds`) instead of exposing
`Matches(IFeature)`. The Esri-only parts are resolved at compile time rather
than pushed into the core vocabulary: `TIMESTAMP` and
`CURRENT_TIMESTAMP ± INTERVAL` fold to literals, the synthetic `OBJECTID`
becomes a field reference resolved against the layer schema (ADR-0037), and
`time` compiles to a disjunction of range tests over the layer's date fields —
the layer's date fields being an Esri concern, the disjunction being a legal
plan. The GeoServices feature, match, identify and find paths then run one
plan per request instead of a full scan plus per-feature evaluation.

**8. The topology verbs stay adapter-side.**
The plan's spatial component is the `BoundingBox` pre-filter it has always
been. `esriSpatialRelContains`/`Within`/`Touches`/`Overlaps`/`Crosses` are
`IGeometryRelations.Relate` (ADR-0036) evaluated by the adapter, and pushing
a DE-9IM relate into every store is a new contract face this decision does not
authorise. `EnvelopeIntersects` and `Intersects` are the same story.

## Consequences

- **Breaking for store implementers, not for API clients.**
  `IFeatureStore.QueryAsync` changes signature and its return type; the
  `filter` text still arrives as the same string, so
  `Spatial.Client`/`Spatial.Cli`, the workbench and the `/api/features/query`
  contract are untouched. Each store migrates by answering the new plan with a
  scan plus an in-memory evaluation first and adding SQL pushdown second —
  correct on day one, faster later, and the fallback is the same code the
  residual evaluation needs.
- **The three duplicated grammars collapse to one.** Two provider parsers
  and their SQL builders go; one boundary parser produces the plan, and
  `MemoryStore` stops refusing filters.
- **`ReadBatchesAsync` stops materialising the whole match set**, because
  `Limit` is a row cap and the cursor has somewhere to continue from.
- **Aggregation becomes honest.** `returnCountOnly`, `outStatistics` and
  `returnDistinctValues` work on every store and are fast on the ones that
  implement `IFeatureAggregateStore`; no store can be asked for a reduction it
  cannot express.
- **Unblocks `.8`** (one grammar, SQL compiler, in-memory evaluator) and
  **`.9`** (projection, ordering, paging, aggregation, distinct, count), which
  are blocked on this decision, and with them the GeoServices
  feature/match/identify/find waves.
- **Two placement questions are deliberately not answered here.** The
  in-memory evaluator and the residual evaluator are implementation code and
  must not enter `Spatial.Core`; the home for them (a neutral shared project,
  versus a per-store copy) is a structural choice left to `.8`, and a
  follow-up bead records it. Likewise, retiring the `filter` text from the
  HTTP API in favour of a JSON plan is a later breaking change, not this one.
- **Not decided:** pushing the DE-9IM relations into stores (8), and the
  aggregate-spec shape `IFeatureAggregateStore` takes, which `.9` settles
  against the Esri statistics surface (ADR-0057).

## Alternatives

- **Keep the string and add a `FeatureQuery` overload.** Two ways to say one
  thing forever, and nothing in the contract can state that the two must
  agree. Rejected.
- **Put the plan in `Spatial.Contracts` next to `IFeatureStore`.** The hard
  wall is that public contracts carry core types; `FeatureBatch`,
  `FeatureSchema` and `AttributeValue` all live in Core, and the plan is the
  same kind of value. Rejected.
- **Keep all-or-nothing pushdown and advertise a `SupportsPushdown` flag,**
  answering the unexpressible plan with a capability error. That turns a
  server's SQL dialect into a client-visible failure and keeps the residual
  evaluation out of the repo, where every provider would need its own. Rejected
  in favour of 4.
- **Make aggregates and count required on `IFeatureStore`.** Forces the demo
  and ArcGIS REST stores to implement SQL aggregation or refuse. Rejected in
  favour of the ADR-0033 optional-face pattern.
- **Model paging as a returned `IAsyncEnumerable` of batches.** The
  batch-page read shape is settled (ADR-0023, ADR-0029) and no read in the
  engine needs a stream; a cursor on a page is enough. Rejected.
- **Model a cursor as a required part of the contract on every read**, so every
  plan is resumable. It buys nothing for the one-shot reads (a scan, a tile
  read, an identify) that dominate. Rejected; the cursor is part of the plan
  and optional.
- **Retire the `filter` text now and accept a JSON plan on
  `/api/features/query`.** A published surface with a shipped client, and this
  decision already breaks the store contract; two breaking changes at once
  would make the migration unattributable. Deferred to a follow-up bead.

## References

- Principles 6 (contracts outlive implementations), 8 (no implementation type
  crosses a boundary), 10 (stores are providers), 13 (open formats at
  boundaries), 15 (pushdown optional, semantics-preserving), 17 (small
  kernel).
- ADR-0028 (parameterised attribute filtering, and the two dialect builders
  this retires), ADR-0023/ADR-0029 (batch pages, the feature model),
  ADR-0033 (in-process interfaces, the optional additive-face pattern),
  ADR-0036 (relations are an adapter-side verb), ADR-0037 (feature editing,
  the synthetic `OBJECTID`), ADR-0038 (`IFeatureLookup`, the additive read
  face), ADR-0056 (Feature query params are an adapter projection),
  ADR-0057 (statistics capability honesty), ADR-0073 (SQL Server, the second
  dialect).
- `src/Spatial.Contracts/IDataStores.cs`,
  `src/Spatial.Stores.Memory/MemoryStore.cs`,
  `src/Spatial.Stores.PostGIS/Core/PostgisFilter*.cs`,
  `src/Spatial.Stores.SqlServer/Core/SqlServerFilter*.cs`,
  `src/Spatial.Stores.PostGIS/PostgisFeatures.cs`,
  `src/Spatial.Esri.Codec/EsriFilterClause.cs`,
  `src/Spatial.Host/Api/StoreEndpoints.cs`,
  `architecture/distilled/contracts.md`, `architecture/distilled/plugins.md`
