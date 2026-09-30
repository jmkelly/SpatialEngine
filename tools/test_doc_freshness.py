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
files. It is **reporting-only** on every lane: the nine checks land as a queue
that drains, and only then are the cheap exact ones promoted (SpatialEngine-imz.2
§implementation notes). Two of them — the register row and the dangling
`ADR-NNNN` citation — are not implemented here at all: they are read out of
`tools/arch-index.py`, which every lane of `eng/verify.sh` already gates on
(ADR-0141), so there is one implementation and one answer.

A note on what this suite is *not* claiming: a green `doc-report.json` is not a
correctness improvement. The evidence is that context strategy does not move
pass rates (arXiv 2607.27250: <=10-15pp, equivalence-bounded); the win here is
stopping confidently-wrong answers and token cost (arXiv 2601.20404: -28.6%
median runtime, -16.6% output tokens).
"""
import importlib.util
import json
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

#: The nine checks, by the id the report uses. `shared` names the check
#: `tools/arch-index.py` already owns and gates on, so it is read rather than
#: reimplemented (SpatialEngine-imz.2 acceptance).
CHECK_IDS = (
    "adr-register",
    "front-matter",
    "adr-citation",
    "digest-staleness",
    "dead-doc-link",
    "skill-reference",
    "context-bloat",
    "init-fossil",
    "instruction-conflict",
    "lint-leakage",
)

SHARED_IDS = ("adr-register", "front-matter", "adr-citation")


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


def clean_tree(root: Path) -> None:
    """A tree that produces no finding from any of the ten checks.

    The three shared checks read the *generated* files, so a clean tree carries
    what the generator makes of it: the register spliced between the markers,
    the index and the breadcrumb rendered. Hand-writing those is how the drift
    happened in the first place.
    """
    write(root / "architecture" / "decisions" / "ADR-0001-a.md", adr("0001", "A thing"))
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
            set(CHECK_IDS), {check["id"] for check in report["checks"]}
        )

    # --- each check fires on the defect it is named for --------------------

    def _dirty_tree(self) -> None:
        """A tree that trips every one of the nine checks at once."""
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
        # restating a wall the architecture tests already fail on.
        write(
            self.root / "RELEASING.md",
            "The gate before done is eng/verify.sh --full, and no third-party "
            "type crosses a public contract.\n",
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
        # single commit is not a hand-written doc that was never revised.
        clean_tree(self.root)
        report = doc_freshness.audit(
            self.root, commit_times={}, commit_counts={"arch-index.md": 1}
        )
        self.assertEqual(
            [], [f for f in report["findings"] if f["check"] == "init-fossil"]
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
        # Every finding in the report is in the queue, worst-first.
        self.assertEqual(
            len(report["findings"]),
            len(re.findall(r"^\| `[a-z-]+` \| (?:high|medium|low) \|", queue, re.M)),
            "the queue and the report disagree on how many findings there are",
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
        self.assertTrue(report["findings"], "the tree is documented as dirty")
        for finding in report["findings"]:
            self.assertIn(finding["check"], CHECK_IDS)
            self.assertFalse(Path(finding["file"]).is_absolute())

    def test_the_live_skill_pointer_is_named_by_id(self):
        # SpatialEngine-imz.2's named live defect:
        # .pi/skills/spatial-engine/SKILL.md:140 cites bare `plugins.md` and
        # `rendering.md`, which resolve inside the skill directory and are not
        # there.
        report = doc_freshness.audit(REPO_ROOT)
        skill = [
            f
            for f in report["findings"]
            if f["check"] == "skill-reference"
            and f["file"] == ".pi/skills/spatial-engine/SKILL.md"
            and "plugins.md" in f["message"]
        ]
        self.assertTrue(
            skill, "the live skill-relative pointer is not in the report"
        )
        # Both bare names of line 140: each resolves to a real digest, which is
        # what makes the bare form a pointer and not a concept.
        self.assertIn("rendering.md", skill[0]["message"])
        for name in ("architecture/distilled/plugins.md",
                     "architecture/distilled/rendering.md"):
            self.assertTrue((REPO_ROOT / name).is_file(), name)

    def test_the_gate_noun_conflict_is_reported_and_never_gated(self):
        # AGENTS.md calls eng/verify.sh "the gate before done", the quality-loop
        # skill runs its own five audits, and README.md lists four verification
        # scripts as if equivalent. The finding is reported; the check is
        # reporting-only, so fixing the prose is a bead, not a gate failure.
        report = doc_freshness.audit(REPO_ROOT)
        conflict = [f for f in report["findings"] if f["check"] == "instruction-conflict"]
        self.assertTrue(conflict, "the gate-noun conflict is not reported")
        for finding in conflict:
            notes = doc_freshness.audit(REPO_ROOT)["notes"]
        self.assertTrue(any("reporting-only" in note.lower() for note in notes))

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
