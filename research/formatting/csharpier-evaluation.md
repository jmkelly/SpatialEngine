# csharpier as a whole-repo formatter: the measurement, not the adoption

**Verdict: do not adopt now.** The speed claim is true and smaller than it
looks; the price is a ~87,000-line reformat diff, a second owner of style, and
four call sites in the gate. This is the evaluation
[ADR-0109](../../architecture/decisions/ADR-0109-the-verification-lanes-implemented.md)
§"On a faster whole-repo formatter" deferred to its own bead
(SpatialEngine-4h0). Nothing here is adopted: the repository still uses scoped
`dotnet format`, and the `.config/dotnet-tools.json` this report evaluated
through was installed to a scratch `--tool-path` and never committed.

Host: the 12-core swarm box, worktree `1mmcart7`, branch `bd/SpatialEngine-4h0`
off `origin/main` @ `eb909b5c`, SDK 10.0.400, measured 2026-10-01.
**The box was shared with other agents throughout** — load average moved between
8 and 139 across the session — so the wall times are a range and the CPU times
are the load-independent figure. Both are given, and the comparison to read is
the ratio measured back-to-back at the same load, not either absolute number.

## 1. The numbers

`csharpier` 1.3.0 (MIT), installed with `dotnet tool install --tool-path` so the
tree stayed clean:

| Command | Wall | CPU (`user`) | Tool's own report |
| --- | --- | --- | --- |
| `csharpier check .` (first run after install, no warm page cache) | 20 s | — | `Checked 1046 files in 19612ms` |
| `csharpier check .` (load 53) | 37.7 s | 2 m 44 s | exit 1, 966 files |
| `csharpier check .` (load 11) | 18.5 s | 1 m 53 s | exit 1, 966 files |
| `csharpier format .` on a pristine `git archive HEAD` copy | 18.7 s / 19.0 s | 1 m 46 s | `Formatted 1046 files in 18659ms` |
| `dotnet format SpatialEngine.slnx --verify-no-changes --no-restore` (build servers shut down, load 8) | 2 m 19 s | 10 m 03 s | exit 2 |
| same, load 13–51 | 3 m 02 s | 11 m 22 s | exit 2 |
| same, load 8–31 | 2 m 27 s | 11 m 03 s | exit 2 |
| same, load 59–139 | 8 m 01 s / 8 m 39 s | 13 m 22 s / 13 m 30 s | exit 2 |

Interleaved A/B/A at load 11–53 on the same box, same minute: csharpier 37.7 s
and 18.5 s against `dotnet format` 3 m 02 s. **csharpier is 3–10× faster in wall
time and ~5–6× cheaper in CPU**, and sub-minute as advertised.

ADR-0109's 677–786 s for the whole-solution formatter **did not reproduce**:
the same command on the same command line costs 2 m 19 s–8 m 39 s wall here.
Its CPU cost (10–13 m) is consistent with a box that was also running eight
agents, so read the ADR's number as the contended end of this range rather than
as a different order of magnitude. The conclusion ADR-0109 drew from it —
scoped `dotnet format` at ~45 s per project beats a whole-solution pass for a
pre-handoff step — is unaffected, and ADR-0134 then took the step off the
hand-off path entirely.

## 2. The style-divergence blast radius

`csharpier check .` reports on **1046 files** (988 `.cs`, 57 `.csproj`/`.props`,
1 other) and would rewrite **966 of them — 92.4%**. 79 already conform. The diff
is **967 files, +64,507 / −22,639 lines**: `src/` 463 files, `tests/` 439,
`clients/` 42, `eng/spike-*` 18, root props 2, AppHost 2.

Classified mechanically, over all 966 files (whitespace-stripped text compared,
then comma-stripped, then a per-literal comparison of runtime string text with
interpolation holes blanked):

| Change | Files |
| --- | --- |
| line breaks and indentation only | 727 |
| the above, plus commas inserted or removed | 238 |
| the above, plus a `using` reorder | 1 (`NtsGeometryRelationsFidelityTests.cs`, an alias moving after the plain usings — the same file and the same rule `dotnet format` flags as `IMPORTS`) |
| **any change to a runtime string value** | **0** |

The one that looks alarming is not: csharpier re-indents the *closing*
delimiter of a `$"""…"""` literal along with every line of it, and C# strips
that indentation from the value, so the SQL and MapLibre-JSON fixtures in
`Spatial.PostGIS.Tests` / `Spatial.Host.Tests` come through byte-identical. The
whole reformatted tree then **builds the entire solution clean: 0 errors, 0
warnings, `TreatWarningsAsErrors` on, 48 s.**

So the diff is mechanical in the strict sense — no token but a comma moves, no
string value changes, it compiles — and it is still a 87,000-line diff that
every future `git blame`, review and merge conflict has to read through.

Three behaviour differences matter more than the diff:

- **It formats project files.** 1.3.0 reformats `.csproj`/`.props` as XML (it
  re-indents them and inserts the space in `<Import Project="x" />`). Those 57
  files are outside what `dotnet format` touches today.
- **It is one style, not an analyzer set.** It reads `.editorconfig` for
  `indent_size` and `max_line_length` — verified in a scratch tree, where
  `indent_size = 2` and `max_line_length = 60` both took effect — and csharpier's
  own layout for everything else. After a switch the `.editorconfig` stops being
  the single source of style and a version-pinned tool becomes part of it.
  `dotnet format` additionally enforces the `.editorconfig`'s `IMPORTS`,
  `csharp_style_namespace_declarations` and `dotnet_diagnostic.*` severities,
  which csharpier knows nothing about. (`EnforceCodeStyleInBuild` is already
  `true`, so the build catches most of those as errors anyway.)
- **It closes the ADR-0143 hole.** A trailing-whitespace violation on a
  comment-only line, which `dotnet format` reports as nothing and exits 0 on,
  is trimmed by csharpier. That is the one place it is strictly better, and it
  does not remove `tools/trailing_whitespace.py`, which also reads `.md`,
  `.py` and `.sh`.

## 3. The pinning story

- csharpier is MIT and ships as a .NET tool, so licensing is not the issue; the
  issue is that it is a **third pinning surface** beside `global.json` (SDK) and
  `Directory.Packages.props` (packages), and the repository has no tool manifest
  today. Versions in `.config/dotnet-tools.json` are exact, so it pins as well as
  the other two — but nothing in the repository reads it.
- Adopting it means `dotnet tool restore` in **four** places that name
  `dotnet format` today: `eng/format.sh:7`, `eng/verify.sh:512` (scoped lane),
  `eng/verify.sh:528` (full lane), `.github/workflows/ci.yml:64`. Each is a
  place where a cold tool cache turns into a network fetch or a missing binary,
  and `tools/verify_scope.py`'s `FORMATTABLE_SUFFIXES` is what decides which
  projects a scoped lane format-checks at all.
- **The Architecture.Tests allowlist does not govern it, in either direction.**
  `Platform_projects_use_only_allowlisted_packages` and
  `No_inline_package_versions` walk project `PackageReference`s under `src/` and
  `tests/`; a tool manifest is invisible to both, so nothing would flag a new
  tool entry — and nothing would catch one being added silently either.
- **Scoping takes it as an ordinary file, not a solution-wide one.** Measured
  with `tools/verify_scope.py`: a change set of exactly
  `{'.config/dotnet-tools.json'}` plans `exhaustive=False`, zero format
  projects and only `Spatial.Architecture.Tests`. That is correct for a tool
  manifest, and it is *not* what the same call says for the root
  `.editorconfig` — see the defect below.

**Defect found while measuring this (not fixed here; its own bead).**
`tools/verify_scope.py._is_solution_wide` is documented to treat a root
`.editorconfig` as solution-wide, and `AGENTS.md` and ADR-0109 both promise that
a change to it "makes every lane fall back to the whole solution". It does not:
`pathlib` reports `Path('.editorconfig').suffix == ''`, so the leading-dot file
has no suffix to match, and the name is not in `SOLUTION_WIDE_FILES` either.
Measured: `_is_solution_wide('.editorconfig')` is `False` where
`_is_solution_wide('Directory.Packages.props')` is `True`, and a change set of
`{'.editorconfig'}` plans `exhaustive=False` — a branch that changes the style
for every project builds one test project. This matters to the adoption
question because an adoption is very likely to edit `.editorconfig`
(`max_line_length`, `csharp_new_line_before_open_brace`).

## 4. Is a faster formatter the right thing to spend on?

No, not now, and the reason is where the cost already sits:

- The **fast lane — the merge gate** — runs no formatter at all since ADR-0134,
  and costs 1–3 min. A faster whole-repo formatter saves it nothing.
- The **full lane** spends 2–9 min of a ~15–18 min gate in the formatter, and it
  runs on CI (`verify`), on an occasional `--bead --full`, and nowhere else.
- The **test suites dominate**: `Spatial.Host.Tests` alone is 21 m 49 s in
  ADR-0109's measurement, and the whole container matrix is far more. The
  formatter is ~15–30% of the only lane that still runs it, on a lane nobody
  waits on.

Trading a 1–7 minute saving on a lane that runs a few times a day for a
one-off 87,000-line reformat, a second owner of style and four gate edits is a
bad trade at this size. It becomes a better one if the repository grows enough
that the whole-solution formatter stops fitting in CI, or if the `.editorconfig`
gains rules csharpier would honour for free.

## What an adoption bead would have to decide

1. One commit that reformats the repository, with no logic changes in it.
2. Who owns style: `.editorconfig` (as today) or csharpier's version-pinned
   layout — and what happens to the `.editorconfig` rules csharpier ignores.
3. Keep `tools/trailing_whitespace.py` for the file types csharpier does not
   read, or find its replacement.
4. `dotnet tool restore` at the four call sites, the `.config/dotnet-tools.json`
   manifest, and an ADR amending ADR-0109's "The formatter is one formatter".
5. Decide what the scoped `--format` lane means under the new tool, and update
   `tools/verify_scope.py`'s `FORMATTABLE_SUFFIXES` if the blast radius moves.
