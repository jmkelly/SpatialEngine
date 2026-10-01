#!/usr/bin/env python3
"""Tests for tools/final_newline.py — the rule no lane runs.

Run: python3 -m unittest tools/test_final_newline.py

`.editorconfig` claims `insert_final_newline = true` for `[*]`, and `dotnet
format` reports a C# file that breaks it as `error FINALNEWLINE: Fix final
newline. Insert '\n'`, exit 2 — but since ADR-0134 the formatter is not on the
merge path, so between an edit and `main` nothing reads that rule. Measured at
`origin/main` `eb909b5c` on 2026-10-01, `dotnet format SpatialEngine.slnx
--verify-no-changes` on a clean tree exited 2 with 22 violations in 16 files,
14 of them this rule, every one of them byte-identical on `main`
(SpatialEngine-744). The detector was CI, which is on the far side of `main`.

The first case here is that defect, reproduced as a test: a clean C# checkout
must exit 0. The rest pin the edges — the file types the formatter reads (a
`.csproj` with no final newline is exit 0 from `dotnet format`, so the check
must not claim it), an empty file, a binary file, the `.editorconfig` opt-out,
directories — and the two that are about the gate rather than the checker: the
repository ships no violation, and every lane and CI run the check at all,
because a checker nothing calls is the same invisible hole one level up.
"""
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parent.parent
SCRIPT = REPO_ROOT / "tools" / "final_newline.py"
VERIFY_SH = REPO_ROOT / "eng" / "verify.sh"
CI_YML = REPO_ROOT / ".github" / "workflows" / "ci.yml"

#: The repository's own `[*]` section, so a fixture tree is configured the way
#: the repository is rather than by whatever the check defaults to.
EDITORCONFIG = """root = true

[*]
insert_final_newline = true

[*.md]
trim_trailing_whitespace = false
"""

#: The violation the formatter reports and this bead's check reports: a C# file
#: that ends in a brace with no newline after it.
NO_FINAL_NEWLINE = "// A comment.\npublic sealed class Sample\n{\n}"


def run_check(root: Path, *args: str) -> subprocess.CompletedProcess:
    """The check over `root`, as the lanes invoke it."""
    return subprocess.run(
        [sys.executable, str(SCRIPT), "--repo", str(root), *args],
        capture_output=True, text=True,
    )


def tree(files: dict) -> tempfile.TemporaryDirectory:
    """A temporary directory holding `files`, keyed by relative path."""
    directory = tempfile.TemporaryDirectory()
    for name, content in files.items():
        path = Path(directory.name) / name
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(content, encoding="utf-8")
    return directory


class ChecksFinalNewlineTests(unittest.TestCase):
    """What the check finds, and what it deliberately does not."""

    def test_a_csharp_file_with_no_final_newline_is_reported(self):
        # The defect: `dotnet format --verify-no-changes` exits 2 on exactly this
        # file, and since ADR-0134 no lane runs the formatter.
        with tree({".editorconfig": EDITORCONFIG,
                   "src/Sample.cs": NO_FINAL_NEWLINE}) as root:
            result = run_check(Path(root), "src/Sample.cs")
        self.assertEqual(result.returncode, 1, result.stdout + result.stderr)
        self.assertIn("src/Sample.cs: no final newline", result.stdout)

    def test_a_clean_file_passes(self):
        with tree({".editorconfig": EDITORCONFIG,
                   "src/Sample.cs": "// A comment.\npublic sealed class Sample\n{\n}\n",
                   }) as root:
            result = run_check(Path(root), "src/Sample.cs")
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)

    def test_a_project_file_is_not_this_checks_business(self):
        # Measured, not assumed: `dotnet format
        # clients/dotnet/Spatial.Client/Spatial.Client.csproj
        # --verify-no-changes` exits 0 on a `.csproj` with no final newline, so
        # `FINALNEWLINE` is a diagnostic over C# source and only C# source.
        # Claiming the rest of `[*]` would be a second project with ~190
        # findings behind it.
        with tree({".editorconfig": EDITORCONFIG,
                   "src/Sample.csproj": NO_FINAL_NEWLINE}) as root:
            result = run_check(Path(root), "src/Sample.csproj")
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)

    def test_an_empty_file_is_clean(self):
        # There is no last byte to end with, so there is nothing to insert.
        with tempfile.TemporaryDirectory() as root:
            (Path(root) / ".editorconfig").write_text(EDITORCONFIG, encoding="utf-8")
            (Path(root) / "Empty.cs").write_bytes(b"")
            result = run_check(Path(root), "Empty.cs")
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)

    def test_a_binary_file_is_not_read_as_text(self):
        with tempfile.TemporaryDirectory() as root:
            (Path(root) / ".editorconfig").write_text(EDITORCONFIG, encoding="utf-8")
            (Path(root) / "logo.cs").write_bytes(b"\x89PNG \x00")
            result = run_check(Path(root), "logo.cs")
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)

    def test_editorconfig_switching_the_rule_off_is_honoured(self):
        # No section in this repository sets `insert_final_newline = false`, but
        # the scope is read rather than assumed, so a file exempted later is
        # honoured instead of being demanded back into compliance.
        body = "// Generated.\npublic sealed class Sample\n{\n}"
        with tree({".editorconfig": EDITORCONFIG + "\n[generated/*.cs]\n"
                   "insert_final_newline = false\n",
                   "generated/Sample.cs": body,
                   "src/Sample.cs": body}) as root:
            generated = run_check(Path(root), "generated/Sample.cs")
            source = run_check(Path(root), "src/Sample.cs")
        self.assertEqual(generated.returncode, 0, generated.stdout + generated.stderr)
        self.assertEqual(source.returncode, 1, source.stdout + source.stderr)

    def test_a_directory_argument_is_walked(self):
        with tree({".editorconfig": EDITORCONFIG,
                   "src/one.cs": NO_FINAL_NEWLINE,
                   "src/nested/two.cs": NO_FINAL_NEWLINE}) as root:
            result = run_check(Path(root), "src")
        self.assertEqual(result.returncode, 1, result.stdout + result.stderr)
        self.assertIn("src/one.cs: no final newline", result.stdout)
        self.assertIn("src/nested/two.cs: no final newline", result.stdout)

    def test_no_argument_scans_the_whole_repository(self):
        # The form the full lane and CI use, over every tracked file plus the
        # untracked ones git is not ignoring.
        with tree({".editorconfig": EDITORCONFIG,
                   "src/Sample.cs": NO_FINAL_NEWLINE}) as root:
            subprocess.run(["git", "-C", root, "init", "--quiet"], check=True)
            subprocess.run(["git", "-C", root, "add", "-A"], check=True)
            result = run_check(Path(root))
        self.assertEqual(result.returncode, 1, result.stdout + result.stderr)
        self.assertIn("src/Sample.cs: no final newline", result.stdout)

    def test_a_path_that_is_not_there_is_an_error_not_a_pass(self):
        with tree({".editorconfig": EDITORCONFIG}) as root:
            result = run_check(Path(root), "src/does-not-exist.cs")
        self.assertEqual(result.returncode, 2, result.stdout + result.stderr)


class LaneWiringTests(unittest.TestCase):
    """The checker is worthless unless the lanes that gate a merge call it."""

    def lanes(self):
        """`eng/verify.sh` split into the text of each lane's block."""
        text = VERIFY_SH.read_text(encoding="utf-8")
        markers = ["# --- the format lane",
                   "# --- the full lane",
                   "# --- the default lane"]
        self.assertTrue(all(m in text for m in markers),
                        "eng/verify.sh no longer carries the lane markers these tests split on")
        blocks = {}
        for index, marker in enumerate(markers):
            end = markers[index + 1] if index + 1 < len(markers) else len(text)
            blocks[marker] = text[text.index(marker):text.index(end) if end != len(text) else len(text)]
        return blocks

    def test_every_verify_lane_runs_the_check(self):
        for marker, block in self.lanes().items():
            self.assertIn("final_newline_step", block,
                          f"the lane at {marker!r} does not run the final-newline check")

    def test_the_helper_runs_the_check(self):
        text = VERIFY_SH.read_text(encoding="utf-8")
        start = text.index("final_newline_step() {")
        body = text[start:text.index("\n}", start)]
        self.assertIn("tools/final_newline.py", body)

    def test_ci_runs_the_check(self):
        # The CI `verify` job spells its steps out rather than calling the
        # script, so wiring the lanes alone would leave the detector that
        # follows every merge with the same hole.
        self.assertIn("tools/final_newline.py",
                      CI_YML.read_text(encoding="utf-8"))


class RepositoryTests(unittest.TestCase):
    """The repository itself, which is the finding the gate is for."""

    def test_no_csharp_file_lacks_its_final_newline(self):
        result = subprocess.run(
            [sys.executable, str(SCRIPT), "--repo", str(REPO_ROOT)],
            capture_output=True, text=True,
        )
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)


if __name__ == "__main__":
    unittest.main()