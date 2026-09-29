#!/usr/bin/env python3
"""Tests for tools/verify_scope.py — what `eng/verify.sh --quick` runs.

Run: python3 -m unittest tools/test_verify_scope.py

The point of the quick lane is that it runs the tests a change can affect and
no others, so the two failure modes are both defects: a broken test in a changed
project must be caught (a narrow lane that under-runs is worse than the flat
gate), and a broken test in an unchanged project must not be (the whole reason
the lane exists). Those are the first two cases here; the rest pin the
boundaries — the architecture guard that never goes away, the python tooling
tests that only run when `tools/**` moves, and the refusal to guess when the
change set cannot be read.
"""
import importlib.util
import os
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path

SCRIPT = Path(__file__).with_name("verify_scope.py")


def load_script():
    """Import the dashed script filename as a module.

    It is registered in `sys.modules` before it runs: a dataclass resolves its
    own module through that registry, and exec'ing an unregistered module
    leaves the decorator looking at `None`.
    """
    spec = importlib.util.spec_from_file_location("verify_scope", SCRIPT)
    module = importlib.util.module_from_spec(spec)
    sys.modules["verify_scope"] = module
    spec.loader.exec_module(module)
    return module


verify_scope = load_script()

CORE = "src/Spatial.Core/Spatial.Core.csproj"
CONTRACTS = "src/Spatial.Contracts/Spatial.Contracts.csproj"
HOST = "src/Spatial.Host/Spatial.Host.csproj"
#: A project nothing else in the fake repository references, so the scoping
#: tests can tell "unrelated" from "reached through a hop".
MAPS = "src/Spatial.Maps/Spatial.Maps.csproj"
CORE_TESTS = "tests/unit/Spatial.Core.Tests/Spatial.Core.Tests.csproj"
MAPS_TESTS = "tests/unit/Spatial.Maps.Tests/Spatial.Maps.Tests.csproj"
HOST_TESTS = "tests/integration/Spatial.Host.Tests/Spatial.Host.Tests.csproj"
ARCHITECTURE = verify_scope.ARCHITECTURE_PROJECT


def csproj(root: Path, path: str, references, is_test: bool) -> str:
    """A project file whose references point at the other projects by path."""
    project_dir = (root / path).parent
    reference_items = "\n".join(
        f'    <ProjectReference Include="{os.path.relpath(root / r, project_dir)}" />'
        for r in references)
    test_property = "    <IsTestProject>true</IsTestProject>" if is_test else ""
    return f"""<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
{test_property}
  </PropertyGroup>
  <ItemGroup>
{reference_items}
  </ItemGroup>
</Project>
"""


class FakeRepo:
    """A throwaway git repository shaped like this one.

    Two chains, so the tests can tell "references it" from "reaches it through
    a hop": Spatial.Host.Tests -> Spatial.Host -> Spatial.Contracts ->
    Spatial.Core, and Spatial.Maps.Tests -> Spatial.Core.
    """

    LAYOUT = {
        CORE: ((), False),
        CONTRACTS: ((CORE,), False),
        HOST: ((CONTRACTS,), False),
        MAPS: ((), False),
        CORE_TESTS: ((CORE,), True),
        HOST_TESTS: ((HOST,), True),
        MAPS_TESTS: ((MAPS,), True),
        ARCHITECTURE: ((CONTRACTS,), True),
    }

    def __init__(self, root: Path):
        self.root = root
        for path, (references, is_test) in self.LAYOUT.items():
            target = root / path
            target.parent.mkdir(parents=True, exist_ok=True)
            target.write_text(
                csproj(root, path, references, is_test), encoding="utf-8")
            source = target.with_suffix(".cs")
            source.write_text("// placeholder\n", encoding="utf-8")
        solution = root / verify_scope.SOLUTION_FILE
        solution.write_text(
            "<Solution>\n" + "".join(
                f'  <Project Path="{p}" />\n' for p in self.LAYOUT)
            + "</Solution>\n", encoding="utf-8")
        self.git("init", "-q", "-b", "main")
        self.git("add", "-A")
        self.git("commit", "-q", "-m", "base", "--author=t <t@e>")
        self.git("branch", "feature")

    def git(self, *args):
        return subprocess.run(["git", *args], cwd=self.root, check=True,
                              capture_output=True, text=True)

    def write(self, relative: str, text: str = "// changed\n"):
        path = self.root / relative
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(text, encoding="utf-8")

    def commit(self, relative: str, text: str = "// changed\n"):
        self.write(relative, text)
        self.git("add", "-A")
        self.git("commit", "-q", "-m", "change " + relative,
                 "--author=t <t@e>")

    def checkout_feature(self):
        self.git("checkout", "-q", "feature")

    def plan(self):
        return verify_scope.plan_quick(
            verify_scope.load_repository(self.root), "main")


class PlanTests(unittest.TestCase):
    """What a branch's change set selects."""

    def setUp(self):
        self._tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self._tmp.cleanup)
        self.repo = FakeRepo(Path(self._tmp.name))
        self.repo.checkout_feature()

    def test_a_changed_project_runs_its_own_tests(self):
        """The under-run guard: a broken test in a changed project must run."""
        self.repo.commit("src/Spatial.Maps/Map.cs")

        plan = self.repo.plan()

        self.assertIn(MAPS_TESTS, plan.test_projects)
        self.assertIn(MAPS, plan.format_projects)

    def test_a_test_reached_through_two_hops_also_runs(self):
        """A change under Spatial.Core is still a change to Spatial.Host."""
        self.repo.commit("src/Spatial.Core/Envelope.cs")

        self.assertIn(HOST_TESTS, self.repo.plan().test_projects)

    def test_an_unchanged_project_s_own_tests_do_not_run(self):
        """The reason the lane exists: nothing else pays for this change."""
        self.repo.commit("src/Spatial.Maps/Map.cs")

        plan = self.repo.plan()

        self.assertIn(MAPS_TESTS, plan.test_projects)
        self.assertNotIn(HOST_TESTS, plan.test_projects,
                         "Spatial.Host is unchanged, so its 22-minute suite "
                         "does not belong in the inner loop")
        self.assertNotIn(CORE_TESTS, plan.test_projects)
        self.assertNotIn(ARCHITECTURE, plan.format_projects)

    def test_architecture_tests_always_run(self):
        """They are the structural guard, so no change set turns them off."""
        self.repo.commit("src/Spatial.Maps/Map.cs")

        self.assertIn(ARCHITECTURE, self.repo.plan().test_projects)

    def test_architecture_tests_run_on_an_empty_branch(self):
        self.repo.plan()

        self.assertEqual(self.repo.plan().test_projects, (ARCHITECTURE,))

    def test_a_changed_test_file_selects_that_test_project(self):
        self.repo.commit("tests/unit/Spatial.Maps.Tests/MapTests.cs")

        plan = self.repo.plan()

        self.assertIn(MAPS_TESTS, plan.test_projects)
        self.assertNotIn(CORE_TESTS, plan.test_projects)
        self.assertIn(MAPS_TESTS, plan.format_projects)

    def test_uncommitted_and_untracked_changes_count(self):
        """The inner loop is mostly dirty working tree, not commits."""
        self.repo.write("src/Spatial.Maps/Map.cs")

        self.assertIn(MAPS_TESTS, self.repo.plan().test_projects)

        self.repo.write("src/Spatial.Maps/NewFile.cs")

        self.assertIn(MAPS_TESTS, self.repo.plan().test_projects)

    def test_a_documentation_change_selects_no_project(self):
        self.repo.commit("src/Spatial.Core/README.md", "# docs\n")

        plan = self.repo.plan()

        self.assertEqual(plan.format_projects, ())
        self.assertEqual(plan.test_projects, (ARCHITECTURE,))

    def test_python_tooling_tests_only_when_tools_moved(self):
        self.repo.commit("src/Spatial.Maps/Map.cs")
        self.assertFalse(self.repo.plan().run_python_tooling)

        self.repo.commit("tools/verify_scope.py", "# edited\n")
        self.assertTrue(self.repo.plan().run_python_tooling)

    def test_a_solution_wide_change_refuses_to_scope(self):
        """Changing the build for every project is not a scoping question."""
        self.repo.commit("Directory.Packages.props", "<Project />\n")

        plan = self.repo.plan()

        self.assertTrue(plan.exhaustive)
        self.assertTrue(plan.run_python_tooling,
                        "a fallback to the full gate runs the tooling tests")

    def test_an_unreadable_change_set_refuses_to_scope(self):
        """A base that does not resolve must not read as 'nothing changed'."""
        plan = verify_scope.plan_quick(
            verify_scope.load_repository(self.repo.root), "origin/nope")

        self.assertTrue(plan.exhaustive)
        self.assertEqual(plan.changed_files, ())


class RepositoryTests(unittest.TestCase):
    """Reading the solution's own project list, not a glob of the tree."""

    def setUp(self):
        self._tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self._tmp.cleanup)
        self.repo = FakeRepo(Path(self._tmp.name))

    def test_references_resolve_to_repository_paths(self):
        repository = verify_scope.load_repository(self.repo.root)

        self.assertEqual(
            repository.by_path()[CONTRACTS].references, (CORE,))
        self.assertTrue(repository.by_path()[CORE_TESTS].is_test)
        self.assertFalse(repository.by_path()[CORE].is_test)

    def test_every_project_owns_its_own_files(self):
        repository = verify_scope.load_repository(self.repo.root)

        self.assertEqual(
            repository.owning_projects(["src/Spatial.Core/Envelope.cs"]),
            {CORE})


class LaneTests(unittest.TestCase):
    """The two CI lanes must partition the test projects.

    Read against the real solution: a lane that quietly dropped a project would
    leave the pull request without a suite it has today, and that is invisible
    in the YAML.
    """

    @classmethod
    def setUpClass(cls):
        cls.repository = verify_scope.load_repository(
            SCRIPT.parent.parent)
        cls.unit = verify_scope.lane_projects(cls.repository, "unit")
        cls.integration = verify_scope.lane_projects(cls.repository, "integration")

    def test_every_test_project_runs_in_exactly_one_lane(self):
        everything = sorted(p.path for p in self.repository.projects
                            if p.is_test)

        self.assertEqual(sorted(self.unit + self.integration), everything)
        self.assertEqual(set(self.unit) & set(self.integration), set())

    def test_the_integration_lane_is_the_container_suites(self):
        self.assertIn(
            "tests/integration/Spatial.PostGIS.Tests/Spatial.PostGIS.Tests.csproj",
            self.integration)
        self.assertIn(
            "tests/integration/Spatial.SqlServer.Tests/Spatial.SqlServer.Tests.csproj",
            self.integration)
        self.assertIn(
            "tests/integration/Spatial.Host.Tests/Spatial.Host.Tests.csproj",
            self.integration)
        self.assertIn(
            "tests/unit/Spatial.Ingest.Codec.Tests/Spatial.Ingest.Codec.Tests.csproj",
            self.integration)

    def test_the_unit_lane_still_runs_the_architecture_guard(self):
        self.assertIn(ARCHITECTURE, self.unit)

    def test_an_unknown_lane_is_refused(self):
        with self.assertRaises(ValueError):
            verify_scope.lane_projects(self.repository, "everything")


class ScriptLaneTests(unittest.TestCase):
    """What each `eng/verify.sh` lane does, checked against the real script.

    The script and the scoping helper are copied into a throwaway repository
    with a known change set, and run with `--plan`, so these assert the exact
    steps a lane would take without spending a lane. Running the real script
    rather than reading it is the point: a lane that quietly fell back to
    everything would still contain every expected line, and only the whole plan
    tells the two apart.
    """

    VERIFY = SCRIPT.parent.parent / "eng" / "verify.sh"

    def setUp(self):
        self._tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self._tmp.cleanup)
        self.repo = FakeRepo(Path(self._tmp.name))
        self.repo.checkout_feature()
        (self.repo.root / "eng").mkdir(exist_ok=True)
        (self.repo.root / "tools").mkdir(exist_ok=True)
        (self.repo.root / "eng" / "verify.sh").write_text(
            self.VERIFY.read_text(encoding="utf-8"), encoding="utf-8")
        (self.repo.root / "tools" / "verify_scope.py").write_text(
            SCRIPT.read_text(encoding="utf-8"), encoding="utf-8")
        # Committed on the base, so the gate itself is not part of the change
        # under test: a branch that moved tools/** runs the python tooling
        # tests, and this suite is about the two-project case.
        self.repo.git("add", "-A")
        self.repo.git("commit", "-q", "-m", "the gate under test",
                      "--author=t <t@e>")
        self.repo.git("branch", "-f", "main", "HEAD")
        self.repo.commit("src/Spatial.Maps/Map.cs")

    def plan(self, *arguments, base="main", ci="false"):
        # `ci` is forced rather than inherited: the CI=true clause below is
        # about what a runner gets, and a suite whose result depends on where
        # it was invoked is not a suite anyone can read.
        env = {key: value for key, value in os.environ.items() if key != "CI"}
        env["CI"] = ci
        result = subprocess.run(
            ["bash", "eng/verify.sh", "--plan", *arguments],
            cwd=str(self.repo.root), check=True, capture_output=True, text=True,
            env={**env, "VERIFY_BASE": base})
        return [line for line in result.stdout.splitlines()
                if line.startswith(("dotnet ", "python3 "))]

    def test_the_default_lane_is_the_build_gate(self):
        """Build, and the tests that reach the change. No format, no full run."""
        self.assertEqual(self.plan(), [
            "dotnet build SpatialEngine.slnx",
            f"dotnet test {ARCHITECTURE} --no-build",
            f"dotnet test {MAPS_TESTS} --no-build",
        ])

    def test_the_default_lane_still_catches_a_broken_test_in_a_changed_project(self):
        """The under-run guard, through the script: the project is in the plan."""
        self.assertIn(f"dotnet test {MAPS_TESTS} --no-build", self.plan())

    def test_the_default_lane_does_not_run_an_unchanged_projects_tests(self):
        """The reason the default lane is cheap at all."""
        plan = " ".join(self.plan())

        self.assertNotIn(f"dotnet test {HOST_TESTS}", plan)
        self.assertNotIn(f"dotnet test {CORE_TESTS}", plan)

    def test_the_default_lane_never_formats(self):
        """Formatting is enforced by the format lane and by CI, not by the loop."""
        self.assertNotIn("dotnet format", " ".join(self.plan()))

    def test_the_default_lane_runs_the_tooling_tests_when_tools_moved(self):
        # Not `tools/verify_scope.py` itself: that is the program the lane
        # shells out to, and emptying it would answer nothing.
        self.repo.commit("tools/adr-next-number.py", "# edited\n")

        self.assertIn("python3 -m unittest discover --start-directory tools",
                      " ".join(self.plan()))

    def test_the_format_lane_is_scoped_to_the_changed_projects(self):
        self.assertEqual(self.plan("--format"), [
            f"dotnet format {MAPS} --verify-no-changes",
        ])

    def test_the_format_lane_never_builds_or_tests(self):
        plan = " ".join(self.plan("--format"))

        self.assertNotIn("dotnet build", plan)
        self.assertNotIn("dotnet test", plan)

    def test_the_full_lane_is_the_old_flat_gate(self):
        self.assertEqual(self.plan("--full"), [
            "dotnet format SpatialEngine.slnx --verify-no-changes",
            "dotnet build SpatialEngine.slnx",
            "dotnet test SpatialEngine.slnx --no-build",
            "python3 -m unittest discover --start-directory tools --pattern test_*.py",
        ])

    def test_quick_is_a_synonym_for_the_default_lane(self):
        self.assertEqual(self.plan("--quick"), self.plan())

    def test_ci_true_with_no_lane_named_runs_the_full_gate(self):
        """A workflow calling the bare script must not get the scoped lane.

        ADR-0118: the bare name is the build gate, so on a runner it means the
        gate until a lane says otherwise. Without this the next workflow
        written by someone not thinking about it silently under-runs.
        """
        self.assertEqual(self.plan(ci="true"), [
            "dotnet format SpatialEngine.slnx --verify-no-changes",
            "dotnet build SpatialEngine.slnx",
            "dotnet test SpatialEngine.slnx --no-build",
            "python3 -m unittest discover --start-directory tools --pattern test_*.py",
        ])

    def test_a_named_lane_beats_ci_true(self):
        """The split CI jobs each own half of --full, so --format stays --format."""
        self.assertEqual(self.plan("--format", ci="true"), [
            f"dotnet format {MAPS} --verify-no-changes",
        ])

    def test_ci_true_is_not_every_value_of_ci(self):
        """Only the literal `true` GitHub Actions sets selects the full lane."""
        for value in ("1", "yes", "True", ""):
            with self.subTest(ci=value):
                self.assertEqual(self.plan(ci=value), self.plan())

    def test_an_unresolvable_base_falls_back_to_everything(self):
        """A scoping failure costs time; a silent under-run costs a defect."""
        self.assertEqual(self.plan(base="origin/does-not-exist"), [
            "dotnet build SpatialEngine.slnx",
            "dotnet test SpatialEngine.slnx --no-build",
            "python3 -m unittest discover --start-directory tools --pattern test_*.py",
        ])

    def test_an_unresolvable_base_falls_back_to_the_whole_formatter(self):
        self.assertEqual(self.plan("--format", base="origin/does-not-exist"), [
            "dotnet format SpatialEngine.slnx --verify-no-changes",
        ])

    def test_an_unknown_argument_is_refused(self):
        with self.assertRaises(subprocess.CalledProcessError):
            self.plan("--everything")

    def test_the_script_is_valid_bash(self):
        subprocess.run(["bash", "-n", str(self.VERIFY)], check=True)


if __name__ == "__main__":
    unittest.main()
