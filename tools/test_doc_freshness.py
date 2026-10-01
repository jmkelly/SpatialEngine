#!/usr/bin/env python3
"""The documentation corpus is audited, the way the code corpus already is.

Run: python3 -m unittest tools/test_doc_freshness.py

The gap this suite is the reproduction for (SpatialEngine-imz.2): the quality
loop audits code five ways — crap4dotnet, coupling metrics, coverage, Stryker,
warnings — and audits documentation zero ways. So the ADR register drifted 22
rows, 33 records carried a status a machine could not read, a SKILL.md pointed
at two files that do not exist, and nothing failed.

The tool under test is `tools/doc-freshness.py`, which writes the same two
artefacts every other audit writes (`doc-report.json`, `doc-queue.md`, both
gitignored) and aggregates into whatever aggregates the other `*-report.json`
files. It is **reporting-only** on every lane: the checks land as a queue
that drains, and only then are the cheap exact ones promoted (SpatialEngine-imz.2
§implementation notes). Three of them — the register row, the record's shape and
the dangling `ADR-NNNN` citation — are not implemented here at all: they are
read out of `tools/arch-index.py`, which every lane of `eng/verify.sh` already
gates on (ADR-0141), so there is one implementation and one answer.

A note on what this suite is *not* claiming: a green `doc-report.json` is not a
correctness improvement. The evidence is that context strategy does not move
pass rates (arXiv 2607.27250: <=10-15pp, equivalence-bounded); the win here is
stopping confidently-wrong answers and token cost (arXiv 2601.20404: -28.6%
median runtime, -16.6% output tokens).
"""
import importlib.util
import json
import os
import re
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parent.parent
TOOL = REPO_ROOT / "tools" / "doc-freshness.py"
ARCH_INDEX = REPO_ROOT / "tools" / "arch-index.py"


def _load(name: str, path: Path):
    spec = importlib.util.spec_from_file_location(name, path)
    module = importlib.util.module_from_spec(spec)
    sys.modules[name] = module
    spec.loader.exec_module(module)
    return module


arch_index = _load("arch_index", ARCH_INDEX)

if TOOL.is_file():
    doc_freshness = _load("doc_freshness", TOOL)
else:  # the tool is written after this suite, so the failure is legible
    doc_freshness = None

#: The checks the report queues as **findings** — the defect class ADR-0177
#: keeps: something a reader could be *wrong* about, and that an edit drains.
#: `shared` names the check `tools/arch-index.py` already owns and gates on, so
#: it is read rather than reimplemented (SpatialEngine-imz.2 acceptance).
CHECK_IDS = (
    "adr-register",
    "front-matter",
    "adr-shape",
    "adr-citation",
    "digest-staleness",
    "dead-doc-link",
    "skill-reference",
    "context-bloat",
    "instruction-conflict",
    "lint-leakage",
)

#: The checks the report carries as **signals** (ADR-0177): facts about the
#: corpus rather than defects. `init-fossil` is one commit, and the restatement
#: census is where the walls are written down — neither is drainable by editing
#: a document, so neither is a row in a queue that has to empty.
SIGNAL_IDS = ("init-fossil", "lint-restatement")

SHARED_IDS = ("adr-register", "front-matter", "adr-shape", "adr-citation")

#: A number at or above `SHAPE_FROM`, so a synthetic tree carries a record the
#: shape rule binds. Assembled from parts: a literal `ADR-NNNN` here would be a
#: citation the structural guard (`AdrNumberingTests`) reads.
SHAPE_NUMBER = "0" + "151"


# --- a synthetic tree ------------------------------------------------------


def write(path: Path, text: str) -> Path:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(text, encoding="utf-8")
    return path


def adr(number: str, title: str, *, status: str = "accepted", **extra: str) -> str:
    lines = [
        "---",
        f"status: {status}",
        "date: 2026-01-02",
        "deciders: someone",
        f"summary: {title}.",
    ]
    for key, value in extra.items():
        lines.append(f"{key}: {value}")
    lines += ["---", "", f"# ADR-{number}: {title}", "", f"Body about {title}.", ""]
    return "\n".join(lines)


def git(root: Path, *arguments: str, env: dict | None = None) -> str:
    """Run git in a synthetic tree, with an identity and no signing."""
    completed = subprocess.run(
        ["git", "-c", "user.name=t", "-c", "user.email=t@example.invalid",
         "-c", "commit.gpgsign=false", *arguments],
        cwd=root, capture_output=True, text=True, check=False,
        env={**(env or {}), "PATH": os.environ.get("PATH", "")},
    )
    if completed.returncode != 0:
        raise AssertionError(f"git {arguments} failed: {completed.stderr}")
    return completed.stdout


def git_commit(root: Path, message: str, day: int = 1) -> None:
    """A commit on a named day, so "after" means after and not "same second"."""
    when = f"2026-01-{day:02d}T00:00:00+00:00"
    git(root, "add", "-A")
    git(root, "commit", "-q", "-m", message,
        env={"GIT_AUTHOR_DATE": when, "GIT_COMMITTER_DATE": when})


def clean_tree(root: Path) -> None:
    """A tree that produces no finding from any of the ten checks.

    The three shared checks read the *generated* files, so a clean tree carries
    what the generator makes of it: the register spliced between the markers,
    the index and the breadcrumb rendered. Hand-writing those is how the drift
    happened in the first place.
    """
    write(root / "architecture" / "decisions" / "ADR-0001-a.md", adr("0001", "A thing"))
    # Written into every synthetic tree, at or above the shape boundary, so a
    # fixture that is not *about* the shape rule is inside it: a record with
    # the required sections, in order, and a narrative inside the budget.
    write(
        root / "architecture" / "decisions" / f"ADR-{SHAPE_NUMBER}-in-shape.md",
        adr(SHAPE_NUMBER, "In shape")
        + "## Context\n\nWhy.\n\n## Decision\n\n**The thing.**\n\n"
        + "## Consequences\n\nWhat follows.\n",
    )
    write(
        root / "architecture" / "decisions" / "ADR-0002-b.md", adr("0002", "B thing")
    )
    corpus = arch_index.load_corpus(root)
    block = arch_index.render_register(corpus)
    write(
        root / "architecture" / "distilled" / "README.md",
        f"# Distilled\n\n{block}\n"
        "See [the index](../decisions/README.md), [core](core.md) and ADR-0001.\n",
    )
    write(root / "architecture" / "distilled" / "core.md", "# Core\n\nGeometry.\n")
    write(root / "architecture" / "decisions" / "README.md", arch_index.render_index(corpus))
    write(root / "arch-index.md", arch_index.render_breadcrumb(corpus))
    write(
        root / "AGENTS.md",
        "# AGENTS\n\n" + "\n".join(f"line {n}" for n in range(20)) + "\n",
    )
    write(root / "eng" / "verify.sh", "#!/usr/bin/env bash\nexit 0\n")
    write(root / ".pi" / "skills" / "x" / "SKILL.md", "# x\n\nRun `eng/verify.sh`.\n")


class DocFreshnessTest(unittest.TestCase):
    def setUp(self):
        self._tmp = tempfile.TemporaryDirectory()
        self.root = Path(self._tmp.name)
        self.addCleanup(self._tmp.cleanup)

    # --- the tool exists at all -------------------------------------------

    def test_the_tool_under_test_exists(self):
        self.assertIsNotNone(
            doc_freshness, "tools/doc-freshness.py does not exist yet"
        )

    # --- a clean tree ------------------------------------------------------

    def test_a_clean_tree_reports_nothing(self):
        clean_tree(self.root)
        report = doc_freshness.audit(self.root)
        self.assertEqual([], report["findings"], json.dumps(report["findings"], indent=2))
        self.assertEqual(0, doc_freshness.main(["--root", str(self.root)]))

    def test_every_check_is_named_in_the_report(self):
        clean_tree(self.root)
        report = doc_freshness.audit(self.root)
        self.assertEqual(
            set(CHECK_IDS) | set(SIGNAL_IDS),
            {check["id"] for check in report["checks"]}
        )

    # --- each check fires on the defect it is named for --------------------

    def _dirty_tree(self) -> None:
        """A tree that trips every one of the checks at once."""
        clean_tree(self.root)
        # 1 + 2: a record with no register row and the retired `Status:` schema.
        write(
            self.root / "architecture" / "decisions" / "ADR-0003-c.md",
            "# ADR-0003: C thing\n\nStatus: accepted\n\nBody.\n",
        )
        # 3: a citation of a number no record carries.
        write(self.root / "README.md", "See ADR-0009 for the rule.\n")
        # 4: a digest older than the record it cites.
        write(
            self.root / "architecture" / "distilled" / "core.md",
            "Values live in Spatial.Core; ADR-0002 decides it.\n",
        )
        # 5: a relative pointer that resolves to nothing.
        write(
            self.root / "architecture" / "distilled" / "plugins.md",
            "See [the host](../docs/host.md) for the composition.\n",
        )
        # 6: a skill-relative path that resolves against neither the skill
        # directory nor the repo root, and a second claim about the gate.
        write(
            self.root / ".pi" / "skills" / "x" / "SKILL.md",
            "# x\n\nRead architecture/distilled/core.md, plugins.md, rendering.md.\n"
            "The gate is eng/verify.sh.\n",
        )
        # 7: context bloat, over the 200-line ceiling.
        write(
            self.root / "AGENTS.md",
            "# AGENTS\n\n" + "\n".join(f"line {n}" for n in range(260)) + "\n",
        )
        # 8: a hand-written doc with a single commit — the shape the check names
        # is a nested AGENTS.md a generation bead adds once and never touches.
        # (The fixture's example used to be a root-level `HANDOFF.md`, which
        # this bead deletes: the root carries no in-flight state, and the audit
        # no longer walks a document named `HANDOFF.md` there, ADR-0148.)
        write(self.root / "src" / "Spatial.Contracts" / "AGENTS.md",
              "# contracts\n\nTwo layers, one direction.\n")
        # 9: two docs naming the gate with different commands, and a doc
        # *contradicting* a wall the architecture tests already fail on — the
        # defect shape of `lint-leakage` (ADR-0177); the restatement beside it
        # is a signal, not a finding, and the dirty tree carries one of each.
        write(
            self.root / "RELEASING.md",
            "The gate before done is eng/verify.sh --full, and no third-party "
            "type crosses a public contract. Spatial.Contracts references "
            "Npgsql, so a contract carries its own store types.\n",
        )
        # 10: a record outside the shape rule (ADR-0150) — a number at or above
        # `SHAPE_FROM`, a free-form heading, and a narrative over the budget.
        # Numbered last so the steps above keep the numbering they had before
        # the shape rule existed. This *replaces* the in-shape record
        # `clean_tree` wrote rather than adding a second record at that number:
        # two records claiming one number is a different defect, and the shape
        # rule is the one under test here.
        write(
            self.root / "architecture" / "decisions" / f"ADR-{SHAPE_NUMBER}-in-shape.md",
            adr(SHAPE_NUMBER, "Out of shape")
            + "\n## Merged from another branch\n\nProse.\n"
            + "\n## Alternatives\n\n" + ("word " * arch_index.NARRATIVE_BUDGET) + "\n",
        )

    def test_a_dirty_tree_reports_every_check(self):
        self._dirty_tree()
        report = doc_freshness.audit(
            self.root,
            commit_times={
                "architecture/distilled/core.md": 100,
                "architecture/decisions/ADR-0002-b.md": 200,
            },
            commit_counts={"src/Spatial.Contracts/AGENTS.md": 1, "AGENTS.md": 9},
        )
        fired = {finding["check"] for finding in report["findings"]}
        self.assertEqual(set(CHECK_IDS), fired, json.dumps(report["findings"], indent=2))
        signalled = {item["check"] for item in report["signals"]}
        self.assertEqual(set(SIGNAL_IDS), signalled, json.dumps(report["signals"], indent=2))

    # --- check 4: staleness is the decision changing, not the file --------

    def _git_tree(self) -> None:
        """A tree with a digest that cites ADR-0001, initialised and uncommitted."""
        clean_tree(self.root)
        write(
            self.root / "architecture" / "distilled" / "core.md",
            "# Core\n\nGeometry values are core; ADR-0001 decides it.\n",
        )
        git(self.root, "init", "-q", "-b", "main")

    def _git_tree_with_a_cited_record(self) -> None:
        """A committed tree whose digest cites a record it was written with."""
        self._git_tree()
        git_commit(self.root, "the digest and the record it cites", day=1)

    def _digest_staleness(self) -> list[dict]:
        report = doc_freshness.audit(self.root, commit_times={}, commit_counts={})
        return [f for f in report["findings"] if f["check"] == "digest-staleness"]

    def test_a_cited_record_whose_metadata_changed_is_not_a_stale_digest(self):
        # The whole of the 99 findings this bead drained: ADR-0141's generated
        # register added a `summary:` line to 60 records in one commit, so every
        # digest citing one read as stale while the decision it states had not
        # moved. Staleness is the *body* of the record changing after the
        # digest's last commit; the front matter is metadata about it.
        self._git_tree_with_a_cited_record()
        record = self.root / "architecture" / "decisions" / "ADR-0001-a.md"
        write(
            record,
            record.read_text(encoding="utf-8").replace(
                "summary: A thing.", "summary: A thing, in a routing clause."
            ),
        )
        git_commit(self.root, "the register's summary for ADR-0001", day=2)
        self.assertEqual(
            [], self._digest_staleness(),
            "a front-matter edit is not the decision changing, so it must not "
            "make every digest that cites the record stale",
        )

    def test_a_cited_record_whose_decision_changed_is_a_stale_digest(self):
        # The other half, so the exemption above is not the check switched off:
        # a digest that states a decision as it stood before the record changed
        # it is the confidently-wrong case this check exists for.
        self._git_tree_with_a_cited_record()
        record = self.root / "architecture" / "decisions" / "ADR-0001-a.md"
        write(
            record,
            record.read_text(encoding="utf-8").replace(
                "Body about A thing.", "Body about A thing, restated."
            ),
        )
        git_commit(self.root, "ADR-0001 decides something else now", day=2)
        stale = [f for f in self._digest_staleness() if f["file"].endswith("core.md")]
        self.assertTrue(stale, "a body change after the digest is not reported")
        self.assertIn("ADR-0001", stale[0]["message"])

    def test_a_digest_written_after_the_decision_changed_is_not_stale(self):
        self._git_tree_with_a_cited_record()
        record = self.root / "architecture" / "decisions" / "ADR-0001-a.md"
        write(
            record,
            record.read_text(encoding="utf-8").replace(
                "Body about A thing.", "Body about A thing, restated."
            ),
        )
        git_commit(self.root, "ADR-0001 decides something else now", day=2)
        write(
            self.root / "architecture" / "distilled" / "core.md",
            "# Core\n\nGeometry values are core; ADR-0001 now says otherwise.\n",
        )
        git_commit(self.root, "the digest follows the record", day=3)
        # Only `core.md` was restated, and only `core.md` is clean: the README
        # digest still cites ADR-0001 in the words it had before the change, so
        # it is stale for its own reason and the check says so.
        stale = [f for f in self._digest_staleness() if f["file"].endswith("core.md")]
        self.assertEqual(
            [], stale, "a digest restated after the record changed is still stale"
        )

    def test_a_record_migrated_to_the_front_matter_schema_is_not_stale(self):
        # ADR-0141 moved `Status: Accepted` out of 33 records' bodies and into
        # front matter in one commit. The decision in each is the decision it
        # was; a digest that cites one is not restating a superseded decision,
        # and 33 records times the digests that cite them is most of the queue
        # this check was reporting.
        self._git_tree()
        record = self.root / "architecture" / "decisions" / "ADR-0001-a.md"
        sections = (
            "## Context\n\nBody about A thing.\n\n## Decision\n\n"
            "**The thing.**\n\n## Consequences\n\nWhat follows.\n"
        )
        write(record, "# ADR-0001: A thing\n\nStatus: Accepted\n\n" + sections)
        git_commit(self.root, "the digest and the record as it was written", day=1)
        write(record, adr("0001", "A thing").replace(
            "Body about A thing.\n", "") + sections)
        git_commit(self.root, "ADR-0141's one metadata schema", day=2)
        stale = [f for f in self._digest_staleness() if f["file"].endswith("core.md")]
        self.assertEqual(
            [], stale,
            "moving a record's status into its front matter is a schema change, "
            "not a changed decision",
        )

    # --- check 9 reads the generated block as generated --------------------

    def _register_row(self, row: str) -> None:
        """One extra row inside the generated register block of the digest README."""
        path = self.root / "architecture" / "distilled" / "README.md"
        text = path.read_text(encoding="utf-8")
        marker = arch_index.REGISTER_END
        self.assertIn(marker, text, "the fixture has no register block to write into")
        path.write_text(
            text.replace(marker, row + "\n" + marker, 1), encoding="utf-8"
        )

    def test_a_generated_register_row_is_neither_a_gate_claim_nor_a_wall(self):
        # The register is rendered from the records' own summaries, so a row
        # that calls something the gate, or restates a gated wall, is the
        # record's sentence and the fix is the record — not the row, and not a
        # queue nobody can drain. The register block is the one generated
        # surface check 9 reads.
        clean_tree(self.root)
        self._register_row(
            "| 0150 | The gate is eng/verify.sh --full and third-party types "
            "never cross a public contract. |"
        )
        report = doc_freshness.audit(self.root, commit_times={}, commit_counts={})
        offending = [
            f for f in report["findings"] + report["signals"]
            if f["check"] in ("instruction-conflict", "lint-leakage",
                              "lint-restatement")
            and f["file"].endswith("distilled/README.md")
        ]
        self.assertEqual(
            [], offending,
            "a generated register row is read as authored prose: "
            + json.dumps(offending, indent=2),
        )

    def test_a_hand_written_row_beside_the_generated_one_is_still_reported(self):
        # ...and the exemption is the block, not the file.
        clean_tree(self.root)
        self._register_row("| 0150 | Nothing to see. |")
        path = self.root / "architecture" / "distilled" / "README.md"
        write(
            path,
            path.read_text(encoding="utf-8")
            + "\nThe gate is eng/verify.sh --full.\n",
        )
        write(
            self.root / "architecture" / "distilled" / "cli.md",
            "# CLI\n\nThe gate is eng/verify.sh --fast.\n",
        )
        report = doc_freshness.audit(self.root, commit_times={}, commit_counts={})
        self.assertTrue(
            [f for f in report["findings"]
             if f["check"] == "instruction-conflict"
             and f["file"].endswith("distilled/README.md")],
            "the hand-written line under the register block is not read at all",
        )

    def test_a_line_that_requires_a_run_is_not_a_gate_claim(self):
        # `Must run in a normal browser - Playwright (eng/workbench-e2e.sh)` is
        # a requirement about where the workbench runs. The check reads it as a
        # claim about *the gate* because the entry point is on the line, and
        # that is the false positive: the sentence never names a gate.
        clean_tree(self.root)
        write(
            self.root / "architecture" / "distilled" / "cli.md",
            "# CLI\n\nThe gate is eng/verify.sh --full.\n",
        )
        write(
            self.root / "architecture" / "distilled" / "plugins.md",
            "# Plugins\n\nMust run in a normal browser (eng/workbench-e2e.sh).\n",
        )
        report = doc_freshness.audit(self.root, commit_times={}, commit_counts={})
        self.assertEqual(
            [], [f for f in report["findings"] if f["check"] == "instruction-conflict"],
            "a requirement about a browser is read as a claim about the gate",
        )

    def test_a_gate_claim_is_still_reported_when_the_corpus_disagrees(self):
        clean_tree(self.root)
        write(
            self.root / "architecture" / "distilled" / "cli.md",
            "# CLI\n\nThe gate is eng/verify.sh --full.\n",
        )
        write(
            self.root / "architecture" / "distilled" / "plugins.md",
            "# Plugins\n\nThe gate is eng/verify.sh --fast.\n",
        )
        report = doc_freshness.audit(self.root, commit_times={}, commit_counts={})
        self.assertTrue(
            [f for f in report["findings"] if f["check"] == "instruction-conflict"]
        )

    # --- check 9, part two: a wall restated is a census, a wall denied is a
    # --- defect (SpatialEngine-3kk, ADR-0177) -----------------------------

    def test_a_restated_wall_is_a_signal_and_never_a_finding(self):
        # The six rows SpatialEngine-3kk inherited: every one is an accurate
        # restatement of a wall in a document whose job is to carry that wall
        # to the agent that must obey it. AGENTS.md's hard-walls section is
        # required by the repository's own instructions, so the row cannot be
        # drained by deleting prose. The check as written was a census of where
        # the walls are restated, reporting on a green corpus.
        clean_tree(self.root)
        write(
            self.root / "src" / "Spatial.Contracts" / "AGENTS.md",
            "# contracts\n\nPublic contracts carry only `Spatial.Core` types "
            "(ADR-0005). No third-party type crosses a contract.\n",
        )
        report = doc_freshness.audit(self.root, commit_times={}, commit_counts={})
        self.assertEqual(
            [], [f for f in report["findings"] if f["check"] == "lint-leakage"],
            "an accurate restatement of a gated wall is queued as a defect",
        )
        restated = [f for f in report["signals"] if f["check"] == "lint-restatement"]
        self.assertTrue(restated, "the restatement census is not reported at all")
        self.assertIn("ArchitectureGuardTests", restated[0]["message"])

    def test_a_doc_denying_a_gated_wall_is_the_defect_this_audit_finds(self):
        # The detectable shape the bead asks for: a doc that *contradicts* a
        # wall a gate already fails on is a reader who is confidently wrong,
        # which is the only defect class this audit claims to find.
        for line in (
            "The contract layer is Spatial.Contracts, and it references Npgsql.",
            "`Spatial.Core` depends on NetTopologySuite for its geometry types.",
            "Algorithms live in `Spatial.Core` beside the values.",
            "`Spatial.Contracts.csproj` takes a PackageReference for Serilog.",
        ):
            with self.subTest(line=line):
                clean_tree(self.root)
                write(self.root / "architecture" / "distilled" / "cli.md",
                      f"# CLI\n\n{line}\n")
                report = doc_freshness.audit(
                    self.root, commit_times={}, commit_counts={}
                )
                denied = [
                    f for f in report["findings"] if f["check"] == "lint-leakage"
                ]
                self.assertEqual(1, len(denied), f"not reported: {line}")
                self.assertIn("Spatial.Architecture.Tests", denied[0]["message"])

    def test_a_one_commit_doc_is_a_signal_rather_than_a_defect(self):
        # Question 2 of the same bead: is "one commit" a defect a revision
        # drains, or a maintenance signal? The four rows SpatialEngine-7o0
        # verified are all currently accurate, and the only fix on offer for an
        # accurate one is an edit manufactured to move a counter — the churn
        # treadmill 7o0 just removed from digest-staleness.
        clean_tree(self.root)
        report = doc_freshness.audit(
            self.root, commit_times={}, commit_counts={"AGENTS.md": 1}
        )
        self.assertEqual(
            [], [f for f in report["findings"] if f["check"] == "init-fossil"],
            "a doc with one commit is queued as a defect nothing but an edit drains",
        )
        fossils = [f for f in report["signals"] if f["check"] == "init-fossil"]
        self.assertEqual(1, len(fossils), "the maintenance signal is not reported")
        self.assertIn("one commit", fossils[0]["message"])

    def test_the_live_corpus_reports_the_census_and_denies_nothing(self):
        # The state this decision leaves the corpus in: the six restatements and
        # the four one-commit docs are signals, and no document in the
        # repository contradicts a gated wall.
        report = doc_freshness.audit(REPO_ROOT)
        self.assertEqual(
            [], [f for f in report["findings"] if f["check"] == "lint-leakage"],
            "a document contradicts a wall ArchitectureGuardTests fails on: "
            + json.dumps(
                [f for f in report["findings"] if f["check"] == "lint-leakage"],
                indent=2),
        )
        signalled = {item["check"] for item in report["signals"]}
        self.assertEqual(set(SIGNAL_IDS), signalled, json.dumps(report["signals"], indent=2))
        self.assertTrue(
            [f for f in report["signals"] if f["check"] == "lint-restatement"],
            "the six restatements were dropped rather than reported as a census",
        )

    def test_the_queue_says_which_half_is_drainable(self):
        clean_tree(self.root)
        write(
            self.root / "src" / "Spatial.Contracts" / "AGENTS.md",
            "# contracts\n\nPublic contracts carry only `Spatial.Core` types.\n",
        )
        report = doc_freshness.audit(
            self.root, commit_times={}, commit_counts={"AGENTS.md": 1}
        )
        rendered = doc_freshness.render_queue(report)
        self.assertIn("Signals", rendered)
        self.assertIn("lint-restatement", rendered)
        # A signal is never counted as a finding, and a finding is never filed
        # under the signals.
        for item in report["signals"]:
            self.assertNotIn(item, report["findings"])
        self.assertEqual(
            report["stats"]["findings"], len(report["findings"]),
        )
        self.assertEqual(report["stats"]["signals"], len(report["signals"]))

    def test_the_report_says_what_it_is_finding(self):
        clean_tree(self.root)
        notes = " ".join(doc_freshness.audit(self.root)["notes"]).lower()
        self.assertIn("signal", notes)
        self.assertIn("cannot be drained", notes)

    def test_the_changelog_is_history_and_not_an_instruction(self):
        # `docs/CHANGELOG.md` records what a release said, including the lanes
        # ADR-0118 named before ADR-0134 renamed them. A reader does not take
        # a command from a changelog entry, so two lanes in one file is a
        # release note, not a conflicting instruction.
        clean_tree(self.root)
        write(
            self.root / "docs" / "CHANGELOG.md",
            "# Changelog\n\n## [0.1.0]\n\nThe gate is eng/verify.sh --full.\n",
        )
        write(
            self.root / "architecture" / "distilled" / "cli.md",
            "# CLI\n\nThe gate is eng/verify.sh --fast.\n",
        )
        report = doc_freshness.audit(self.root, commit_times={}, commit_counts={})
        self.assertEqual(
            [], [f for f in report["findings"]
                 if f["check"] == "instruction-conflict"
                 and f["file"].startswith("docs/")],
        )

    def test_the_live_skill_reference_defect_is_reported_by_check_6(self):
        # The defect the bead names: a path in a SKILL.md resolves against the
        # *skill* directory, not the repo root, so a bare `plugins.md` is
        # `.pi/skills/spatial-engine/plugins.md` — which does not exist.
        self._dirty_tree()
        report = doc_freshness.audit(
            self.root, commit_times={}, commit_counts={}
        )
        skill = [
            f
            for f in report["findings"]
            if f["check"] == "skill-reference"
            and f["file"] == ".pi/skills/x/SKILL.md"
            and "plugins.md" in f["message"]
        ]
        self.assertTrue(skill, "the bare `plugins.md` in a SKILL.md is not reported")

    def test_a_skill_reference_that_resolves_against_the_repo_root_is_not_reported(self):
        clean_tree(self.root)
        write(
            self.root / ".pi" / "skills" / "x" / "SKILL.md",
            "# x\n\nRead architecture/distilled/core.md and AGENTS.md.\n",
        )
        report = doc_freshness.audit(self.root, commit_times={}, commit_counts={})
        self.assertEqual(
            [], [f for f in report["findings"] if f["check"] == "skill-reference"]
        )

    def test_context_bloat_is_a_two_hundred_line_ceiling(self):
        write(
            self.root / "AGENTS.md",
            "# AGENTS\n\n" + "\n".join(f"line {n}" for n in range(198)) + "\n",
        )
        report = doc_freshness.audit(self.root, commit_times={}, commit_counts={})
        self.assertEqual(
            [], [f for f in report["findings"] if f["check"] == "context-bloat"]
        )
        write(
            self.root / "AGENTS.md",
            "# AGENTS\n\n" + "\n".join(f"line {n}" for n in range(201)) + "\n",
        )
        report = doc_freshness.audit(self.root, commit_times={}, commit_counts={})
        over = [f for f in report["findings"] if f["check"] == "context-bloat"]
        self.assertEqual(1, len(over))
        self.assertIn(
            str(doc_freshness.BLOAT_CEILING), over[0]["message"],
        )

    def test_generated_files_are_not_init_fossilization(self):
        # architecture/decisions/README.md and arch-index.md are generated, so a
        # single commit is not a hand-written doc that was never revised. It is
        # a *signal* either way (ADR-0177), so this reads `signals`.
        clean_tree(self.root)
        report = doc_freshness.audit(
            self.root, commit_times={}, commit_counts={"arch-index.md": 1}
        )
        self.assertEqual(
            [], [f for f in report["signals"] if f["check"] == "init-fossil"]
        )

    # --- the three shared checks are read, not reimplemented ---------------

    def test_the_shared_checks_are_read_out_of_arch_index(self):
        source = TOOL.read_text(encoding="utf-8")
        self.assertNotIn(
            "ADR_CITATION",
            source.replace("arch_index.ADR_CITATION", ""),
            "doc-freshness re-declares the citation regex instead of using "
            "tools/arch-index.py's",
        )
        for check_id in SHARED_IDS:
            entry = next(
                check
                for check in doc_freshness.audit(self.root)["checks"]
                if check["id"] == check_id
            )
            self.assertTrue(entry["shared"], f"{check_id} is not marked shared")
            self.assertIn("arch-index.py", entry["sharedWith"])

    def test_a_stale_register_and_a_dangling_citation_agree_with_arch_index(self):
        clean_tree(self.root)
        write(
            self.root / "architecture" / "distilled" / "README.md",
            "# Distilled\n\n<!-- arch-index:register:begin -->\n| 0001 | made up |\n"
            "<!-- arch-index:register:end -->\n",
        )
        write(self.root / "README.md", "See ADR-0009 for the rule.\n")

        report = doc_freshness.audit(self.root, commit_times={}, commit_counts={})
        mine = {f["check"]: f["message"] for f in report["findings"]}
        theirs = "\n".join(arch_index.check(self.root))
        self.assertIn("architecture/distilled/README.md", mine["adr-register"])
        self.assertIn(
            mine["adr-register"],
            theirs,
            "the register finding is not the one tools/arch-index.py --check prints",
        )
        self.assertIn("ADR-0009", mine["adr-citation"])
        self.assertIn(
            mine["adr-citation"],
            theirs,
            "the citation finding is not the one tools/arch-index.py --check prints",
        )

    def test_a_generated_queue_is_not_cited_by_the_gate(self):
        """The audit writes `doc-queue.md` into the tree it reports on, and the
        gate's citation read walks the tree — so a queue that quotes a line
        carrying an `ADR-NNNN` made the gate's verdict depend on whether an
        untracked report happened to be on disk, and the *reporting-only* half
        of the doc gate turned into a gate.

        A queue is machine-generated, gitignored and rewritten on every run, so
        it is not a citation surface: `tools/arch-index.py` skips the whole set
        of loop artefacts by name, the way it already skips vendored trees.
        """
        clean_tree(self.root)
        write(self.root / "README.md", "See ADR-0009 for the rule.\n")
        # The finding the queue would carry: the cited line, verbatim, as a
        # queue row. ADR-0009 has no record, so the citation read would fail.
        self.assertEqual(
            0, doc_freshness.main(["--root", str(self.root), "--report", "--quiet"]),
            "the audit is reporting-only, so a finding is not a failure")
        self.assertTrue((self.root / "doc-queue.md").exists(),
                        "the audit did not write a queue to read back")

        findings = arch_index.check(self.root)
        self.assertTrue(
            any("README.md" in f and "ADR-0009" in f for f in findings),
            "the citation read found nothing at all, so the skip below proves "
            "nothing: fix the fixture rather than deleting the assertion",
        )
        self.assertFalse(
            any("doc-queue.md" in f for f in findings),
            "the gate read a generated loop artefact",
        )
        self.assertFalse(
            any("doc-queue.md" in f
                for f in arch_index.dangling_citations(
                    self.root, arch_index.load_corpus(self.root))),
            "the gate read a generated loop artefact",
        )

    def test_the_shared_findings_say_they_are_also_gated_by_verify(self):
        clean_tree(self.root)
        write(self.root / "README.md", "See ADR-0009 for the rule.\n")
        report = doc_freshness.audit(self.root, commit_times={}, commit_counts={})
        citation = [f for f in report["findings"] if f["check"] == "adr-citation"][0]
        self.assertIn("arch-index.py --check", citation["gate"])
        self.assertIn("eng/verify.sh", citation["gate"])
        self.assertTrue(citation["shared"])

    # --- the artefacts -----------------------------------------------------

    def test_the_audit_writes_the_queue_and_the_report(self):
        self._dirty_tree()
        code = doc_freshness.main(["--root", str(self.root)])
        self.assertEqual(0, code, "the audit is reporting-only, so findings are not a failure")
        report = json.loads((self.root / "doc-report.json").read_text(encoding="utf-8"))
        queue = (self.root / "doc-queue.md").read_text(encoding="utf-8")
        self.assertEqual("doc-freshness", report["audit"])
        self.assertTrue(report["findings"])
        self.assertIn("doc-queue.md", queue)
        # Every finding in the report is in the queue, worst-first — counted
        # over the findings half only, since the signals table below it has the
        # same row shape and is not part of the queue (ADR-0177).
        findings_half = queue.split("## Signals", 1)[0]
        self.assertEqual(
            len(report["findings"]),
            len(re.findall(r"^\| `[a-z-]+` \| (?:high|medium|low) \|",
                           findings_half, re.M)),
            "the queue and the report disagree on how many findings there are",
        )
        self.assertEqual(
            len(report["signals"]),
            len(re.findall(r"^\| `[a-z-]+` \| (?:high|medium|low) \|",
                           queue, re.M)) - len(report["findings"]),
            "the signals section and the report disagree on how many there are",
        )
        severities = [
            finding["severity"] for finding in report["findings"]
        ]
        order = {"high": 0, "medium": 1, "low": 2}
        self.assertEqual(sorted(severities, key=order.get), severities)

    def test_the_artifacts_are_gitignored(self):
        ignored = (REPO_ROOT / ".gitignore").read_text(encoding="utf-8")
        for artefact in ("doc-queue.md", "doc-report.json"):
            self.assertIn(artefact, ignored.splitlines())

    def test_the_report_says_it_is_not_a_gate(self):
        # "Do NOT gate on all five at once" is the bead's own instruction, and a
        # report that reads as a verdict is how a reporting-only audit becomes a
        # gate by accident.
        report = doc_freshness.audit(REPO_ROOT)
        self.assertFalse(report["gate"])
        self.assertTrue(any("Reporting-only" in note for note in report["notes"]))
        self.assertTrue(any("not a correctness improvement" in note
                            for note in report["notes"]))

    # --- the repository's own findings -------------------------------------

    def test_the_repository_audits_without_crashing(self):
        report = doc_freshness.audit(REPO_ROOT)
        # The corpus is audited clean of *findings* after SpatialEngine-7o0, and
        # stays that way under ADR-0177: what it still carries is signals, which
        # this asserts as the other half rather than as a dirty tree.
        self.assertTrue(report["signals"], "the corpus carries no signals at all")
        for item in report["findings"] + report["signals"]:
            self.assertIn(item["check"], CHECK_IDS + SIGNAL_IDS)
            self.assertFalse(Path(item["file"]).is_absolute())

    def test_the_live_skill_pointer_is_drained(self):
        # SpatialEngine-imz.2 named this defect and it stayed in the tree:
        # `.pi/skills/spatial-engine/SKILL.md:140` cited bare `plugins.md` and
        # `rendering.md`, which resolve inside the skill directory and are not
        # there. The fixture above still proves the check fires on that shape;
        # this one says the repository's own skill no longer carries it.
        # (SpatialEngine-7o0)
        report = doc_freshness.audit(REPO_ROOT)
        skill = [
            f
            for f in report["findings"]
            if f["check"] == "skill-reference"
            and f["file"] == ".pi/skills/spatial-engine/SKILL.md"
        ]
        self.assertEqual(
            [], skill,
            "a skill cites a path that resolves in neither the skill directory "
            "nor the repository root: " + json.dumps(skill, indent=2),
        )
        # Both bare names of line 140: each resolves to a real digest, which is
        # what makes the bare form a pointer and not a concept.
        for name in ("architecture/distilled/plugins.md",
                     "architecture/distilled/rendering.md"):
            self.assertTrue((REPO_ROOT / name).is_file(), name)

    def test_the_gate_noun_conflict_is_drained_and_never_gated(self):
        # The corpus said `eng/verify.sh`, `--fast` and `--full` were each "the
        # gate", and the check is reporting-only: the drain is the prose, never
        # a gate on it. Three of the seven findings were the generated register
        # quoting records that are about the lanes, one was the changelog
        # quoting the lane names as they were, one was a line about running in
        # a browser that never said "gate", and the refactor skill told an
        # agent to raise its safety net on `--full` (ADR-0134 took that off the
        # hand-off path). What is left is nothing, and the fixture tests above
        # are what keep the check itself alive.
        report = doc_freshness.audit(REPO_ROOT)
        conflict = [f for f in report["findings"] if f["check"] == "instruction-conflict"]
        self.assertEqual(
            [], conflict,
            "one gate noun, two referents, somewhere in the corpus: "
            + json.dumps(conflict, indent=2),
        )
        self.assertTrue(
            any("reporting-only" in note.lower() for note in report["notes"])
        )

    def test_vendored_skills_are_out_of_scope(self):
        # .agents/skills is a vendored copy bootstrapped once (git log over it is
        # two commits), so neither bloat nor fossilization is this repo's debt.
        report = doc_freshness.audit(REPO_ROOT)
        for finding in report["findings"]:
            self.assertFalse(
                finding["file"].startswith(".agents/"),
                f"a vendored skill is reported: {finding}",
            )

    # --- the wiring --------------------------------------------------------

    def test_eng_quality_audit_runs_the_doc_audit(self):
        script = REPO_ROOT / "eng" / "quality-audit.sh"
        self.assertTrue(script.is_file(), "eng/quality-audit.sh does not exist")
        text = script.read_text(encoding="utf-8")
        self.assertIn("tools/doc-freshness.py", text)

    def test_eng_quality_audit_aggregates_the_doc_report_with_the_others(self):
        text = (REPO_ROOT / "eng" / "quality-audit.sh").read_text(encoding="utf-8")
        for report in ("crap-report.json", "metrics-report.json", "doc-report.json"):
            self.assertIn(report, text, f"{report} is not aggregated")

    def test_verify_runs_the_doc_audit_as_a_reporting_step(self):
        text = (REPO_ROOT / "eng" / "verify.sh").read_text(encoding="utf-8")
        self.assertIn("quality-audit.sh", text)
        # Reporting-only: the step is asked for the report, not for a verdict.
        self.assertIn("--report", text)
        self.assertNotIn("doc-freshness.py --check", text)

    def test_every_lane_of_verify_prints_the_reporting_step(self):
        completed = subprocess.run(
            ["bash", str(REPO_ROOT / "eng" / "verify.sh"), "--plan"],
            cwd=REPO_ROOT,
            capture_output=True,
            text=True,
            env={"PATH": "/usr/bin:/bin:/usr/local/bin", "HOME": str(Path.home())},
        )
        self.assertEqual(0, completed.returncode, completed.stderr)
        # Every lane reaches the reporting step through doc_gate(), and --plan
        # prints it without running it.
        self.assertIn("eng/quality-audit.sh --report", completed.stdout)


if __name__ == "__main__":
    unittest.main()
