#!/usr/bin/env python3
"""The documentation corpus is audited, the way the code corpus already is.

Run: python3 -m unittest tools/test_doc_freshness.py

The tool under test is `tools/doc-freshness.py`, which writes the same two
artefacts every other audit writes (`doc-report.json`, `doc-queue.md`, both
gitignored). It is **reporting-only** on every lane: the checks land as a queue
that drains. The code is the documentation; `architecture/principles.md` is
the philosophy.
"""
import importlib.util
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parent.parent
TOOL = REPO_ROOT / "tools" / "doc-freshness.py"


def _load(name: str, path: Path):
    spec = importlib.util.spec_from_file_location(name, path)
    module = importlib.util.module_from_spec(spec)
    sys.modules[name] = module
    spec.loader.exec_module(module)
    return module


doc_freshness = _load("doc_freshness", TOOL)

CHECK_IDS = (
    "dead-doc-link",
    "skill-reference",
    "context-bloat",
    "instruction-conflict",
    "lint-leakage",
)

SIGNAL_IDS = ("init-fossil", "lint-restatement")


def write(path: Path, text: str) -> Path:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(text, encoding="utf-8")
    return path


def clean_tree(root: Path) -> None:
    write(root / "architecture" / "principles.md", "# Principles\n")
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
        clean_tree(self.root)
        self.addCleanup(self._tmp.cleanup)

    def test_the_tool_under_test_exists(self):
        self.assertTrue(TOOL.is_file())

    def test_a_clean_tree_reports_nothing(self):
        report = doc_freshness.audit(self.root)
        self.assertEqual(report["findings"], [])

    def test_every_check_is_named_in_the_report(self):
        report = doc_freshness.audit(self.root)
        ids = [check["id"] for check in report["checks"]]
        for expected in (*CHECK_IDS, *SIGNAL_IDS):
            self.assertIn(expected, ids)

    def test_a_dead_relative_link_is_a_finding(self):
        write(self.root / "architecture" / "guide.md", "# x\n\nSee [gone](gone.md).\n")
        findings = doc_freshness.dead_doc_links(self.root)
        self.assertTrue(any("gone.md" in f["message"] for f in findings))

    def test_context_bloat_is_a_two_hundred_line_ceiling(self):
        write(self.root / "AGENTS.md", "# x\n\n" + "\n".join(f"line {n}" for n in range(250)) + "\n")
        findings = doc_freshness.context_bloat(self.root)
        self.assertTrue(any("context-bloat" == f["check"] for f in findings))

    def test_the_audit_writes_the_queue_and_the_report(self):
        done = subprocess.run(
            [sys.executable, str(TOOL), "--root", str(self.root)],
            capture_output=True, text=True,
        )
        self.assertEqual(done.returncode, 0, done.stdout + done.stderr)
        self.assertTrue((self.root / "doc-report.json").is_file())
        self.assertTrue((self.root / "doc-queue.md").is_file())

    def test_verify_runs_the_doc_audit_as_a_reporting_step(self):
        verify = (REPO_ROOT / "eng" / "verify.sh").read_text(encoding="utf-8")
        self.assertIn("eng/quality-audit.sh --report", verify)


if __name__ == "__main__":
    unittest.main()
