#!/usr/bin/env python3
"""Tests for `tools/skip_gate.py` — the lane's answer to a mass-skipped suite.

Run: python3 -m unittest tools/test_skip_gate.py

A suite that reports 10 passed / 104 skipped has not passed. The container-backed
suites skip every case they cannot reach — `Skip.If(!DockerAvailable, …)` — so
contention under parallel worktrees, or a daemon that is not there at all, shows
up as a wall of skips and `dotnet test` exits 0 (SpatialEngine-8lj, from
SpatialEngine-u2x.58's merge). The gate has to read the per-suite skip count and
fail, because a merge tool that merges on exit 0 cannot tell the two apart.

The first class is the checker on synthetic trx files: the mass-skip case, the
all-green case, the small number of conditional skips that must not fail a
merge, and a run that produced fewer result files than suites in scope (a
`trx` file name is stamped to the second, so two projects finishing together
can overwrite one another — a suite that vanished is a suite that must not
count as a pass either). The second class runs the *real* `eng/verify.sh` with
`dotnet` stubbed, because the acceptance is the lane's exit code, not the
checker's.
"""
import os
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path

# The packages whose nested `AGENTS.md` the lane's repository check reads, so
# this fixture seeds one per package for the same reason it seeds the checks
# themselves: a lane that fails for a package the fixture never modelled would
# be measuring the gates rather than the skip gate.
from tools.package_agents import PACKAGES as _PACKAGES  # noqa: E402  (path is set by the runner)

REPO = Path(__file__).resolve().parent.parent
PACKAGES = _PACKAGES
SKIP_GATE = REPO / "tools" / "skip_gate.py"
VERIFY = REPO / "eng" / "verify.sh"

#: The run that was merged on a green: Spatial.SqlServer.Tests under parallel
#: worktrees, with the two conformance tests the bead existed to fix among the
#: skipped (SpatialEngine-u2x.58).
SQLSERVER = ("Spatial.SqlServer.Tests", 114, 104)
POSTGIS = ("Spatial.PostGIS.Tests", 212, 0)
HOST = ("Spatial.Host.Tests", 790, 0)


def trx(project, total, skipped, failed=0, passed=None):
    """A trx file as the VSTest trx logger writes one, minimal but real.

    Only the parts the gate reads: the `Counters` the summary carries, and one
    `UnitTestResult` per test so the file parses and the assembly can be named.
    """
    if passed is None:
        passed = total - skipped - failed
    results = "\n".join(
        f'    <UnitTestResult testName="{project}.Suite.Test{index}" '
        f'outcome="{"NotExecuted" if index < skipped else "Passed"}" />'
        for index in range(min(total, 3)))
    return (
        '<?xml version="1.0" encoding="utf-8"?>\n'
        '<TestRun id="1" name="host 2026-09-30 22:48:32"\n'
        '         xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">\n'
        '  <Results>\n'
        f'{results}\n'
        '  </Results>\n'
        '  <ResultSummary outcome="Completed">\n'
        f'    <Counters total="{total}" executed="{total - skipped}" '
        f'passed="{passed}" failed="{failed}" error="0" timeout="0" aborted="0" '
        'inconclusive="0" passedButRunAborted="0" notRunnable="0" '
        f'notExecuted="{skipped}" disconnected="0" warning="0" completed="0" '
        'inProgress="0" pending="0" />\n'
        '  </ResultSummary>\n'
        '</TestRun>\n')


class SkipGateTests(unittest.TestCase):
    """`tools/skip_gate.py` over a directory of trx files."""

    def setUp(self):
        self._tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self._tmp.cleanup)
        self.results = Path(self._tmp.name)

    def write(self, name, text):
        (self.results / f"{name}.trx").write_text(text, encoding="utf-8")

    def run_gate(self, *arguments):
        return subprocess.run(
            [sys.executable, str(SKIP_GATE), "--results-dir", str(self.results),
             *arguments],
            capture_output=True, text=True)

    def test_a_mass_skipped_suite_fails_the_gate(self):
        """The reported case: 10 passed / 104 skipped, and the gate says no.

        `Spatial.PostGIS.Tests` and `Spatial.Host.Tests` being green is the
        tell that Docker is reachable and this is contention, not a missing
        socket, so the other suite's skips are not an environment excuse.
        """
        for project, total, skipped in (POSTGIS, HOST, SQLSERVER):
            self.write(project, trx(project, total, skipped))

        result = self.run_gate()

        self.assertNotEqual(0, result.returncode)
        self.assertIn("Spatial.SqlServer.Tests", result.stdout)
        self.assertIn("104", result.stdout)

    def test_a_green_run_is_green(self):
        for project, total, skipped in (POSTGIS, HOST):
            self.write(project, trx(project, total, skipped))

        self.assertEqual(0, self.run_gate().returncode)

    def test_a_handful_of_conditional_skips_is_not_a_mass_skip(self):
        """Six skipped cases out of 790 is a conditional test, not a suite that
        did not run — the threshold is what keeps this from becoming noise the
        swarm routes around."""
        self.write("Spatial.Host.Tests", trx("Spatial.Host.Tests", 790, 6))

        result = self.run_gate()

        self.assertEqual(0, result.returncode)
        self.assertIn("6 skipped", result.stdout)

    def test_the_threshold_is_readable_from_the_environment(self):
        """A suite with a genuinely conditional case can be moved over the
        line, loudly, without editing the gate."""
        self.write("Spatial.Host.Tests", trx("Spatial.Host.Tests", 790, 6))
        env = {**os.environ, "VERIFY_MIN_SKIPPED": "5",
               "VERIFY_SKIP_RATIO": "0.005"}

        result = subprocess.run(
            [sys.executable, str(SKIP_GATE), "--results-dir", str(self.results)],
            capture_output=True, text=True, env=env)

        self.assertNotEqual(0, result.returncode)

    def test_a_suite_with_no_result_file_is_not_a_pass(self):
        """A trx file is stamped to the second, so two projects finishing in the
        same second can overwrite one another. Fewer result files than suites in
        scope means a suite's numbers were lost, and a lost suite is not a green
        suite either."""
        self.write("Spatial.PostGIS.Tests", trx("Spatial.PostGIS.Tests", 212, 0))
        self.write("Spatial.SqlServer.Tests", trx("Spatial.SqlServer.Tests", 114, 104))

        result = self.run_gate("--expect", "3")

        self.assertNotEqual(0, result.returncode)
        self.assertIn("2 of 3", result.stdout)

    def test_a_green_run_with_a_missing_suite_still_fails_the_gate(self):
        """Two green suites and a third that left no file is the uncovered case.

        The test above (`test_a_suite_with_no_result_file_is_not_a_pass`) does
        name an unmet `--expect`, but it puts the mass-skipped
        `Spatial.SqlServer.Tests` in the directory alongside the green one — so
        it passes on the mass-skip rule and would pass with the `--expect` check
        deleted outright. Here every suite that *did* report is green, so the
        only thing wrong with the run is the suite whose numbers were lost, and
        a hard ERROR printed over exit 0 is exactly how a missing suite reaches
        `tools/bd-merge-bead.py` and merges (SpatialEngine-huv).
        """
        for project, total, skipped in (POSTGIS, HOST):
            self.write(project, trx(project, total, skipped))

        result = self.run_gate("--expect", "3")

        self.assertNotEqual(
            0, result.returncode,
            f"a missing suite was reported and the gate still exited 0:\n"
            f"{result.stdout}")
        self.assertIn("2 of 3", result.stdout)

    def test_the_expected_count_is_not_invented_when_no_run_happened(self):
        """`--expect 0` is a run with no suites in scope, and a directory with
        no trx in it is the same thing: nothing to judge, nothing to fail."""
        self.assertEqual(0, self.run_gate("--expect", "0").returncode)


#: A stub `dotnet` that answers every subcommand and, for `test`, writes a trx
#: for each project **in the solution it was handed** whose name appears in
#: SKIP_COUNTS. It stands in for the real thing so the lane's own wiring — the
#: logger, the results directory, the exit code — is what is under test.
#:
#: The solution, not SKIP_COUNTS, is what decides which suites ran. A stub that
#: wrote a trx for every entry would make a mass-skip failure mean "the fixture
#: mentioned this suite" rather than "the lane selected it", and a suite the
#: lane had dropped would still leave a result file behind (SpatialEngine-0cd).
STUB_DOTNET = """#!/usr/bin/env bash
set -euo pipefail
args=("$@")
results=""
for ((i = 0; i < ${#args[@]}; i++)); do
  case "${args[$i]}" in
    --results-directory) results="${args[$((i + 1))]}" ;;
  esac
done
if [[ "${args[0]:-}" == "test" && -n "$results" ]]; then
  mkdir -p "$results"
  solution="${args[1]:-}"
  paths="$(grep -o 'Project Path="[^"]*"' "$solution" 2>/dev/null \\
    | sed 's/.*Path="//; s/"$//' || true)"
  for path in ${paths}; do
    name="$(basename "$path" .csproj)"
    entry=""
    for candidate in ${SKIP_COUNTS:-}; do
      [[ "${candidate%%=*}" == "$name" ]] && entry="$candidate"
    done
    [[ -n "$entry" ]] || continue
    counts="${entry#*=}"
    total="${counts%%:*}"
    skipped="${counts#*:}"
    cat > "$results/${name}_net10.0.trx" <<TRX
<?xml version="1.0" encoding="utf-8"?>
<TestRun id="1" name="host 2026-09-30 22:48:32"
         xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
  <Results>
    <UnitTestResult testName="${name}.Suite.Test0" outcome="Passed" />
  </Results>
  <ResultSummary outcome="Completed">
    <Counters total="${total}" executed="$((total - skipped))" \
passed="$((total - skipped))" failed="0" error="0" timeout="0" aborted="0" \
inconclusive="0" passedButRunAborted="0" notRunnable="0" notExecuted="${skipped}" \
disconnected="0" warning="0" completed="0" inProgress="0" pending="0" />
  </ResultSummary>
</TestRun>
TRX
  done
fi
exit 0
"""

CSPROJ = """<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    {test}
  </PropertyGroup>
  <ItemGroup>
{references}  </ItemGroup>
</Project>
"""

#: A one-record corpus for the lane's doc gate (ADR-0141): front matter in the
#: one schema, which is what the generator reads.
FIXTURE_ADR = """---
status: accepted
date: 2026-09-30
deciders: maintainer + agent
summary: The fixture carries a decision corpus so the doc gate has one to read.
---

# ADR-0001: A fixture decision

## Context

The lane under test is a repository, and a repository has decision records.
"""

#: The digest, with the markers the register is generated between and nothing
#: else: the first run of `tools/arch-index.py --write` fills the block.
FIXTURE_DIGEST = """# Distilled

<!-- arch-index:register:begin -->
<!-- arch-index:register:end -->
"""

ARCHITECTURE = ("tests/architecture/Spatial.Architecture.Tests/"
                "Spatial.Architecture.Tests.csproj")
MAPS = "src/Spatial.Maps/Spatial.Maps.csproj"
MAPS_TESTS = "tests/unit/Spatial.Maps.Tests/Spatial.Maps.Tests.csproj"
SQLSERVER_TESTS = ("tests/integration/Spatial.SqlServer.Tests/"
                   "Spatial.SqlServer.Tests.csproj")
SQLSERVER_TESTS_NAME = "Spatial.SqlServer.Tests"
ARCHITECTURE_NAME = "Spatial.Architecture.Tests"
MAPS_TESTS_NAME = "Spatial.Maps.Tests"


def reference_from(referrer, target):
    """The `ProjectReference` include one project writes for another.

    Relative to the *referring* project — which is how MSBuild reads it, and
    how `tools/verify_scope.py` resolves it — in the Windows-separator form
    every project in this repository uses and the scoper normalises
    (`../..` read as a single filename resolves to nothing, so a fixture that
    wrote the form a scoper cannot read would scope to no suite at all).
    """
    relative = os.path.relpath(target, os.path.dirname(referrer))
    return relative.replace("/", "\\")


class HelpUsageTests(unittest.TestCase):
    """`eng/verify.sh --help`: the opt-out has to be findable in the usage block.

    The usage block is the run of lines the header writes as `#   <flag> …` —
    one entry per lane, each with its continuations. Its end is *derived* from
    that shape rather than a line number, because `print_header` prints the
    whole header and a range that stops short silently drops whatever was
    appended (SpatialEngine-2hf).
    """

    def help_usage_block(self):
        """The lines `eng/verify.sh --help` prints as a usage entry.

        The block is the run of lines the header indents under `#   ` — one
        entry per flag, each with its continuations — so its end is derived from
        that shape rather than a line number: `print_header` prints the whole
        header, and a range that stopped short silently dropped whatever was
        appended (SpatialEngine-2hf).
        """
        result = subprocess.run(["bash", str(VERIFY), "--help"], check=True,
                                capture_output=True, text=True)
        return [line for line in result.stdout.splitlines()
                if line.startswith("#   ")]

    def test_the_usage_block_documents_skip_tests(self):
        """`--skip-tests` was documented only in the in-script comment that
        `--help` strips, and in a passing mention in the skip-counts paragraph
        — so a reader who ran `--help` to find the opt-out for a contended
        container suite found no usage line for it at all, while
        `test_the_space_separated_form_the_help_documents_is_accepted` asserted
        against a help that never printed the spelling (SpatialEngine-abj)."""
        usage = self.help_usage_block()

        entries = [line for line in usage
                   if line.lstrip("# ").startswith("eng/verify.sh --skip-tests")]

        self.assertTrue(
            entries,
            "the usage block has no --skip-tests entry:\n" + "\n".join(usage))
        self.assertTrue(any("<substring>" in line for line in entries),
                        f"the entry does not show the value's form:\n{entries[0]}")

        # The entry's own paragraph has to say the rule, not leave it in the
        # comment `--help` strips: a value that normalises to no pattern — a
        # space, a comma, `", ,"` — is rejected by name rather than accepted
        # and dropped on the floor (SpatialEngine-drb). The paragraph runs to
        # the next entry's first line, which is the next usage line that opens
        # with a flag or its synonym column.
        start = usage.index(entries[0])
        paragraph = [entries[0]]
        for line in usage[start + 1:]:
            stripped = line.lstrip("# ").lstrip()
            if stripped.startswith(("eng/", "(or:")):
                break
            paragraph.append(line)

        self.assertIn("rejected by name", "\n".join(paragraph),
                      "the --skip-tests entry does not say a value that "
                      "normalises to no pattern is rejected by name")


class LaneExitCodeTests(unittest.TestCase):
    """`eng/verify.sh` with a stubbed `dotnet`: the acceptance is the exit code.

    The gate is what merges (ADR-0134), so "reports a hard ERROR" is not the
    same as the bead and only a non-zero exit stops
    `tools/bd-merge-bead.py`.
    """

    def setUp(self):
        self._tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self._tmp.cleanup)
        self.root = Path(self._tmp.name) / "repo"
        self.root.mkdir()

        def project(relative, references=(), is_test=False):
            path = self.root / relative
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_text(CSPROJ.format(
                test="<IsTestProject>true</IsTestProject>" if is_test else "",
                # The form this repository writes, and the one
                # `tools/verify_scope.py` normalises: MSBuild resolves a
                # `ProjectReference` against the *referring* project's
                # directory, so a repo-root-relative path is a reference to
                # nothing and the fixture's reference graph is empty (the
                # scoped plan then selects no suite at all).
                references="".join(
                    f'    <ProjectReference Include="{reference_from(relative, r)}" />\n'
                    for r in references)), encoding="utf-8")
            return relative

        paths = [project(MAPS), project(MAPS_TESTS, (MAPS,), is_test=True),
                 project(SQLSERVER_TESTS, (MAPS,), is_test=True),
                 project(ARCHITECTURE, (), is_test=True)]
        (self.root / "SpatialEngine.slnx").write_text(
            "<Solution>\n" + "".join(f'  <Project Path="{p}" />\n' for p in paths)
            + "</Solution>\n", encoding="utf-8")
        # The lane runs repo-wide checks before anything scoped: the
        # trailing-whitespace check (ADR-0143), the final-newline check
        # (ADR-0186), the conflict-marker check (ADR-0146), the
        # repository-root check (ADR-0148) and the doc gate
        # (ADR-0141), then the reporting-only documentation-freshness audit
        # (`eng/quality-audit.sh --report`, SpatialEngine-imz.2). The fixture
        # therefore carries all of those tools, the bead-protocol gate among
        # them (ADR-0152), and — because the doc gate
        # regenerates the ADR register and index and fails on a stale one — a
        # decision corpus and its generated register. The harness build
        # (ADR-0190) is in that list too: every lane builds the `*.csproj`
        # under `eng/` the solution does not name, so a fixture without the
        # tool would be red on a gate that has nothing to do with skips. The
        # stub `dotnet` on PATH answers it. A fixture without any of
        # them would be measuring the gates rather than the skip gate, and the
        # lane would go red for a reason that has nothing to do with skips.
        for name in ("eng/verify.sh", "eng/quality-audit.sh", "tools/verify_scope.py",
                     "tools/skip_gate.py", "tools/trailing_whitespace.py",
                     "tools/final_newline.py",
                     "tools/conflict_markers.py", "tools/doc_surface.py",
                     "tools/changelog.py",
                     "tools/doc-freshness.py", "tools/beads_gate.py",
                     "tools/package_agents.py", "tools/spike_harnesses.py"):
            (self.root / name).parent.mkdir(parents=True, exist_ok=True)
            (self.root / name).write_text(
                (REPO / name).read_text(encoding="utf-8"), encoding="utf-8")
        # The changelog check reads the same file (ADR-0173), and the fixture's
        # changelog carries no `## [Unreleased]` section, which is the shape the
        # repository now ships.
        # The root check reads the version and the changelog, so the fixture
        # carries a root that passes it: a product version and the one
        # changelog at the documented path (ADR-0148).
        (self.root / "Directory.Build.props").write_text(
            "<Project>\n  <PropertyGroup>\n    <Version>0.1.0</Version>\n"
            "  </PropertyGroup>\n</Project>\n", encoding="utf-8")
        (self.root / "docs").mkdir()
        (self.root / "docs" / "CHANGELOG.md").write_text(
            "# Changelog\n\n## [0.1.0] - 2026-09-12\n\n- **Fixture release**\n",
            encoding="utf-8")
        # The repository's own `trim_trailing_whitespace` rule, so the lane's
        # first step is a real check over the fixture's own file rather than a
        # missing tool.
        (self.root / ".editorconfig").write_text(
            "root = true\n\n[*]\ntrim_trailing_whitespace = true\n", encoding="utf-8")
        # The loop's own artefacts and python's bytecode, gitignored as they are
        # in this repository (`tools/test_doc_freshness.py` asserts the real
        # `.gitignore` lists the artefacts, and it has always listed
        # `__pycache__/`). Without this the reporting step's `doc-queue.md` /
        # `doc-report.json` and the `.pyc` its shared-check import writes are
        # untracked files, and a second lane run in the same fixture reads them
        # as part of the change set — which `tools/verify_scope.py` answers
        # with the unscoped plan, so the fast gate silently becomes the full one
        # and the lane runs a tooling discover the fixture cannot satisfy
        # (SpatialEngine-imz.2).
        (self.root / ".gitignore").write_text(
            "doc-queue.md\ndoc-report.json\n__pycache__/\n", encoding="utf-8")

        # The nested-AGENTS check reads the whole repository, so the fixture
        # carries one conforming file per hazardous package rather than failing
        # the lane for a package it never meant to model. Two records, a
        # never-list, a test command and a route pointer each: the shape the
        # check enforces, at the fixture's own scale.
        for package in PACKAGES:
            directory = self.root / package
            directory.mkdir(parents=True, exist_ok=True)
            (directory / "AGENTS.md").write_text(
                "# Fixture package\n\n## Never\n\n"
                "- A thing this fixture does not do.\n\n## Commands\n\n"
                "- `dotnet test` — the fixture's suite.\n", encoding="utf-8")

        self.git("init", "-q", "-b", "main")
        self.git("config", "user.email", "t@e")
        self.git("config", "user.name", "t")
        self.git("add", "-A")
        self.git("commit", "-q", "-m", "base", "--author=t <t@e>")
        self.git("branch", "feature")
        self.git("checkout", "-q", "feature")
        # A change to Spatial.Maps reaches its own suite and the SQL Server one
        # through the reference edge, which is the closure the fast lane runs.
        (self.root / "src/Spatial.Maps/Map.cs").write_text("// changed\n",
                                                           encoding="utf-8")
        self.git("add", "-A")
        self.git("commit", "-q", "-m", "change", "--author=t <t@e>")

        # The stub goes first on PATH, so the lane's `dotnet` is this one.
        self.bin = Path(self._tmp.name) / "bin"
        self.bin.mkdir()
        dotnet = self.bin / "dotnet"
        dotnet.write_text(STUB_DOTNET, encoding="utf-8")
        dotnet.chmod(0o755)

    def git(self, *args):
        subprocess.run(["git", *args], cwd=self.root, check=True,
                       capture_output=True, text=True)

    def lane(self, *arguments, skip_counts):
        env = {k: v for k, v in os.environ.items() if k != "CI"}
        # VERIFY_NO_QUEUE: the bead-protocol gate reads the beads queue for
        # check 1, and a fixture running a lane has no business reading the
        # repository's real one — `tools/test_no_real_queue.py` fails the whole
        # tooling suite if it does. The lane then reports check 1 as not
        # judged, which is what this suite is measuring either way (ADR-0152).
        env.update({"PATH": f"{self.bin}{os.pathsep}{env['PATH']}",
                    "SKIP_COUNTS": skip_counts,
                    "VERIFY_BASE": "main",
                    "VERIFY_NO_QUEUE": "1"})
        return subprocess.run(
            ["bash", "eng/verify.sh", *arguments], cwd=self.root, env=env,
            capture_output=True, text=True)

    def test_the_fixture_reaches_its_suites_through_the_reference_edges(self):
        """The fixture has to scope, or every other assertion in this class is
        close to vacuous.

        `tools/verify_scope.py` resolves a `ProjectReference` against the
        *referring project*'s directory, which is what MSBuild does, and
        `../..`-style Windows separators are the form this repository writes.
        A fixture that wrote its references repo-root-relative therefore had an
        empty reference graph: `dependents_of` found nothing, the scoped plan
        came back as the architecture guard alone, and the lane under test had
        no droppable suite — so a `--skip-tests` test passed because the drop
        was a no-op, and a mass-skip test passed because the stub wrote a trx
        for every project it was told about rather than for the ones the lane
        selected.
        """
        plan = subprocess.run(
            [sys.executable, "tools/verify_scope.py", "--base", "main",
             "--list", "plan", "--machine"], cwd=self.root,
            capture_output=True, text=True, check=True).stdout

        selected = {line.removeprefix("tests:")
                    for line in plan.splitlines() if line.startswith("tests:")}
        self.assertEqual({MAPS_TESTS, SQLSERVER_TESTS, ARCHITECTURE}, selected,
                         f"the fixture scopes to the wrong suites:\\n{plan}")

    def test_the_lane_fails_when_a_suite_in_its_scope_mass_skips(self):
        result = self.lane(
            skip_counts=f"{SQLSERVER_TESTS_NAME}=114:104 "
                        f"{MAPS_TESTS_NAME}=56:0 {ARCHITECTURE_NAME}=84:0")

        self.assertNotEqual(0, result.returncode,
                            f"the lane merged a mass-skipped suite:\n{result.stdout}")
        self.assertIn(SQLSERVER_TESTS_NAME, result.stdout)

    def test_the_lane_passes_when_every_suite_in_its_scope_ran(self):
        result = self.lane(
            skip_counts=f"{SQLSERVER_TESTS_NAME}=114:0 "
                        f"{MAPS_TESTS_NAME}=56:0 {ARCHITECTURE_NAME}=84:0")

        self.assertEqual(0, result.returncode,
                         f"a green run was reported red:\n{result.stdout}")

    def test_a_dropped_suite_is_absent_from_the_count_rather_than_skipped(self):
        """`--skip-tests` is the opt-out, and it drops the suite from the run —
        so a lane that dropped the suite must not then fail for the suite it no
        longer ran.

        The dropped suite is stated as mass-skipping on purpose. A drop that
        did nothing would leave that suite in the scoped solution, and the run
        would be red: this passes only because the lane removed the suite from
        the build *and* the test list (SpatialEngine-0cd)."""
        for form in (("--skip-tests=SqlServer",), ("--skip-tests", "SqlServer")):
            with self.subTest(form=form):
                result = self.lane(*form,
                                   skip_counts=f"{SQLSERVER_TESTS_NAME}=114:104 "
                                               f"{MAPS_TESTS_NAME}=56:0 "
                                               f"{ARCHITECTURE_NAME}=84:0")

                self.assertEqual(
                    0, result.returncode,
                    f"a dropped suite still failed the lane:\n{result.stdout}")
                self.assertIn(SQLSERVER_TESTS, result.stdout)

    def test_the_space_separated_form_the_help_documents_is_accepted(self):
        """The spelling the help, the runbook and ADR-0134 §3 all print.

        `--skip-tests <substring>` is what a reader copies out of
        `eng/verify.sh --help`, and the first pass over `$@` matched only
        `--skip-tests=*`, so its `*)` case fired on the bare flag: exit 2,
        `unknown argument: --skip-tests`, before the second pass that did
        handle the two-argument form ever ran (SpatialEngine-0v9). The merge
        tool passes the `=` form, which is why this survived.
        """
        result = self.lane("--skip-tests", "SqlServer", "--plan",
                           skip_counts="")

        self.assertEqual(0, result.returncode,
                         f"the documented form was rejected:\n{result.stderr}")
        self.assertNotIn("unknown argument", result.stderr)

    def test_a_valueless_skip_tests_is_rejected(self):
        """A flag that swallowed the next argument, or arrived with nothing at
        all, would be a parameter accepted and ignored — and swallowing
        `--plan` would silently turn the exhaustive question off."""
        for form in (("--skip-tests",), ("--skip-tests", "--plan"),
                     ("--skip-tests=",)):
            with self.subTest(form=form):
                result = self.lane(*form, skip_counts="")

                self.assertEqual(
                    2, result.returncode,
                    f"a valueless --skip-tests was accepted:\n{result.stdout}")
                self.assertIn("--skip-tests needs a substring", result.stderr)

    def test_a_skip_tests_that_normalises_to_nothing_is_rejected(self):
        """A value that trims and drops to no pattern at all is a valueless
        `--skip-tests` wearing a costume, and the same rejection is owed it.

        `add_skip_patterns` splits on commas, strips whitespace from each
        element and drops the empty ones, so `" "`, `",,"` and `", ,"` all
        arrive at the lane as a *present* argument that leaves `SKIP_PATTERNS`
        empty: the flag is accepted, the lane runs, and nothing is dropped —
        a parameter accepted and ignored, against the hard wall. It matters
        because `tools/bd-merge-bead.py` passes `--skip-tests=<substring>` on
        the merge gate, so a caller whose suite filter matched nothing gets a
        merge that silently runs the suite it meant to leave to CI, with no
        `skipped by --skip-tests` line to say so in the close reason.

        (Before the fix the worst case was quieter than ignored: because the
        helper's last command was the `[[ -n "$pattern" ]]` test, a value whose
        *final* element was empty failed under `set -e` and took the whole
        script down with it, exit 1 and no message at all.)
        """
        for value in (" ", ",", ", ,", ",,,"):
            for form in (("--skip-tests=",), ("--skip-tests",)):
                with self.subTest(value=value, form=form):
                    result = self.lane(*form, value, "--plan",
                                       skip_counts="")

                    self.assertEqual(
                        2, result.returncode,
                        f"a --skip-tests of {value!r} was accepted and did "
                        f"nothing:\n{result.stdout}\n{result.stderr}")
                    self.assertIn("--skip-tests needs a substring",
                                  result.stderr)

    def test_a_skip_tests_whose_empty_element_is_not_last_still_runs(self):
        """The rejection is for a value that normalises to *nothing*, not for
        a value with a stray separator in it: `--skip-tests=,SqlServer` is one
        pattern with an empty element before it, and it has to be honoured
        (SpatialEngine-drb)."""
        result = self.lane("--skip-tests=,SqlServer", "--plan", skip_counts="")

        self.assertEqual(0, result.returncode,
                         f"a separator-prefixed list was rejected:\n{result.stderr}")
        self.assertIn("skipped by --skip-tests SqlServer", result.stdout)
        self.assertIn(SQLSERVER_TESTS, result.stdout)

    def test_both_spellings_can_be_combined_and_mixed_with_a_lane(self):
        """One pass, both forms, and the lane flag in between — the arguments
        have to be read wherever they appear, not as a leading pair only.

        Both suites are stated as mass-skipping, and the architecture guard is
        what may not be dropped, so this asserts that the patterns were
        *honoured* rather than merely accepted: a run that left either suite in
        the scoped solution is red. It is observable now that the fixture's
        `ProjectReference` edges resolve and the fixture's test projects are in
        the plan at all (SpatialEngine-0cd).
        """
        result = self.lane("--skip-tests", "SqlServer", "--fast",
                           "--skip-tests=Maps", skip_counts=(
                               f"{SQLSERVER_TESTS_NAME}=114:104 "
                               f"{MAPS_TESTS_NAME}=56:0 {ARCHITECTURE_NAME}=84:0"))

        self.assertEqual(0, result.returncode,
                         f"a mixed invocation was not honoured:\n{result.stdout}")
        self.assertIn(ARCHITECTURE_NAME, result.stdout)


if __name__ == "__main__":
    unittest.main()
