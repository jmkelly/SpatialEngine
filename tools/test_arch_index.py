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

_doc_spec = importlib.util.spec_from_file_location(
    "doc_freshness", REPO_ROOT / "tools" / "doc-freshness.py"
)
doc_freshness = importlib.util.module_from_spec(_doc_spec)
sys.modules["doc_freshness"] = doc_freshness
_doc_spec.loader.exec_module(doc_freshness)

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


class ShapeTests(unittest.TestCase):
    """A record is one decision, with a fixed section set and a word budget.

    The reproduction for SpatialEngine-6l6: the corpus had grown to 135 records
    with a median of 882 words and a maximum of 2,549, a section vocabulary
    that was whatever each writer reached for (36 records carried a heading
    outside the five the rest of the corpus agreed on, including free-form
    numbered subsections), and no way to tell an amendment from a fresh
    decision except by reading it — 27 of the 39 `amends` links had no
    reciprocal `amended-by`, so a family of eleven records over one topic was
    only discoverable by grepping. Nothing failed on any of it: the gate
    checked that a number identifies one record and that the allocator agrees
    with the tree, and said nothing about a record's size, section set or shape.
    """

    #: A well-formed record at or above the shape boundary, in miniature.
    RECORD = (
        "---\nstatus: accepted\ndate: 2026-10-01\ndeciders: nobody\n"
        "summary: A shape test record.\n---\n\n"
        "# ADR-{number}: {title}\n\n"
        "## Context\n\nOne paragraph of why.\n\n"
        "## Decision\n\n**The thing.**\n\n"
        "## Consequences\n\nOne paragraph of what follows.\n"
    )

    #: A number above `SHAPE_FROM`, so a fixture is inside the rule it tests.
    #: Assembled from parts for the reason the citation fixtures are: a literal
    #: `ADR-NNNN` in a tracked file is a citation the structural guard
    #: (`AdrNumberingTests`) reads, and this fixture record does not exist in
    #: the real tree.
    IN_SHAPE = "0" + "151"

    def write(self, root: Path, number: str, body: str, title: str = "a-record"):
        path = root / "architecture" / "decisions" / f"ADR-{number}-{title}.md"
        path.write_text(
            self.RECORD.format(number=number, title=title) + body, encoding="utf-8"
        )
        return path

    def shape(self, root: Path) -> list[str]:
        return arch_index.shape_findings(root, arch_index.load_corpus(root))

    # --- the corpus ---------------------------------------------------------

    def test_the_repository_is_inside_the_shape_rule(self):
        findings = arch_index.shape_findings(REPO_ROOT, arch_index.load_corpus(REPO_ROOT))
        self.assertEqual(
            findings,
            [],
            "a decision record is outside the shape rule (ADR-0150):\n  "
            + "\n  ".join(findings),
        )

    def test_the_record_that_decided_the_shape_obeys_it(self):
        """The boundary is this record's own number, so it is gated by its own rule."""
        self.assertEqual(
            arch_index.SHAPE_FROM,
            "0150",
            "the shape rule applies from the number of the record that decided "
            "it; a different boundary is a decision this record has not made",
        )
        self.assertIn(
            f"ADR-{arch_index.SHAPE_FROM}",
            [f"ADR-{r.number}" for r in arch_index.load_corpus(REPO_ROOT)],
            "the record that fixed the shape boundary is not on disk",
        )

    # --- the section set ----------------------------------------------------

    def test_a_record_missing_a_required_section_fails(self):
        for missing in ("Context", "Decision", "Consequences"):
            with self.subTest(missing=missing), tempfile.TemporaryDirectory() as raw:
                root = copy_repo(Path(raw) / "repo")
                path = self.write(root, self.IN_SHAPE, "")
                text = path.read_text(encoding="utf-8")
                start = text.index(f"## {missing}")
                end = text.find("\n## ", start + 1)
                path.write_text(
                    text[:start] + (text[end + 1:] if end != -1 else ""),
                    encoding="utf-8",
                )
                findings = self.shape(root)
                self.assertTrue(
                    any(missing in finding for finding in findings),
                    f"a record with no ## {missing} passed: {findings}",
                )

    def test_a_retired_free_form_section_fails(self):
        """The vocabulary is closed, so `## §applied` and `## Rejected` are gone."""
        for heading in ("§applied", "Rejected", "Implementation status",
                        "Merge note (SpatialEngine-u2x.8)"):
            with self.subTest(heading=heading), tempfile.TemporaryDirectory() as raw:
                root = copy_repo(Path(raw) / "repo")
                self.write(root, self.IN_SHAPE, f"\n## {heading}\n\nProse.\n")
                findings = self.shape(root)
                self.assertTrue(
                    any(heading in finding for finding in findings),
                    f"a free-form `## {heading}` passed: {findings}",
                )

    def test_the_sections_come_in_the_reading_order(self):
        with tempfile.TemporaryDirectory() as raw:
            root = copy_repo(Path(raw) / "repo")
            self.write(
                root, self.IN_SHAPE,
                "\n## Consequences\n\nLater.\n\n## Decision\n\nEarlier.\n",
            )
            findings = self.shape(root)
            self.assertTrue(
                any("order" in finding for finding in findings),
                f"a record whose sections are out of order passed: {findings}",
            )

    # --- the word budget ----------------------------------------------------

    def test_an_over_budget_record_fails(self):
        with tempfile.TemporaryDirectory() as raw:
            root = copy_repo(Path(raw) / "repo")
            self.write(
                root, self.IN_SHAPE,
                "\n## Alternatives\n\n" + ("word " * arch_index.NARRATIVE_BUDGET) + "\n",
            )
            findings = self.shape(root)
            self.assertTrue(
                any("budget" in finding for finding in findings),
                f"a {arch_index.NARRATIVE_BUDGET}-word record passed: {findings}",
            )

    def test_measurements_and_references_do_not_count_against_the_budget(self):
        """A long gate is long because it carries measurements, which may be load-bearing."""
        with tempfile.TemporaryDirectory() as raw:
            root = copy_repo(Path(raw) / "repo")
            self.write(
                root, self.IN_SHAPE,
                "\n## Measurements\n\n" + ("measured " * arch_index.NARRATIVE_BUDGET)
                + "\n\n## References\n\n" + ("cited " * arch_index.NARRATIVE_BUDGET) + "\n",
            )
            self.assertEqual(
                [f for f in self.shape(root) if "budget" in f],
                [],
                "evidence counted against the narrative budget: "
                + "; ".join(self.shape(root)),
            )

    def test_an_over_budget_record_may_declare_why(self):
        with tempfile.TemporaryDirectory() as raw:
            root = copy_repo(Path(raw) / "repo")
            path = self.write(
                root, self.IN_SHAPE,
                "\n## Alternatives\n\n" + ("word " * arch_index.NARRATIVE_BUDGET) + "\n",
            )
            self.assertTrue([f for f in self.shape(root) if "budget" in f])
            text = path.read_text(encoding="utf-8")
            path.write_text(
                text.replace(
                    "summary: A shape test record.",
                    "summary: A shape test record.\n"
                    "over-budget: the one decision does not fit a shorter record",
                ),
                encoding="utf-8",
            )
            self.assertEqual(
                [f for f in self.shape(root) if "budget" in f], [],
                "a declared over-budget record still failed",
            )

    def test_an_exemption_nobody_needed_is_rejected(self):
        """A parameter is honoured or rejected by name, never accepted and ignored."""
        with tempfile.TemporaryDirectory() as raw:
            root = copy_repo(Path(raw) / "repo")
            path = self.write(root, self.IN_SHAPE, "")
            path.write_text(
                path.read_text(encoding="utf-8").replace(
                    "summary: A shape test record.",
                    "summary: A shape test record.\nover-budget: no reason to give",
                ),
                encoding="utf-8",
            )
            findings = self.shape(root)
            self.assertTrue(
                any("over-budget" in finding for finding in findings),
                f"an over-budget exemption on a record inside the budget passed: {findings}",
            )

    def test_an_empty_exemption_is_rejected(self):
        """Present and blank is a different case from absent, and both are read."""
        with tempfile.TemporaryDirectory() as raw:
            root = copy_repo(Path(raw) / "repo")
            path = self.write(root, self.IN_SHAPE, "")
            path.write_text(
                path.read_text(encoding="utf-8").replace(
                    "summary: A shape test record.", "summary: A shape test record.\nover-budget:"
                ),
                encoding="utf-8",
            )
            result = run_tool("--check", root=root)
            self.assertNotEqual(result.returncode, 0, "a blank exemption passed the gate")
            self.assertIn(
                arch_index.OVER_BUDGET_FIELD,
                result.stdout + result.stderr,
                "a blank exemption failed for some other reason",
            )

    # --- the boundary -------------------------------------------------------

    def test_a_record_written_before_the_boundary_is_not_judged(self):
        """The rule is not a 122-file diff: what predates it is grandfathered."""
        grandfathered = "0098"  # 2,549 words, three free-form amendment sections
        with tempfile.TemporaryDirectory() as raw:
            root = copy_repo(Path(raw) / "repo")
            victim = next(
                (root / "architecture" / "decisions").glob(f"ADR-{grandfathered}-*.md")
            )
            path = victim
            text = path.read_text(encoding="utf-8")
            path.write_text(
                text + "\n## A section nobody agreed on\n\n"
                + ("prose " * arch_index.NARRATIVE_BUDGET) + "\n",
                encoding="utf-8",
            )
            self.assertEqual(
                [f for f in self.shape(root) if grandfathered in f],
                [],
                f"ADR-{grandfathered} predates the shape rule and is not judged by it",
            )

    # --- the gate and the template -----------------------------------------

    def test_the_gate_fails_on_a_record_outside_the_shape_rule(self):
        with tempfile.TemporaryDirectory() as raw:
            root = copy_repo(Path(raw) / "repo")
            self.write(root, self.IN_SHAPE, "\n## §licence\n\nProse.\n")
            result = run_tool("--check", root=root)
            self.assertNotEqual(result.returncode, 0, "an out-of-shape record passed the gate")
            self.assertIn(
                "§licence",
                result.stdout + result.stderr,
                "the gate failed for some other reason, so the shape rule is not "
                "what it read: " + result.stdout + result.stderr,
            )

    def test_the_template_satisfies_the_rule_it_states(self):
        """The template is the shape in prose; a gate nothing can satisfy is a trap."""
        template = (REPO_ROOT / "architecture" / "decisions" / "TEMPLATE.md").read_text(
            encoding="utf-8"
        )
        headings = re.findall(r"^##\s+(.+?)\s*$", template, re.M)
        self.assertEqual(
            sorted(set(headings) - set(arch_index.ALLOWED_SECTIONS)),
            [],
            f"TEMPLATE.md carries a heading outside the allowed set: {headings}",
        )
        for required in arch_index.REQUIRED_SECTIONS:
            self.assertIn(required, headings, f"TEMPLATE.md has no `## {required}`")
        self.assertLess(
            len(template.split()),
            arch_index.NARRATIVE_BUDGET,
            "TEMPLATE.md is itself over the budget it states",
        )
        for field in arch_index.FIELDS:
            self.assertIn(
                f"{field}:", template, f"TEMPLATE.md does not show the `{field}` field"
            )

    def test_the_documentation_audit_reports_the_shape_rule(self):
        """One implementation, one set of findings, one gate each (ADR-0141)."""
        findings = doc_freshness.shared_findings(REPO_ROOT)
        self.assertTrue(
            any(item["check"] == "adr-shape" for item in findings) is False
            and any(
                item["check"] == "adr-shape" for item in doc_freshness.audit(REPO_ROOT)["findings"]
            ) is False,
            "the audit does not read the shape rule at all",
        )
        report = doc_freshness.audit(REPO_ROOT)
        shape_check = [c for c in report["checks"] if c["id"] == "adr-shape"]
        self.assertEqual(len(shape_check), 1, "the audit has no `adr-shape` check")
        self.assertTrue(
            shape_check[0]["shared"] and shape_check[0]["sharedWith"] == "tools/arch-index.py",
            "the shape rule is read from arch-index.py, so the audit says so",
        )


class ReadingOrderTests(unittest.TestCase):
    """The pushdown family is an order, not eleven files a reader has to sort.

    The reproduction for SpatialEngine-vl1: ADR-0150 made the family a lookup
    by deriving an **Amended by** list from each record's own `amends:`, and
    said so itself — "a family over one topic is a row here rather than a
    reading order the reader has to assemble across eleven files". That is
    still what a reader has to do. Sixteen records over the store query surface
    — ADR-0074's plan, the two pushdown rules, and the thirteen refiners —
    carried no order at all, and half of them did not declare the refinement
    their own prose states, so the derived list could not have produced one.

    The reading order is generated from the same links, so it cannot drift from
    them: a family is the records a root reaches through `amends:`, read root
    first and then by number, with every record that has since been refined
    marked by the records that narrow it.
    """

    #: A four-record family, in miniature: a root, a refiner of it, a refiner of
    #: the refiner, and a second refiner of the root. Assembled from parts for
    #: the reason the other fixtures are: a literal `ADR-NNNN` in a tracked file
    #: is a citation the structural guard (`AdrNumberingTests`) reads.
    def record(self, number: str, title: str, amends: str = "") -> str:
        return (
            "---\nstatus: accepted\ndate: 2026-10-01\ndeciders: nobody\n"
            f"summary: Fixture {title}.\n"
            + (f"amends: ADR-{amends}\n" if amends else "")
            + "---\n\n"
            f"# ADR-{number}: {title}\n\n"
            "## Context\n\nProse.\n\n## Decision\n\n**The thing.**\n\n"
            "## Consequences\n\nProse.\n"
        )

    def fixture(self, root: Path) -> None:
        decisions = root / "architecture" / "decisions"
        decisions.mkdir(parents=True, exist_ok=True)
        for number, title, amends in (
            ("0201", "the root", ""),
            ("0202", "the first refiner", "0201"),
            ("0203", "the second refiner", "0201"),
            ("0204", "the refiner of the refiner", "0202"),
        ):
            (decisions / f"ADR-{number}-{title.replace(' ', '-')}.md").write_text(
                self.record(number, title, amends), encoding="utf-8"
            )

    def render(self, root: Path) -> str:
        return arch_index.render_index(arch_index.load_corpus(root))

    # --- the corpus ---------------------------------------------------------

    def test_the_index_carries_a_reading_order(self):
        index = (INDEX).read_text(encoding="utf-8")
        self.assertIn(
            "## Reading order",
            index,
            "architecture/decisions/README.md has no reading order; the bead "
            "SpatialEngine-vl1's acceptance is that the order is written where "
            "the index already links from (ADR-0150 left it out deliberately)",
        )

    def test_the_pushdown_family_reads_from_its_root(self):
        """The bead's own question: how a pushed-down string comparison works."""
        corpus = arch_index.load_corpus(REPO_ROOT)
        families = arch_index.reading_order_families(corpus)
        root = "0074"
        self.assertIn(
            root,
            families,
            "the store-pushdown family is not keyed to its root: ADR-0074 is "
            "the plan every one of those records refines, and it declares no "
            "refiner, so it cannot be the head of a reading order",
        )
        members = families[root]
        self.assertEqual(members[0], root, "a family does not read from its root")
        for earlier, later in (("0098", "0121"), ("0121", "0123"), ("0123", "0126"),
                               ("0126", "0130"), ("0133", "0137"), ("0137", "0157")):
            self.assertLess(
                members.index(earlier),
                members.index(later),
                f"ADR-{later} reads before ADR-{earlier} in the pushdown family",
            )
        self.assertIn("0121", members, "the ordinal-order record is not in the family")
        self.assertIn("0132", members, "the folded-pattern record is not in the family")

    def test_a_record_that_refines_another_declares_it(self):
        """The order can only be derived from links the records state themselves."""
        undeclared = arch_index.undeclared_refinements(
            REPO_ROOT, arch_index.load_corpus(REPO_ROOT)
        )
        self.assertEqual(
            undeclared,
            [],
            "records whose register row declares a refinement the front matter "
            "does not, so no reading order can include them:\n  "
            + "\n  ".join(undeclared),
        )

    # --- the shape of the section ------------------------------------------

    def test_a_family_reads_root_first_then_by_number(self):
        with tempfile.TemporaryDirectory() as raw:
            root = Path(raw)
            self.fixture(root)
            section = self.render(root)
            positions = [section.index(f"](ADR-{n}-") for n in
                         ("0201", "0202", "0203", "0204")]
            self.assertEqual(positions, sorted(positions), section)

    def test_a_narrowed_record_is_marked_with_its_refiners(self):
        with tempfile.TemporaryDirectory() as raw:
            root = Path(raw)
            self.fixture(root)
            section = self.render(root)
            self.assertIn("narrowed by", section, "nothing says what narrowed what")
            self.assertRegex(
                section,
                r"0202.*narrowed by.*0204",
                "the first refiner is refined by the refiner of the refiner and "
                "the reading order does not say so, which is the half of the "
                "question ADR-0150 left open",
            )

    def test_a_lone_record_is_not_a_family(self):
        with tempfile.TemporaryDirectory() as raw:
            root = Path(raw)
            decisions = root / "architecture" / "decisions"
            decisions.mkdir(parents=True)
            (decisions / ("ADR-" + "0201" + "-alone.md")).write_text(
                self.record("0201", "alone"), encoding="utf-8"
            )
            self.assertEqual(arch_index.reading_order_families(
                arch_index.load_corpus(root)), {})


class ReciprocityTests(unittest.TestCase):
    """`amended-by:` is derived, so a hand-written copy may not disagree.

    ADR-0150 rejected *requiring* the reciprocal field on 27 links. The index
    derives the other end of every `amends:` link, so reciprocity is not
    information a record has to carry — but seven records do carry one, and
    nothing compared it with what the generator derives, so the copy in the
    record and the copy in the index were free to drift. A field that can say
    something the index contradicts is accepted and ignored; this gate is the
    other half of that rule.
    """

    def test_a_hand_written_amended_by_must_agree_with_the_derived_list(self):
        with tempfile.TemporaryDirectory() as raw:
            root = copy_repo(Path(raw) / "repo")
            path = root / "architecture" / "decisions" / "ADR-0058-map-export-time-dynamic-layers-layer-option-cached-root.md"
            text = path.read_text(encoding="utf-8")
            self.assertIn("amended-by:", text, "the fixture record changed shape")
            path.write_text(
                text.replace("amended-by: ADR-0100", "amended-by: ADR-0001"),
                encoding="utf-8",
            )
            findings = arch_index.reciprocity_findings(root, arch_index.load_corpus(root))
            self.assertTrue(
                any("amended-by" in finding for finding in findings),
                f"a hand-written `amended-by:` that contradicts the derived "
                f"list passed: {findings}",
            )

    def test_the_repository_has_no_contradicted_reciprocity(self):
        findings = arch_index.reciprocity_findings(REPO_ROOT, arch_index.load_corpus(REPO_ROOT))
        self.assertEqual(
            findings, [], "a record's `amended-by:` contradicts the index:\n  "
            + "\n  ".join(findings),
        )

    def test_the_gate_reports_reciprocity_after_a_regeneration(self):
        """Not a staleness failure: the index is current and the record is wrong."""
        with tempfile.TemporaryDirectory() as raw:
            root = copy_repo(Path(raw) / "repo")
            path = root / "architecture" / "decisions" / "ADR-0058-map-export-time-dynamic-layers-layer-option-cached-root.md"
            path.write_text(
                path.read_text(encoding="utf-8").replace(
                    "amended-by: ADR-0100", "amended-by: ADR-0001"
                ),
                encoding="utf-8",
            )
            self.assertEqual(run_tool("--write", root=root).returncode, 0)
            result = run_tool("--check", root=root)
            self.assertNotEqual(result.returncode, 0, "the gate passed a wrong reciprocity")
            self.assertIn("amended-by", result.stdout + result.stderr)


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
