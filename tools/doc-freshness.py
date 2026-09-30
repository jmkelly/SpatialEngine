#!/usr/bin/env python3
"""Audit the documentation corpus, the way the quality loop audits the code.

    python3 tools/doc-freshness.py            write doc-report.json and doc-queue.md
    python3 tools/doc-freshness.py --quiet    ... and print only the summary
    python3 tools/doc-freshness.py --root DIR read a different tree (tests)

The gap this closes (SpatialEngine-imz.2): the quality loop audits code five
ways — crap4dotnet, coupling metrics, coverage, Stryker, warnings — and audits
documentation zero ways. So the ADR register drifted 22 rows, 33 records carried
a status a machine could not read, `architecture/distilled/README.md` still
described ADR-0074 in the shape ADR-0097 and ADR-0098 replaced, and a SKILL.md
pointed at two files that do not exist. Every one of those is a case where an
agent is not merely uninformed but *confidently wrong*, which is the only
defect class worth automating (Chroma's context-rot result, arXiv 2606.15828;
the transfer study, arXiv 2606.09090).

Nine checks, all deterministic:

  1. `adr-register`          every record has a current register row  (shared)
  2. `front-matter`          one metadata schema, well formed, on all (shared)
  3. `adr-citation`          every `ADR-NNNN` cited resolves         (shared)
  4. `digest-staleness`      a `distilled/*.md` older than a record it cites
  5. `dead-doc-link`         every relative link in a doc resolves
  6. `skill-reference`       every path a SKILL.md cites resolves
  7. `context-bloat`         >200 lines in an AGENTS.md or a SKILL.md
  8. `init-fossil`           a hand-written doc with a single commit
  9. `instruction-conflict`  one gate noun with two referents; lint leakage

Checks 1-3 are **read out of `tools/arch-index.py`**, not reimplemented: the
register row, the record schema and the dangling citation already have one
implementation, one set of findings and a gate — every lane of
`eng/verify.sh` runs `arch-index.py --check` (ADR-0141). This audit reports them
so the queue is one list, and says on each finding that the gate already fails
on it. Two implementations of "is the register current" would be two answers
and one of them would be the one nobody runs.

**Reporting-only, deliberately.** The nine land as a queue that drains. The
bead's own instruction is not to gate on all of them at once, and the cheap
exact ones (1, 2, 3) are already gates by way of arch-index. The six new
checks need judgement to act on and their first queue is not empty, so a gate
over them would be a red lane nobody could clear in the time they had. There
is no `--check` mode here on purpose; `eng/verify.sh` calls this with
`--report`, which prints the count and never fails.

What this is *not* claiming: a green `doc-report.json` is not a correctness
improvement. arXiv 2607.27250 ran 288 agent runs across two frontier agents and
found context strategy does not move correctness (<=10-15pp,
equivalence-bounded) — agents failed on implementation skill, not on missing
repository knowledge. The win being bought here is stopping confidently-wrong
answers and token cost (arXiv 2601.20404: -28.6% median runtime, -16.6% output
tokens). The queue is a list of ways a reader was confidently wrong, not a
pass-rate lever.
"""
from __future__ import annotations

import argparse
import importlib.util
import json
import re
import subprocess
import sys
from pathlib import Path

ARCH_INDEX = Path(__file__).resolve().parent / "arch-index.py"


def _load_arch_index() -> object:
    """`tools/arch-index.py` by path — the name is hyphenated, like its peers'."""
    spec = importlib.util.spec_from_file_location("arch_index", ARCH_INDEX)
    module = importlib.util.module_from_spec(spec)
    sys.modules.setdefault("arch_index", module)
    spec.loader.exec_module(module)
    return module


arch_index = _load_arch_index()

#: The two artefacts, in the shape every other audit in the quality loop writes
#: (crap4dotnet, coverage, metrics, warnings, stryker), and in the repository's
#: .gitignore beside them.
QUEUE_NAME = "doc-queue.md"
REPORT_NAME = "doc-report.json"

#: Check 7's ceiling. The context-bloat study measured the *smallest* bloated
#: file at 216 lines, so 200 is the line above which an instruction file is
#: costing more than it carries; root AGENTS.md is 141, so this gate protects a
#: property that is currently true, which is the point of a gate.
BLOAT_CEILING = 200

#: Directories no documentation check walks: vendored, generated or built trees
#: where a `.md` is somebody else's (`.agents/skills` is a copy of the published
#: skills, bootstrapped once — its debt is upstream's, and reporting it here
#: would bury this repository's own findings under ~1500 lines of Beads and
#: Playwright documentation).
SKIP_DIRECTORIES = arch_index.SKIP_DIRECTORIES | {".agents"}

#: Where the docs an agent is pointed at live. Everything walked is under one of
#: these, or is a root-level document. `docs` is here because the release
#: changelog moved there (ADR-0148): a `DOC_ROOTS` that stops naming the tree a
#: document is in stops reading that document, and the largest document in the
#: repository is the one to stop reading last.
DOC_ROOTS = ("architecture", "eng", "tools", "clients", "src", "tests", "research",
             "docs")
ROOT_DOCS = (
    "AGENTS.md", "README.md", "RELEASING.md",
)

#: Files the generators own: a single commit is a generator run, not a
#: hand-written doc that was never revised.
GENERATED_DOCS = ("arch-index.md", "architecture/decisions/README.md")

#: The skill trees check 6 walks. `.agents/skills` is vendored (see
#: SKIP_DIRECTORIES), so the skill-relative resolution question is asked of the
#: skills this repository wrote and runs.
SKILL_ROOTS = (".pi/skills",)

SKILL_FILE = re.compile(r"^SKILL\.md$")
AGENTS_FILE = re.compile(r"^AGENTS\.md$")

#: A path-shaped token in prose: backticked, or bare with a real extension. The
#: extension set is what keeps `ADR-0001` and `the foo bar` out of it.
PATH_TOKEN = re.compile(
    r"`([^`\n]+)`|(?<![\w`/.-])((?:[\w.-]+/)*[\w.-]+\.[A-Za-z][\w]{0,5})"
)
PATH_SUFFIXES = {
    ".md", ".sh", ".py", ".cs", ".csproj", ".sln", ".slnx", ".json", ".js",
    ".mjs", ".ts", ".tsx", ".csx", ".yml", ".yaml", ".props", ".editorconfig",
    ".sql", ".http", ".txt", ".xml",
}

#: Files the quality loop writes at the repository root and `.gitignore`s: a
#: SKILL.md that points at one is pointing at an artefact an audit run
#: produces, not at a file in the tree. Naming them is not a broken pointer.
GENERATED_ARTEFACTS = frozenset({
    "crap-queue.md", "crap-report.json", "coverage-queue.md",
    "coverage-report.json", "coverage-history.csv", "metrics-queue.md",
    "metrics-report.json", "warnings-queue.md", "warnings-report.json",
    "stryker-queue.md", "stryker-report.json", "doc-queue.md",
    "doc-report.json",
})

MARKDOWN_LINK = re.compile(r"\[[^\]\n]*\]\(([^)\s]+)(?:\s+\"[^\"]*\")?\)")
#: Links that are not a path into this tree.
NOT_A_PATH = re.compile(r"^(?:[a-z][a-z0-9+.-]*:|//|#|/)", re.I)

#: Check 9, part one. A line that claims what the gate *is*: the phrase plus,
#: somewhere on the line, the entry point it names. Conflicting Instructions ran
#: at 28% prevalence in the wild and a model resolves a contradiction between
#: two instructions arbitrarily rather than asking.
GATE_CLAIM = re.compile(
    r"\b(?:the|this|that|a|our)\s+gate\b|\bgate before\b|\bbefore (?:you )?done\b"
    r"|\bmust (?:run|pass)\b|\bhand[- ]?off step\b|\bmerge gate\b",
    re.I,
)
#: The entry points a doc may call the gate. Listed rather than discovered, so
#: the report names what it compared and the queue is reviewable.
ENTRY_POINTS = (
    "eng/verify.sh", "eng/quality-audit.sh", "eng/test.sh", "eng/build.sh",
    "eng/format.sh", "eng/conformance.sh", "eng/e2e-web.sh",
    "eng/workbench-e2e.sh", "eng/cli-e2e.sh", "scripts/quality-loop.py",
)
ENTRY_POINT = re.compile("|".join(re.escape(name) for name in ENTRY_POINTS))

#: Check 9, part two: Lint Leakage — a doc restating a rule a gate already fails
#: on, at 62% prevalence in the wild. This repository is structurally exposed
#: because it enforces its style walls in `tests/architecture`, so a doc that
#: repeats a wall is a second place to drift and no place to be authoritative.
#: Each entry is the prose a doc would restate, and the gate that owns it.
LINT_WALLS = (
    (
        re.compile(r"third[- ]party types?", re.I),
        "tests/architecture/Spatial.Architecture.Tests → "
        "ArchitectureGuardTests.Contracts_source_names_no_raster_or_third_party_type",
    ),
    (
        re.compile(r"contracts? (?:reference|references|carry|carries|stay|stays) "
                   r"only .{0,20}\bcore\b", re.I),
        "tests/architecture/Spatial.Architecture.Tests → "
        "ArchitectureGuardTests.Contracts_references_only_core",
    ),
    (
        re.compile(r"`?Spatial\.Core`? (?:stays|remains) structural", re.I),
        "tests/architecture/Spatial.Architecture.Tests → "
        "ArchitectureGuardTests.Core_has_no_dependencies",
    ),
)

SEVERITY_ORDER = {"high": 0, "medium": 1, "low": 2}


# --- the finding ------------------------------------------------------------


def finding(check: str, file: str, line: int, message: str, severity: str, **extra) -> dict:
    record = {
        "check": check, "file": file, "line": line,
        "message": message, "severity": severity,
    }
    record.update(extra)
    return record


# --- the shared checks ------------------------------------------------------


def shared_findings(root: Path) -> list[dict]:
    """Checks 1-3, read out of `tools/arch-index.py` (ADR-0141)."""
    corpus = arch_index.load_corpus(root)
    gate = "tools/arch-index.py --check, which every lane of eng/verify.sh runs"
    findings = []
    for message in arch_index.register_findings(root, corpus):
        findings.append(finding(
            "adr-register", _first_path(root, message), 0, message, "high",
            shared=True, gate=gate,
        ))
    for message in arch_index.corpus_findings(root, corpus):
        findings.append(finding(
            "front-matter", _first_path(root, message), 0, message, "high",
            shared=True, gate=gate,
        ))
    for message in arch_index.dangling_citations(root, corpus):
        relative, _, rest = message.partition(":")
        number = ""
        match = re.match(r"(\d+):", rest)
        if match:
            number = int(match.group(1))
        findings.append(finding(
            "adr-citation", relative, number, rest, "high",
            shared=True, gate=gate,
        ))
    return findings


def _first_path(root: Path, message: str) -> str:
    """The repository-relative path a `tools/arch-index.py` finding names."""
    head = message.split(":", 1)[0]
    if head.startswith("ADR-"):
        return "architecture/decisions/"
    return head if head.endswith(".md") else "architecture/distilled/README.md"


# --- check 4: digest staleness ---------------------------------------------


def git_history(root: Path, paths: list[Path]) -> dict[str, tuple[int | None, int]]:
    """The last commit time and the commit count of each path, in one pass.

    One `git log` per answer rather than one per file: the corpus is ~150
    documents and 150 subprocesses is a second of the audit's budget spent on
    asking git the same question. Commit time rather than filesystem mtime,
    because a checkout rewrites mtime — an mtime reading reports a fresh tree
    as stale on one host and an old one as current on another. A path with no
    history (untracked, or a synthetic tree) is `(None, 0)`, and every check
    that needs history skips it rather than guessing.
    """
    answers: dict[str, tuple[int | None, int]] = {str(path): (None, 0) for path in paths}
    # Relative to the root, because that is what a git pathspec is: an absolute
    # path is not a pathspec, and `git log -- /tmp/.../AGENTS.md` matches
    # nothing and says so by finding nothing.
    names = {str(path): _git_name(root, path) for path in paths}
    output, code, _ = _git(root, [
        "log", "--format=%x00%ct", "--name-only", "--",
    ] + [name for name in names.values()])
    if code != 0 or not output:
        return answers
    commit_time: int | None = None
    # `git log` is newest-first, so the LAST time a path appears in this stream
    # is its most recent commit: the assignment overwrites as it goes and the
    # final value is the answer.
    for raw in output.splitlines():
        if raw.startswith("\x00"):
            try:
                commit_time = int(raw[1:])
            except ValueError:  # a commit with no resolvable date
                commit_time = None
            continue
        if raw in names.values():
            for key, name in names.items():
                if name == raw:
                    answers[key] = (commit_time, answers[key][1] + 1)
    return answers


def _git_name(root: Path, path: Path) -> str:
    """The repository-relative, forward-slashed name git knows a path by."""
    try:
        return str(path.resolve().relative_to(root))
    except ValueError:  # a file outside the tree
        return str(path)


def commit_times(root: Path, paths: list[Path]) -> dict[str, int | None]:
    """The last commit time of each path."""
    return {key: value[0] for key, value in git_history(root, paths).items()}


def commit_counts(root: Path, paths: list[Path]) -> dict[str, int | None]:
    """How many commits touched each path (0 = untracked)."""
    return {key: value[1] for key, value in git_history(root, paths).items()}


def as_absolute(root: Path, table: dict | None) -> dict:
    """A test's override table, keyed however it was written, made absolute."""
    if not table:
        return {}
    return {
        (str(root / key) if not Path(key).is_absolute() else str(key)): value
        for key, value in table.items()
    }


def digest_staleness(root: Path, times: dict | None = None) -> list[dict]:
    """A `distilled/*.md` that predates a record it cites (check 4).

    The digests are the routing layer: an agent reads `distilled/core.md` and
    does not open the record behind it. A digest that predates the record it
    summarizes is a digest stating a superseded decision as a live one, which is
    the confidently-wrong case again — the record wins on conflict, and the
    reader does not know there is a conflict.
    """
    findings: list[dict] = []
    digests = sorted((root / "architecture" / "distilled").glob("*.md"))
    if not digests:
        return findings
    decisions = {path.name: path for path in (root / "architecture" / "decisions").glob("ADR-*.md")}
    needed = list(digests) + list(decisions.values())
    resolved = commit_times(root, needed)
    resolved.update(as_absolute(root, times))
    pairs: dict[tuple[str, str], list[int]] = {}
    for digest in digests:
        digest_time = resolved.get(str(digest))
        if digest_time is None:
            continue
        try:
            lines = digest.read_text(encoding="utf-8").splitlines()
        except (OSError, UnicodeDecodeError):
            continue
        for number, line in enumerate(lines, start=1):
            if _is_generated_line(digest, number):
                # The register quotes every record's summary, so a record
                # amended after the digest's last commit lands here as a
                # finding that no restating of the digest can fix. `arch-index
                # --check` owns the generated block's currency.
                continue
            for cited in sorted(set(arch_index.ADR_CITATION.findall(line))):
                record = next(
                    (path for name, path in decisions.items()
                     if name.startswith(f"ADR-{cited}-")),
                    None,
                )
                if record is None:
                    continue  # check 3's finding, not this one
                record_time = resolved.get(str(record))
                if record_time is None or record_time <= digest_time:
                    continue
                # One finding per (digest, record), not per line: a digest that
                # cites a record on 67 lines has one thing to re-read, and 67
                # rows of it would bury the seven that are real.
                pairs.setdefault((str(digest.relative_to(root)), cited), []).append(
                    number
                )

    for (relative, cited), lines in sorted(pairs.items()):
        repeats = (
            f" (and {len(lines) - 1} more line(s) citing it)" if len(lines) > 1 else ""
        )
        record = next(
            (name for name in decisions if name.startswith(f"ADR-{cited}-")), ""
        )
        findings.append(finding(
            "digest-staleness", relative, lines[0],
            f"this digest's last commit predates ADR-{cited} ({record}), so it "
            f"states the decision as it stood before that record changed"
            f"{repeats}; re-read the record and restate the digest",
            "medium",
        ))
    return findings


# --- check 5: dead relative links between docs ------------------------------


def iter_doc_files(root: Path) -> list[Path]:
    """Every documentation file this repository wrote, in a stable order."""
    paths: set[Path] = set()
    for name in ROOT_DOCS:
        candidate = root / name
        if candidate.is_file():
            paths.add(candidate)
    for name in DOC_ROOTS:
        base = root / name
        if not base.is_dir():
            continue
        for path in base.rglob("*.md"):
            if any(part in SKIP_DIRECTORIES for part in path.parts):
                continue
            paths.add(path)
    for tree in SKILL_ROOTS:
        base = root / tree
        if not base.is_dir():
            continue
        for path in base.rglob("*.md"):
            if any(part in SKIP_DIRECTORIES for part in path.parts):
                continue
            paths.add(path)
    return sorted(paths)


def dead_doc_links(root: Path) -> list[dict]:
    """A relative link in a doc that resolves to nothing (check 5)."""
    findings: list[dict] = []
    for path in iter_doc_files(root):
        try:
            text = path.read_text(encoding="utf-8")
        except (OSError, UnicodeDecodeError):
            continue
        for number, line in enumerate(text.splitlines(), start=1):
            for target in MARKDOWN_LINK.findall(line):
                if NOT_A_PATH.match(target) or "*" in target:
                    continue
                resolved = (path.parent / target.split("#", 1)[0]).resolve()
                if not resolved.exists():
                    findings.append(finding(
                        "dead-doc-link", str(path.relative_to(root)), number,
                        f"`{target}` resolves to nothing: a pointer that does "
                        f"not resolve is a reader sent to a place that is not there",
                        "medium",
                    ))
    return findings


# --- check 6: skill-relative reference resolution --------------------------

#: A cited path in a skill resolves against the skill's own directory FIRST and
#: the repository root second — the rule a reader follows without knowing it.
#: The two orders are not the same, and the difference is the finding: a bare
#: `plugins.md` in `.pi/skills/spatial-engine/SKILL.md` is
#: `.pi/skills/spatial-engine/plugins.md`, which is not there, and it is not
#: `plugins.md` at the root either.
def skill_references(root: Path) -> list[dict]:
    findings: list[dict] = []
    for tree in SKILL_ROOTS:
        base = root / tree
        if not base.is_dir():
            continue
        for path in sorted(base.rglob("SKILL.md")):
            if any(part in SKIP_DIRECTORIES for part in path.parts):
                continue
            try:
                lines = path.read_text(encoding="utf-8").splitlines()
            except (OSError, UnicodeDecodeError):
                continue
            for number, line in enumerate(lines, start=1):
                unresolvable: list[str] = []
                for candidate in _path_tokens(line):
                    if _resolves(path, root, candidate):
                        continue
                    if "/" not in candidate and not _exists_by_name(root, candidate):
                        # A bare word with no file of that name anywhere is a
                        # concept ("a spatial.json project file"), not a
                        # pointer. Only a bare name that a file in the tree
                        # answers to is a pointer, and that is the case worth
                        # reporting: the reader is one word away from right and
                        # the word is missing.
                        continue
                    unresolvable.append(candidate)
                if unresolvable:
                    findings.append(finding(
                        "skill-reference", str(path.relative_to(root)), number,
                        "resolves against the skill directory or the repository "
                        f"root, not in either: {', '.join(unresolvable)} — "
                        "skills are the most heavily loaded context in this "
                        "repository, so an unresolvable pointer here is the "
                        "most expensive instance of this defect class",
                        "high",
                    ))
    return findings


def _exists_by_name(root: Path, name: str) -> bool:
    """Whether a file of that name exists anywhere in the tree."""
    for _ in root.rglob(name):
        return True
    return False


def _path_tokens(line: str) -> list[str]:
    """The path-shaped tokens of one line, from backticks or bare, deduped."""
    tokens: list[str] = []
    for match in PATH_TOKEN.finditer(line):
        candidate = (match.group(1) or match.group(2) or "").strip()
        if not candidate or any(char in candidate for char in "*?<>|"):
            continue
        if candidate.endswith("/") or " " in candidate:
            continue
        # A comma-separated run inside one backtick span: "core.md, plugins.md".
        for piece in re.split(r"[,;]\s*", candidate):
            piece = piece.strip().strip("`'\"")
            if piece.lower().endswith(tuple(PATH_SUFFIXES)):
                if Path(piece).name in GENERATED_ARTEFACTS:
                    continue
                if piece not in tokens:
                    tokens.append(piece)
    return tokens


def _resolves(path: Path, root: Path, candidate: str) -> bool:
    """Skill directory first, repository root second — the order a reader uses."""
    for base in (path.parent, root):
        try:
            if (base / candidate).exists():
                return True
        except (OSError, ValueError):
            continue
    return False


# --- check 7: context bloat -------------------------------------------------


def context_bloat(root: Path) -> list[dict]:
    """An instruction file past the ceiling (check 7).

    One line of code, and it is the smell that gates the other five: a file
    nobody finishes reading is a file whose rules are not applied at all, and
    the answer is then to add another file rather than to prune.
    """
    findings: list[dict] = []
    for path in _instruction_files(root):
        try:
            count = len(path.read_text(encoding="utf-8").splitlines())
        except (OSError, UnicodeDecodeError):
            continue
        if count > BLOAT_CEILING:
            findings.append(finding(
                "context-bloat", str(path.relative_to(root)), 0,
                f"{count} lines, over the {BLOAT_CEILING}-line ceiling for an "
                "instruction file; prune it, because a file nobody finishes "
                "reading is a file whose rules are not applied",
                "medium",
            ))
    return findings


def _instruction_files(root: Path) -> list[Path]:
    paths: set[Path] = set()
    for path in root.rglob("AGENTS.md"):
        if any(part in SKIP_DIRECTORIES for part in path.parts):
            continue
        paths.add(path)
    for tree in SKILL_ROOTS:
        base = root / tree
        if base.is_dir():
            for path in base.rglob("SKILL.md"):
                if any(part in SKIP_DIRECTORIES for part in path.parts):
                    continue
                paths.add(path)
    return sorted(paths)


# --- check 8: init fossilization --------------------------------------------


def init_fossil(root: Path, counts: dict | None = None) -> list[dict]:
    """A hand-written doc with a single commit: written by `/init`, never revised.

    Deterministic through `git log`. The point is the *new* nested AGENTS.md a
    generation bead adds once and never touches again: nothing in it is wrong on
    the day it lands, and a year later it is a set of rules that were true once.
    Generated files are excluded — one commit is what a generator looks like.
    """
    candidates = [
        path for path in iter_doc_files(root)
        if path.name in ROOT_DOCS or AGENTS_FILE.match(path.name)
        or SKILL_FILE.match(path.name)
    ]
    excluded = {str(path.relative_to(root)) for path in
                (root / name for name in GENERATED_DOCS)}
    candidates = [path for path in candidates if str(path.relative_to(root)) not in excluded]
    if not candidates:
        return []
    resolved = commit_counts(root, candidates)
    resolved.update(as_absolute(root, counts))
    findings: list[dict] = []
    for path in candidates:
        count = resolved.get(str(path))
        if count != 1:
            # Zero commits is a file the working tree has not committed yet,
            # which is a transient, not a doc that was never revised.
            continue
        findings.append(finding(
            "init-fossil", str(path.relative_to(root)), 0,
            "one commit: written by /init (or copied) and never revised since",
            "low",
        ))
    return findings


# --- check 9: conflicting instructions, lint leakage ------------------------


def instruction_conflict(root: Path) -> list[dict]:
    """One gate noun with two referents (check 9), and lint leakage.

    REPORTING ONLY, never a gate: both need judgement, and this repository's
    judgement lives in the ADRs. Conflicting Instructions ran at 28%
    prevalence in the wild, and a model handed two contradicting instructions
    resolves between them arbitrarily rather than asking which is current.

    The rule is a comparison, not a judgement: collect every line that calls
    something the gate and names an entry point, and report the line when the
    corpus does not agree on one answer. Two shapes of disagreement, both
    deterministic — the same entry point named with different commands (one doc
    says the gate is `eng/verify.sh --full`, another says `eng/verify.sh`), or
    two entry points both called the gate.
    """
    findings: list[dict] = []
    claims: dict[tuple[str, str], list[tuple[str, int, str]]] = {}
    for path in agent_docs(root):
        try:
            lines = path.read_text(encoding="utf-8").splitlines()
        except (OSError, UnicodeDecodeError):
            continue
        relative = str(path.relative_to(root))
        for number, line in enumerate(lines, start=1):
            if _is_generated_line(path, line):
                continue
            if not GATE_CLAIM.search(line):
                continue
            for name in set(ENTRY_POINT.findall(line)):
                claims.setdefault((name, _command_of(line, name)), []).append(
                    (relative, number, line.strip())
                )
    entries = sorted(claims)
    for (name, command), sites in sorted(claims.items()):
        # What else the corpus calls the gate, and does not agree with this.
        others = [
            (other, other_command) for (other, other_command) in entries
            if (other, other_command) != (name, command)
            and (other == name or len({key[0] for key in entries}) > 1)
        ]
        if not others:
            continue
        elsewhere = ", ".join(
            f"{other} {other_command}".strip() for other, other_command in others
        )
        for relative, number, text in sites:
            findings.append(finding(
                "instruction-conflict", relative, number,
                f"calls {name} {command or '(with no command)'} the gate, and "
                f"the corpus also calls {elsewhere} the gate: "
                f"\"{text[:120]}\" — reporting only; fix the prose, do not "
                "gate the fix",
                "low",
                reportingOnly=True,
            ))

    for path in agent_docs(root):
        try:
            lines = path.read_text(encoding="utf-8").splitlines()
        except (OSError, UnicodeDecodeError):
            continue
        relative = str(path.relative_to(root))
        for number, line in enumerate(lines, start=1):
            if _is_generated_line(path, line):
                continue
            for pattern, gate in LINT_WALLS:
                if not pattern.search(line):
                    continue
                findings.append(finding(
                    "lint-leakage", relative, number,
                    f"restates a wall a gate already fails on ({gate}): "
                    f"\"{line.strip()[:120]}\" — reporting only; the gate is "
                    "the authority, the doc is the second place to drift",
                    "low",
                    reportingOnly=True,
                ))
    return findings


def agent_docs(root: Path) -> list[Path]:
    """The docs an agent is routed by, minus the decision records.

    `architecture/decisions/*.md` is the corpus of decisions: a record that
    states a wall is *quoting* the decision behind the wall, not keeping a
    second copy of a gate, so check 9 does not read it. Everything a reader is
    actually pointed at — the digests, README.md, the skills, AGENTS.md — is.
    """
    return [
        path for path in iter_doc_files(root)
        if "decisions" not in Path(path).parts
    ]


#: Which lines of which file a generator wrote, cached per path: the register
#: block in `architecture/distilled/README.md` (arch-index) and the two files
#: arch-index regenerates wholesale. A generated line restating a gate is the
#: generator's output, and the fix for that is the record, not the row.
_GENERATED_LINES: dict[str, frozenset[int]] = {}


def _is_generated_line(path: Path, number: int) -> bool:
    """Whether a line sits inside a generated block, and so is not authored."""
    key = str(path)
    if key not in _GENERATED_LINES:
        _GENERATED_LINES[key] = frozenset(_generated_lines(path))
    return number in _GENERATED_LINES[key]


def _generated_lines(path: Path) -> set[int]:
    relative = path.parts
    if path.name in GENERATED_DOCS:
        return set(range(1, _line_count(path) + 1))
    if path.name != "README.md" or "distilled" not in relative:
        return set()
    try:
        lines = path.read_text(encoding="utf-8").splitlines()
    except (OSError, UnicodeDecodeError):
        return set()
    generated: set[int] = set()
    inside = False
    for number, line in enumerate(lines, start=1):
        if arch_index.REGISTER_BEGIN in line:
            inside = True
        if inside:
            generated.add(number)
        if arch_index.REGISTER_END in line:
            inside = False
    return generated


def _line_count(path: Path) -> int:
    try:
        return len(path.read_text(encoding="utf-8").splitlines())
    except (OSError, UnicodeDecodeError):
        return 0


def _command_of(line: str, name: str) -> str:
    """The flags a doc names with an entry point: `eng/verify.sh --full`."""
    match = re.search(re.escape(name) + r"(?:\s+(--[\w-]+(?:=\S+)?))?", line)
    return match.group(1) if match and match.group(1) else ""


# --- the audit --------------------------------------------------------------

CHECKS = (
    ("adr-register", "every decision record has a current register row", "high", True),
    ("front-matter", "one metadata schema, well formed, on every record", "high", True),
    ("adr-citation", "every `ADR-NNNN` cited resolves to a record", "high", True),
    ("digest-staleness", "a distilled digest is not older than a record it cites", "medium", False),
    ("dead-doc-link", "every relative link between docs resolves", "medium", False),
    ("skill-reference", "every path a SKILL.md cites resolves", "high", False),
    ("context-bloat", f"an instruction file is at most {BLOAT_CEILING} lines", "medium", False),
    ("init-fossil", "a hand-written doc has been revised", "low", False),
    ("instruction-conflict", "one gate noun has one referent", "low", False),
    ("lint-leakage", "a doc does not restate what a gate already fails on", "low", False),
)

NOTES = (
    "Reporting-only: no lane fails on this report. The cheap exact checks "
    "(adr-register, front-matter, adr-citation) are read from "
    "tools/arch-index.py and are already gates — `python3 tools/arch-index.py "
    "--check` runs in every lane of eng/verify.sh (ADR-0141).",
    "instruction-conflict and lint-leakage are reporting-only by construction, "
    "never a gate: both need judgement and this repository's judgement lives in "
    "the ADRs.",
    "`.agents/skills` is a vendored copy of the published skills, so it is out "
    "of scope for every check: its debt is upstream's, and reporting it would "
    "bury this repository's own findings under ~1500 lines of documentation "
    "nobody here wrote.",
    "A green report is not a correctness improvement. Context strategy does not "
    "move pass rates (arXiv 2607.27250: <=10-15pp, equivalence-bounded); the "
    "win bought here is stopping confidently-wrong answers and token cost "
    "(arXiv 2601.20404: -28.6% median runtime, -16.6% output tokens).",
)


def audit(root: Path, commit_times=None, commit_counts=None) -> dict:
    """Read the corpus, and return the report the queue and the JSON are made of.

    `commit_times` and `commit_counts` are the injection points for the tests:
    the git read is one subprocess per file, and a synthetic tree has no
    history to read.
    """
    root = Path(root).resolve()
    findings: list[dict] = []
    findings.extend(shared_findings(root))
    findings.extend(digest_staleness(root, commit_times))
    findings.extend(dead_doc_links(root))
    findings.extend(skill_references(root))
    findings.extend(context_bloat(root))
    findings.extend(init_fossil(root, commit_counts))
    findings.extend(instruction_conflict(root))

    findings.sort(key=lambda item: (
        SEVERITY_ORDER.get(item["severity"], 9), item["check"], item["file"],
        -item["line"],
    ))
    checks = []
    for identifier, title, severity, shared in CHECKS:
        mine = [item for item in findings if item["check"] == identifier]
        checks.append({
            "id": identifier,
            "title": title,
            "severity": severity,
            "shared": shared,
            "sharedWith": "tools/arch-index.py" if shared else "",
            "count": len(mine),
        })
    return {
        "audit": "doc-freshness",
        "gate": False,
        "root": str(root),
        "checks": checks,
        "findings": findings,
        "stats": {
            "findings": len(findings),
            "high": sum(1 for item in findings if item["severity"] == "high"),
            "medium": sum(1 for item in findings if item["severity"] == "medium"),
            "low": sum(1 for item in findings if item["severity"] == "low"),
            "docs": len(iter_doc_files(root)),
            "adrs": len(arch_index.load_corpus(root)),
        },
        "notes": list(NOTES),
        "warnings": [],
    }


# --- the artefacts ----------------------------------------------------------


def render_queue(report: dict) -> str:
    """`doc-queue.md`: every finding, worst first, with the file and the line."""
    lines = [
        "# Documentation-freshness queue",
        "",
        "<!-- `doc-queue.md`, generated by `python3 tools/doc-freshness.py`,",
        "     whose machine form is `doc-report.json`. Regenerated on every run;",
        "     do not hand-edit. Reporting-only — nothing fails on this file. -->",
        "",
        f"**{report['stats']['findings']} finding(s)** over "
        f"{report['stats']['docs']} documentation file(s) and "
        f"{report['stats']['adrs']} decision record(s): "
        f"{report['stats']['high']} high, {report['stats']['medium']} medium, "
        f"{report['stats']['low']} low.",
        "",
        "| check | severity | finding |",
        "| --- | --- | --- |",
    ]
    for item in report["findings"]:
        where = item["file"] + (f":{item['line']}" if item["line"] else "")
        lines.append(
            f"| `{item['check']}` | {item['severity']} | "
            f"**{where}** — {item['message']} |"
        )
    if not report["findings"]:
        lines.append("| — | — | nothing: the corpus audits clean |")
    lines += ["", "## By check", "", "| check | what it asks | findings |", "| --- | --- | --- |"]
    for check in report["checks"]:
        lines.append(
            f"| `{check['id']}` | {check['title']}"
            + (f" (shared with {check['sharedWith']})" if check["shared"] else "")
            + f" | {check['count']} |"
        )
    lines += ["", "## What this is not", ""]
    lines += [f"- {note}" for note in report["notes"]]
    lines.append("")
    return "\n".join(lines)


def write(root: Path, report: dict) -> None:
    (root / REPORT_NAME).write_text(
        json.dumps(report, indent=2) + "\n", encoding="utf-8"
    )
    (root / QUEUE_NAME).write_text(render_queue(report), encoding="utf-8")


# --- the plumbing -----------------------------------------------------------


def _git(root: Path, arguments: list[str]) -> tuple[str, int, str]:
    try:
        completed = subprocess.run(
            ["git", *arguments], cwd=root, capture_output=True, text=True,
            timeout=60, check=False,
        )
    except (OSError, subprocess.SubprocessError):
        return "", 1, "git is not available"
    return completed.stdout, completed.returncode, completed.stderr


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--root", default=".", help="repository root (default: .)")
    parser.add_argument(
        "--report", action="store_true",
        help="accepted and ignored: this audit never fails. It is here so a "
        "caller asking for a verdict gets a printed answer rather than a "
        "usage error, and so nobody wires it up as a gate by accident",
    )
    parser.add_argument(
        "--quiet", action="store_true", help="print the summary line only"
    )
    arguments = parser.parse_args(argv)
    root = Path(arguments.root).resolve()

    report = audit(root)
    write(root, report)

    stats = report["stats"]
    if arguments.quiet:
        print(
            f"doc-freshness: {stats['findings']} finding(s) "
            f"({stats['high']} high, {stats['medium']} medium, {stats['low']} low) "
            f"→ {QUEUE_NAME}, {REPORT_NAME}"
        )
    else:
        print(
            f"doc-freshness: {stats['findings']} finding(s) over "
            f"{stats['docs']} doc file(s) and {stats['adrs']} record(s) — "
            f"reporting-only, nothing fails on this"
        )
        for check in report["checks"]:
            if not check["count"]:
                continue
            shared = f" [shared with {check['sharedWith']}]" if check["shared"] else ""
            print(f"  {check['count']:>3}  {check['id']}{shared}")
        print(f"  queue: {QUEUE_NAME}   report: {REPORT_NAME}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
