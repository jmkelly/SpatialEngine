#!/usr/bin/env python3
"""Generate the ADR register and the ADR index, and gate both (ADR-0141).

    python3 tools/arch-index.py --write     regenerate the generated files
    python3 tools/arch-index.py --check     fail if anything is stale (the gate)
    python3 tools/arch-index.py --citations dangling ADR-NNNN citations only
    python3 tools/arch-index.py --root DIR  read a different tree (tests)

Three generated artefacts, from one read of `architecture/decisions/*.md`:

*   the **register table** inside `architecture/distilled/README.md`, between
    the `arch-index:register` markers. It used to be hand-edited, and it was
    missing 33 of the 126 records on disk — including ADR-0097 and ADR-0098,
    the two that rewrote the feature-query contract ADR-0074's row still
    described in its original shape;
*   **`architecture/decisions/README.md`**, one row per ADR: title, status,
    date, and what it supersedes, is superseded by, amends, is amended by and
    relates to. The one-line answer to "is this decision still live?" used to
    be a grep through prose, and a third of the corpus would not answer it;
*   **`arch-index.md`**, a ten-line breadcrumb at the repository root so
    routing is pointer-first: AGENTS.md → distilled/README.md →
    decisions/README.md.

One metadata schema, which is the input to all three: YAML front matter with
`status`, `date`, `deciders` and `summary` required, and `supersedes`,
`superseded-by`, `amends`, `amended-by`, `related` and `consulted` optional.
The retired schema — a `Status:` line (or a `- **Status:**` bullet) after the
H1 — is rejected by name, because a record that states its status two ways
states it wrongly in one of them.

It also carries the **shape rule** (ADR-0150): the section set of a record is
closed, the three sections that state a decision are required and in order, and
a record's narrative — every section except the evidence it carries — is inside
a word budget. A length gate alone would be a bad proxy, because the long
records are long for load-bearing reasons (ADR-0105, ADR-0134), so the budget is
measured over Context + Decision + Consequences + Alternatives + Not decided and
`References` and `Measurements` are exempt; a record over the budget that has
one decision to say declares `over-budget:` in its front matter, and a
declaration nobody needed is rejected rather than accepted and ignored.

The rule applies from the number of the record that decided it
(`SHAPE_FROM`), so it is not a 135-record diff: a corpus written under no rule
is grandfathered rather than rewritten, which is what makes it adoptable
(SpatialEngine-6l6).

`--check` is the gate `eng/verify.sh` runs on every lane, and it is what fails
on a deleted or renamed ADR, a register row that no longer matches its file, a
record in the retired schema, a record outside the shape rule, and a citation
of an ADR that does not exist.
`--citations` is the same citation read on its own, because it is the cheapest
check in the repository and the documentation-freshness audit wants it without
the rest (SpatialEngine-imz.1).

`corpus_findings`, `register_findings` and `dangling_citations` are split out
as the shared entry points, because `tools/doc-freshness.py` answers the same
three questions from here rather than a second time its own way
(SpatialEngine-imz.2): a register row, a record schema and a citation are one
implementation, one set of findings and one gate each, and the audit reports
them so the queue is one list.

The citation rule is also enforced, in C# and over more file types, by
`AdrNumberingTests.Every_adr_reference_resolves_to_exactly_one_record` in the
structural guard. The two agree by construction: a number is citable when a
record carries it, and the burned numbers are written into the generated index
as **bare** numbers so neither read trips over the one line that names numbers
without records.
"""
from __future__ import annotations

import argparse
import re
import sys
from dataclasses import dataclass, field
from pathlib import Path

#: ADR numbers that will never carry a record: they were taken and given back,
#: or burned by a branch whose work never landed. They are recorded in the
#: generated index as burned rather than left looking like records that went
#: missing, and a file appearing at one of them is an error — the number is
#: spent. Every *other* gap is not this list's business: `tools/adr-next-number.py
#: --list` shows numbers held by open reservations on parallel branches, and
#: those are in flight rather than burned.
BURNED_NUMBERS = (
    "0093", "0094", "0095", "0096", "0099", "0102", "0103", "0104",
)

#: The front matter keys, in the order the index renders them. The first four
#: are required; the rest are the cross-references that make "which ADRs bind
#: this project" a lookup rather than a search.
LIST_FIELDS = (
    "supersedes", "superseded-by", "amends", "amended-by", "related", "consulted",
)
REQUIRED_FIELDS = ("status", "date", "deciders", "summary")
#: `over-budget` is not a list: it is a sentence saying why one decision does not
#: fit the budget, and it is honoured only on a record that is actually over it
#: (ADR-0150). A field nobody can act on is a parameter accepted and ignored.
OVER_BUDGET_FIELD = "over-budget"
FIELDS = REQUIRED_FIELDS + LIST_FIELDS + (OVER_BUDGET_FIELD,)

#: The number of the record that decided the shape rule. Records below it were
#: written under no rule and are not judged by it — a policy that arrives as a
#: 135-file diff will not be applied, so the rule binds what is written next
#: (ADR-0150, SpatialEngine-6l6).
SHAPE_FROM = "0150"

#: The sections a record must carry, in the order a reader takes them.
REQUIRED_SECTIONS = ("Context", "Decision", "Consequences")

#: The sections a record may carry beyond those. The set is closed: a heading
#: outside it is retired by name rather than tolerated, because "Alternatives in
#: 48 of 122" and "Rejected in 5" and "§applied in 1" is a vocabulary nobody
#: chose (ADR-0150).
ALLOWED_SECTIONS = REQUIRED_SECTIONS + (
    "Alternatives", "Not decided", "References", "Measurements",
)

#: Sections that carry evidence rather than the decision, and so are not
#: counted against the narrative budget. A record is long because it measured
#: something, and the measurement may be load-bearing (ADR-0105, ADR-0134).
EVIDENCE_SECTIONS = ("References", "Measurements")

#: The narrative budget, in words, over the sections that are not evidence.
#: Measured over the corpus as it stood: median 826, p75 1342, p90 1724 — so
#: this binds the tail rather than the middle, which is the intent.
NARRATIVE_BUDGET = 1500

SECTION_HEADING = re.compile(r"^##\s+(.+?)\s*$", re.M)

#: The prose this generator is retiring, matched by name rather than tolerated.
RETIRED_STATUS_PATTERNS = (
    re.compile(r"^Status:", re.M),
    re.compile(r"^-\s+\*\*Status:\*\*", re.M),
    re.compile(r"^status:\s", re.M),
)

ADR_FILE = re.compile(r"^ADR-(\d{4})-(.+)\.md$")
ADR_TITLE = re.compile(r"^#\s+ADR-(\d{4}):\s*(.+?)\s*$", re.M)
ADR_CITATION = re.compile(r"ADR-(\d{4})")

REGISTER_BEGIN = "<!-- arch-index:register:begin -->"
REGISTER_END = "<!-- arch-index:register:end -->"

#: Directories the citation read does not walk: vendored and generated trees
#: where an "ADR-0001" is somebody else's, or a copy of ours.
SKIP_DIRECTORIES = {
    ".git", "node_modules", "bin", "obj", "dist", "build", "__pycache__",
    ".verify-test-results", "coverage", "TestResults",
}

#: The extension set the citation read covers: prose an agent reads and code an
#: agent greps. A citation in a comment is the common case.
CITED_SUFFIXES = (".md", ".cs")

#: The loop's own artefacts, by name, wherever they sit. Every audit in the
#: quality loop writes a `*-queue.md` / `*-report.json` pair at the repository
#: root, both gitignored (SpatialEngine-imz.2). A queue is machine-generated and
#: rewritten on every run, and a queue row quotes the line it is about — so the
#: citation read over one reports a citation the author never wrote, and whether
#: it does is a function of whether an untracked file happens to be on disk. The
#: skip is the same argument as `SKIP_DIRECTORIES`: not a citation surface.
SKIP_ARTIFACTS = frozenset({
    "crap-queue.md", "crap-report.json",
    "coverage-queue.md", "coverage-report.json",
    "metrics-queue.md", "metrics-report.json",
    "warnings-queue.md", "warnings-report.json",
    "stryker-queue.md", "stryker-report.json",
    "doc-queue.md", "doc-report.json",
})


@dataclass
class Adr:
    """One decision record, as read from disk."""

    number: str
    path: Path
    title: str = ""
    fields: dict = field(default_factory=dict)
    error: str = ""

    def get(self, key: str) -> str:
        return self.fields.get(key, "")

    @property
    def superseded_by(self) -> str:
        """The number this record is superseded by, from the status or the field."""
        if self.get("superseded-by"):
            return self.get("superseded-by")
        match = re.match(r"superseded by (ADR-\d{4})", self.get("status"), re.I)
        return match.group(1) if match else ""

    @property
    def is_superseded(self) -> bool:
        return bool(self.superseded_by) or self.get("status").lower().startswith(
            "superseded"
        )


def parse_front_matter(text: str) -> tuple[dict, str]:
    """The `key: value` pairs of a leading `---` block, and an error string."""
    lines = text.splitlines()
    if not lines or lines[0].strip() != "---":
        return {}, "no YAML front matter (the file starts with the H1)"
    try:
        end = next(
            index for index, line in enumerate(lines[1:], start=1) if line.strip() == "---"
        )
    except StopIteration:
        return {}, "unterminated YAML front matter"
    fields: dict[str, str] = {}
    for line in lines[1:end]:
        if not line.strip():
            continue
        if line.startswith((" ", "\t", "-")):
            return {}, f"front matter is not one `key: value` per line: {line.strip()!r}"
        key, separator, value = line.partition(":")
        key = key.strip()
        if not separator:
            return {}, f"front matter line is not `key: value`: {line.strip()!r}"
        if key in fields:
            return {}, f"front matter repeats {key!r}"
        fields[key] = value.strip()
    return fields, ""


def parse_adr(path: Path) -> Adr:
    """Read one record, and say what is wrong with it rather than guessing."""
    match = ADR_FILE.match(path.name)
    number = match.group(1) if match else ""
    record = Adr(number=number, path=path)
    try:
        text = path.read_text(encoding="utf-8")
    except (OSError, UnicodeDecodeError) as error:  # pragma: no cover - unreadable file
        record.error = f"unreadable: {error}"
        return record

    title = ADR_TITLE.search(text)
    if not title:
        record.error = "no `# ADR-NNNN: title` H1"
        return record
    if title.group(1) != number:
        record.error = (
            f"the H1 says ADR-{title.group(1)} but the file is ADR-{number}"
        )
        return record
    record.title = title.group(2)

    fields, error = parse_front_matter(text)
    if error:
        record.error = error
        return record
    for key in fields:
        if key not in FIELDS:
            record.error = f"unknown front matter field {key!r}"
            return record
    missing = [key for key in REQUIRED_FIELDS if not fields.get(key)]
    if missing:
        record.error = "front matter is missing " + ", ".join(missing)
        return record
    if OVER_BUDGET_FIELD in fields and not fields[OVER_BUDGET_FIELD].strip():
        record.error = (
            f"`{OVER_BUDGET_FIELD}:` is present and empty; it is a reason, so a "
            "blank one is a field that says nothing — remove it, or say why"
        )
        return record
    if not re.match(r"^\d{4}-\d{2}-\d{2}$", fields["date"]):
        record.error = f"date {fields['date']!r} is not YYYY-MM-DD"
        return record
    body = text.split("---", 2)[-1]
    for pattern in RETIRED_STATUS_PATTERNS[:2]:
        if pattern.search(body):
            record.error = (
                "status is a prose line after the H1; the one schema is front "
                "matter (`status:`)"
            )
            return record
    record.fields = fields
    return record


def load_corpus(root: Path) -> list[Adr]:
    """Every record on disk, in number order, whether or not it is well formed."""
    decisions = root / "architecture" / "decisions"
    records = [parse_adr(path) for path in decisions.glob("ADR-*.md")]
    records.sort(key=lambda record: record.number)
    return records


# --- the generated register -------------------------------------------------


def register_row(record: Adr) -> str:
    """One register row: the number, the one-line decision, its standing."""
    if record.is_superseded:
        return (
            f"| {record.number} | ~~{record.get('summary')}~~ — superseded by "
            f"{record.superseded_by[-4:]}. |"
        )
    return f"| {record.number} | {record.get('summary')} |"


def register_rows(corpus: list[Adr]) -> dict[str, str]:
    return {record.number: register_row(record) for record in corpus}


def render_register(corpus: list[Adr]) -> str:
    """The generated block, markers included, as it sits in the distilled digest."""
    lines = [
        REGISTER_BEGIN,
        "",
        "<!-- Generated by `python3 tools/arch-index.py --write` from",
        "     architecture/decisions/*.md. Do not hand-edit: `eng/verify.sh`",
        "     fails if this block differs from what the records say. -->",
        "",
        "| ADR | Decision in one line |",
        "| --- | --- |",
    ]
    for record in corpus:
        lines.append(register_row(record))
    lines += [
        "",
        f"{len(corpus)} records on disk. The full index — status, date and every",
        "cross-reference — is `architecture/decisions/README.md`.",
        "",
        REGISTER_END,
    ]
    return "\n".join(lines)


def splice_register(existing: str, block: str) -> str:
    """Replace the generated block in the digest, markers included."""
    start = existing.find(REGISTER_BEGIN)
    end = existing.find(REGISTER_END)
    if start == -1 or end == -1:
        raise ValueError(
            f"architecture/distilled/README.md has no {REGISTER_BEGIN} .. "
            f"{REGISTER_END} block; the register is generated between those "
            "markers and the markers are added once, by hand"
        )
    return existing[:start] + block + existing[end + len(REGISTER_END):]


# --- the generated index ----------------------------------------------------


def _list(value: str) -> str:
    return value or "—"


def _crosses(record: Adr) -> str:
    return _list(
        ", ".join(
            part
            for part in (
                record.get("supersedes"),
                record.superseded_by,
                record.get("amends"),
                record.get("amended-by"),
                record.get("related"),
            )
            if part
        )
    )


def render_index(corpus: list[Adr]) -> str:
    """`architecture/decisions/README.md` in full."""
    burned = ", ".join(BURNED_NUMBERS)
    superseders: dict[str, list[str]] = {}
    amenders: dict[str, list[str]] = {}
    for record in corpus:
        superseder = record.superseded_by
        if superseder:
            superseders.setdefault(superseder, []).append(record.number)
        for number in re.findall(r"\d{4}", record.get("amends")):
            amenders.setdefault(number, []).append(record.number)

    lines = [
        "# Architecture decision records",
        "",
        "<!-- Generated by `python3 tools/arch-index.py --write` from the",
        "     ADR-NNNN-*.md files beside this file. Do not hand-edit:",
        "     `eng/verify.sh` fails if this file differs from what the records",
        "     say. To record a decision, write the ADR (reserve the number with",
        "     `tools/adr-next-number.py --reserve` first) and re-run",
        "     the generator. -->",
        "",
        "The digests in `architecture/distilled/` beat nothing here: an ADR is",
        "the decision, and this file is the lookup for which one is live.",
        "",
        f"**{len(corpus)} records on disk.** `status` is the record's own word;",
        "`supersedes`/`superseded by`/`amends`/`amended by`/`related` are its",
        "front matter, so a record's reach is a row rather than a search.",
        "",
        "| ADR | Title | Status | Date | Cross-references |",
        "| --- | --- | --- | --- | --- |",
    ]
    for record in corpus:
        lines.append(
            f"| [{record.number}]({record.path.name}) | {record.title} "
            f"| {record.get('status')} | {record.get('date')} | {_crosses(record)} |"
        )
    lines += [
        "",
        "## Amended by",
        "",
        "The refiners of each record, **derived from their own `amends:` fields**",
        "rather than maintained by hand. A record that refines another says so",
        "once, in its own front matter, and this list is the other end of it — so",
        "a family over one topic is a row here rather than a reading order the",
        "reader has to assemble across eleven files. A record nobody amends does",
        "not appear.",
        "",
    ]
    if amenders:
        for target in sorted(amenders):
            lines.append(
                f"- **{target}** is amended by " + ", ".join(sorted(amenders[target]))
            )
    else:
        lines.append("- none")
    lines += [
        "",
        "## Superseded, by superseder",
        "",
        "The groups the digests used to keep by hand. A record is superseded when",
        "its own `status` says so, so this list cannot drift from the records.",
        "",
    ]
    if superseders:
        for superseder in sorted(superseders):
            lines.append(
                f"- **{superseder}** supersedes "
                + ", ".join(superseders[superseder])
            )
    else:
        lines.append("- none")
    lines += [
        "",
        "## Burned numbers",
        "",
        f"{burned} are spent: a number was taken and given back, or burned by work",
        "that never landed. They are written here as bare numbers, not as",
        "`ADR-NNNN`, because a citation of a record that does not exist is a",
        "dangling citation and this line is the one place that is allowed to",
        "name a number with no record. They are recorded so a reader looking for",
        "a number finds that it was retired rather than concluding the record",
        "was lost. Every other gap below the highest record is a number held by",
        "an open reservation (`python3 tools/adr-next-number.py --list`) rather",
        "than a missing decision, and a file appearing at a burned number is an",
        "error.",
        "",
    ]
    return "\n".join(lines)


# --- the generated breadcrumb ----------------------------------------------


def render_breadcrumb(corpus: list[Adr]) -> str:
    """`arch-index.md` at the repository root: routing, pointer-first."""
    return "\n".join(
        [
            "# arch-index",
            "",
            "<!-- Generated by `python3 tools/arch-index.py --write`. -->",
            "",
            f"{len(corpus)} decision records live in `architecture/decisions/`.",
            "Route by pointer, in this order:",
            "",
            "1. `AGENTS.md` — the hard walls, the commands, the queue protocol.",
            "2. `architecture/distilled/README.md` — route by task, then the",
            "   generated register (one line per record).",
            "3. `architecture/decisions/README.md` — every record with its status,",
            "   date and cross-references; the record itself is the ADR file.",
            "",
        ]
    )


# --- the citation read ------------------------------------------------------


def iter_cited_files(root: Path):
    for path in sorted(root.rglob("*")):
        if not path.is_file() or path.suffix not in CITED_SUFFIXES:
            continue
        if any(part in SKIP_DIRECTORIES for part in path.parts):
            continue
        if path.name in SKIP_ARTIFACTS:
            continue
        yield path


def dangling_citations(root: Path, corpus: list[Adr]) -> list[str]:
    """`ADR-NNNN` citations of a number no record carries, as `file:line: text`."""
    known = {record.number for record in corpus} | set(BURNED_NUMBERS)
    findings = []
    for path in iter_cited_files(root):
        try:
            text = path.read_text(encoding="utf-8")
        except (OSError, UnicodeDecodeError):
            continue
        for number, line in enumerate(text.splitlines(), start=1):
            for cited in set(ADR_CITATION.findall(line)):
                if cited not in known:
                    findings.append(
                        f"{path.relative_to(root)}:{number}: ADR-{cited} has no "
                        f"record: {line.strip()[:120]}"
                    )
    return findings


# --- the gate ---------------------------------------------------------------


def corpus_findings(root: Path, corpus: list[Adr]) -> list[str]:
    """The records themselves: a bad schema, a spent number, a repeated number.

    Split out of `check` so the documentation-freshness audit reads the front
    matter question — every record on one schema, and that schema well formed —
    from here rather than answering it a second time its own way
    (SpatialEngine-imz.2: implement once, share it).
    """
    findings: list[str] = []
    for record in corpus:
        if record.error:
            findings.append(f"{record.path.relative_to(root)}: {record.error}")
    for number in BURNED_NUMBERS:
        if any(record.number == number for record in corpus):
            findings.append(
                f"architecture/decisions/ADR-{number}-*.md: {number} is a burned "
                "number (architecture/decisions/README.md) and cannot carry a record"
            )
    duplicates = {
        number
        for number in (record.number for record in corpus)
        if sum(1 for other in corpus if other.number == number) > 1
    }
    for number in sorted(duplicates):
        findings.append(f"ADR-{number}: more than one record carries this number")
    return findings


def in_shape(record: Adr) -> bool:
    """Whether the shape rule binds this record, by its number (ADR-0150)."""
    return record.number >= SHAPE_FROM


def sections_of(text: str) -> list[str]:
    """The `##` headings of a record's body, in order, as written."""
    return [match.group(1).strip() for match in SECTION_HEADING.finditer(text)]


def narrative_words(text: str) -> int:
    """A record's words, less the sections that carry evidence rather than decision."""
    total = 0
    for part in re.split(r"^##\s+", text, flags=re.M)[1:]:
        heading = part.split("\n", 1)[0].strip().lower()
        if heading in {name.lower() for name in EVIDENCE_SECTIONS}:
            continue
        total += len(part.split())
    return total


def shape_findings(root: Path, corpus: list[Adr]) -> list[str]:
    """The shape rule: a closed section set, a reading order, a word budget.

    Split out of `check` and read by `tools/doc-freshness.py` for the reason
    `corpus_findings` and `register_findings` are (ADR-0141, SpatialEngine-imz.2):
    one answer to "is this record shaped like a decision record", one
    implementation, and one gate. A length gate on its own is a bad proxy —
    the long records are long for load-bearing reasons — so the budget is
    measured over the sections that state the decision and exempts the two that
    carry the evidence, and a record with one decision too many for the budget
    says so in `over-budget` rather than being cut to fit.
    """
    findings: list[str] = []
    for record in corpus:
        if not in_shape(record) or record.error:
            continue
        where = record.path.relative_to(root)
        try:
            text = record.path.read_text(encoding="utf-8")
        except (OSError, UnicodeDecodeError):  # pragma: no cover - unreadable
            continue
        body = text.split("---", 2)[-1]
        headings = sections_of(body)

        retired = [name for name in headings if name not in ALLOWED_SECTIONS]
        for name in retired:
            findings.append(
                f"{where}: `## {name}` is not a section a decision record carries; "
                f"the set is {', '.join(ALLOWED_SECTIONS)}. Fold it into the section "
                "it belongs to, or move the evidence to `## Measurements`."
            )
        missing = [name for name in REQUIRED_SECTIONS if name not in headings]
        if missing:
            findings.append(
                f"{where}: no {' or '.join(f'## {name}' for name in missing)}; a "
                "record states the decision in Context, Decision and Consequences."
            )
        present = [name for name in headings if name in REQUIRED_SECTIONS]
        if present != [name for name in REQUIRED_SECTIONS if name in present]:
            findings.append(
                f"{where}: the sections are out of reading order ({', '.join(present)}); "
                f"they are read as {', '.join(REQUIRED_SECTIONS)}."
            )

        words = narrative_words(body)
        if words > NARRATIVE_BUDGET:
            if record.get(OVER_BUDGET_FIELD):
                continue
            findings.append(
                f"{where}: {words} words of narrative, over the {NARRATIVE_BUDGET}-"
                f"word budget (References and Measurements are exempt). Split it — "
                "a second decision is a second record, with `amends:` naming this "
                f"one — or state why one record cannot be shorter, in `{OVER_BUDGET_FIELD}: "
                "<reason>`."
            )
        elif record.get(OVER_BUDGET_FIELD):
            findings.append(
                f"{where}: carries `{OVER_BUDGET_FIELD}: "
                f"{record.get(OVER_BUDGET_FIELD)}` at {words} words, inside the "
                f"{NARRATIVE_BUDGET}-word budget. An exemption nobody needs is a "
                "parameter accepted and ignored; delete it."
            )
    return findings


def register_findings(root: Path, corpus: list[Adr]) -> list[str]:
    """Whether the register block in the distilled digest is what the records say.

    Shared with `tools/doc-freshness.py` for the same reason as
    `corpus_findings` and `dangling_citations`: there is one answer to "does
    every record have a register row, and is that row current", and the gate
    here is where it is enforced (ADR-0141).
    """
    findings: list[str] = []
    register = root / "architecture" / "distilled" / "README.md"
    block = render_register(corpus)
    if not register.is_file():
        findings.append(f"{register.relative_to(root)}: no such file")
        return findings
    text = register.read_text(encoding="utf-8")
    if REGISTER_BEGIN not in text or REGISTER_END not in text:
        findings.append(
            f"{register.relative_to(root)}: no {REGISTER_BEGIN} .. "
            f"{REGISTER_END} block; the register is generated between those "
            "markers and the markers are added once, by hand"
        )
    else:
        start = text.find(REGISTER_BEGIN)
        end = text.find(REGISTER_END) + len(REGISTER_END)
        if text[start:end].strip() != block.strip():
            findings.append(
                f"{register.relative_to(root)}: the register does not match "
                "architecture/decisions/*.md; run "
                "`python3 tools/arch-index.py --write`"
            )
    return findings


def check(root: Path) -> list[str]:
    """Every way the generated corpus can be stale, as one list of findings."""
    findings: list[str] = []
    corpus = load_corpus(root)

    findings.extend(corpus_findings(root, corpus))
    findings.extend(shape_findings(root, corpus))
    findings.extend(register_findings(root, corpus))

    for path, rendered in (
        (root / "architecture" / "decisions" / "README.md", render_index(corpus)),
        (root / "arch-index.md", render_breadcrumb(corpus)),
    ):
        if not path.is_file():
            findings.append(f"{path.relative_to(root)}: no such file; run "
                            "`python3 tools/arch-index.py --write`")
        elif path.read_text(encoding="utf-8") != rendered:
            findings.append(
                f"{path.relative_to(root)}: is not what the records generate; run "
                "`python3 tools/arch-index.py --write`"
            )

    findings.extend(dangling_citations(root, corpus))
    return findings


def write(root: Path) -> int:
    """Regenerate the three artefacts in place. Returns a process exit code."""
    corpus = load_corpus(root)
    register = root / "architecture" / "distilled" / "README.md"
    try:
        spliced = splice_register(
            register.read_text(encoding="utf-8"), render_register(corpus)
        )
    except (OSError, ValueError) as error:
        print(f"arch-index: {error}", file=sys.stderr)
        return 1
    register.write_text(spliced, encoding="utf-8")
    (root / "architecture" / "decisions" / "README.md").write_text(
        render_index(corpus), encoding="utf-8"
    )
    (root / "arch-index.md").write_text(render_breadcrumb(corpus), encoding="utf-8")
    print(
        f"arch-index: {len(corpus)} records → "
        "architecture/distilled/README.md (register), "
        "architecture/decisions/README.md, arch-index.md"
    )
    return 0


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--root", default=".", help="repository root (default: .)")
    mode = parser.add_mutually_exclusive_group()
    mode.add_argument("--check", action="store_true", help="fail if anything is stale")
    mode.add_argument("--write", action="store_true", help="regenerate in place")
    mode.add_argument(
        "--citations", action="store_true", help="read dangling ADR-NNNN citations only"
    )
    mode.add_argument(
        "--register",
        action="store_true",
        help="read the record schema and the register block only, not the "
        "generated index, the breadcrumb or the citations",
    )
    arguments = parser.parse_args(argv)
    root = Path(arguments.root).resolve()

    if arguments.write:
        return write(root)

    if arguments.citations:
        findings = dangling_citations(root, load_corpus(root))
    elif arguments.register:
        corpus = load_corpus(root)
        findings = (
            corpus_findings(root, corpus)
            + register_findings(root, corpus)
            + shape_findings(root, corpus)
        )
    elif arguments.check or True:
        findings = check(root)
    if findings:
        print(f"arch-index: {len(findings)} finding(s):", file=sys.stderr)
        for finding in findings:
            print(f"  {finding}", file=sys.stderr)
        return 1
    print("arch-index: generated corpus is current")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
