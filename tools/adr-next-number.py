#!/usr/bin/env python3
"""Reserve the next free ADR number, or check whether one is still free.

    python3 tools/adr-next-number.py --reserve            # hold 0088
    python3 tools/adr-next-number.py                      # next free number
    python3 tools/adr-next-number.py --check 0088         # exit 1 if taken
    python3 tools/adr-next-number.py --list               # who holds what
    python3 tools/adr-next-number.py --release 0088       # give it back

A number is a reference: it only means something while it identifies exactly
one record, and AdrNumberingTests proves that (SpatialEngine-u2x.24). A branch
that picks its number from the base it branched off collides with any branch
merged since — one coordinator tick (2026-09-28) saw four branches claim
ADR-0075 and ADR-0081 that main already held, and the renumbering fell to the
coordinator. So allocation reads the *base* (`origin/main` when it exists) and
the *working tree* — the record this branch has already written counts as
claimed — and hands back one past the highest claim.

Reading a base is not the same as holding a number: six branches reading one
base get the same answer, and the loser only finds out at merge time. So
`--reserve` also *takes* the number, by creating `<git-common-dir>/
adr-reservations/NNNN.json` with O_EXCL. The common dir is shared by every
worktree of the repository, which is where the swarm's branches live, and the
exclusive create is what makes the reservation mutual: exactly one branch can
win a given number, and the losers move on to the next one.

Re-run `--check` immediately before writing the record: a base that has moved
since the bead started will have taken the number this branch reserved, and
`--check` turns that into a non-zero exit rather than a merge-time conflict.
A reservation ends when the record lands (`--release`, once the branch is
rebased onto a main that carries the record) or when it goes stale — a branch
that no longer exists, a file that cannot be read, or one older than
`--max-age-days`. A worker that dies mid-bead must not hold a number for ever.
"""
from __future__ import annotations

import argparse
import json
import os
import re
import subprocess
import sys
from datetime import datetime, timedelta, timezone
from pathlib import Path

DECISIONS = "architecture/decisions"

# A record's number is the ADR-NNNN its path starts with; anything else (a
# README, a draft below the directory, a source file) is not a claim.
RECORD = re.compile(rf"^{DECISIONS}/ADR-(\d{{4}})-.+\.md$")
BASE_CANDIDATES = ("origin/main", "main")

# Reservations are coordination state, not history: they live beside the
# repository's own git metadata, shared by every worktree, and never committed.
RESERVATION_DIRNAME = "adr-reservations"
DEFAULT_MAX_AGE_DAYS = 14


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


def common_dir(str_repo: Path) -> Path:
    """The git dir every worktree of the repository shares."""
    shown = git("rev-parse", "--git-common-dir", str_repo=str_repo)
    if shown.returncode != 0 or not shown.stdout.strip():
        raise SystemExit("not inside a git repository, so numbers cannot be "
                         "reserved; the reservation store is the git common "
                         f"dir: {shown.stderr.strip()}")
    path = Path(shown.stdout.strip())
    return path if path.is_absolute() else str_repo / path


def reservation_dir(str_repo: Path) -> Path:
    return common_dir(str_repo) / RESERVATION_DIRNAME


def current_branch(str_repo: Path) -> str:
    """The branch the caller is on; HEAD when it is not on one."""
    named = git("rev-parse", "--abbrev-ref", "HEAD", str_repo=str_repo)
    return named.stdout.strip() or "HEAD" if named.returncode == 0 else "HEAD"


def branch_exists(str_repo: Path, branch: str) -> bool:
    if branch in ("", "HEAD"):
        return True
    return ref_exists(str_repo, f"refs/heads/{branch}")


def reservation_path(str_repo: Path, number: int) -> Path:
    return reservation_dir(str_repo) / f"{number:04d}.json"


def read_reservation(path: Path) -> dict | None:
    """The holder recorded in a reservation file, or None if it is unreadable.

    An unreadable file is not a number nobody holds: it is a reservation whose
    holder cannot be identified, which is exactly the case that must not wedge
    every later allocation, so the caller treats it as stale.
    """
    try:
        held = json.loads(path.read_text())
    except (OSError, ValueError):
        return None
    return held if isinstance(held, dict) else None


def is_stale(held: dict | None, str_repo: Path, now: datetime,
             max_age_days: int) -> bool:
    if held is None:
        return True
    if not branch_exists(str_repo, str(held.get("branch") or "")):
        return True
    reserved_at = held.get("reserved_at")
    if not isinstance(reserved_at, str):
        return True
    try:
        taken = datetime.fromisoformat(reserved_at)
    except ValueError:
        return True
    if taken.tzinfo is None:
        taken = taken.replace(tzinfo=timezone.utc)
    return now - taken > timedelta(days=max_age_days)


def holder(str_repo: Path, number: int, max_age_days: int) -> dict | None:
    """The live reservation for a number, if any; stale ones are removed."""
    path = reservation_path(str_repo, number)
    if not path.exists():
        return None
    held = read_reservation(path)
    if is_stale(held, str_repo, datetime.now(timezone.utc), max_age_days):
        path.unlink(missing_ok=True)
        return None
    return held


def holders(str_repo: Path, max_age_days: int) -> dict[int, dict]:
    store = reservation_dir(str_repo)
    if not store.is_dir():
        return {}
    live = {}
    for path in sorted(store.glob("*.json")):
        held = holder(str_repo, int(path.stem), max_age_days)
        if held is not None:
            live[int(path.stem)] = held
    return live


def claim(str_repo: Path, number: int, branch: str, bead: str | None,
          base_ref: str) -> dict | None:
    """Take a number for `branch`, or None if another holder got there first.

    O_EXCL is the whole mechanism: the kernel serialises the create, so two
    branches racing for the same number produce one winner and one None, with
    no lock file to keep alive and no window between the check and the write.
    """
    store = reservation_dir(str_repo)
    store.mkdir(parents=True, exist_ok=True)
    record = {"number": number, "branch": branch, "bead": bead,
              "base": base_ref,
              "reserved_at": datetime.now(timezone.utc).isoformat()}
    try:
        descriptor = os.open(str(reservation_path(str_repo, number)),
                             os.O_CREAT | os.O_EXCL | os.O_WRONLY, 0o644)
    except FileExistsError:
        return None
    with os.fdopen(descriptor, "w") as handle:
        json.dump(record, handle, indent=2)
        handle.write("\n")
    return record


def reserve_number(str_repo: Path, claimed: set[int], base_ref: str,
                   bead: str | None, max_age_days: int) -> int:
    """Hold the lowest number that is neither claimed nor already reserved."""
    branch = current_branch(str_repo)
    first = next_free_number(claimed)
    for candidate in range(first, first + 1000):
        if candidate in claimed:
            continue
        existing = holder(str_repo, candidate, max_age_days)
        if existing is not None:
            if existing.get("branch") == branch:
                # Re-reserving after a rebase is part of the documented flow,
                # and it must keep the number this branch is already writing.
                return candidate
            continue
        if claim(str_repo, candidate, branch, bead, base_ref) is not None:
            return candidate
    raise SystemExit("no free ADR number in the next thousand; is the "
                     "reservation store wedged?")


def release_number(str_repo: Path, numbers: list[int] | None, branch: str,
                   max_age_days: int) -> int:
    """Give up this branch's hold, so the number can be allocated again."""
    wanted = ([int(n) for n in numbers] if numbers
              else sorted(holders(str_repo, max_age_days)))
    if not wanted:
        print(f"{branch} holds no ADR number.", file=sys.stderr)
        return 1
    for number in wanted:
        held = holder(str_repo, number, max_age_days)
        if held is None:
            print(f"ADR-{number:04d} is not reserved.", file=sys.stderr)
            return 1
        if held.get("branch") != branch:
            print(f"ADR-{number:04d} is reserved by {held.get('branch')!r}, "
                  f"not {branch!r}.", file=sys.stderr)
            return 1
        reservation_path(str_repo, number).unlink(missing_ok=True)
        print(f"{number:04d}")
    return 0


def main(argv=None) -> int:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--base", help="ref to allocate against "
                        "(default: origin/main if it exists, else main)")
    parser.add_argument("--check", nargs="+", metavar="NNNN",
                        help="for each number: print it if it is free, or say "
                             "why not; exit 0 only if all of them are free")
    parser.add_argument("--reserve", action="store_true",
                        help="hold the next free number for this branch and "
                             "print it")
    parser.add_argument("--release", nargs="?", const="", metavar="NNNN",
                        help="give up this branch's reservation (all of them "
                             "when no number is given)")
    parser.add_argument("--list", action="store_true",
                        help="print every live reservation and its holder")
    parser.add_argument("--bead", metavar="ID",
                        help="bead the reservation is for, recorded so a "
                             "collision names the branch that holds the number")
    parser.add_argument("--max-age-days", type=int, default=DEFAULT_MAX_AGE_DAYS,
                        help="a reservation older than this, or one whose "
                             f"branch is gone, is stale (default "
                             f"{DEFAULT_MAX_AGE_DAYS})")
    args = parser.parse_args(argv)

    if sum(bool(flag) for flag in (args.check, args.reserve,
                                   args.release is not None, args.list)) > 1:
        parser.error("--check, --reserve, --release and --list are exclusive")

    str_repo = repository_root()
    base_ref = args.base or resolve_base_ref(str_repo)

    if args.list:
        branch = current_branch(str_repo)
        for number, held in sorted(holders(str_repo, args.max_age_days).items()):
            print(f"{number:04d}\t{held.get('branch')}\t"
                  f"{held.get('bead') or '-'}\t{held.get('base') or '-'}\t"
                  f"{held.get('reserved_at')}"
                  f"{'  (this branch)' if held.get('branch') == branch else ''}")
        return 0

    if args.release is not None:
        numbers = [args.release.strip()] if args.release.strip() else None
        return release_number(str_repo, numbers, current_branch(str_repo),
                              args.max_age_days)

    claimed = claimed_numbers(str_repo, base_ref)

    if args.reserve:
        print(f"{reserve_number(str_repo, claimed, base_ref, args.bead,
                               args.max_age_days):04d}")
        return 0

    if args.check is None:
        print(f"{next_free_number(claimed):04d}")
        return 0

    # Batched because the numbering suite asks about every record in the tree
    # at once, and one process per record is a minute of gate time.
    free = 0
    for argument in args.check:
        requested = argument.strip()
        if not re.fullmatch(r"\d{4}", requested):
            print(f"--check wants a four-digit number, got {argument!r}",
                  file=sys.stderr)
            return 2
        number = int(requested)
        if number in claimed:
            print(f"ADR-{requested} is already taken on {base_ref} or in the "
                  f"working tree; take {next_free_number(claimed):04d} instead.")
            free = 1
            continue
        held = holder(str_repo, number, args.max_age_days)
        if held is not None and held.get("branch") != current_branch(str_repo):
            taken = held.get("branch")
            for_held = f" for {held['bead']}" if held.get("bead") else ""
            spare = reserve_number(str_repo, claimed, base_ref, args.bead,
                                   args.max_age_days)
            print(f"ADR-{requested} is reserved by branch {taken!r}{for_held}; "
                  f"take {spare:04d} instead.")
            free = 1
            continue
        print(requested)
    return free


if __name__ == "__main__":
    raise SystemExit(main())
