---
status: accepted
date: 2026-10-02
deciders: maintainer + agent
summary: The `insert_final_newline` the `.editorconfig` claims for `[*]` is enforced over **every text file the repository ships**, not C# alone — `tools/final_newline.py` loses its file-type table and reads whatever the claim names, and the exemptions are **captured and vendored artefacts, expressed as `insert_final_newline = false` sections in `.editorconfig`** the way ADR-0143 exempted `psql` captures, not a suffix list inside the check. The 166 files git shipped without a final newline are one byte each; the 52 captures and bundles that are exempt are exempt because appending a byte to them stops them being the record of what the tool or upstream shipped.
amends: ADR-0186
related: ADR-0143, ADR-0134, ADR-0146
---

# ADR-0188: the final-newline rule covers all of `[*]`, and captures are exempt through `.editorconfig`

## Context

ADR-0186 closed the hole ADR-0134 opened — `dotnet format` left the merge path,
so a C# file with no final newline reached `main` and turned the post-merge CI
run red — by checking the rule in `tools/final_newline.py` and calling it from
every lane. It scoped that check to `.cs`, because `FINALNEWLINE` is a Roslyn
diagnostic: `dotnet format` on a `.csproj` with no final newline exits 0.

The consequence, named in ADR-0186 §2 and filed as SpatialEngine-3rz, is that
`.editorconfig` claims `insert_final_newline = true` for `[*]` and the check
enforced it over **4%** of it. About 166 tracked files shipped with no final
newline and nothing in the repository said so: 65 markdown (every ADR from
ADR-0001 forward, `architecture/principles.md`, the `apps/**` and
`src/**` orientation digests), 16 `.csproj`, 13 authored `.json`, 7 `tools/*.py`,
4 `eng/*.sh`, 4 `.mjs`, `.editorconfig` itself and `.node-version`. The formatter
covers none of them — it is not on the merge path, and where it does run it
reads C#.

Two categories in that set are not slips. `research/compat/ground-truth/*.json`
(13), `tests/fixtures/esri-docs/**.json` and `tests/fixtures/qgis/*.json` (36)
and `clients/typescript/scripts/openapi.snapshot.json` are **captured** — frozen
ArcGIS Server responses and a `curl -o` of the host's OpenAPI document, recorded
by `research/arcgis/harvest.py` and `eng/e2e-web.sh`. `apps/workbench-web/public/*.mjs`
is the vendored `maplibre-gl` v6.6.0 bundle. ADR-0143 already decided what to do
with a capture: exempt it through `.editorconfig` with the reason in it, because
"the padding is the record of what the tool printed", and retyping it to satisfy
a gate destroys the thing the file is.

## Decision

**`tools/final_newline.py` reads every text file `.editorconfig` names for the
rule, and captured and vendored artefacts are exempted through `.editorconfig`
rather than a file-type table inside the check.**

### 1. No file-type table

The check's `FORMATTER_SUFFIXES = (".cs",)` is gone. A file is read when it is
non-empty, under the size cap, has no NUL byte in its first 8 KiB, and its most
specific matching `.editorconfig` section does not set
`insert_final_newline = false`. There is nothing to grow later: the suffix list
was an accident of which formatter existed, and a rule about *which files a
repository check reads* is the one thing that silently rots.

### 2. The exemptions are `.editorconfig` sections, ADR-0143's shape

Five sections, each with the reason in it: the captured ArcGIS ground truth, the
`esri-docs` replay corpus, the QGIS capture, the recorded OpenAPI snapshot, and
the vendored MapLibre bundles. `.editorconfig` is the file the claim is written
in, so the scope of the claim is expressed in the claim rather than in a second
place that has to be kept in step — and ADR-0186 already made the check *read*
the scope, so a later exemption is honoured rather than demanded back.

### 3. The hard case was measured, not assumed

The bead's worry was that a test or drift check compares the bytes of
`research/compat/ground-truth/*.json` or `openapi.snapshot.json`, so appending a
newline would break it. It does not: the ground-truth corpus is read as parsed
JSON by the Host tests and the workbench parity harness,
`EsriDocsReplayTests` applies a semantic JSON diff at a `1e-6` tolerance, and
`scripts/check-generated.mjs` compares a **regenerated** `src/generated-types.ts`
against the committed one rather than the snapshot's bytes. That is why these
are exempt rather than fixed — not because a comparison exists, but because a
capture is the record of what the server sent, and the day a byte comparison
does arrive, an exempt capture is the right answer to it too.

### 4. The 114 files that are not captures got the byte

A trailing `\n` each, in this branch. 65 markdown records, 16 `.csproj`, both
root `.props`, 13 authored `.json`, 7 `tools/*.py` (including the check itself),
4 `eng/*.sh`, the workbench and SDK TypeScript, `.editorconfig` and
`.node-version`. Nothing else changed: no reformatting, no retyping, no content.

## Alternatives

- **Leave the rule unenforced outside C#** and let the formatter own it (the
  ADR-0186 status quo). Rejected: it leaves a rule claimed in configuration that
  96% of the repository can break invisibly, which is the whole shape of the
  hole ADR-0186 was written to close.
- **Fix all 166 files, captures included.** Rejected for the reason ADR-0143 gave
  and §3 measured: appending a byte to a capture makes it no longer the record,
  and re-recording the corpus is a network harvest this bead should not be
  standing on.
- **Keep a suffix table in the check and extend it** (`.cs`, `.csproj`, `.json`,
  `.md`, …). Rejected: it is the same claim as `[*]` written twice, and the two
  copies drift the first time somebody adds a file type.
- **Exempt the captures by path prefix inside `tools/final_newline.py`** rather
  than in `.editorconfig`. Rejected: ADR-0186 §2 already made the check read the
  scope from `.editorconfig` and honour it, so a second exemption list in the
  script would be the scope read twice, with the two disagreeing.
- **Make the check fix the files** (`--fix`). Rejected for ADR-0143's verbatim
  reason: it is a check, it has no `--fix`, and a formatter is what CI and the
  occasional `--format` run are for.

## Not decided

Whether the indentation and import-ordering rules ADR-0186 §4 left to the
formatter ever get an SDK-independent check. Still nothing cheap reads them.

Whether a capture should instead be normalised at capture time — `harvest.py`
and `eng/e2e-web.sh` writing a trailing newline, which would let the captures
join the enforced set. That is a change to what the captures *are*, so it is its
own bead rather than a line here.

## Consequences

- The `[*]` claim is enforced in full: a markdown record, a `.csproj`, a shell
  script or a `.editorconfig` with no final newline now fails the fast lane and
  CI, where it previously reached `main` and nothing said so.
- 114 files carry one extra byte each in this branch; no content changed, so no
  review reads as a diff of substance.
- 52 files — the two captured corpora, the ground truth, the OpenAPI snapshot and
  the MapLibre bundles — stay byte-exact, and the reason each is exempt is in
  `.editorconfig` rather than in a list nobody reads at review time.
- The check now reads every file the lanes hand it rather than filtering by
  suffix. Its cost is unchanged in shape (ADR-0186 §3 measured ~2 s repository-
  wide for a `grep`-shaped pass) and the `.editorconfig` read is per-invocation
  either way; the measured repository-wide run is in §Measurements.
- A captured corpus that is *regenerated* into an exempt path stays clean, and a
  new corpus that is not exempted fails the lane on the commit that adds it —
  which is the intended prompt to add the section.

## References

- `tools/final_newline.py`, `tools/test_final_newline.py`, `.editorconfig`
- ADR-0186 (the check, and the `[*]` gap this record closes), ADR-0143 (the
  `psql` capture exemption this one copies), ADR-0134 (why the formatter is not
  the reader), ADR-0146 (the shape a later repository rule took)
- `AGENTS.md` ("Commands"), `eng/verify.sh` (`final_newline_step`)
- SpatialEngine-3rz (this record's bead), SpatialEngine-744 and ADR-0186 (where
  the measurement came from)

## Measurements

Taken 2026-10-02 in worktree `1mmcart7`, on this branch's base (the sweep over
`git ls-files --cached --others --exclude-standard`, the check's own file list).

| Question | Measurement |
| --- | --- |
| How many tracked files shipped with no final newline? | 166, counted by the same empty/binary/size exclusions the check uses: 65 `.md`, 55 `.json`, 16 `.csproj`, 9 `.ts`, 7 `.py`, 4 `.mjs`, 4 `.sh`, 2 `.props`, 1 `.tsx`, 1 `.html`, 1 `.editorconfig`, 1 `.node-version`, 1 extensionless. ADR-0186 measured "about 190" before its `.cs` table landed; 166 is the same sweep with the repository as it stands. |
| How many are captures or vendored bundles? | 52, by the five `.editorconfig` sections: `tests/fixtures/**` (36: 28 under `esri-docs`, plus the per-slice `manifest.json` files, `policy.json`, the eight `edgecases` cases and the QGIS capture), `research/compat/**` (13: the ground-truth corpus including `manifest.json` and `catalog.sampleserver6.json`), `apps/workbench-web/public/*.mjs` (2), `clients/typescript/scripts/openapi.snapshot.json` (1). The other 114 are authored and were fixed. |
| Does anything compare those captures' bytes? | No. `clients/typescript/scripts/check-generated.mjs` regenerates `src/generated-types.ts` from the snapshot and compares the regenerated text against the committed text; `research/arcgis/harvest.py`'s only hash is `sha256` of a cache key, not of a file; `EsriDocsReplayTests` diffs parsed JSON at a `1e-6` tolerance; the ground truth is read by `JsonDocument.Parse` and by the workbench parity harness. The exemption is a decision about captures, not a fix for a byte comparison. |
| How long does the check take now that it reads every file? | 1.06 s clean over `git ls-files --cached --others --exclude-standard` on the whole repository (three runs, this host), against ADR-0186's ~2 s and ADR-0143's 1.9 s for the same shape of pass over `.cs` and the whitespace rule — reading every text file rather than filtering by suffix is not the expensive part, and the `.editorconfig` parse is per-invocation either way. 0.18 s over this branch's 115-file change set. |
