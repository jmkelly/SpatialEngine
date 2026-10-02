#!/usr/bin/env python3
"""Report the Orientation coverage: one line per bead, in the digest that owns it.

    python3 tools/orientation.py                       # the coverage report
    python3 tools/orientation.py --list architecture/distilled/core.md
    python3 tools/orientation.py --root /some/checkout

**What an Orientation line is.** At `bd close` the closing agent answers one
question — *where did the first hour go?* — and a line goes under an
`## Orientation` heading in the digest that owns that area, in the imperative
("start at X; Y is a decoy because Z"). Not prose volume: one line per closed
bead, and only facts that cost time when they are absent. That shape is what
the evidence supports and it is worth being precise about, because the
documented mental model of a context file is the part the evidence refutes:

*   arXiv 2607.27250 (288 runs, two frontier agents, 17 real tasks): context
    strategy does **not** measurably move correctness (≤10–15pp, equivalence-
    bounded). Failure triage shows agents failing on implementation skill —
    feature design, pattern selection, exact wiring — not on missing repository
    knowledge. A context file cannot supply what the model already knows how to
    do;
*   arXiv 2606.20512: probe-and-refine tuning of repo guidance *is* the
    intervention that moves correctness (33.0% vs 28.3% static KB vs 25.5%
    unguided on SWE-bench Verified, p<0.001) — and the gain is **coverage**, not
    precision: refined guidance produced evaluable patches for +14.5pp more
    instances while per-patch precision stayed flat (~59%, p=0.119). Guidance
    helps an agent *reach the correct file*. It does not make the agent write
    better code once there;
*   arXiv 2608.04661: preserved Agent Plans carry the same value — steps,
    concrete file locations, testing information, which is this line's shape;
*   arXiv 2601.20404: an `AGENTS.md` buys −28.6% median runtime and −16.6%
    output tokens. Efficiency, not correctness.

So the class of fact is narrow on purpose: a file, a subsystem, a trap, or a
search that was dead. Never a rule the agent would have followed anyway.

**This is a report, not a gate.** The bead (`SpatialEngine-rzq`, gap G9 of
epic `SpatialEngine-imz`) says so explicitly: it cannot be mechanised, it is the
one place in the doc surface where prose is the right instrument, and it is
precisely what the epic's pruning children will not produce. Nothing in
`eng/verify.sh` calls this, and `tools/test_orientation.py` pins that it is not
called. What is mechanisable, and what the tests pin, is the shape: the
delimiters below, a bead id on every line, and the twenty-bead floor.

**`--list` is the other half.** The G4 child bead (`SpatialEngine-imz.5`) has to
derive its nested per-package `AGENTS.md` files rather than hand-write them, so
that the two beads compose instead of competing. A generator reads one package's
block through this tool and gets the lines and nothing around them.

Traceability is read from `git log`: an Orientation line names the bead it came
from, and a `Task: <id>` trailer in history is that bead's receipt. A CI
checkout is shallow, so the report says history was not deep enough rather than
calling every line untraceable (`history_is_shallow`).
"""
from __future__ import annotations

import argparse
import dataclasses
import re
import subprocess
import sys
from pathlib import Path

DEFAULT_REPO = Path(__file__).resolve().parent.parent

#: The block delimiters. A generator reads between them and nothing else, so
#: they are load-bearing rather than decorative — and they are HTML comments so
#: they render as nothing.
BEGIN = "<!-- orientation:begin -->"
END = "<!-- orientation:end -->"

#: A bead id at the end of a line, in either spelling the corpus uses: a
#: parenthetical or an em-dash attribution, optionally in code font.
BEAD = re.compile(r"\(?\s*`?(SpatialEngine-[A-Za-z0-9][A-Za-z0-9.\-]*)`?\s*\)?\s*$")

#: Directories no `AGENTS.md` is read out of.
SKIP_DIRS = frozenset({".git", "node_modules", "bin", "obj", "dist", "TestResults"})

#: Below this many commits a checkout is assumed shallow enough that a bead id
#: cannot be traced, and the report says so rather than reporting a miss. CI
#: checks out depth 1 (`actions/checkout@v7` at its default).
SHALLOW_HISTORY = 50


class OrientationError(Exception):
    """A block that is missing, unbalanced or repeated — the shape is broken."""


@dataclasses.dataclass(frozen=True)
class Line:
    """One Orientation line: the fact, the bead it came from, the file it is in.

    `bead` is None for a line that names none, which is a finding and not an
    error: the block still reads, and the discipline is what is missing.
    """

    path: str
    text: str
    bead: str | None


@dataclasses.dataclass(frozen=True)
class Block:
    """Every Orientation line in one document."""

    path: str
    lines: tuple[Line, ...]


@dataclasses.dataclass(frozen=True)
class Report:
    """Coverage, which is what the bead asks for — not a verdict on quality."""

    lines: int
    files: tuple[str, ...]
    beads: frozenset[str]
    untraceable: frozenset[str]
    history_judged: bool


def parse_block(text: str, path: str) -> list[Line]:
    """The Orientation lines in `text`.

    A line is a bullet. Wrapped continuation lines are joined onto the bullet
    above them, because these are written at the prose width the rest of the
    corpus uses and a wrapped fact is still one line — one bead, one line.
    Anything else inside the delimiters (a heading, the preamble, a blank) is
    structure the reader sees and the report does not count. One block per
    file: two blocks in one document is a shape failure, not two sections,
    because the reader — and the generator — is owed one place to look.
    """
    # Markers are found anywhere in the text rather than as whole lines: two
    # blocks run together on one line is the shape failure the sequence check
    # below is there to name, so it cannot be read as one marker per line.
    markers = [(m.start(), m.group(0)) for m in
               re.finditer(re.escape(BEGIN) + "|" + re.escape(END), text)]
    if not markers:
        return []
    shape = [END if marker == END else BEGIN for _, marker in markers]
    if shape != [BEGIN, END]:
        raise OrientationError(
            f"{path}: markers read {' '.join(shape)}, one BEGIN/END pair is the shape")

    body = text[markers[0][0] + len(BEGIN):markers[1][0]]
    entries: list[str] = []
    for line in body.splitlines():
        stripped = line.strip()
        if stripped.startswith(("- ", "* ")):
            entries.append(stripped)
        elif entries and stripped and not stripped.startswith(("#", "<!--")):
            # A continuation of the bullet above, wrapped at the prose width.
            entries[-1] = f"{entries[-1]} {stripped}"

    parsed: list[Line] = []
    for entry in entries:
        # The bead is read off the joined line, because wrapping can leave the
        # id on a continuation of its own.
        match = BEAD.search(entry)
        parsed.append(Line(path, entry, match.group(1) if match else None))
    return parsed


def untraced(lines: list[Line]) -> list[Line]:
    """The lines that name no bead — the ones nobody can trace to a close."""
    return [line for line in lines if line.bead is None]


def blocks_in(root: Path, paths: list[Path]) -> list[Block]:
    """Parse `paths` (relative to `root`) into blocks, skipping the ones empty."""
    blocks = []
    for path in paths:
        name = path.relative_to(root).as_posix()
        lines = parse_block(path.read_text(encoding="utf-8"), name)
        if lines:
            blocks.append(Block(name, tuple(lines)))
    return blocks


def markdown_files(root: Path) -> list[Path]:
    """Every markdown file in the tree, tracked or not.

    The filesystem rather than `git ls-files`, so a block written in a working
    tree counts before it is committed — the report is run by the agent that
    just wrote the line, not only by CI.
    """
    return sorted(p for p in root.rglob("*.md")
                  if not SKIP_DIRS.intersection(p.relative_to(root).parts))


def discover(root: Path) -> list[Block]:
    """Every document carrying an Orientation block.

    Every markdown file, not a hand-picked list: a line written anywhere is
    coverage, and a report that missed the document someone wrote it in would
    under-report the one thing it exists to report. Parsing a document costs a
    marker search, so the corpus of 200-odd files is milliseconds.
    """
    candidates = [p for p in markdown_files(root)
                  if BEGIN in p.read_text(encoding="utf-8", errors="replace")]
    return blocks_in(root, candidates)


def history_beads(root: Path) -> set[str]:
    """The bead ids `git log` carries a `Task: <id>` trailer for."""
    listing = subprocess.run(["git", "-C", str(root), "log", "--format=%b"],
                             capture_output=True)
    if listing.returncode != 0:
        return set()
    return set(re.findall(r"Task:\s*(SpatialEngine-[A-Za-z0-9.\-]+)",
                          listing.stdout.decode("utf-8", errors="replace")))


def history_is_shallow(root: Path, threshold: int = SHALLOW_HISTORY) -> bool:
    """True when the checkout carries too little history to judge traceability."""
    counted = subprocess.run(["git", "-C", str(root), "rev-list", "--count", "HEAD"],
                             capture_output=True)
    if counted.returncode != 0:
        return True
    try:
        return int(counted.stdout.decode().strip()) < threshold
    except ValueError:
        return True


def coverage(blocks: list[Block], history: set[str], judged: bool = True) -> Report:
    """Lines, files, distinct beads, and the beads history has no receipt for."""
    lines = [line for block in blocks for line in block.lines]
    beads = frozenset(line.bead for line in lines if line.bead)
    return Report(
        lines=len(lines),
        files=tuple(sorted({block.path for block in blocks})),
        beads=beads,
        untraceable=frozenset(beads - set(history)) if judged else frozenset(),
        history_judged=judged,
    )


def render(report: Report) -> str:
    """The coverage report as text."""
    out = [f"orientation: {report.lines} lines across {len(report.files)} documents, "
           f"{len(report.beads)} distinct beads"]
    for path in report.files:
        out.append(f"  {path}")
    out.append(f"  beads: {', '.join(sorted(report.beads))}")
    if not report.history_judged:
        out.append("  traceability: NOT JUDGED (history too shallow to trace a bead id)")
    elif report.untraceable:
        out.append(f"  untraceable (no 'Task: <id>' in history): "
                   f"{', '.join(sorted(report.untraceable))}")
    else:
        out.append("  traceability: every cited bead carries a 'Task: <id>' trailer")
    return "\n".join(out)


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(
        description="Report Orientation coverage, or list one document's block.")
    parser.add_argument("--root", type=Path, default=DEFAULT_REPO,
                        help="repository root (default: the checkout holding this script)")
    parser.add_argument("--list", metavar="PATH", default=None,
                        help="print one document's Orientation block and exit")
    parser.add_argument("--report", action="store_true",
                        help="the coverage report (the default)")
    args = parser.parse_args(argv)
    root = args.root.resolve()

    if args.list:
        path = root / args.list
        if not path.is_file():
            raise OrientationError(f"{args.list} is not a file under {root}")
        lines = parse_block(path.read_text(encoding="utf-8"), args.list)
        if not lines:
            raise OrientationError(f"{args.list} carries no orientation block")
        for line in lines:
            print(line.text)
        return 0

    judged = not history_is_shallow(root)
    print(render(coverage(discover(root), history_beads(root), judged=judged)))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
