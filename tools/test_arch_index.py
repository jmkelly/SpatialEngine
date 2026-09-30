#!/usr/bin/env python3
"""The ADR register, the ADR index and the doc gate are generated, not authored.

Run: python3 -m unittest tools/test_arch_index.py

Three defects this suite is the reproduction for (SpatialEngine-imz.1):

*   the hand-edited register table in `architecture/distilled/README.md` was
    missing 33 of the 126 records on disk — including ADR-0097 (pushdown
    identity) and ADR-0098 (the store query surface), so an agent routing
    through ADR-0074's row concluded the feature-query contract still had its
    original shape;
*   ADR metadata was in two schemas (front matter, and a `Status:` line after
    the H1), so "is this decision still live?" was a grep through prose and a
    third of the corpus would not answer it at all;
*   nothing failed when an ADR was deleted, renamed, or re-stated in the old
    schema, and a citation of a non-existent ADR was not a finding.

The tool under test is `tools/arch-index.py`; the gate is `arch-index.py
--check`, which every lane of `eng/verify.sh` runs (ADR-0141).
"""
import importlib.util
import re
import shutil
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parent.parent
TOOL = REPO_ROOT / "tools" / "arch-index.py"

#: The tool is a script with a hyphen in its name (like its neighbours
#: `skip_gate.py`'s callers and `adr-next-number.py`), so it is loaded by path.
_spec = importlib.util.spec_from_file_location("arch_index", TOOL)
arch_index = importlib.util.module_from_spec(_spec)
sys.modules["arch_index"] = arch_index
_spec.loader.exec_module(arch_index)

REGISTER = REPO_ROOT / "architecture" / "distilled" / "README.md"
INDEX = REPO_ROOT / "architecture" / "decisions" / "README.md"
BREADCRUMB = REPO_ROOT / "arch-index.md"
DECISIONS = REPO_ROOT / "architecture" / "decisions"

#: Clauses the register carried on `main` on 2026-10-01 and that a regenerated
#: register must still carry, one per record, each distinctive enough that only
#: the record's own prose can supply it (ADR-0145 §3). They are the register's
#: job — an agent reads them to decide whether to open the record — so a
#: generator that shortens a row is proposing to delete one.
HAND_ENRICHED_CLAUSES = {
    "0074": "reductions are an additive `IFeatureAggregateStore` face",
    "0109": "failing loud to the whole solution",
    "0112": "find pushes only the null test",
    "0122": "`PostgisOptions.DescriptionCacheTtl`, 30s",
    "0128": "belong to the **reduction**, not the plan",
    "0131": "a page of an ungrouped reduction past its one group is **no groups**",
    "0132": "folded over the **ASCII alphabet**",
    "0134": "a *generated scoped solution*",
    "0135": "timing out on machine load, not on a defect",
    "0136": "a column that declares one keeps it",
    "0142": "none of the 18 `hasZ: true` layers answers with a flagged geometry",
}


def run_tool(*args, root=REPO_ROOT):
    return subprocess.run(
        [sys.executable, str(TOOL), "--root", str(root), *args],
        capture_output=True,
        text=True,
    )


def copy_repo(destination):
    """A copy of the parts of the tree the doc gate reads.

    `architecture/` (the records, the digests and the generated files), `eng/`
    and `AGENTS.md` are what the tool reads; copying the whole tree — six times
    over, once per mutation test — is minutes of I/O for the same findings. The
    repository-level tests below read the real tree in place instead.
    """
    for name in ("architecture", "eng", "tools", "AGENTS.md", "arch-index.md"):
        source = REPO_ROOT / name
        target = destination / name
        if source.is_dir():
            shutil.copytree(
                source,
                target,
                ignore=shutil.ignore_patterns(
                    "node_modules", "bin", "obj", "dist", "__pycache__"
                ),
            )
        else:
            shutil.copy2(source, target)
    return destination


class RegisterTests(unittest.TestCase):
    def test_every_adr_on_disk_has_a_register_row(self):
        corpus = arch_index.load_corpus(REPO_ROOT)
        rows = arch_index.register_rows(corpus)
        self.assertEqual(
            [record.number for record in corpus if record.number not in rows],
            [],
            "ADRs with no row in the register table of "
            f"{REGISTER.relative_to(REPO_ROOT)}; it is generated, so "
            "`python3 tools/arch-index.py --write`",
        )

    def test_the_committed_register_is_what_the_tool_generates(self):
        result = run_tool("--check")
        self.assertEqual(
            result.returncode, 0, f"the doc gate failed:\n{result.stdout}{result.stderr}"
        )

    def test_the_committed_index_and_breadcrumb_are_generated(self):
        corpus = arch_index.load_corpus(REPO_ROOT)
        for path, rendered in (
            (INDEX, arch_index.render_index(corpus)),
            (BREADCRUMB, arch_index.render_breadcrumb(corpus)),
        ):
            self.assertEqual(
                path.read_text(encoding="utf-8"),
                rendered,
                f"{path.relative_to(REPO_ROOT)} is not what arch-index.py generates; "
                "run `python3 tools/arch-index.py --write`",
            )

    def test_a_no_op_run_is_byte_identical(self):
        with tempfile.TemporaryDirectory() as raw:
            root = copy_repo(Path(raw) / "repo")
            before = {
                name: (root / name).read_bytes()
                for name in (
                    "architecture/distilled/README.md",
                    "architecture/decisions/README.md",
                    "arch-index.md",
                )
            }
            result = run_tool("--write", root=root)
            self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
            for name, content in before.items():
                self.assertEqual((root / name).read_bytes(), content, name)

    def test_register_keeps_the_hand_enriched_routing_clauses(self):
        """The register is generated, so prose can only survive as the record's.

        The reproduction for SpatialEngine-imz.9: ADR-0141's generator rendered
        every row from the record's `summary`, and the summaries it was written
        with were one-line condensations. Rebuilt over `main` — whose register
        had been hand-enriched row by row since the branch was cut — that
        silently deleted the routing clauses an agent reads to decide whether a
        record is relevant: ADR-0134's measured scoping numbers, ADR-0122's
        cache TTL's configuration name, ADR-0132's reason the fold is over the
        ASCII alphabet, and so on. The register is generated wholesale
        (ADR-0145), so the fix is that the clause is written into the record's
        `summary` and rendered from there; this test is what makes a future
        condensation of one of these rows fail by name rather than pass a
        review nobody was looking for.
        """
        corpus = arch_index.load_corpus(REPO_ROOT)
        rows = arch_index.register_rows(corpus)
        missing = [
            f"ADR-{number} lost the clause {clause!r}"
            for number, clause in sorted(HAND_ENRICHED_CLAUSES.items())
            if clause not in rows.get(number, "")
        ]
        self.assertEqual(
            missing,
            [],
            "the register dropped routing clauses `main` had hand-enriched; the "
            "register is generated wholesale (ADR-0145), so a clause that is "
            "worth routing on belongs in the record's `summary`: "
            + "; ".join(missing),
        )

    def test_the_breadcrumb_points_the_way_routing_goes(self):
        text = BREADCRUMB.read_text(encoding="utf-8")
        for pointer in ("AGENTS.md", "architecture/distilled/README.md",
                        "architecture/decisions/README.md"):
            self.assertIn(pointer, text)


class MetadataSchemaTests(unittest.TestCase):
    def test_every_adr_carries_the_one_schema(self):
        problems = []
        for path in sorted(DECISIONS.glob("ADR-*.md")):
            record = arch_index.parse_adr(path)
            if record.error:
                problems.append(f"{path.name}: {record.error}")
        self.assertEqual(
            problems,
            [],
            "ADR metadata in the retired schema:\n  " + "\n  ".join(problems),
        )

    def test_no_adr_carries_a_status_line_after_the_h1(self):
        offenders = []
        for path in sorted(DECISIONS.glob("ADR-*.md")):
            body = path.read_text(encoding="utf-8")
            if re.search(r"^-\s+\*\*Status:\*\*", body, re.M) or re.search(
                r"^Status:", body, re.M
            ):
                offenders.append(path.name)
        self.assertEqual(
            offenders,
            [],
            "ADRs whose status is a prose line rather than front matter: "
            + ", ".join(offenders),
        )

    def test_the_gate_fails_on_a_status_line_adr(self):
        with tempfile.TemporaryDirectory() as raw:
            root = copy_repo(Path(raw) / "repo")
            path = root / "architecture" / "decisions" / "ADR-0001-geometry-is-core.md"
            path.write_text(
                "# ADR-0001: Geometry is part of the spatial core\n\n"
                "Status: Accepted\n\n## Context\n\nProse.\n",
                encoding="utf-8",
            )
            self.assertIn("Status:", path.read_text(encoding="utf-8"))
            result = run_tool("--check", root=root)
            self.assertNotEqual(
                result.returncode, 0, "a Status-line ADR passed the gate"
            )


class GateTests(unittest.TestCase):
    def test_a_deleted_adr_fails_the_gate(self):
        with tempfile.TemporaryDirectory() as raw:
            root = copy_repo(Path(raw) / "repo")
            victim = next(
                (root / "architecture" / "decisions").glob("ADR-0105-*.md")
            )
            victim.unlink()
            result = run_tool("--check", root=root)
            self.assertNotEqual(result.returncode, 0, result.stdout)
            self.assertIn("0105", result.stdout + result.stderr)

    def test_a_renamed_adr_fails_the_gate(self):
        with tempfile.TemporaryDirectory() as raw:
            root = copy_repo(Path(raw) / "repo")
            source = next((root / "architecture" / "decisions").glob("ADR-0105-*.md"))
            source.rename(source.with_name("ADR-0105-renamed-by-hand.md"))
            result = run_tool("--check", root=root)
            self.assertNotEqual(result.returncode, 0, result.stdout)

    def test_a_dangling_citation_fails_the_gate(self):
        with tempfile.TemporaryDirectory() as raw:
            root = copy_repo(Path(raw) / "repo")
            (root / "architecture" / "distilled" / "core.md").write_text(
                # Assembled rather than written: the structural guard
                # (`AdrNumberingTests`) fails on any `ADR-NNNN` in a tracked
                # file that has no record, and this suite's own fixtures are
                # tracked files.
                "The envelope is core (ADR-" + "0999" + ").\n", encoding="utf-8"
            )
            result = run_tool("--citations", root=root)
            self.assertNotEqual(result.returncode, 0, result.stdout)
            self.assertIn("0999", result.stdout + result.stderr)

    def test_the_repository_has_no_dangling_citation(self):
        result = run_tool("--citations")
        self.assertEqual(
            result.returncode, 0, f"dangling ADR citations:\n{result.stdout}{result.stderr}"
        )

    def test_burned_numbers_are_recorded_rather_than_lost(self):
        corpus = arch_index.load_corpus(REPO_ROOT)
        index = arch_index.render_index(corpus)
        for number in arch_index.BURNED_NUMBERS:
            self.assertIn(number, index, f"burned ADR number {number} is not recorded")

    def test_a_burned_number_with_a_file_is_an_error(self):
        self.assertNotIn(
            "0096", [r.number for r in arch_index.load_corpus(REPO_ROOT)]
        )
        with tempfile.TemporaryDirectory() as raw:
            root = copy_repo(Path(raw) / "repo")
            # Assembled for the same reason as the citation above: a literal
            # burned number in this file is itself a dangling citation.
            burned = "ADR-" + "0096"
            (root / "architecture" / "decisions" / f"{burned}-taken-anyway.md").write_text(
                "---\nstatus: accepted\ndate: 2026-09-30\ndeciders: nobody\n"
                f"summary: A burned number is burned.\n---\n\n# {burned}: x\n",
                encoding="utf-8",
            )
            result = run_tool("--check", root=root)
            self.assertNotEqual(result.returncode, 0, result.stdout)


class VerifyWiringTests(unittest.TestCase):
    def test_every_lane_of_verify_runs_the_doc_gate(self):
        script = (REPO_ROOT / "eng" / "verify.sh").read_text(encoding="utf-8")
        self.assertIn("arch-index.py", script)

    def test_the_plan_lane_prints_the_doc_gate(self):
        result = subprocess.run(
            [str(REPO_ROOT / "eng" / "verify.sh"), "--plan"],
            capture_output=True,
            text=True,
            cwd=REPO_ROOT,
        )
        self.assertIn("arch-index.py", result.stdout)


if __name__ == "__main__":
    unittest.main()
