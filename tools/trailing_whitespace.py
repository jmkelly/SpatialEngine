#!/usr/bin/env python3
"""Fail on a line of trailing whitespace the formatter does not look at.

    python3 tools/trailing_whitespace.py                # every file git tracks
    python3 tools/trailing_whitespace.py src/Some.cs    # just these

`.editorconfig` sets `trim_trailing_whitespace = true` for `[*]`, and
`dotnet format` — the only formatter in this repository, and the whole of what
`eng/verify.sh --format` and the CI `verify` job run — enforces it on a line
that carries code and **not** on a comment-only line. Measured on SDK 10.0.400
by injecting the violation and running `dotnet format <project>
--verify-no-changes` (SpatialEngine-emo):

| Where the whitespace is | What the formatter says |
| --- | --- |
| a code line | `error WHITESPACE: Fix whitespace formatting. Delete 2 characters`, exit 2 |
| a comment-only line, mid-file | nothing, exit 0 — and `dotnet format whitespace` without `--verify-no-changes` leaves it there |

So the repository asserted a rule over a subset of the files that break it, and
the subset the formatter skipped was invisible to every lane: the exhaustive
gate and the CI job were as blind to it as the scoped one. That is a hole in
the merge gate rather than in the formatter, and it is the same shape as the
unresolved-conflict-marker gap `tools/test_no_conflict_markers.py` closes —
a repository-wide claim nothing read.

**This is a check, not a second formatter.** It reads one rule, reports
`path:line`, and changes nothing: no `--fix`, no writing files back. The
alternative the bead named — a Roslyn analyzer for IDE0055 over comment trivia
— would enforce the same rule in the build, and that is the better shape if
the rule ever grows; the measurement that a build-time analyzer needs ~700 s of
solution-wide work to run (ADR-0109) is the reason it is not that here. A
`grep` that costs under a second belongs on every lane, and it is deliberately
independent of the .NET SDK, so it also runs in a checkout with no toolchain.

**What it does read from `.editorconfig`** is the *scope* of the rule, not the
rule: a file whose most specific matching section sets
`trim_trailing_whitespace = false` is not reported. `[*.md]` does exactly that
in this repository, and it is load-bearing — two trailing spaces at the end of
a markdown line are a hard line break, so a check that ignored the section
would fail every prose wrap in the documentation. Section order is editorconfig's
own: the last matching section wins, and a `[*]` with no value leaves the rule
on (fail closed).

Files come from the command line, or — with no arguments — from
`git ls-files --cached --others --exclude-standard`: everything the repository
ships, plus the untracked files that are not build output. Build directories
are not special-cased, so a checked-in artefact under one is still checked.
Binary files (a NUL byte in the first block) are skipped: a PNG's bytes are not
whitespace violations however they fall.
"""
from __future__ import annotations

import argparse
import fnmatch
import subprocess
import sys
from pathlib import Path

DEFAULT_REPO = Path(__file__).resolve().parent.parent

#: The rule, when no `.editorconfig` says otherwise.
TRIM_BY_DEFAULT = True

#: How much of a file is read looking for a NUL byte before it is called
#: binary. Enough for every text format the repository holds, and cheap enough
#: to run over every tracked file on a lane that also builds the solution.
BINARY_SNIFF_BYTES = 8192

#: A file with more of these is not read whole: the finding is reported on the
#: first offending line, and a megabyte of generated JSON is not a gate.
MAX_FILE_BYTES = 4 * 1024 * 1024


def editorconfig_rules(root: Path) -> list[tuple[str, bool]]:
    """The `trim_trailing_whitespace` sections of the root `.editorconfig`.

    A list of (glob, value) in file order, because the last matching section
    wins and that order is the file's. Unparseable lines are skipped rather
    than diagnosed: a rule this check cannot read is a rule it does not apply,
    and an `.editorconfig` with an exotic glob in it should not stop a lane.
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
        if glob is not None and key.strip().lower() == "trim_trailing_whitespace":
            rules.append((glob, value.strip().lower() == "true"))
    return rules


def trims(root: Path, relative: str, rules: list[tuple[str, bool]]) -> bool:
    """Whether the trailing-whitespace rule applies to `relative`.

    editorconfig matches a pattern with no `/` against the file name and a
    pattern with one against the path from the directory holding the
    `.editorconfig`; that is what `[*]` and `[*.md]` are relying on here, and
    it is the whole of the matching this check needs.
    """
    applies = TRIM_BY_DEFAULT
    name = relative.rsplit("/", 1)[-1]
    for glob, value in rules:
        matched = (fnmatch.fnmatch(name, glob) if "/" not in glob
                   else fnmatch.fnmatch(relative, glob))
        if matched:
            applies = value
    return applies


def violations(path: Path) -> list[tuple[int, int]]:
    """The (line number, character count) pairs with trailing whitespace.

    Trailing whitespace is a space or a tab before the line's end, which is
    why this is written on the split line rather than on a stripped copy: the
    stripped copy is the thing with the violation removed.
    """
    try:
        data = path.read_bytes()
    except OSError:
        return []
    if b"\0" in data[:BINARY_SNIFF_BYTES] or len(data) > MAX_FILE_BYTES:
        return []
    found = []
    for number, line in enumerate(data.decode("utf-8", errors="replace").splitlines(), 1):
        stripped = line.rstrip(" \t")
        if stripped != line:
            found.append((number, len(line) - len(stripped)))
    return found


def repository_files(root: Path) -> list[str]:
    """Every file the repository ships, plus untracked files git is not ignoring.

    Tracked *and* untracked-not-ignored rather than tracked alone: a branch adds
    new files, and a new file with the violation in it is the case this check
    exists for. `exclude-standard` is what keeps `bin/`, `obj/` and the
    worktree's own noise out without this script knowing the ignore rules.
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

    A path that is not there is a usage error and not a finding: a typo in a
    lane's argument list must not read as a clean file, and must not read as a
    violation either, so it exits 2 with the name on stderr.
    """
    if not given:
        return repository_files(root), []
    problems = [name for name in given if not (root / name).exists()]
    return given, problems


def check(root: Path, names: list[str], out) -> int:
    """Report every trailing-whitespace line in `names`; return the exit code."""
    rules = editorconfig_rules(root)
    findings = 0
    for name in names:
        path = root / name
        if path.is_dir():
            children = sorted(p for p in path.rglob("*") if p.is_file())
            for child in children:
                findings += report(root, child, rules, out)
            continue
        findings += report(root, path, rules, out)
    return 1 if findings else 0


def report(root: Path, path: Path, rules: list[tuple[str, bool]], out) -> int:
    """The findings for one file, as `path:line:` lines, honouring the rules."""
    try:
        relative = path.resolve().relative_to(root.resolve()).as_posix()
    except ValueError:
        relative = path.as_posix()
    if not trims(root, relative, rules):
        return 0
    found = violations(path)
    for number, count in found:
        print(f"{relative}:{number}: trailing whitespace ({count} character"
              f"{'s' if count != 1 else ''})", file=out)
    return len(found)


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(
        description="Fail on trailing whitespace `dotnet format` does not check.")
    parser.add_argument("paths", nargs="*",
                        help="files or directories to check; default is the whole repository")
    parser.add_argument("--repo", type=Path, default=DEFAULT_REPO,
                        help="repository root, which is where .editorconfig is read from")
    args = parser.parse_args(argv)
    root = args.repo.resolve()
    names, problems = collect(root, args.paths)
    if problems:
        for problem in problems:
            print(f"trailing-whitespace: {problem}", file=sys.stderr)
        return 2
    return check(root, names, sys.stdout)


if __name__ == "__main__":
    raise SystemExit(main())
