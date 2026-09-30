#!/usr/bin/env python3
"""Tests for tools/trailing_whitespace.py — the check `dotnet format` cannot do.

Run: python3 -m unittest tools/test_trailing_whitespace.py

`.editorconfig` claims `trim_trailing_whitespace = true` for `[*]`, and the only
formatter in this repository enforces it on a line that carries code. It does
not enforce it on a **comment-only** line: measured on SDK 10.0.400 by
injecting the violation and running `dotnet format <project>
--verify-no-changes`, a code-line violation is `error WHITESPACE: Fix
whitespace formatting. Delete 2 characters` and exit 2, and the same violation
on a comment-only line is exit 0 and nothing reported — at project scope and at
solution scope, and `dotnet format whitespace` without `--verify-no-changes`
leaves it in place (SpatialEngine-emo). So the repository's stated rule was
enforced over a subset of the files that break it, and no lane could see the
other subset: the formatter is the whole of what `eng/verify.sh --format` and
the CI `verify` job run for whitespace.

The first case here is that defect, reproduced as a test: a comment-only line
with trailing whitespace in a file whose sibling code line is clean. The second
is the case the formatter already covers, so the replacement is a superset of
the old signal rather than a different one. The rest pin the edges — the
`.editorconfig` opt-out that markdown depends on, directories, binary files,
a path that is not there — and the two that are about the gate rather than the
checker: the repository ships no violation, and every lane (and CI) runs the
check at all, because a checker nothing calls is the same invisible hole one
level up.
"""
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parent.parent
SCRIPT = REPO_ROOT / "tools" / "trailing_whitespace.py"
VERIFY_SH = REPO_ROOT / "eng" / "verify.sh"
CI_YML = REPO_ROOT / ".github" / "workflows" / "ci.yml"

#: The repository's own `[*]` section, so a fixture tree is configured the way
#: the repository is rather than by whatever the check defaults to.
EDITORCONFIG = """root = true

[*]
trim_trailing_whitespace = true

[*.md]
trim_trailing_whitespace = false
"""

#: A C# file in which the only trailing whitespace is on a comment-only line —
#: invisible to `dotnet format` on 10.0.400. Built by concatenation so this file
#: carries no trailing whitespace of its own to be found by.
COMMENT_VIOLATION = (
    "// A comment whose trailing spaces no formatter deletes.   \n"
    "public sealed class Sample\n"
    "{\n"
    "}\n"
)

#: The same file with the violation on the code line, which the formatter does
#: report — the case that must not be lost by the replacement.
CODE_VIOLATION = (
    "// A clean comment.\n"
    "public sealed class Sample   \n"
    "{\n"
    "}\n"
)


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


class ChecksTrailingWhitespaceTests(unittest.TestCase):
    """What the check finds, and what it deliberately does not."""

    def test_a_comment_only_line_with_trailing_whitespace_is_reported(self):
        # The defect: `dotnet format --verify-no-changes` exits 0 on exactly
        # this file (SpatialEngine-emo), so nothing in any lane sees it.
        with tree({".editorconfig": EDITORCONFIG,
                   "src/Sample.cs": COMMENT_VIOLATION}) as root:
            result = run_check(Path(root), "src/Sample.cs")
        self.assertEqual(result.returncode, 1, result.stdout + result.stderr)
        self.assertIn("src/Sample.cs:1:", result.stdout)
        self.assertIn("trailing whitespace", result.stdout)

    def test_a_code_line_with_trailing_whitespace_is_still_reported(self):
        # What the formatter already caught, so the check is a superset of the
        # old signal and not a different one.
        with tree({".editorconfig": EDITORCONFIG,
                   "src/Sample.cs": CODE_VIOLATION}) as root:
            result = run_check(Path(root), "src/Sample.cs")
        self.assertEqual(result.returncode, 1, result.stdout + result.stderr)
        self.assertIn("src/Sample.cs:2:", result.stdout)

    def test_a_clean_file_passes(self):
        with tree({".editorconfig": EDITORCONFIG,
                   "src/Sample.cs": "// A clean comment.\npublic sealed class Sample\n{\n}\n",
                   }) as root:
            result = run_check(Path(root), "src/Sample.cs")
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)

    def test_editorconfig_switching_the_rule_off_is_honoured(self):
        # `[*.md] trim_trailing_whitespace = false` is load-bearing: two trailing
        # spaces at the end of a markdown line are a hard line break, so a check
        # that ignored it would demand a documentation change on every prose
        # wrap.
        body = "A line with a hard break.  \nThe next line.\n"
        with tree({".editorconfig": EDITORCONFIG, "notes.md": body,
                   "notes.txt": body}) as root:
            markdown = run_check(Path(root), "notes.md")
            other = run_check(Path(root), "notes.txt")
        self.assertEqual(markdown.returncode, 0, markdown.stdout + markdown.stderr)
        self.assertEqual(other.returncode, 1, other.stdout + other.stderr)

    def test_a_directory_argument_is_walked(self):
        with tree({".editorconfig": EDITORCONFIG,
                   "src/one.cs": COMMENT_VIOLATION,
                   "src/nested/two.cs": COMMENT_VIOLATION}) as root:
            result = run_check(Path(root), "src")
        self.assertEqual(result.returncode, 1, result.stdout + result.stderr)
        self.assertIn("src/one.cs:1:", result.stdout)
        self.assertIn("src/nested/two.cs:1:", result.stdout)

    def test_no_argument_scans_the_whole_repository(self):
        # The form the full lane and CI use, over every tracked file plus the
        # untracked ones git is not ignoring.
        with tree({".editorconfig": EDITORCONFIG,
                   "src/Sample.cs": COMMENT_VIOLATION}) as root:
            subprocess.run(["git", "-C", root, "init", "--quiet"], check=True)
            subprocess.run(["git", "-C", root, "add", "-A"], check=True)
            result = run_check(Path(root))
        self.assertEqual(result.returncode, 1, result.stdout + result.stderr)
        self.assertIn("src/Sample.cs:1:", result.stdout)

    def test_a_file_with_no_trailing_newline_is_not_a_violation(self):
        # `insert_final_newline` is the formatter's business; this check reads
        # one rule and does not grow into a second formatter nobody measured.
        with tree({".editorconfig": EDITORCONFIG,
                   "src/Sample.cs": "// A comment with no newline after it."}) as root:
            result = run_check(Path(root), "src/Sample.cs")
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)

    def test_a_binary_file_is_not_read_as_text(self):
        with tempfile.TemporaryDirectory() as root:
            (Path(root) / ".editorconfig").write_text(EDITORCONFIG, encoding="utf-8")
            (Path(root) / "logo.png").write_bytes(b"\x89PNG \x00 \n")
            result = run_check(Path(root), "logo.png")
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)

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
        # Each lane calls the same helper, so the helper is asserted once and
        # the call is asserted per lane: a lane that stopped calling it is the
        # invisible hole this bead is about, and a helper that stopped running
        # the check is the same hole one level down.
        for marker, block in self.lanes().items():
            self.assertIn("whitespace_step", block,
                          f"the lane at {marker!r} does not run the trailing-whitespace check")

    def test_the_helper_runs_the_check(self):
        text = VERIFY_SH.read_text(encoding="utf-8")
        start = text.index("whitespace_step() {")
        body = text[start:text.index("\n}", start)]
        self.assertIn("tools/trailing_whitespace.py", body)

    def test_ci_runs_the_check(self):
        # The CI `verify` job spells its steps out rather than calling the
        # script, so wiring the lanes alone would leave the detector that
        # follows every merge with the same hole.
        self.assertIn("tools/trailing_whitespace.py",
                      CI_YML.read_text(encoding="utf-8"))


class RepositoryTests(unittest.TestCase):
    """The repository itself, which is the finding the gate is for."""

    def test_no_tracked_file_carries_trailing_whitespace(self):
        result = subprocess.run(
            [sys.executable, str(SCRIPT), "--repo", str(REPO_ROOT)],
            capture_output=True, text=True,
        )
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)


if __name__ == "__main__":
    unittest.main()
