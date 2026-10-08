#!/usr/bin/env python3
"""The swarm's file-overlap lock is derived from the beads, not hand-listed.

    python3 tools/swarm_lock.py                     # the lock, over the queue
    python3 tools/swarm_lock.py --bead <id>         # may this bead start? (gate)
    python3 tools/swarm_lock.py read <id> [<id>…]   # the resolved read list
    python3 tools/swarm_lock.py metrics             # the four incidents, counted
    python3 tools/swarm_lock.py --beads <file.json> # a bead list, not the queue

`SpatialEngine-imz.6`. `eng/swarm-runbook.md` told the coordinator its
ordering rule in prose — "`.2` before `.16`, `.4` before `.21`" — for a rule
the repository already knew was incomplete: new overlapping beads appear every
run, which is exactly why recovery keeps being needed. A hand list is stale
the moment the next bead lands, and it fails silently, because the overlap
that was never written down is simply not scheduled against anything. Two of
the four incidents this bead counts are the visible half of that.

So the ordering is read out of what the beads say:

  * `mentioned_paths` finds the files in a bead's own title and description,
    and `paths_overlap` decides whether two of them are in the same place —
    a directory overlaps what is under it, a glob overlaps what it matches,
    and `and/or` is prose rather than a path;
  * `plan` answers the only question the coordinator actually has — may this
    bead start — and answers it with the bead it collides with and the files
    they share, so the skip is a decision rather than an instinct;
  * a bead labelled `human` holds nothing and is never spawnable: it is
    waiting on a maintainer, so no worker is on it and none may start, and a
    reserved file set for a bead that cannot run is what turns one human
    block into a swarm halt (SpatialEngine-u2x.64). It is still listed, as
    awaiting a maintainer, so a worker told `free` sees the file;
  * a bead that names no file is reported as *order this one by hand*: the
    lock cannot invent a file set, and saying so beats guessing.

Two more things the runbook was doing by hand, both in the same shape — an
index handed to each worker to resolve again, and an argument about how well
it is going:

  * `read_list` resolves a bead's ADR list **once**, from the citations the
    bead itself carries and the routing rows in
    `architecture/distilled/README.md`, and names the row it matched so a
    reader can see why. The ADR numbers come from that document, never from
    here, so a routing change lands by editing the routing table (ADR-0141
    generates and gates it). Cognition's "don't build multi-agents" is not
    stylistic: actions carry implicit decisions, conflicting decisions carry
    bad results. The register row and the resolved list are that principle
    implemented with files instead of messages — worker N+1 reads worker N's
    decision instead of re-deriving it.
  * `metrics` counts mis-dispatches, wrongly-reclaimed leases, non-trivial
    merge conflicts and WIP-preservation commits over a range. The first and
    the last are in git; the reclaim race is a point-in-time read through
    `tools/bed-safe-reclaim.py`, the same question `tools/beads_gate.py`
    already asks, so it is not answered twice. A lock that cannot be measured
    is a lock nobody can tell is working — and the runbook's cap is held at 4
    until these counts fall over a full drain cycle.

The queue is read through one seam, `run`, and only when `--beads` does not
supply the beads instead: it is local coordination state, absent from CI, and
`tools/test_no_real_queue.py` makes reaching the real one a property of the
whole tooling suite rather than a convention each call site has to remember.
"""
from __future__ import annotations

import argparse
import importlib.util
import json
import re
import subprocess
import sys
from dataclasses import dataclass, field
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parent.parent

#: Top-level directories a repository path may start at. A token is only read
#: as a path when it starts at one of these, so `and/or`, `here/or/there` and a
#: branch name are prose rather than files. The branch namespaces are here
#: rather than handled separately because every worker's notes carry its own
#: `bd/<id>` branch: read as a file, two beads naming the *same* branch shared
#: a file and the lock blocked work that touched nothing in common.
TOP_LEVEL = (".github", ".dependably", "architecture", "clients", "docs",
             "eng", "research", "src", "tests", "tools")

#: Extensions of a path that does not start at a top-level directory. Lower
#: case only, which is what keeps `ENG/VERIFY.SH` — a branch, spelled as one —
#: out of a repository whose files are lower case.
EXTENSION = re.compile(r"^[a-z][a-z0-9]*$")

#: One path-shaped token: a slash-separated run of word, dot, `@`, `*` and
#: `-` characters. The lookbehind stops a URL's `host/path.html` from reading
#: as a repository path, and the character class stops a token at the `:` of
#: `eng/verify.sh:412`, so no line-number or punctuation trimming is needed.
PATH_TOKEN = re.compile(r"(?<![\w.@*~/-])(?P<path>(?:[\w.@*-]+/)+[\w.@*-]*)")

#: `ADR-NNNN`, and the `ADR-0014/0019/0020` spelling a record uses for a
#: family. A bare four-digit number is not a citation: descriptions carry
#: counts and years.
ADR_CITATION = re.compile(r"\bADR-(\d{4})\b(?:/(\d{4}))*")
ADR_CONTINUED = re.compile(r"/(\d{4})")

#: Words too ordinary to route a bead on, kept out of the keyword match below.
ROUTING_STOPWORDS = frozenset({
    "about", "also", "does", "each", "from", "here", "into", "only", "over",
    "that", "there", "their", "them", "then", "this", "when", "which", "with",
})

#: The subject the coordinator writes when it caps a worker and preserves its
#: work — one of the four incidents `metrics` counts.
WIP_SUBJECT = re.compile(r"^\s*WIP:\s*preserve\b", re.IGNORECASE)


# --- the files a bead names ------------------------------------------------


def _is_path(candidate: str) -> bool:
    """Whether a slash-separated token is a repository path.

    Three things are not, and all three were measured on the real queue:
    a git range (`origin/main..main`, `bd/x...HEAD`), a branch namespace
    (`bd/<id>`, `origin/main`), and a prose list of bead id fragments
    (`imz.4/imz.9/u2x.59`). A path names a file that exists in the tree, so
    the test is a top-level directory, or an extension off one that is not a
    dotted id fragment.
    """
    if ".." in candidate:
        return False
    head, _, tail = candidate.partition("/")
    if head in TOP_LEVEL:
        return True
    if "." in head:
        return False  # `imz.4/imz.9`, `.10/.13`
    extension = tail.rsplit(".", 1)[-1] if "." in tail else ""
    return bool(EXTENSION.match(extension))


def mentioned_paths(text: str) -> list[str]:
    """The repository paths a bead's own text names, in the order it names them.

    Backslashes are folded to `/` first, because a description may quote the
    Windows spelling a `ProjectReference` carries (ADR-0134) and the two are
    the same file. Duplicates are kept — the caller decides what to do with a
    path named twice — and order is document order, so a reader can see which
    bullet named what.
    """
    if not text:
        return []
    found: list[str] = []
    for match in PATH_TOKEN.finditer(str(text).replace("\\", "/")):
        candidate = match.group("path").rstrip(".,;:")
        if not candidate or not _is_path(candidate):
            continue
        found.append(candidate)
    return found


def bead_files(record: dict) -> list[str]:
    """The files one bead says it will touch, deduplicated and sorted."""
    text = " ".join(str(record.get(key) or "")
                    for key in ("title", "description", "notes"))
    return sorted(set(mentioned_paths(text)))


def _glob(path: str) -> re.Pattern:
    """`path` as a pattern: `*` spans separators, a trailing `/` takes any tail."""
    pattern = re.sub(r"\\\*", ".*", re.escape(path))
    if path.endswith("/"):
        pattern += ".*"
    return re.compile(pattern)


def paths_overlap(left: str, right: str) -> bool:
    """Whether two named paths are in the same place.

    Matched in both directions, because only one of the two may be a
    directory or a glob: `src/**` covers `src/Spatial.Core/Point.cs` and a
    plain file covers nothing but itself. The trailing `/` is a path-prefix
    match at a segment boundary, so `src/Spatial.Core/` never matches
    `src/Spatial.CoreEx/`.
    """
    if left == right:
        return True
    return (_glob(left).fullmatch(right) is not None
            or _glob(right).fullmatch(left) is not None)


def shared_paths(left, right) -> list[str]:
    """The pairs of named paths that put two file sets in the same place."""
    return [(a, b) for a in left for b in right if paths_overlap(a, b)]


# --- the lock --------------------------------------------------------------


IN_FLIGHT_LABEL = "in-flight"

#: The coordinator's label for a bead awaiting a maintainer decision
#: (`eng/swarm-runbook.md`: never reclaim one, never spawn one). No agent
#: works it and no worker may take it, so its files are not occupied by the
#: swarm — see `is_in_flight`.
HUMAN_LABEL = "human"


def _labels(bead: dict) -> list[str]:
    labels = bead.get("labels") or []
    return [labels] if isinstance(labels, str) else list(labels)


def is_in_flight(bead: dict) -> bool:
    """Whether this bead is being worked right now.

    Two signals, because the coordinator uses both: `bd` marks a claim
    `in_progress`, and the coordinator also labels a bead `in-flight` and
    records the agent in its notes — a bead it has stopped, or one whose
    merge has not landed yet, is still holding its files.

    A closed bead holds nothing, whatever labels it carries: `bd list --all`
    is every bead the swarm ever ran and a label from its time is never taken
    off, which read 122 beads as in flight on the day this was written.

    A `human` bead holds nothing either, for the same kind of reason: it is
    waiting on a maintainer, so no worker is on it and none may start. Its
    claim is left in place deliberately — `tools/bd-safe-reclaim.py` will not
    take a maintainer decision back — but the claim was never occupancy, and
    reading it as occupancy reserved the whole file set of a bead that cannot
    run. On 2026-10-04 one such bead blocked all 13 ready beads for a seventh
    consecutive tick (SpatialEngine-u2x.64, on `SpatialEngine-u2x.63`). The
    lock reports such a bead as awaiting a maintainer rather than dropping it,
    so a worker told `free` still sees the file a maintainer holds.
    """
    if str(bead.get("status", "")).lower() == "closed":
        return False
    labels = _labels(bead)
    if HUMAN_LABEL in labels:
        return False
    return (str(bead.get("status", "")).lower() == "in_progress"
            or IN_FLIGHT_LABEL in labels)


@dataclass
class Verdict:
    """One ready bead's answer, and the evidence for it."""

    bead_id: str
    title: str
    files: list[str] = field(default_factory=list)
    blocked_by: list[str] = field(default_factory=list)
    shared: list[str] = field(default_factory=list)
    ordered_by_hand: bool = False

    @property
    def blocked(self) -> bool:
        return bool(self.blocked_by)

    def rank(self) -> int:
        """Report order: free, then blocked, then the ones the lock cannot judge."""
        if self.ordered_by_hand:
            return 2
        return 1 if self.blocked else 0


def plan(ready, in_flight) -> list[Verdict]:
    """Which ready bead may start, and which in-flight one stops it.

    `ready` keeps the coordinator's own ordering (epic children first, then
    everything else); `in_flight` is every bead being worked now. Both are
    bead dicts as `bd` prints them, so a rehearsal reads a JSON file and a
    test never reaches the queue.
    """
    flying_files = [(str(bead.get("id", "")), bead_files(bead))
                    for bead in in_flight if is_in_flight(bead)]
    verdicts: list[Verdict] = []
    for bead in ready:
        files = bead_files(bead)
        verdict = Verdict(bead_id=str(bead.get("id", "")),
                          title=str(bead.get("title", "")),
                          files=files,
                          ordered_by_hand=not files)
        for bead_id, other_files in flying_files:
            for pair in shared_paths(files, other_files):
                verdict.blocked_by.append(bead_id)
                if pair[0] not in verdict.shared:
                    verdict.shared.append(pair[0])
                break
        # Deduplicate the holders, keep their order: two in-flight beads on
        # one file is one reason to skip and two names worth reporting.
        verdict.blocked_by = list(dict.fromkeys(verdict.blocked_by))
        verdicts.append(verdict)
    return sorted(verdicts, key=Verdict.rank)


# --- the resolved read list ------------------------------------------------


@dataclass
class Row:
    """One row of the routing table, as `architecture/distilled/README.md` states it."""

    task: str
    reads: list[str] = field(default_factory=list)
    adrs: list[str] = field(default_factory=list)
    matched_on: list[str] = field(default_factory=list)

    @property
    def matched(self) -> bool:
        return bool(self.matched_on)


@dataclass
class Read:
    """One resolved read: either what the bead cites, or a routing row."""

    adrs: list[str] = field(default_factory=list)
    reads: list[str] = field(default_factory=list)
    task: str = ""
    source: str = ""
    matched_on: list[str] = field(default_factory=list)

    @property
    def matched(self) -> bool:
        return bool(self.matched_on)


#: The routing table's own heading, so the parse stops at the section rather
#: than at the first line that happens to look like a table row.
ROUTING_HEADING = re.compile(r"^#+\s*Route by task\s*$", re.MULTILINE)

#: `0014–0019`: the range spelling the routing table uses, in either dash.
ADR_RANGE = re.compile(r"(\d{4})\s*[–—-]\s*(\d{4})")
#: A digest or reference the Read column names.
READ_NAME = re.compile(r"[\w./-]+\.md")


def _cell_adrs(cell: str) -> list[str]:
    """`0035, 0037, 0048` and `0014–0019` as `ADR-NNNN`, in the order written."""
    text = cell.replace("ADR-", "")
    numbers: list[str] = []
    ranges = [(int(a), int(b)) for a, b in ADR_RANGE.findall(text)]
    for start, end in ranges:
        numbers += [f"{n:04d}" for n in range(start, min(end, start + 64) + 1)]
    stripped = ADR_RANGE.sub(" ", text)
    for token in re.findall(r"\d{4}", stripped):
        numbers.append(token)
    seen: list[str] = []
    for number in numbers:
        adr = f"ADR-{number}"
        if adr not in seen:
            seen.append(adr)
    return seen


def parse_routing(markdown: str) -> list[Row]:
    """The routing table out of the distilled index, ADR numbers included.

    Read from the document rather than restated here, so a routing change
    lands by editing the bead text it was read from
    and every lane gates (ADR-0141). Two answer to "what does this task read"
    is one too many, and the one nobody runs is the one that ships.
    """
    heading = ROUTING_HEADING.search(markdown or "")
    if heading is None:
        return []
    rows: list[Row] = []
    for line in markdown[heading.end():].splitlines():
        if line.startswith("#"):
            break
        if not line.lstrip().startswith("|"):
            continue
        cells = [cell.strip() for cell in line.strip().strip("|").split("|")]
        if len(cells) < 3:
            continue
        task, reads_cell, adrs_cell = cells[0], cells[1], cells[2]
        if not re.search(r"\w", task) or set(task) <= set("- "):
            continue  # the separator row
        rows.append(Row(task=task,
                        reads=list(dict.fromkeys(READ_NAME.findall(reads_cell))),
                        adrs=_cell_adrs(adrs_cell)))
    return rows


def cited_adrs(bead: dict) -> list[str]:
    """The records a bead's own text cites, in the order it cites them."""
    text = " ".join(str(bead.get(key) or "")
                    for key in ("title", "description", "notes"))
    cited: list[str] = []
    for match in ADR_CITATION.finditer(text):
        for number in [match.group(1)] + ADR_CONTINUED.findall(match.group(0)):
            adr = f"ADR-{number}"
            if adr not in cited:
                cited.append(adr)
    return cited


def _keywords(text: str) -> list[str]:
    return [word for word in re.findall(r"[a-z]{4,}", (text or "").lower())
            if word not in ROUTING_STOPWORDS]


def match_routing(bead: dict, rows) -> list[Read]:
    """The routing rows this bead's own title and labels point at.

    Keyword-matched, and deliberately so: the rows are prose and the bead is
    prose, and a mechanical reading of one against the other is a *routing
    aid* rather than an authority — which is why every match names the row and
    the words that selected it, so a reader can see a wrong one immediately.
    A bead that matches nothing is reported as matching nothing rather than
    given the whole table.
    """
    text = " ".join(str(bead.get(key) or "") for key in ("title",))
    labels = bead.get("labels") or []
    text += " " + " ".join(labels) if isinstance(labels, list) else " " + str(labels)
    haystack = set(_keywords(text))
    matched: list[Read] = []
    for row in rows:
        hits = [word for word in _keywords(row.task) if word in haystack]
        if not hits:
            continue
        matched.append(Read(adrs=list(row.adrs), reads=list(row.reads),
                            task=row.task,
                            source="architecture/distilled/README.md",
                            matched_on=hits))
    return matched


def read_list(bead: dict, rows) -> list[Read]:
    """The whole resolved list for one bead: its own citations, then routing.

    The bead's own citations lead because they are exact — a record the bead
    names is a record it depends on — and the routing rows follow as the
    places to look when the citations do not answer it. That order is what
    makes this a *resolved* list rather than an index: the worker is handed
    the ADRs, not the table to find them in.
    """
    resolved: list[Read] = []
    cited = cited_adrs(bead)
    if cited:
        resolved.append(Read(adrs=cited, source="cited by the bead"))
    resolved += match_routing(bead, rows)
    return resolved


# --- the four incidents, counted -------------------------------------------


def _load_sibling(filename: str, module_name: str):
    """A `tools/*.py` next to this one, loaded the way beads_gate loads them."""
    path = Path(__file__).resolve().parent / filename
    spec = importlib.util.spec_from_file_location(module_name, path)
    if spec is None or spec.loader is None:
        raise RuntimeError(f"{path}: cannot load")
    module = importlib.util.module_from_spec(spec)
    sys.modules[spec.name] = module
    spec.loader.exec_module(module)
    return module


def metrics(root: Path, since: str | None = None, gate=None) -> dict:
    """Mis-dispatches and WIP-preservation commits over a range.

    `since` is a revision the range starts at (`None` reads all history).
    The mis-dispatch count reuses `tools/beads_gate.py`'s own readers for a
    merge subject and for the `Task:` trailers on the work it merged, so
    "which bead did this merge actually do" has one answer here rather than a
    second one to drift from the gate that fails on it.

    The reclaim race is not in git — it is a point-in-time pairing of the
    queue against the live agents — so it is read through the same
    `bd-safe-reclaim.py` question `beads_gate.py` asks and is reported as
    *not measured* unless `gate` (a loaded `beads_gate` module) and the queue
    are both supplied. A number this tool cannot compute is not printed as a
    zero.
    """
    beads_gate = gate or _load_sibling("beads_gate.py", "beads_gate")
    revision = "HEAD" if since is None else f"{since}..HEAD"
    commits = beads_gate.walk(root, revision=revision)
    merges = [commit for commit in commits
              if beads_gate.merge_bead(commit.subject)]

    wip = [commit for commit in commits if WIP_SUBJECT.search(commit.subject)]
    mis_dispatch = []
    for commit in merges:
        bead_id = beads_gate.merge_bead(commit.subject)
        named = beads_gate.work_beads(root, commit.sha)
        # A merge whose work names no bead at all is beads_gate's finding
        # (a missing trailer), not this one's: counting it here would read
        # one incident as two.
        if named and bead_id not in named:
            mis_dispatch.append(bead_id)

    return {
        "range": "all history" if since is None else f"{since}..HEAD",
        "commits": len(commits),
        "merges": len(merges),
        "wip_preservation": len(wip),
        "wip_commits": [commit.subject for commit in wip],
        "mis_dispatch": mis_dispatch,
        "reclaim_race": "not measured here: a point-in-time pairing of the "
                        "queue against the live agents, read by "
                        "tools/beads_gate.py (which runs on every lane)",
    }


# --- the queue -------------------------------------------------------------


class Completed:
    """The slice of `subprocess.CompletedProcess` this tool reads."""

    def __init__(self, returncode, stdout, stderr):
        self.returncode = returncode
        self.stdout = stdout
        self.stderr = stderr


def _default_run(cmd, cwd=None):
    return subprocess.run(cmd, capture_output=True, text=True, cwd=cwd)


def _bd_json(run, command, db, what):
    command = ["bd"] + list(command)
    if db:
        command += ["--db", db]
    done = run(command)
    if done.returncode != 0:
        detail = (done.stderr or done.stdout or "").strip()
        raise RuntimeError(f"{what} failed: {detail or done.returncode}")
    try:
        return json.loads(done.stdout or "[]")
    except json.JSONDecodeError as error:
        raise RuntimeError(f"{what} printed no JSON ({error})")


def load_beads(run, db=None, beads_file=None) -> list[dict]:
    """Every bead the lock reads: the whole queue, or one file's worth of it.

    One `bd list --all` rather than `bd ready` plus `bd list`: readiness is
    the coordinator's ordering and the lock only needs to know which beads
    exist and which of them are being worked, so a second call would be a
    second answer to one question. `--beads` supplies a list instead, which is
    how a rehearsal and a test read this without the queue
    (`tools/test_no_real_queue.py`).
    """
    if beads_file:
        return json.loads(Path(beads_file).read_text(encoding="utf-8"))
    return _bd_json(run, ["list", "--all", "--json", "-n", "0"], db, "bd list")


# --- the command -----------------------------------------------------------


def _verdict_dict(verdict: Verdict) -> dict:
    return {"id": verdict.bead_id, "title": verdict.title,
            "files": verdict.files, "blocked": verdict.blocked,
            "blocked_by": verdict.blocked_by, "shared": verdict.shared,
            "ordered_by_hand": verdict.ordered_by_hand}


def _report(verdicts, flying, awaiting, out) -> None:
    say = lambda line: print(line, file=out)
    say(f"swarm-lock: {len(flying)} bead(s) in flight, "
        f"{len(verdicts)} ready")
    for bead_id, files in flying:
        say(f"  in flight  {bead_id}  {', '.join(files) if files else '(no file named)'}")
    for bead_id, files in awaiting:
        say(f"  maintainer {bead_id}  {', '.join(files) if files else '(no file named)'} "
            "(labelled human: holds nothing here, but a maintainer is editing it)")
    for verdict in verdicts:
        if verdict.ordered_by_hand:
            say(f"  by hand    {verdict.bead_id}  {verdict.title} "
                "(names no file: the lock cannot judge it)")
        elif verdict.blocked:
            say(f"  BLOCKED    {verdict.bead_id}  {verdict.title}")
            say(f"      overlaps {', '.join(verdict.blocked_by)} on "
                f"{', '.join(verdict.shared)}")
        else:
            say(f"  free       {verdict.bead_id}  {verdict.title}  "
                f"[{', '.join(verdict.files)}]")


def _print_read(bead_id, resolved, out) -> None:
    say = lambda line: print(line, file=out)
    say(f"swarm-lock: resolved read list for {bead_id}")
    for entry in resolved:
        adrs = ", ".join(entry.adrs) if entry.adrs else "no ADR named"
        reads = f"; read {', '.join(entry.reads)}" if entry.reads else ""
        if entry.matched_on:
            say(f"  {entry.source}: {entry.task} -> {adrs}{reads} "
                f"(matched: {', '.join(entry.matched_on)})")
        else:
            say(f"  {entry.source}: {adrs}{reads}")
    if not resolved:
        say("  nothing resolved: this bead names no ADR and its title matches "
            "no routing row — read architecture/distilled/README.md by hand")


def _print_metrics(counts, out) -> None:
    say = lambda line: print(line, file=out)
    say(f"swarm-lock: incidents over {counts['range']}")
    say(f"  merges: {counts['merges']}")
    say(f"  wip_preservation: {counts['wip_preservation']}")
    for subject in counts["wip_commits"]:
        say(f"      {subject}")
    say(f"  mis_dispatch: {len(counts['mis_dispatch'])}")
    for subject in counts["mis_dispatch"]:
        say(f"      {subject}")
    say(f"  reclaim_race: {counts['reclaim_race']}")
    say("  merge_conflicts: not derivable from git; count them in the tick "
        "report (bd-merge-bead.py aborts a rebase conflict rather than "
        "resolving one, and the tick report is where that surfaces)")


def main(argv=None, run=None, out=None) -> int:
    argv = list(sys.argv[1:] if argv is None else argv)
    run = run or _default_run
    out = out or sys.stdout

    mode = "lock"
    if argv and argv[0] in ("read", "metrics"):
        mode, argv = argv[0], argv[1:]

    parser = argparse.ArgumentParser(
        prog="swarm-lock", description=__doc__.splitlines()[0])
    parser.add_argument("ids", nargs="*", metavar="BEAD",
                        help="bead ids (read mode)")
    parser.add_argument("--root", default=str(REPO_ROOT),
                        help="repository root (default: this repository)")
    parser.add_argument("--beads", dest="beads_file", metavar="FILE",
                        help="read the bead list from this JSON file instead "
                             "of the queue")
    parser.add_argument("--db", metavar="PATH",
                        help="a different beads queue (a rehearsal, a test)")
    parser.add_argument("--bead", metavar="ID",
                        help="gate on this one bead: exit 1 when it is blocked")
    parser.add_argument("--base", metavar="REF",
                        help="metrics: start the range here (default: all "
                             "history)")
    parser.add_argument("--json", action="store_true",
                        help="machine-readable output")
    parser.add_argument("--plan", action="store_true",
                        help="print what would be read, and read nothing")
    args = parser.parse_args(argv)
    root = Path(args.root).resolve()

    if args.plan:
        print(f"swarm-lock: would read every bead's own title and description, "
              f"and report which ready bead shares a file with one in flight "
              f"(a bead labelled human holds nothing, and is listed as awaiting "
              f"a maintainer instead)")
        if mode == "read":
            print("swarm-lock: would resolve each named bead's ADR list from "
                  "its citations and the routing rows it matches")
        if mode == "metrics":
            print(f"swarm-lock: would count mis-dispatches and WIP commits "
                  f"over {args.base or 'all history'}")
        return 0

    if mode == "metrics":
        counts = metrics(root, since=args.base)
        if args.json:
            print(json.dumps(counts, indent=2, sort_keys=True), file=out)
        else:
            _print_metrics(counts, out)
        return 0

    routing: list[Row] = []
    if mode == "read":
        readme = root / "architecture" / "distilled" / "README.md"
        # A root with no routing table resolves to the bead's own citations
        # and says so, rather than raising: a rehearsal or a scratch
        # directory is a legitimate place to run this from.
        if readme.exists():
            routing = parse_routing(readme.read_text(encoding="utf-8"))
        elif not args.json:
            print(f"swarm-lock: no routing table at {readme}; resolving from "
                  "the bead's own citations only", file=out)

    try:
        beads = load_beads(run, db=args.db, beads_file=args.beads_file)
    except (RuntimeError, OSError) as error:
        print(f"swarm-lock: {error}", file=sys.stderr)
        return 2
    by_id = {str(bead.get("id")): bead for bead in beads
             if isinstance(bead, dict)}

    if mode == "read":
        if not args.ids:
            print("swarm-lock: read takes at least one bead id", file=sys.stderr)
            return 2
        for bead_id in args.ids:
            record = by_id.get(bead_id)
            if record is None:
                print(f"swarm-lock: the queue has no bead {bead_id}",
                      file=sys.stderr)
                return 2
            if args.json:
                entries = [{"adrs": entry.adrs, "reads": entry.reads,
                            "task": entry.task, "source": entry.source,
                            "matched_on": entry.matched_on}
                           for entry in read_list(record, routing)]
                print(json.dumps({"id": bead_id, "read": entries}, sort_keys=True),
                      file=out)
            else:
                _print_read(bead_id, read_list(record, routing), out)
        return 0

    records = [bead for bead in beads if isinstance(bead, dict)]
    in_flight = [bead for bead in records if is_in_flight(bead)]
    flying = [(str(bead.get("id", "")), bead_files(bead)) for bead in in_flight]
    # A bead awaiting a maintainer holds nothing (see `is_in_flight`) and is
    # never spawnable either: `eng/swarm-runbook.md` tells the coordinator to
    # skip anything labelled `human`, so excluding it from occupancy must not
    # hand it out as ready.
    awaiting = [(str(bead.get("id", "")), bead_files(bead))
                for bead in records if HUMAN_LABEL in _labels(bead)
                and str(bead.get("status", "")).lower() != "closed"]
    ready = [bead for bead in records
             if not is_in_flight(bead)
             and HUMAN_LABEL not in _labels(bead)
             and str(bead.get("status", "")).lower() == "open"]
    verdicts = plan(ready, in_flight)

    if args.bead:
        verdicts = [verdict for verdict in verdicts
                    if verdict.bead_id == args.bead]
        if not verdicts:
            # Not ready (claimed, closed, or not in this list) is not blocked:
            # the lock has nothing to say about a bead that is not up.
            print(f"swarm-lock: {args.bead} is not in the ready set; the lock "
                  "does not judge it", file=out)
            return 0

    payload = {
        "in_flight": [{"id": bead_id, "files": files} for bead_id, files in flying],
        "awaiting_maintainer": [{"id": bead_id, "files": files}
                                for bead_id, files in awaiting],
        "verdicts": [_verdict_dict(verdict) for verdict in verdicts],
    }
    if args.json:
        print(json.dumps(payload, indent=2, sort_keys=True), file=out)
    else:
        _report(verdicts, flying, awaiting, out)
    return 1 if any(verdict.blocked for verdict in verdicts) else 0


if __name__ == "__main__":
    raise SystemExit(main())
