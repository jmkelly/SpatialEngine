#!/usr/bin/env python3
"""The release section of the changelog is generated from git (ADR-0173).

    python3 tools/changelog.py --check                    # this checkout
    python3 tools/changelog.py --range v0.3.0..HEAD --print
    python3 tools/changelog.py --range v0.3.0..HEAD --date 2026-10-01 --write
    python3 tools/changelog.py --verify-release 0.4.0
    python3 tools/changelog.py --root /some/where ...     # another tree (tests)

`docs/CHANGELOG.md` grew a `## [Unreleased]` section of 1038 lines that was
hand-merged from merge to merge by a rule no gate touched — 79 commits touched
it in the 17 days after `v0.3.0`, and 107 merges landed in that span. ADR-0148
relocated the file out of the repository root and deferred generating the
section, with the measurements for doing so, and left one question open: **what
committed input, identical on a feature branch and on `main`, can answer "what
shipped since the last release"?**

The answer is the one thing a merge writes down and nothing else does: **the
commits**. A work commit carries the bead id in a `Task:` trailer, the bead title
as its subject, the `ADR-NNNN` ids the change cites and the narrative written
for it; a merge commit carries `Merge <bead>: <title>`. That is a git-only
input — identical on a branch, on `main` and in a CI clone — unlike the bead
queue, which is gitignored, lives in the git common dir and is absent from
every CI clone.

**Generation is a release-time step and nothing else.** That is what makes it
usable on the merge path: a merge-scoped generated section would need a
`--write`, a follow-up commit and a red lane in between on every merge, which is
the cost ADR-0134 took off the merge path on purpose, and a check that fails
whenever the committed text differs from the generated text fails on every
merge for the same reason. So nothing is generated between releases. The release
checklist renders the section from `<previous tag>..<this tag>` and writes it
once, and the check every lane runs is the one that is true on every merge:

    **no `## [Unreleased]` heading is hand-maintained between releases.**

A hand-merge is what rotted, so a hand-merge is the finding — and unlike a
stale-generated-text comparison it cannot be red on a merge that did not
hand-merge anything. It reads one file, so it is milliseconds, needs no .NET
SDK, changes nothing and has no `--fix`; the lanes call it directly rather than
leaving it to the `tools/**`-only tooling suite, because a hand-merge is a
**docs** change (ADR-0146).

Two details make a range renderable at both ends of the release:

*   the commit that carries a release is named by a `Release: <version>` trailer
    rather than by an assumption that the tag sits on the tip, so rendering
    `v0.3.0..HEAD` before the tag and `v0.3.0..v0.4.0` after it give the same
    section;
*   `--verify-release` says *not judged* rather than failing when a tag is not
    in the clone it is reading, which is every default CI clone.
"""
from __future__ import annotations

import argparse
import re
import subprocess
import sys
from pathlib import Path

#: The one changelog, and the path `RELEASING.md` names (ADR-0148).
CHANGELOG_PATH = "docs/CHANGELOG.md"

#: Where `<Version>` is single-sourced.
BUILD_PROPS = "Directory.Build.props"

#: The trailer that names the bead a commit closed, written by every lane's
#: bead-protocol gate (ADR-0152). One bead is one entry, however many commits
#: landed it and whatever the merge commit carries as well.
TASK_TRAILER = re.compile(r"^Task:\s*(\S+)\s*$", re.MULTILINE)

#: The trailer that names the release a commit *is*, so the commit carrying a
#: release section is never an entry inside it.
RELEASE_TRAILER = re.compile(r"^Release:\s*(\S+)\s*$", re.MULTILINE)

#: A merge commit's subject, which is where a bead's full title lives when the
#: work commit's subject is only a fragment of it.
MERGE_SUBJECT = re.compile(r"^Merge\s+(\S+?):\s*(.+)$")

ADR_CITATION = re.compile(r"ADR-\d{4}")

RELEASE_HEADING = re.compile(r"^##\s+\[(\d+(?:\.\d+)*)\](?:\s*-\s*(\S+))?\s*$", re.MULTILINE)
UNRELEASED_HEADING = re.compile(r"^##\s+\[Unreleased\]\s*$", re.MULTILINE)
VERSION_PROPERTY = re.compile(r"<Version>\s*([^<]+?)\s*</Version>")


class GitUnavailable(RuntimeError):
    """The history this render needs is not in the tree being read."""


def read(path: Path) -> str:
    """The file's text, or empty for one that is absent or unreadable."""
    try:
        return path.read_text(encoding="utf-8")
    except (OSError, UnicodeDecodeError):
        return ""


def git(root: Path, *arguments: str) -> str:
    """Run git in a repository, raising `GitUnavailable` rather than crashing."""
    result = subprocess.run(
        ["git", "-C", str(root), *arguments],
        capture_output=True, text=True,
    )
    if result.returncode != 0:
        raise GitUnavailable(result.stderr.strip() or f"git {' '.join(arguments)} failed")
    return result.stdout


def product_version(root: Path) -> str | None:
    """The `<Version>` in `Directory.Build.props`, single-sourced (ADR-0148)."""
    match = VERSION_PROPERTY.search(read(root / BUILD_PROPS))
    return match.group(1) if match else None


def entries(root: Path, revision_range: str) -> list[dict]:
    """One record per commit in the range, newest first.

    Records are separated by `\\x1e` and their fields by `\\x1f` rather than by
    newlines, because the field this reads is a commit body: a body is prose
    with blank lines in it, and a line-oriented format would need the reader to
    know which lines are the subject.
    """
    output = git(root, "log", "--format=%x1e%H%x1f%s%x1f%b", revision_range)
    records = []
    for chunk in output.split("\x1e"):
        chunk = chunk.strip("\n")
        if not chunk:
            continue
        fields = chunk.split("\x1f")
        if len(fields) != 3:
            continue
        records.append({"hash": fields[0].strip(), "subject": fields[1], "body": fields[2]})
    return records


def body_without_trailers(body: str, patterns: tuple[re.Pattern[str], ...]) -> str:
    """The narrative part of a commit body: no trailer block, no comment noise.

    Git keeps trailers in the last paragraph, so the trailer block is cut at the
    last blank line that is followed only by trailer-shaped lines, and the
    remaining prose is dedented and trimmed.
    """
    paragraphs = body.strip().split("\n\n")
    while paragraphs and any(
        pattern.search(line) for line in paragraphs[-1].splitlines() for pattern in patterns
    ):
        paragraphs.pop()
    text = "\n\n".join(paragraphs).strip()
    return "\n".join(line.strip() for line in text.splitlines()).strip()


def bead_id(record: dict) -> str | None:
    """The bead a commit closed, from its `Task:` trailer or its merge subject.

    The trailer is the gate's contract (ADR-0152) and every merge carries it
    today; the subject is the fallback for the merges that landed before the
    gate existed, because a `Merge SpatialEngine-x: …` commit names its bead
    just as plainly and a release note that lists it twice is worse than one
    that reads the subject.
    """
    match = TASK_TRAILER.search(record["body"])
    if match:
        return match.group(1)
    merged = merge_title(record)
    return merged[0] if merged else None


def is_release_commit(record: dict) -> bool:
    """Whether the commit *is* a release rather than a change that shipped."""
    return RELEASE_TRAILER.search(record["body"]) is not None


def merge_title(record: dict) -> tuple[str, str] | None:
    """The bead id and full title a merge commit's subject carries."""
    match = MERGE_SUBJECT.match(record["subject"])
    return (match.group(1), match.group(2).strip()) if match else None


def titles(records: list[dict]) -> dict[str, str]:
    """The bead title per bead, from the merge subject where there is one.

    A work commit's subject is the bead title truncated to a line often enough
    (`Krovak stays out on its axes: refuse a projected definition that declares
    any (ADR-0170)`), so the merge subject — which `tools/bd-merge-bead.py`
    writes as `Merge <bead>: <title>` — wins, and the work subject is the
    fallback for a bead that never went through a merge commit.
    """
    chosen: dict[str, str] = {}
    for record in records:
        identifier = bead_id(record)
        if identifier is None:
            continue
        merged = merge_title(record)
        if merged is not None:
            chosen[identifier] = merged[1]
        else:
            chosen.setdefault(identifier, record["subject"].strip())
    return chosen


def render_entry(identifier: str | None, title: str, prose: str, citations: list[str]) -> str:
    """One changelog entry: the title, its provenance, and the narrative.

    The provenance is the bead id and the `ADR-NNNN` ids the change's own
    commits cite, because those are the two things a reader of a release note
    follows and neither is derivable from the prose. The narrative is indented
    under the bullet so the entry stays one list item in rendered markdown.
    """
    mark = f" ({', '.join([identifier, *citations])})" if identifier else ""
    entry = f"- **{title}**{mark}"
    if prose:
        indented = "\n".join(
            f"  {line}" if line else "" for line in prose.splitlines()
        )
        entry = f"{entry}\n\n{indented}"
    return entry


def render_release_section(root: Path, revision_range: str, version: str, date: str) -> str:
    """The `## [x.y.z] - date` section for a range, or empty for an empty one.

    One entry per bead, newest first, in `git log` order — the order a reader
    wants a release's changes in, and the order they landed in.
    """
    records = entries(root, revision_range)
    bead_titles = titles(records)

    # Prose is collected first so a bead's entries can be written in the order
    # the *bead* landed — `git log` order, newest first — rather than in the
    # order its individual commits happen to come back.
    bodies: dict[str, list[str]] = {}
    for record in records:
        identifier = bead_id(record)
        if identifier is None or is_release_commit(record):
            continue
        prose = body_without_trailers(record["body"], (TASK_TRAILER, RELEASE_TRAILER))
        if prose:
            bodies.setdefault(identifier, []).append(prose)
    # `git log` reads newest first; a bead's narrative reads in the order it was
    # written, so each bead's own prose is reversed back.
    bodies = {identifier: list(reversed(prose)) for identifier, prose in bodies.items()}

    rendered: list[str] = []
    claimed: set[str] = set()
    for record in records:
        if is_release_commit(record):
            continue
        identifier = bead_id(record)
        if identifier is None:
            # A commit with no bead of its own still shipped, so it is still an
            # entry — with its hash, because nothing else names it.
            prose = body_without_trailers(record["body"], (TASK_TRAILER, RELEASE_TRAILER))
            rendered.append(render_entry(None, f"{record['subject'].strip()} "
                                              f"({record['hash'][:7]})", prose, []))
            continue
        if identifier in claimed:
            continue
        claimed.add(identifier)
        prose = "\n\n".join(bodies.get(identifier, []))
        citations = sorted(set(ADR_CITATION.findall(record["body"] + "\n" + prose)))
        rendered.append(render_entry(identifier, bead_titles.get(identifier, record["subject"].strip()),
                                     prose, citations))

    if not rendered:
        return ""
    body = "\n\n".join(rendered)
    return f"## [{version}] - {date}\n\n{body}\n"


def released_versions(root: Path) -> list[tuple[str, str]]:
    """The `(version, date)` of every release heading in the changelog, in order."""
    return [(version, date or "") for version, date in RELEASE_HEADING.findall(read(root / CHANGELOG_PATH))]


def section_of(root: Path, version: str) -> str | None:
    """The committed text of one release section, or `None` if it is not there."""
    match = re.search(rf"^##\s+\[{re.escape(version)}\].*?(?=^##\s|\Z)",
                      read(root / CHANGELOG_PATH), flags=re.MULTILINE | re.DOTALL)
    return match.group(0) if match else None


def strip_unreleased(text: str) -> str:
    """Drop a hand-merged `## [Unreleased]` section whole, heading and prose.

    This is the transition the record exists for: a release step run on a tree
    that still carries the old section must not leave it behind as a second
    answer to "what shipped since the last release".
    """
    return re.sub(r"^##\s+\[Unreleased\]\s*$.*?(?=^##\s|\Z)", "", text,
                  flags=re.MULTILINE | re.DOTALL)


def insert_section(text: str, section: str, version: str) -> str:
    """Put `section` above the newest release heading, replacing one of its own."""
    existing = re.search(rf"^##\s+\[{re.escape(version)}\].*?(?=^##\s|\Z)", text,
                         flags=re.MULTILINE | re.DOTALL)
    if existing:
        return text[:existing.start()] + section + "\n" + text[existing.end():]
    match = RELEASE_HEADING.search(text)
    if match:
        return text[:match.start()] + section + "\n" + text[match.start():]
    return text.rstrip("\n") + "\n\n" + section


def write_release_section(root: Path, revision_range: str, version: str, date: str) -> str:
    """Render the range and write it into the changelog. Returns what it wrote."""
    section = render_release_section(root, revision_range, version, date)
    if not section:
        raise GitUnavailable(
            f"{revision_range} holds nothing to release: no section was written, so the "
            "changelog is unchanged rather than carrying an empty release")
    path = root / CHANGELOG_PATH
    path.write_text(
        insert_section(strip_unreleased(read(path)), section, version), encoding="utf-8")
    return section


def verify_release(root: Path, version: str) -> list[str]:
    """Whether a committed release section is the one the history generates.

    Empty when the tags it needs are not in this clone: a release-time check
    that failed on a shallow clone would be a false red on the job that follows
    every release, and *not judged* is the answer ADR-0148 §5 gives the bead
    queue for the same reason.
    """
    sections = released_versions(root)
    versions = [released for released, _ in sections]
    if version not in versions:
        return [f"{CHANGELOG_PATH}: no `## [{version}]` section to verify"]
    index = versions.index(version)
    previous, date = sections[index]
    try:
        start = f"v{previous}" if index > 0 else git(root, "rev-list", "--max-parents=0", "HEAD").strip()
        # `v<previous>..v<version>` is the range the release wrote, which
        # excludes the commit carrying the section: it is named by its
        # `Release:` trailer and is never an entry inside itself. The date is
        # read back out of the committed heading, because it is the release's
        # to state rather than this check's.
        revision_range = f"{start}..v{version}"
        generated = render_release_section(root, revision_range, version, date)
    except GitUnavailable:
        return []
    committed = (section_of(root, version) or "").rstrip("\n")
    expected = generated.rstrip("\n")
    if committed == expected:
        return []
    return [f"{CHANGELOG_PATH}: the `## [{version}]` section is not the one "
            f"`tools/changelog.py` generates from {revision_range}; regenerate it "
            "with `python3 tools/changelog.py --range <previous>..v<version> --write`"]


def findings(root: Path) -> list[str]:
    """Every way the changelog can go back to being hand-merged."""
    reported: list[str] = []
    text = read(root / CHANGELOG_PATH)
    for match in UNRELEASED_HEADING.finditer(text):
        line = text[:match.start()].count("\n") + 1
        reported.append(
            f"{CHANGELOG_PATH}:{line}: an `## [Unreleased]` section is hand-merged "
            "from merge to merge by a rule no gate touched — 1038 lines of it, "
            "hand-edited across 107 merges. A release section is generated once, "
            "by `python3 tools/changelog.py --range <previous>..HEAD --date "
            "<date> --write` (`RELEASING.md` step 3), from the `Task:` trailers "
            "and the narrative every work commit already carries. Between "
            "releases there is nothing to hand-merge: `git log v<previous>..HEAD` "
            "answers what shipped (ADR-0173)"
        )
    return reported


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--root", default=".", help="repository root (default: .)")
    parser.add_argument("--range", dest="revision_range",
                        help="the commits a release section is rendered from, e.g. v0.3.0..HEAD")
    parser.add_argument("--date", default="", help="the release date, e.g. 2026-10-01")
    parser.add_argument("--check", action="store_true",
                        help="fail on a hand-merged `## [Unreleased]` section (the lanes' check)")
    parser.add_argument("--print", dest="print_only", action="store_true",
                        help="print the rendered section and write nothing")
    parser.add_argument("--write", action="store_true",
                        help="write the rendered section into the changelog (`RELEASING.md` step 3)")
    parser.add_argument("--verify-release", metavar="VERSION",
                        help="check that a committed release section is the generated one")
    arguments = parser.parse_args(argv)
    root = Path(arguments.root).resolve()

    if arguments.verify_release:
        reported = verify_release(root, arguments.verify_release)
        if reported:
            print("changelog:", file=sys.stderr)
            for finding in reported:
                print(f"  {finding}", file=sys.stderr)
            return 1
        print(f"changelog: the {arguments.verify_release} section is the generated one, "
              "or the tags to check it with are not in this clone")
        return 0

    if arguments.check:
        reported = findings(root)
        if reported:
            print(f"changelog: {len(reported)} finding(s):", file=sys.stderr)
            for finding in reported:
                print(f"  {finding}", file=sys.stderr)
            return 1
        print("changelog: nothing hand-merges the release section")
        return 0

    if not arguments.revision_range:
        parser.error("--range is required to render a release section")
    version = product_version(root)
    if version is None:
        print(f"changelog: no <Version> in {BUILD_PROPS} to head the section with; "
              "`RELEASING.md` step 2 is the bump", file=sys.stderr)
        return 1
    try:
        if arguments.write:
            section = write_release_section(root, arguments.revision_range, version, arguments.date)
        else:
            section = render_release_section(root, arguments.revision_range, version, arguments.date)
    except GitUnavailable as unavailable:
        print(f"changelog: {unavailable}", file=sys.stderr)
        return 1
    if not section:
        print(f"changelog: {arguments.revision_range} holds nothing to release", file=sys.stderr)
        return 1
    if arguments.write:
        print(f"changelog: wrote the {version} section from {arguments.revision_range} "
              f"({len(section.splitlines())} lines) into {CHANGELOG_PATH}")
    if arguments.print_only or not arguments.write:
        print(section, end="" if section.endswith("\n") else "\n")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())