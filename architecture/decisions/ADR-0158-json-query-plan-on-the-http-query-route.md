---
status: accepted
date: 2026-10-02
deciders: maintainer + agent
summary: `POST /api/features/query` accepts the ADR-0074 `FeatureQuery` plan as JSON under `plan` and answers a page (`batches`, `nextCursor`, `totalCount`, `hasMore`); the top-level `bbox`/`filter` stay as sugar for the plan's `boundingBox`/`where` for one more release and a request that sends both spellings of one member must agree; the predicate tree crosses the boundary as a discriminated `op` node; the .NET client gains `QueryPlanAsync` and the CLI a `dataset query` verb. Removing the filter text is a separate breaking change.
amends: ADR-0074
---

# ADR-0158: The query plan crosses the HTTP boundary as JSON, and the filter text stays as sugar

## Context

ADR-0074 made the feature-read contract a core-typed `FeatureQuery` plan and
retired every per-provider filter grammar, but it drew the line at the HTTP
boundary: "`GET /api/features/query?filter=` keeps the syntax clients already
send — it is a published surface and one breaking change at a time is the rule".
Its Consequences section names the shape of the gap that leaves:

> The plan's projection/order/limit/offset/cursor members only become useful at
> the boundary once the plan itself can be sent.

Today `POST /api/features/query` takes `{dataset, bbox?, filter?}`, builds
`new FeatureQuery(BoundingBox: bbox, Where: FeatureFilter.Parse(filter))` and
answers `FeatureBatchesResponse(Batches)` — the `.Batches` of the page, with the
continuation, the total and the "one more" signal dropped on the floor. So the
store contract gained ordering, paging and cursors, every store answers them
(`FeatureQueryPage.HasMore`, `NextCursor`, `TotalCount`), the Esri adapter pages
with them, and the one route a non-Esri client can reach cannot use any of it:
the .NET client sends `filter` and cannot express an ordering or ask for a page,
and the CLI has no feature query at all.

The remaining consumers of the sugar are small and knowable:
`Spatial.Client`'s `QueryAsync`, the TypeScript SDK's `queryFeatures`, the
workbench's chaos panel and `Spatial.Cli` (which has no verb). None can be
migrated in the same change that publishes the plan, which is exactly the "one
breaking change at a time" the earlier record asked for.

## Decision

**1. The route accepts the plan, and the sugar is sugar.** `FeatureQueryRequest`
gains a `plan` member carrying the ADR-0074 plan — `ids`, `where`, `bbox`,
`projection`, `order`, `limit`, `offset`, `cursor` — under the same camelCase
names the plan's own members have. The existing top-level `bbox` and `filter`
stay, and mean exactly what they meant: the plan's `boundingBox` and the plan's
`where`, with the text parsed once at the boundary by the same
`FeatureFilter.Parse` (ADR-0074 §3).

**2. Two spellings of one member must agree, or the request is refused.** A
request that sends `filter` *and* `plan.where`, or `bbox` *and* `plan.bbox`,
with different values is `invalid.arguments` naming the member; sending the same
value twice is accepted and used once. A parameter is honoured or rejected by
name, never accepted and ignored — a request whose `limit` disagrees with
itself cannot be answered with one of the two numbers.

**3. The answer is the page.** The route answers `FeatureQueryResponse`
(`batches`, `nextCursor`, `totalCount`, `hasMore`) instead of the bare batch
list. `batches` keeps its name, shape and Base64 encoding, so every current
consumer — the TypeScript SDK, the workbench — reads the response unchanged and
gains the page fields it was ignoring. `/api/features/scan` keeps
`FeatureBatchesResponse`: a scan is the unbounded read and has no page to
report.

**4. The predicate crosses as a discriminated node, not as text.** `where` is a
node with an `op`:

| `op` | members |
| --- | --- |
| `and`, `or` | `terms` (one or more nodes) |
| `compare` | `field`, `operator`, `value` |
| `isNull` | `field`, `negated?` |
| `isIn` | `field`, `values`, `negated?` |
| `constant` | `truth` (a boolean) |

`constant` takes a bare boolean under its own member rather than a boolean
literal: it is the one node with no field and no literal, and a bare
`{"kind":"boolean","boolean":true}` for "always true" is a shape a client would
have to be taught to spell.

`operator` is the `ComparisonOperator` name in camelCase (`equals`, `notEquals`,
`lessThan`, `like`, `likeFolded`), and a value is
`{kind, text?|number?|boolean?}` over `LiteralKind`, with `integer` carried as
text so no precision is lost and `dateTime` as epoch milliseconds. A member the
node's `op` does not carry — a `field` on an `and`, a `terms` on an `isNull` —
is `invalid.arguments`, and an `op` outside the six is too. There is no `not`
node because the core vocabulary has none: negation is a flag on the two tests
that have it, and a JSON `not` would be a second spelling of one idea.

**5. Validation stays where ADR-0074 put it.** The plan is not re-validated at
the route: each store validates the plan against the dataset's schema when it
compiles it (`FeatureQueryValidation`, `FeaturePlanExecutor`, the two SQL plan
readers), so a plan the route accepts is a plan every store accepts and a plan
naming a field the dataset lacks fails as `invalid.arguments` the same way
whether it is evaluated in memory or pushed to SQL. The route's only new
obligation is to build one plan from the two spellings without discarding a
value it was sent.

**6. The .NET client sends the plan and returns the page.**
`SpatialDataClient.QueryPlanAsync(dataset, plan, store)` posts the plan spelling
and answers the core `FeatureQueryPage` — the client already references
`Spatial.Core` and already decodes batches to core values, so the page a store
returns is the page a caller gets. `QueryAsync(dataset, bbox, filter, store)`
keeps its signature and keeps answering batches; it is now the sugar spelling,
implemented over the same route. The CLI gains `dataset query`, which takes
`--filter`/`--where`, `--bbox`, `--project`, `--order`, `--limit`, `--offset`,
`--ids` and `--cursor` and prints the page's counts.

**7. `filter` is deprecated, not removed.** The CHANGELOG records that the text
spelling answers for one more release and names the removal as a separate
breaking change. This record publishes the replacement; it does not withdraw the
surface its clients still send.

## Alternatives

- **Put the plan at the top level of the request** (`{dataset, where, order, …}`
  beside `{dataset, bbox, filter}`). Two spellings of `where` and `bbox` in one
  flat object cannot be distinguished at all — there is no way to tell
  `where: null` from "no where sent" — so the agreement rule of 2 is
  unimplementable. Rejected in favour of a nested `plan`.
- **Publish the plan on a new route** (`/api/features/query/v2`) and leave the
  old one alone. Two routes for one query, and no route where a client reads a
  page, is exactly the surface the TypeScript SDK would not be able to move to
  without a second break. Rejected.
- **Accept `where` as the filter text under the plan** (`plan.where = "name = 'x'"`).
  A string or a tree in one member means the client cannot tell a malformed plan
  from a malformed expression, and the tree is the contract (ADR-0074 §2) — the
  text exists because a published surface sends it, not because it is a shape.
  Rejected; the sugar stays at the top level, where it already is.
- **Serialize `Predicate` directly with a `$type` discriminator.** The core type
  is an abstract record with nested cases; System.Text.Json polymorphism puts a
  `$type` property in the JSON, which the host's OpenAPI emitter and the
  TypeScript SDK generator both model as `unknown`, and the generated SDK type
  would carry a discriminator no client should write. Rejected for a flat node
  with an `op`.
- **Retire `filter` in the same change.** Two breaking changes at once make the
  migration unattributable — the reason ADR-0074 deferred this one in the first
  place. Deferred again, to a bead that names the release.

## Not decided

- **Whether the aggregate and distinct faces (`IFeatureAggregateStore`) get a
  JSON wire.** They are reductions rather than plans and have no
  `/api/features/*` route to extend; the Esri statistics surface serves them. A
  follow-up bead, when a client other than the Esri adapter wants a reduction.
- **Whether the plan ever crosses as GeoJSON.** The Esri surface answers with
  GeoJSON/JSON and the OGC surface with GML; the neutral plan is neither, and
  which of those a neutral route should answer is not settled by this record.
- **Whether the TypeScript SDK and the workbench move to the plan spelling** in
  the same release that removes `filter`. They keep sending the sugar until the
  removal, which is what makes 7 safe.

## Consequences

- **The .NET client can order, page and resume a query for the first time**,
  which is the whole point: every store in the repository already answers
  `limit`, `offset`, `order` and `cursor`, and none of it was reachable.
- **The route's response is a superset of what it was.** `batches` is unchanged,
  so no client breaks; the TypeScript SDK's generated types gain three optional
  properties on the query response and its `FeatureBatchesResponse` stays for
  `scan`.
- **The published OpenAPI grows a predicate schema**, which is the first
  structured filter in the neutral API. A client that hand-writes a predicate
  gets typed `invalid.arguments` on a member it spelled wrongly, which is the
  point of the strictness in 4 — but the schema is now part of the surface, and
  the `Literal` kind/kind-name pairing is the part a future reader is most
  likely to want changed.
- **The cost**: two spellings of the same query live on one route for a release,
  with a rule (2) to police requests that use both. If the removal bead slips,
  that cost is indefinite — the sugar is the thing that has to be deleted, and
  nothing else about this record expires.
- **`Spatial.Cli` gains a verb**, so `ISpatialGateway` gains a query member and
  the fakes in the CLI suite gain a recording for it.

## References

- ADR-0074 (the plan contract, the filter text as a boundary format, and the
  deferral this record pays), ADR-0033 (the host API shapes and camelCase JSON),
  ADR-0028 (parameterised filtering; the literals here bind, never interpolate),
  ADR-0116 (`FeatureQueryPage.HasMore`, ADR-0074 §5), ADR-0052 (the CLI's
  gateway seam).
- `src/Spatial.Contracts/Http/SpatialHttpContracts.cs`,
  `src/Spatial.Contracts/Http/FeatureQueryWire.cs`,
  `src/Spatial.Host/Api/StoreEndpoints.cs`,
  `src/Spatial.Core/Features/Query/Predicate.cs`,
  `src/Spatial.Core/Features/Query/FeatureQueryPage.cs`,
  `clients/dotnet/Spatial.Client/SpatialDataClient.cs`,
  `clients/dotnet/Spatial.Cli/Commands/DatasetCommands.cs`,
  `architecture/distilled/contracts.md`, `architecture/distilled/host-and-clients.md`.
- SpatialEngine-gd2.
