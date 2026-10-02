#!/usr/bin/env python3
"""Fail on a tracked file that does not end in a newline.

    python3 tools/final_newline.py                  # every file git ships
    python3 tools/final_newline.py src/Some.cs      # just these

`.editorconfig` sets `insert_final_newline = true` for `[*]`, and `dotnet format`
is the only formatter in this repository — but since ADR-0134 it is not on the
merge path: `eng/verify.sh --fast` does not format, so between an edit and
`main` nothing runs the formatter at all. Measured on 2026-10-01 at `origin/main`
`eb909b5c`, SDK 10.0.400, `dotnet format SpatialEngine.slnx
--verify-no-changes` on a clean tree exited 2 with 22 violations in 16 files,
and **14 of them were this one rule**: files ending in `}` with no trailing
newline (SpatialEngine-744). CI caught them, but only *after* the merge — the
lane's own claim is that CI is the detector, and the detector is on the far side
of `main`.

**This is a check, not a formatter**, and the reasoning is ADR-0143's verbatim:
a rule that costs about two seconds belongs on every lane, while the formatter
costs ~700 s solution-wide (ADR-0109) or ~45 s per project scoped (ADR-0134).
It reports `path: no final newline`, exits 1, changes nothing and has no
`--fix`, and it needs no .NET SDK, so it also runs in a checkout with no
toolchain.

It is a **sibling** of `tools/trailing_whitespace.py` rather than a rule inside
it, because ADR-0143 §2 closed that door on purpose: "The check does not read
`insert_final_newline`, `indent_size` or anything else: one rule, and no growth
into a second formatter nobody measured." A second rule in a check whose
acceptance criteria and record both say it is one rule turns a named decision
into an accumulation; a second script, wired the same way, is the shape the
repository already uses for every other repository check
(`tools/conflict_markers.py`, ADR-0146).

**Which files it reads is the `.editorconfig` claim, `[*]`, and nothing less.**
It began as `.cs` only, because `FINALNEWLINE` is a Roslyn diagnostic:
`dotnet format clients/dotnet/Spatial.Client/Spatial.Client.csproj
--verify-no-changes` exits **0** on a project file with no final newline, while
the same violation in a `.cs` file is `error FINALNEWLINE: Fix final newline.
Insert '\n'` and exit 2 (SpatialEngine-744). That left the rest of `[*]` claimed
in configuration and enforced by nothing (ADR-0186 §2, which filed the gap as
SpatialEngine-3rz). The gap is closed here (ADR-0188): a `.md`, `.json`, `.py`,
`.sh` or `.csproj` file with no final newline is as unformatted as a `.cs` one,
so the check reads every file the claim names, and the *scope* is where captured
and generated artefacts are exempted — through `.editorconfig`, ADR-0143's
`psql` precedent, rather than by a file-type table here.

So there is no suffix list to grow: a file is read when it is text (no NUL byte
in its first 8 KiB), non-empty and small enough to open, and the `.editorconfig`
section matching it does not set `insert_final_newline = false`.

**What it reads from `.editorconfig`** is the *scope* of the rule, not the rule:
a file whose most specific matching section sets `insert_final_newline = false`
is not reported. No section in this repository does, but the scope is read
rather than assumed, so a file exempted later is honoured rather than deleted
back into compliance (the same rule `tools/trailing_whitespace.py` applies to
`trim_trailing_whitespace`, and the reason `[*.md]` is load-bearing there).

Three shapes are not findings, and all are pinned in
`tools/test_final_newline.py`: an **empty** file, which has nothing to end; a
**binary** file, whose last byte is not a line; and a file whose matching
`.editorconfig` section turns the rule off, which is where the captured
`research/compat/ground-truth` and `tests/fixtures` corpora, the recorded Esri
OpenAPI snapshot and the vendored MapLibre bundles live.

Files come from the command line, or — with no arguments — from
`git ls-files --cached --others --exclude-standard`: everything the repository
ships, plus the untracked files that are not build output.
"""
from __future__ import annotations

import argparse
import fnmatch
import subprocess
import sys
from pathlib import Path

DEFAULT_REPO = Path(__file__).resolve().parent.parent

#: The rule, when no `.editorconfig` says otherwise.
INSERT_BY_DEFAULT = True

#: How much of a file is read looking for a NUL byte before it is called
#: binary. Enough for every text format the repository holds.
BINARY_SNIFF_BYTES = 8192

#: A file with more of these is not read whole: the finding is a property of the
#: last byte, but a megabyte of generated JSON is not a gate.
MAX_FILE_BYTES = 4 * 1024 * 1024


def editorconfig_rules(root: Path) -> list[tuple[str, bool]]:
    """The `insert_final_newline` sections of the root `.editorconfig`.

    A list of (glob, value) in file order, because the last matching section
    wins and that order is the file's. Unparseable lines are skipped rather
    than diagnosed, for the reason `tools/trailing_whitespace.py` gives: a
    section this check cannot read is a section it does not apply.
    """
    path = root / ".editorconfig"
    if not path.is_file():
        return []
    rules: list[tuple[str, bool]] = []
    glob: str | None = None
    for raw in path.read_text(encoding="utf-8", errors="replace").splitlines():
        line = raw.strip()
        if not line or line.startswith(("#", ";")) or line.startswith("["):
            glob = line[1:-1].strip() if line.startswith("[") and line.endswith("]") else None
            continue
        key, _, value = line.partition("=")
        if glob is not None and key.strip().lower() == "insert_final_newline":
            rules.append((glob, value.strip().lower() == "true"))
    return rules


def inserts(root: Path, relative: str, rules: list[tuple[str, bool]]) -> bool:
    """Whether the final-newline rule applies to `relative`.

    editorconfig matches a pattern with no `/` against the file name and one
    with a `/` against the path from the directory holding the `.editorconfig`,
    last matching section winning — the matching `tools/trailing_whitespace.py`
    uses, and the only matching this check needs.
    """
    applies = INSERT_BY_DEFAULT
    name = relative.rsplit("/", 1)[-1]
    for glob, value in rules:
        matched = (fnmatch.fnmatch(name, glob) if "/" not in glob
                   else fnmatch.fnmatch(relative, glob))
        if matched:
            applies = value
    return applies


def missing_final_newline(path: Path) -> bool:
    """Whether `path` is a text file whose last byte is not a newline.

    An empty file is clean: there is no last byte, and `dotnet format` on a
    file with no content has nothing to insert. A file whose final byte is a
    newline is clean whatever precedes it — this is one rule, and trimming a
    blank line at the end is a different one nobody measured.
    """
    try:
        with path.open("rb") as handle:
            size = path.stat().st_size
            if size == 0:
                return False
            if size > MAX_FILE_BYTES:
                return False
            head = handle.read(min(size, BINARY_SNIFF_BYTES))
            if b"\0" in head:
                return False
            handle.seek(max(0, size - 1))
            return handle.read(1) != b"\n"
    except OSError:
        return False


def repository_files(root: Path) -> list[str]:
    """Every file the repository ships, plus untracked files git is not ignoring.

    Tracked *and* untracked-not-ignored rather than tracked alone, because a
    branch adds new files and a new file is the case this check exists for
    (the ADR-0143 argument, seconded).
    """
    listing = subprocess.run(
        ["git", "-C", str(root), "ls-files", "-z", "--cached", "--others",
         "--exclude-standard"],
        capture_output=True,
    )
    if listing.returncode != 0:
        raise SystemExit(f"git could not list the files under {root}")
    return [name for name in listing.stdout.decode("utf-8", errors="replace").split("\0") if name]


def collect(root: Path, given: list[str]) -> tuple[list[str], list[str]]:
    """The (files to read, problems with the request) for this invocation.

    A path that is not there is a usage error and not a finding, for the reason
    `tools/trailing_whitespace.py` gives: a typo in a lane's argument list must
    not read as a clean file, and must not read as a violation either.
    """
    if not given:
        return repository_files(root), []
    problems = [name for name in given if not (root / name).exists()]
    return given, problems


def check(root: Path, names: list[str], out) -> int:
    """Report every file in `names` with no final newline; return the exit code."""
    rules = editorconfig_rules(root)
    findings = 0
    for name in names:
        path = root / name
        if path.is_dir():
            for child in sorted(p for p in path.rglob("*") if p.is_file()):
                findings += report(root, child, rules, out)
            continue
        findings += report(root, path, rules, out)
    return 1 if findings else 0


def report(root: Path, path: Path, rules: list[tuple[str, bool]], out) -> int:
    """The finding for one file, if any, honouring the `.editorconfig` scope."""
    try:
        relative = path.resolve().relative_to(root.resolve()).as_posix()
    except ValueError:
        relative = path.as_posix()
    if not inserts(root, relative, rules):
        return 0
    if not missing_final_newline(path):
        return 0
    print(f"{relative}: no final newline", file=out)
    return 1


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(
        description="Fail on a file with no final newline, as the .editorconfig claims.")
    parser.add_argument("paths", nargs="*",
                        help="files or directories to check; default is the whole repository")
    parser.add_argument("--repo", type=Path, default=DEFAULT_REPO,
                        help="repository root, which is where .editorconfig is read from")
    args = parser.parse_args(argv)
    root = args.repo.resolve()
    names, problems = collect(root, args.paths)
    if problems:
        for problem in problems:
            print(f"final-newline: {problem}", file=sys.stderr)
        return 2
    return check(root, names, sys.stdout)


if __name__ == "__main__":
    raise SystemExit(main())
