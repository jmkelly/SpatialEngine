---
status: accepted
date: 2026-09-30
deciders: maintainer + agent
summary: The vocabulary has a **second, separate** pattern comparison, `ILIKE`, whose case behaviour it states rather than inherits: the same whole-value pattern test as `LIKE` with the value and the pattern folded over the **ASCII alphabet** — the one fold every back end states identically — and each dialect writes the fold out (`translate`, `TRANSLATE` under the binary collation) rather than reaching for `ILIKE` or a case-insensitive collation, which are a locale's and differ between deployments. It is a separate comparison because folding for a **search** is not folding for a **key**: `delta` and `Delta` stay two features under `=`, `<` and `LIKE`. MapServer `find` pushes its search text on it (ADR-0132 §6), a plan whose only text comparison states its own fold does not pay the collation probe, and an ArcGIS REST plan narrows to the part the Esri `where` grammar can spell rather than failing the read (amends 0074, 0098, 0112, 0123, 0126).
---

# ADR-0132: A text search compares under a fold the store states — case folding is a comparison of its own, and it is not an identity

## Context

The one predicate vocabulary (ADR-0074 §2) had no case-folding text
comparison. Its only pattern comparison was `LIKE`, and ADR-0123 had just made
every pushed `LIKE` state the **byte** order the contract compares strings in —
which is right for an identity, an ordering and an exact match, and wrong for a
search. MapServer `find` is a case-insensitive `contains` / `startsWith` over a
layer's string fields, so `find(searchText=ALP)` has to match a feature named
`alpha` on every store, and no pushed plan could say so: a `LIKE` is
case-sensitive on Postgres and case-insensitive on SQL Server's shipped default
collation, so a pushed pattern is a subset of the served match on one and a
superset on the other, and the answer would depend on the store's collation.
`MapMatchPushdown.Search` pushed only the null test — "a searched field is not
null" — and kept the text in the adapter (ADR-0112).

That is the same shape as ADR-0121 and ADR-0123 in front of it, one statement
earlier: a comparison whose meaning is the *database's* is a comparison the
engine does not own. What is different here is that neither of those records
could fix it by adding a collation, because a folded match is not a match at all
under the contract's rule — `delta` and `Delta` are two strings (ADR-0098 §3),
and `ILIKE` would make them one.

The obvious way to add it is `ILIKE` on Postgres and a case-insensitive
collation on SQL Server. Neither is the same comparison:

- Postgres `ILIKE` folds under the **database's** collation. It is `Épsilon` for
  `epsilon` on a stock `en_US.utf8` container and byte-exact on a `C` one — and
  ADR-0121 already established that the fixture database may be either. The
  same plan would answer two different row sets on two deployments of the same
  store.
- `Latin1_General_100_CI_AS` folds Unicode case on SQL Server, which is *not*
  the reference evaluator's answer either — it would push a row the reference
  never selects, and the pushed row set is the answer on a fully pushed plan
  (ADR-0097 §2), so a superset is a wrong answer and not a slower one.

So the comparison could not be "the pattern, with the database's case folding
swapped for the contract's". It had to state a fold that every back end states
identically, and the reference evaluator had to state the same one.

## Decision

**1. The vocabulary grows a second pattern comparison, `ILIKE`, beside `LIKE`,
and it is a separate comparison rather than a flag on `LIKE`.** It is the same
whole-value pattern test — `%` is any run, `_` is one character, the pattern is
anchored at both ends — with the value and the pattern **folded over the ASCII
alphabet** (`A`–`Z` to `a`–`z`) first and nothing else folded.
`ComparisonOperator.LikeFolded` is the member; the filter-text spelling is
`ILIKE`, in `FeatureFilterText`'s one grammar (ADR-0074 §3), so a plan can carry
it and every back end compiles it. `PredicateCompatibility` answers it exactly
as it answers `Like` — a text column against a text literal, and a comparison
with no meaning matches nothing.

**2. The fold is ASCII because that is the one fold every back end states
identically, and a comparison that answers differently on two deployments is
not the contract's comparison.** `ToLowerInvariant`'s Unicode simple case
folding is *not* stated identically anywhere: it is not `ILIKE` on a `C`
database, not `ILIKE` on a Turkish one, and not a Windows collation. So a case
pair outside `A`–`z` — `É`/`é`, `Σ`/`σ`/`ς`, `ǲ`/`ǳ` — is not folded by the
comparison, on any provider, and the conformance suite pins it. This is the
honest bound of the pushdown: Unicode case folding is not expressible as one
answer across these back ends, so it is not claimed as one.

**3. Each dialect writes the fold itself rather than reaching for a server's.**

- PostGIS: `translate("code", 'ABC…Z', 'abc…z') LIKE @p0`. `LIKE` is
  case-sensitive whatever the database's collation is, so the fold is the whole
  of the case behaviour and the statement needs no `COLLATE` term.
- SQL Server: `TRANSLATE([code], N'ABC…Z', N'abc…z') COLLATE
  Latin1_General_100_BIN2 LIKE @p0`. Both halves are needed and neither is
  optional: the fold alone would leave the comparison under the database's
  case-insensitive default, and the collation alone would leave the fold to the
  database.

The two alphabets are engine text, never client text, and every literal still
binds as a parameter (ADR-0028). The pattern binds **folded** — the same
re-encoding of a literal to its column's kind this compiler already does for a
guid and an instant, because the server has no operator for the unfolded pair
that means the same thing.

**4. A folded comparison does not make a plan read the database's collation.**
`PostgisPredicateSql.ComparesText` asks whether a plan compares text *by bytes*,
so a plan whose only text comparison states its own fold does not pay the
cached catalog read ADR-0121 introduced (ADR-0123 §3). A byte-ordered
comparison beside a folded one still does, and a group row's operand is folded
in a `HAVING` the same way it is in a `WHERE`.

**5. The Esri `where` grammar does not grow the comparison, and a plan carrying
one does not fail an ArcGIS REST read.** ArcGIS has no `ILIKE` and its `LIKE`
folds case by whatever collation the service was published with, so there is no
honest spelling. `EsriWhereText.Statable` narrows a plan to the part a remote
service can be asked about: an unstatable comparison is dropped, a group that
loses every term asks the remote for everything, and the reference executor
finishes the plan over what came back — so a dropped term admits more rows and
never fewer, and a readable remote layer never becomes an `invalid.arguments`
for a clause the caller never sent. This is the same pre-filter discipline the
map surfaces use, applied to the one provider whose restriction is remote.

**6. MapServer `find` pushes the search text.** `MapMatchPushdown.Search` takes
the text and the `contains` flag and compiles one folded pattern per searched
string field — `%text%` for a contains search, `text%` for a startsWith one —
which is the restriction ADR-0112 could not state. The adapter's own
case-insensitive match stays the answer over the rows the plan admitted. A
search text **outside the ASCII alphabet** is not pushed as a pattern and keeps
the older restriction (a searched field is not null), because the served search
folds case in Unicode and a plan whose fold is narrower than the search's would
*drop* rows the search has to match — the direction a pre-filter may not take. A
search text carrying a **backslash** is not pushed either, for a different
reason: `\` is Postgres's `LIKE` escape character and nothing at all in T-SQL, so
a `startsWith` search ending in one is a statement Postgres refuses
(`LIKE pattern must not end with escape character`) and a pattern SQL Server
reads as two characters. The plan is one tree for every store, so a text any
provider would read differently stays in the adapter.

## Consequences

- `find(searchText=ALP)` is a plan now, on every store that can express one, and
  it still matches `alpha`. So does a filter written as `code ILIKE 'ALPH%'` on
  the published `filter` surface — and `code LIKE 'ALPH%'` still matches nothing,
  which is the contrast that makes the difference measurable.
- **The fold is ASCII, and the served find is not.** A search that needs Unicode
  case folding (`Écoute` for `écoute`) is still answered — by the adapter, over
  the wider restriction. Nothing the search could match before is lost; what
  changes is that the store is no longer asked to narrow on it.
- **A folded text filter can no longer seek an index, on either provider.** A
  btree index is built with the column's collation and the comparison wraps the
  column in a function, so a folded filter is a scan. On SQL Server the
  `COLLATE` term alone already cost the index (ADR-0123) and the fold costs the
  same trade; on Postgres this is the same trade ADR-0121 made for the sort key.
  That is the price of the contract's answer (principle 15), and a deployment
  that wants both wants an ASCII-folded generated column and an index on it,
  which is a schema decision this record does not make.
- The reference evaluator owns the fold (`ReferencePredicate.FoldedLike`), and
  the facade's per-feature evaluator calls it rather than writing a second one.
- `PostgisFoldedPatternTests` and `SqlServerFoldedPatternTests` pin the two
  statements in every shape (folded alone, folded beside a byte-ordered
  comparison, over a column that is not text, on a second text field, and the
  byte-ordered `LIKE` unchanged); `FoldedPatternSemanticsTests` pins the
  reference's answers; `ArcGisRestFoldedWhereTests` pins the narrowing;
  `MapFindFoldedPushdownTests` pins the plan and the unchanged responses; and
  the shared predicate conformance suite carries the cases on every store that
  runs it — memory, demo, PostGIS and SQL Server, in memory and through a pushed
  `WHERE`.

## Not decided

- **Unicode case folding in the vocabulary.** It is not expressible as one
  answer across `ILIKE` under a locale, `ILIKE` under `C`, and a Windows
  collation, so it is not in the comparison. A deployment that needs it needs
  either a per-dialect fold table (three definitions of one comparison, which
  ADR-0074 §3 exists to prevent) or a search that stays in the adapter, and
  which of those is right is a different decision from this one.
- **A text search that is not a pattern match.** MapServer `find` is a
  `contains` / `startsWith`, and it compiles to `%text%` / `text%` because the
  vocabulary's text tests are whole-value patterns. A vocabulary comparison for
  "contains" as such is not added.
- **The search text's own wildcards.** A `%` or `_` in the client's text is a
  wildcard in the pushed pattern, which *widens* the plan — the right direction
  for a pre-filter, and the adapter's literal match then rejects what the
  pattern admitted. Escaping them would need a per-dialect escape (`\` on
  Postgres, `[…]` in T-SQL) and a plan carries one literal, so the pattern is
  left to widen.
- **The SQL Server order pushdown's `DISTINCT` and grouped reductions**, and the
  per-column collation term, remain the follow-ups ADR-0098 §4, ADR-0116 and
  SpatialEngine-u2x.49 named.

## Alternatives

- **`ILIKE` on Postgres and a case-insensitive collation on SQL Server.** One
  line each and the shape every database offers. Rejected for the reason above:
  both folds are a collation's, and the two are not even the same collation's, so
  the same plan answers differently on a `C` database and a locale one and on
  two providers — which is the drift ADR-0121 and ADR-0123 exist to remove, in
  the statement that decides *which rows a query sees*.
- **Make `LIKE` fold, and add nothing.** One comparison in the vocabulary. It
  would move an identity comparison into a folded one: `code = 'delta'`'s
  neighbour `code LIKE 'delta'` would stop being a byte match, and ADR-0126's
  text identity — `delta` and `Delta` are two features — would have no spelling
  left in the grammar. Folding for a search is not folding for a key.
- **Fold only the pattern in managed code and let the database compare.** The
  column's case still has to be folded somewhere, and every function that does
  it (`lower()`, `ILIKE`, a case-insensitive collation) folds per locale.
- **Keep `find` un-pushed and add the comparison for the `filter` surface
  only.** Narrower, and it leaves the served surface the one thing the bead was
  opened for: MapServer `find` is a text search, and the whole point of a
  comparison the back ends agree on is that a text search can use it.
- **Push the folded pattern and let the store's fold stand as the answer.**
  Rejected: on a fully pushed plan the SQL row set *is* the answer
  (ADR-0097 §2), so a superset is a wrong answer, not a slower one — the reason
  the reference fold is stated rather than approximated.

## References

- Principles 6 (contracts outlive implementations), 10 (stores are providers),
  15 (pushdown optional, semantics-preserving), 17 (small kernel).
- ADR-0028 (parameterised SQL, no client text), ADR-0074 §2-4 and §7 (the
  vocabulary, the pushdown surface, the Esri grammar), ADR-0097 §2 (the identity
  column, the ordinal fallback, which pairs are comparable), ADR-0098 §3 (strings
  compare ordinally), ADR-0112 (the map and per-feature read pushdown), ADR-0121
  (byte-order pushdown, the collation probe), ADR-0123 (a pushed comparison
  states its order), ADR-0126 (a text identity is a byte identity), ADR-0132 is
  this record.
- `src/Spatial.Core/Features/Query/Predicate.cs`,
  `src/Spatial.Core/Features/Query/PredicateCompatibility.cs`,
  `src/Spatial.Core/Features/Query/FeatureFilterText.cs`,
  `src/Spatial.Querying/ReferencePredicate.cs`,
  `src/Spatial.Stores.PostGIS/Core/PostgisPredicateSql.cs`,
  `src/Spatial.Stores.SqlServer/Core/SqlServerPredicateSql.cs`,
  `src/Spatial.Esri.Codec/EsriWhere.cs`,
  `src/Spatial.Stores.ArcGisRest/ArcGisRestMapper.cs`,
  `src/Spatial.Adapter.GeoServices/EsriPredicateEvaluator.cs`,
  `src/Spatial.Adapter.GeoServices/MapMatchPushdown.cs`,
  `src/Spatial.Adapter.GeoServices/MapFindEngine.cs`,
  `tests/conformance/Spatial.PredicateConformance/PredicateConformanceSuite.cs`,
  `tests/unit/Spatial.Stores.Memory.Tests/FoldedPatternSemanticsTests.cs`,
  `tests/unit/Spatial.Stores.PostGIS.Tests/PostgisFoldedPatternTests.cs`,
  `tests/unit/Spatial.Stores.SqlServer.Tests/SqlServerFoldedPatternTests.cs`,
  `tests/unit/Spatial.Stores.ArcGisRest.Tests/ArcGisRestFoldedWhereTests.cs`,
  `tests/unit/Spatial.Adapter.GeoServices.Tests/MapFindFoldedPushdownTests.cs`.
