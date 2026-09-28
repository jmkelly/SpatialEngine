#!/usr/bin/env python3
"""Print the next free ADR number, or check whether one is still free.

    python3 tools/adr-next-number.py              # next free number, e.g. 0083
    python3 tools/adr-next-number.py --check 0083 # exit 1 if 0083 is taken
    python3 tools/adr-next-number.py --base main  # against another base

A number is a reference: it only means something while it identifies exactly
one record, and AdrNumberingTests proves that (SpatialEngine-u2x.24). A branch
that picks its number from the base it branched off collides with any branch
merged since — one coordinator tick (2026-09-28) saw four branches claim
ADR-0075 and ADR-0081 that main already held, and the renumbering fell to the
coordinator. So allocation reads the *base* (`origin/main` when it exists) and
the *working tree* — the record this branch has already written counts as
claimed — and hands back one past the highest claim, so two branches that both
run this get different answers.

Re-run it immediately before writing the record: a base that has moved since
the bead started will have taken the number this branch was about to use, and
`--check` turns that into a non-zero exit rather than a merge-time conflict.
"""
from __future__ import annotations

import argparse
import re
import subprocess
import sys
from pathlib import Path

DECISIONS = "architecture/decisions"

# A record's number is the ADR-NNNN its path starts with; anything else (a
# README, a draft below the directory, a source file) is not a claim.
RECORD = re.compile(rf"^{DECISIONS}/ADR-(\d{{4}})-.+\.md$")
BASE_CANDIDATES = ("origin/main", "main")


def numbers_in(paths) -> set[int]:
    """The numbers claimed by decision-record paths in `paths`."""
    return {int(match.group(1))
            for path in paths
            for match in [RECORD.match(path.replace("\\", "/"))] if match}


def next_free_number(claimed: set[int]) -> int:
    """One past the highest claim.

    Deliberately not the lowest gap: every branch looking at the same base
    would find the same hole and take it, which is the collision itself.
    """
    return max(claimed, default=0) + 1


def git(*args, str_repo: Path) -> subprocess.CompletedProcess:
    return subprocess.run(
        ["git", *args], cwd=str_repo, capture_output=True, text=True)


def ref_exists(str_repo: Path, ref: str) -> bool:
    return git("rev-parse", "--verify", "--quiet", f"{ref}^{{commit}}",
               str_repo=str_repo).returncode == 0


def resolve_base_ref(str_repo: Path) -> str:
    """The ref other branches merge into: origin/main, then main, then HEAD."""
    for candidate in BASE_CANDIDATES:
        if ref_exists(str_repo, candidate):
            return candidate
    return "HEAD"


def files_at_ref(str_repo: Path, ref: str) -> list[str]:
    listed = git("ls-tree", "-r", "--name-only", ref, "--", DECISIONS,
                 str_repo=str_repo)
    if listed.returncode != 0:
        raise SystemExit(
            f"cannot read {ref}: {listed.stderr.strip()}\n"
            "fetch the base (git fetch origin main) or pass --base.")
    return [line for line in listed.stdout.splitlines() if line]


def records_in_tree(str_repo: Path) -> list[str]:
    directory = str_repo / DECISIONS
    if not directory.is_dir():
        return []
    return [f"{DECISIONS}/{path.name}"
            for path in directory.iterdir() if path.is_file()]


def repository_root() -> Path:
    """The checkout the agent is standing in, not wherever the tool was run from."""
    top = git("rev-parse", "--show-toplevel", str_repo=Path.cwd())
    if top.returncode == 0 and top.stdout.strip():
        return Path(top.stdout.strip())
    return Path(__file__).resolve().parent.parent


def claimed_numbers(str_repo: Path, base_ref: str) -> set[int]:
    """Every number taken on the base, plus any this branch has already taken."""
    return numbers_in(files_at_ref(str_repo, base_ref)) | \
        numbers_in(records_in_tree(str_repo))


def main(argv=None) -> int:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--base", help="ref to allocate against "
                        "(default: origin/main if it exists, else main)")
    parser.add_argument("--check", metavar="NNNN",
                        help="exit 0 if NNNN is still free, 1 if it is taken")
    args = parser.parse_args(argv)

    str_repo = repository_root()
    base_ref = args.base or resolve_base_ref(str_repo)
    claimed = claimed_numbers(str_repo, base_ref)

    if args.check is None:
        print(f"{next_free_number(claimed):04d}")
        return 0

    requested = args.check.strip()
    if not re.fullmatch(r"\d{4}", requested):
        print(f"--check wants a four-digit number, got {args.check!r}",
              file=sys.stderr)
        return 2
    if int(requested) in claimed:
        print(f"ADR-{requested} is already taken on {base_ref} or in the "
              f"working tree; take {next_free_number(claimed):04d} instead.")
        return 1
    print(requested)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
