---
status: accepted
date: 2026-09-28
deciders: maintainer + agent
---

# ADR-0081: A pushdown is allowed only where it is identity-preserving, and a literal binds as its column's kind

## Context

ADR-0074 replaced the filter string with a core-typed `Predicate` tree, one
grammar parsed once at each boundary, and per-provider compilers that keep the
ADR-0028 parameterisation guarantee. It settled that pushdown is *per-conjunct,
best-effort and result-preserving*, and that "a valid plan is never refused for
a dialect gap".

Two questions that rule raises are not answered by it, and implementing the
change surfaced both as real defects rather than as design questions.

**1. The object id is a function of the rows the store returns.** A read-only
layer with no integer identity column gets a synthetic `OBJECTID` that is the
feature's ordinal in the whole-dataset scan (ADR-0037). The GeoServices facade
assigns it by counting the rows a read returns, and before ADR-0074 that read
was always a full scan followed by a per-feature match, so the count was
over the whole dataset. Handing the clause to the store changes that: the store
returns the *matching* rows, the facade numbers those rows 1, 2, 3, and the same
feature comes back with a different `OBJECTID` depending on the query. Caught
by the Esri docs replay fixtures (`query-where` expected ids `[2,3,4,6,7]` and
got `[1,2,3,4,5]`), and reproduced directly in
`EsriObjectIdStabilityTests`. This is not a cosmetic renumbering: `objectIds`,
`returnIdsOnly`, `resultOffset`/`resultRecordCount` paging and the edit
round-trip all treat that number as a stable key, so a pushdown that moves it
breaks the served surface while still returning the right features.

**2. The parameter's type was the literal's, not the column's.** Both
compilers bound a literal by its own `LiteralKind`, so a string literal against
a `uuid` column produced `uuid = @p0` with a text parameter. Postgres has no
`uuid = text` operator: the query fails at execution with `42883`, which
surfaces as `store.unavailable` for what is really a filter that should have
matched rows. The same shape is unreachable-by-accident in the other direction
(`text > double precision`). Nothing in the conformance fixture caught the
first one because the fixture's guid case is the only thing that compares a
uuid to text, and the suite had never run against a live PostGIS.

## Decision

**1. Push an attribute clause down only when the object id is store-derived.**
A layer whose `OBJECTID` comes from an integer identity column may have its
clause pushed down, with a clause naming `OBJECTID` rewritten onto that column
first. A layer whose `OBJECTID` is the scan ordinal keeps the clause as a
residual per-feature match, whatever the clause names — not only when it names
the `OBJECTID`. The scan ordinal and the row count a store returns are the same
number, so filtering at the store renumbers the key by construction; there is
no predicate the store could apply without that effect.

This is a statement about *what a layer can push down*, not about which
dialect supports which operator, and it composes with ADR-0074 §4: the residual
evaluator already exists and is already the documented answer for the facets a
store cannot express. The cost is that a string-identity or ordinal-scheme layer
filters in the facade rather than in SQL; the benefit is that the pushdown is
result-preserving on the key, which is what §4 requires.

**2. A literal binds as the column's kind, or the comparison matches nothing.**
The compiler already resolves the field against the dataset schema, so the
schema decides the parameter's CLR type: a string literal compared with a
`Guid` column binds as `Guid`, and a value that is not a guid — or a number
against a `Guid` column, or any literal against a `LIKE` on a non-text column —
is a comparison that can never match. The compiler emits the constant truth
value for it rather than the comparison.

The alternative — coercing so the comparison is answerable, or refusing the plan
as `invalid.arguments` — is rejected on both sides. Coercing gives a store a
different answer from `MemoryStore` for the same plan, and ADR-0074 §4 requires
the answer to be the evaluation of the plan over the whole dataset every time.
Refusing makes a server's type system a client-visible failure, which is
precisely the failure mode §4 was written to end, and would make the stores
disagree with the reference evaluator that the conformance suite holds them to.
"Never matches" is what the reference evaluator already answers for these
inputs, so it is the only answer that keeps one vocabulary with one meaning.

**3. A number against a date-time column is re-encoded as the instant it
already is.** A `DateTimeOffset` column compares with any numeric literal,
because a date-time and an epoch-millisecond count are the same point on the
same axis and the reference evaluator reads both that way. That makes the
comparison answerable *in meaning* but not in SQL: neither server has a
`timestamptz = bigint` or a `datetimeoffset = bigint`. The compiler therefore
binds the number as a `DateTimeOffset` — the same lossless re-encoding that
binds a guid-formatted string as a `Guid` — rather than emitting a comparison
the server cannot evaluate, and rather than answering "no rows", which would
disagree with the reference evaluator for a plan that does match. The
sub-millisecond fraction of a fractional literal is preserved, because the
reference evaluator compares the right-hand side as the double the client
wrote; truncating it would move the bound.

This is a re-encoding of one meaning into the column's type, not a conversion
between meanings: it is applied only where rule 2 has already established that
the literal denotes the column's kind. A string against a date-time column is
still unanswerable, because a string is not an instant.

The which-pairs-are-answerable table itself is
`Spatial.Core.Features.Query.PredicateCompatibility`: it is a classification of
the value types, so it is structural and belongs beside the tree, while the
binding of an answerable literal stays in each provider. The reference
evaluators answer the same table in their own back ends, which is what the
conformance suite holds them to.

An `IN` list drops the values the column can never equal rather than failing:
a value that cannot match is not an error and does not poison the list, so the
remaining values still answer. The list answering nothing at all emits the
negated or plain truth value, because removing a never-matching value from a
`NOT IN` is only a no-op while the list is non-empty.

## Consequences

- `Spatial.Stores.Memory` remains the reference evaluator, and the shared
  conformance fixture (`tests/conformance/Spatial.PredicateConformance`) is what
  makes the compilers and the evaluators one vocabulary rather than three
  dialects: the PostGIS and SQL Server suites run it against live containers, so
  a type that binds wrongly is a red test, not a runtime error in a host.
- A new store provider must implement the same kind-aware binding, or its
  answers will drift from the reference on exactly the cases above. The
  conformance suite is the check; there is no capability flag for it.
- The pushdown rule is a property of the layer, so a provider cannot widen it:
  a store that could answer the clause still is not asked to, because the
  renumbering happens above the store. Work that pushes *more* into the store —
  the feature-match envelope — has to keep this in view, since anything that
  changes which rows a read returns changes the ordinal unless the layer's
  object id is store-derived.
- Both rules are invisible to a client: no parameter, response shape or
  capability flag changed. The `where` text, the `filter` query parameter and
  the served `OBJECTID` values are the same ones they were.
- The two residual/evaluator copies the change leaves in place (`MemoryPredicate`
  and the facade's own) are the open structural question ADR-0074 deferred, and
  stay deferred.

## Alternatives

- **Keep the clause residual on every layer.** Simplest and always correct, and
  it throws away the pushdown for every identity-backed layer, which is the
  point of the change and the only case a client can edit. Rejected.
- **Have the store return the ordinal alongside the row.** Would let every layer
  push down, but adds a synthetic column to a core-typed feature batch, changes
  the contract, and still cannot number a row the store did not return. Rejected
  in favour of the narrower rule; recorded here as the follow-up shape if a
  store ever needs it.
- **Cast the literal to the column's SQL type** (`@p0::uuid`, `@p0::bigint`).
  Standard SQL, and it would make the parameter text rather than typed. It also
  turns a client typo into a cast error the server reports as a store failure,
  and it embeds provider type names in a layer that has no SQL at all. Rejected
  in favour of binding the right CLR type — which is what rule 3 needs for an
  instant, where the binding itself is the re-encoding.
- **Report an incomparable comparison as `invalid.arguments`.** Tells the client
  something actionable, but the plan is valid — the memory store answers it — so
  the same plan would succeed on one store and fail on another, which is the
  dialect-gap-as-client-error failure ADR-0074 §4 exists to remove. Rejected.
- **Let the unbindable value through and let the server reject it.** Zero code,
  and the failure is a `store.unavailable` (exit 4) for a client mistake, with
  the position and the offending value nowhere in the message. Rejected.

## References

- ADR-0074 (the plan contract; per-conjunct result-preserving pushdown, and the
  text grammar as a boundary format), ADR-0037 (the synthetic `OBJECTID` and
  the scan ordinal), ADR-0028 (parameterised attribute filtering, and the
  dialect builders this reuses), ADR-0038 (`IFeatureLookup`, the additive read
  face), ADR-0056 (Feature query params are an adapter projection),
  Principles 6, 8, 15, 17.
- `src/Spatial.Adapter.GeoServices/EsriPredicateEvaluator.cs` (`EsriWhereResolver`),
  `src/Spatial.Adapter.GeoServices/FeatureSpatialMatcher.cs` (where the ordinal
  is assigned), `src/Spatial.Core/Features/Query/PredicateCompatibility.cs`,
  `src/Spatial.Stores.PostGIS/Core/PostgisPredicateSql.cs`,
  `src/Spatial.Stores.SqlServer/Core/SqlServerPredicateSql.cs`,
  `tests/conformance/Spatial.PredicateConformance/PredicateConformanceSuite.cs`,
  `tests/unit/Spatial.Core.Tests/PredicateCompatibilityTests.cs`,
  `tests/unit/Spatial.Adapter.GeoServices.Tests/EsriObjectIdStabilityTests.cs`.
