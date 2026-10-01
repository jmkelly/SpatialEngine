#!/usr/bin/env python3
"""Every hazardous package carries a short nested AGENTS.md, and it stays short.

Run: python3 -m unittest tools/test_package_agents.py

Nested `AGENTS.md` covered 3 of the 50 projects, and the three were the pure
boundaries — `Spatial.Core`, `Spatial.Contracts`, the architecture tests —
where an inclusion test is genuinely the right content. Two thirds of the
1100 `.cs` files sat under a project with no local guidance, and the missing
ones are the packages that own a hazard: raw SQL, byte formats, font
embedding, raster ownership, grid re-composition. By the nearest-file-wins
rule of `AGENTS.md` those are the files an agent actually reads.

The rules live in `tools/package_agents.py`, which is what the lanes call, for
the reason ADR-0148 gives: a `tools/test_*.py` the tooling suite happens to
discover runs only on a change set that touches `tools/**`, and a nested
`AGENTS.md` is a `src/**` change. This file keeps the unit tests and pins the
wiring.
"""
import re
import subprocess
import sys
import unittest
from pathlib import Path

from tools.package_agents import (  # noqa: E402  (path is set by the runner)
    MAX_LINES,
    PACKAGES,
    findings,
)

REPO = Path(__file__).resolve().parents[1]

#: A citation that carries no record. Assembled rather than written out, so
#: this file does not itself trip `AdrNumberingTests
#: .Every_adr_reference_resolves_to_exactly_one_record`, which reads every
#: tracked file for a dangling number.
BOGUS_ADR = "ADR-" + "9" * 4

AGENTS = """\
# Package

Prose that names the package and what it owns.
Two records bind it: ADR-0001 and ADR-0002.
Route by task: `architecture/distilled/core.md`.

## Never

- One thing.
- Another thing.

## Commands

- `dotnet test tests/unit/X.Tests` — the suite.
"""


def _write(root: Path, package: str, body: str) -> Path:
    path = root / package / "AGENTS.md"
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(body, encoding="utf-8")
    return path


class _Fixture(unittest.TestCase):
    def setUp(self) -> None:
        import tempfile

        self._tmp = tempfile.TemporaryDirectory()
        self.root = Path(self._tmp.name)
        for package in PACKAGES:
            (self.root / package).mkdir(parents=True, exist_ok=True)
        records = self.root / "architecture" / "decisions"
        records.mkdir(parents=True, exist_ok=True)
        for number in ("0001", "0002"):
            (records / f"ADR-{number}-a-record.md").write_text("# record\n", encoding="utf-8")
        self.addCleanup(self._tmp.cleanup)

    def seed_all(self, body: str = AGENTS) -> None:
        for package in PACKAGES:
            _write(self.root, package, body)


class PackageAgentsTests(_Fixture):
    def test_a_package_with_no_nested_agents_file_is_a_finding(self):
        self.seed_all()
        (self.root / PACKAGES[0] / "AGENTS.md").unlink()
        found = findings(self.root)
        self.assertTrue(
            any(PACKAGES[0] in f and "no nested AGENTS.md" in f for f in found), found
        )

    def test_the_shape_holds_for_every_package(self):
        self.seed_all()
        self.assertEqual([], findings(self.root))

    def test_a_file_over_the_line_budget_is_a_finding(self):
        self.seed_all()
        _write(self.root, PACKAGES[0], AGENTS + "\n" + "filler\n" * MAX_LINES)
        found = findings(self.root)
        self.assertTrue(any(PACKAGES[0] in f and "line budget" in f for f in found), found)

    def test_the_generated_orientation_block_does_not_spend_the_budget(self):
        self.seed_all()
        orientation = "<!-- orientation:begin -->\n\n## Orientation\n\n" + (
            "- one line. (SpatialEngine-rzq)\n"
        ) * 8 + "<!-- orientation:end -->\n"
        _write(self.root, PACKAGES[0], AGENTS + "\n" + orientation)
        self.assertEqual([], findings(self.root))

    def test_one_cited_record_is_not_two(self):
        self.seed_all()
        _write(self.root, PACKAGES[0], AGENTS.replace("ADR-0001 and ADR-0002", "ADR-0001"))
        found = findings(self.root)
        self.assertTrue(any(PACKAGES[0] in f and "binding ADRs" in f for f in found), found)

    def test_a_citation_of_a_record_that_does_not_exist_is_a_finding(self):
        self.seed_all()
        _write(self.root, PACKAGES[0], AGENTS.replace("ADR-0001", BOGUS_ADR))
        found = findings(self.root)
        self.assertTrue(any(BOGUS_ADR in f for f in found), found)

    def test_a_file_without_a_never_list_is_a_finding(self):
        self.seed_all()
        _write(self.root, PACKAGES[0], AGENTS.replace("## Never", "## Habits"))
        found = findings(self.root)
        self.assertTrue(any(PACKAGES[0] in f and "never-list" in f for f in found), found)

    def test_a_file_without_a_test_command_is_a_finding(self):
        self.seed_all()
        _write(self.root, PACKAGES[0], AGENTS.replace("## Commands", "## Reading"))
        found = findings(self.root)
        self.assertTrue(any(PACKAGES[0] in f and "test command" in f for f in found), found)

    def test_a_file_without_a_route_pointer_is_a_finding(self):
        self.seed_all()
        _write(self.root, PACKAGES[0], AGENTS.replace("architecture/distilled/core.md", "the docs"))
        found = findings(self.root)
        self.assertTrue(any(PACKAGES[0] in f and "route row" in f for f in found), found)


class RepositoryTests(unittest.TestCase):
    def test_the_repository_carries_one_per_hazardous_package(self):
        found = findings(REPO)
        self.assertEqual([], found)


class LaneWiringTests(unittest.TestCase):
    def test_every_lane_runs_the_check_rather_than_the_tooling_suite(self):
        verify = (REPO / "eng" / "verify.sh").read_text(encoding="utf-8")
        self.assertIn("package_agents_step() {", verify)
        self.assertIn("step python3 tools/package_agents.py", verify)
        # Defined once, called by every lane: the fast gate, the format lane
        # and the exhaustive lane. Two calls is a lane that does not see it.
        self.assertGreaterEqual(len(re.findall(r"^\s*package_agents_step$", verify, re.M)), 3)

    def test_the_check_passes_on_this_checkout(self):
        result = subprocess.run(
            [sys.executable, "tools/package_agents.py", "--root", str(REPO)],
            capture_output=True,
            text=True,
        )
        self.assertEqual(0, result.returncode, result.stdout + result.stderr)


if __name__ == "__main__":
    unittest.main()
