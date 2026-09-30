#!/usr/bin/env python3
"""Tests for tools/verify_scope.py — what `eng/verify.sh --fast` runs.

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
#: A project under `tests/` that is not a suite. It lives in the test folder,
#: it carries the test folder's name, and it declares
#: `<IsTestProject>false</IsTestProject>` because it is support code the store
#: suites run rather than a suite the runner can execute: `dotnet test` skips
#: it and writes no trx.
CONFORMANCE = ("tests/conformance/Spatial.QueryConformance/"
               "Spatial.QueryConformance.csproj")
#: Every lane runs the doc gate before anything scoped (ADR-0141): the
#: generated ADR register and index, and the dangling-citation read. It is in
#: these expected plans because a lane that quietly lost it would be a gate
#: that stopped reading the decision records. It follows the trailing-whitespace
#: check in every plan, which is the cheaper of the two repo-wide steps
#: (ADR-0143) and is first for the same reason.
DOC_GATE = "python3 tools/arch-index.py --check"
# The conflict-marker check: every lane calls it directly rather than finding
# it through the tools/**-only tooling suite, which is the hole ADR-0146 closes.
CONFLICT_MARKER_GATE = "python3 tools/conflict_markers.py"
# The repository-root check: the root carries no document answering "what is
# happening now" and the changelog is at `docs/CHANGELOG.md` (ADR-0148). It
# follows the conflict-marker check, for the same reason it is not in the
# tools/**-only tooling suite.
DOC_SURFACE_GATE = "python3 tools/doc_surface.py"

#: A real project in this repository, and the test suites that reach it.
CORE_PROJECT = "src/Spatial.Core/Spatial.Core.csproj"


def repository_test_closure(repository):
    """Every test project in `repository` that a change to Spatial.Core reaches."""
    return {p.path for p in repository.projects
            if p.is_test and p.path in repository.dependents_of({CORE_PROJECT})}


#: A project the solution does not list, and the suite that reaches it.
CLI_PROJECT = "clients/dotnet/Spatial.Cli/Spatial.Cli.csproj"
CLI_TESTS = "tests/unit/Spatial.Cli.Tests/Spatial.Cli.Tests.csproj"


def csproj(root: Path, path: str, references, is_test,
           packages: tuple = ()) -> str:
    """A project file whose references point at the other projects by path.

    `is_test` is `True`, `False` (no property at all), or the literal text of
    the property — so a fixture can write `<IsTestProject>false</IsTestProject>`,
    which is what `tests/conformance/Spatial.QueryConformance` carries and what
    the read has to answer for.
    """
    project_dir = (root / path).parent
    reference_items = "\n".join(
        f'    <ProjectReference Include="{os.path.relpath(root / r, project_dir)}" />'
        for r in references)
    package_items = "\n".join(f'    <PackageReference Include="{p}" />'
                              for p in packages)
    if isinstance(is_test, str):
        test_property = f"    <IsTestProject>{is_test}</IsTestProject>"
    else:
        test_property = "    <IsTestProject>true</IsTestProject>" if is_test else ""
    return f"""<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
{test_property}
  </PropertyGroup>
  <ItemGroup>
{package_items}
  </ItemGroup>
  <ItemGroup>
{reference_items}
  </ItemGroup>
</Project>
"""


#: A container-backed suite that lives under tests/unit, which is the shape a
#: hand-kept lane list cannot tell apart from a slow in-process suite.
CONTAINER_UNIT_TESTS = ("tests/unit/Spatial.Fake.Container.Tests/"
                        "Spatial.Fake.Container.Tests.csproj")


def _repository_with_container_suite_under_tests_unit() -> "object":
    """A throwaway repository whose unit folder holds a container-backed suite.

    The repository is read eagerly, so the throwaway tree can be deleted as
    soon as it has been read: what the lane is asked to classify afterwards is
    a `Repository` of strings.
    """
    with tempfile.TemporaryDirectory() as tmp:
        root = Path(tmp)
        for path, (references, is_test), packages in (
                (CORE, ((), False), ()),
                (CORE_TESTS, ((CORE,), True), ("xunit",)),
                (CONTAINER_UNIT_TESTS, ((CORE,), True),
                 ("xunit", "Testcontainers.PostgreSql"))):
            target = root / path
            target.parent.mkdir(parents=True, exist_ok=True)
            target.write_text(csproj(root, path, references, is_test, packages),
                              encoding="utf-8")
        (root / verify_scope.SOLUTION_FILE).write_text(
            "<Solution>\n" + "".join(
                f'  <Project Path="{p}" />\n'
                for p in (CORE, CORE_TESTS, CONTAINER_UNIT_TESTS))
            + "</Solution>\n", encoding="utf-8")
        return verify_scope.load_repository(root)


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
        # A throwaway repo with no identity of its own inherits one from the
        # machine it was made on, so `git commit` succeeds on a developer box
        # and exits 128 ("Committer identity unknown") on a CI runner that has
        # none. `--author` sets the author, not the committer, so it does not
        # save the commit. Name the committer in the repo instead, once, so
        # every commit here is the same commit everywhere. The sibling fixtures
        # (test_adr_next_number, test_migrate_tasks_to_beads) pass -c per call;
        # this one commits from two places, so the repo-local config is the
        # single place that covers both.
        self.git("config", "user.email", "t@e")
        self.git("config", "user.name", "t")
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
        self.assertEqual(self.repo.plan().test_projects, (ARCHITECTURE,))
        self.assertEqual(self.repo.plan().build_projects, (ARCHITECTURE,))

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

    def test_a_documentation_change_selects_nothing_to_format(self):
        self.repo.commit("src/Spatial.Core/README.md", "# docs\n")

        plan = self.repo.plan()

        self.assertEqual(plan.format_projects, ())
        # It still builds and tests the project it lives in: a document beside
        # a project cannot break it, but a lane that ignored every unformattable
        # file would also ignore a changed fixture.
        self.assertIn(CORE, plan.build_projects)
        self.assertIn(CORE_TESTS, plan.test_projects)

    def test_a_changed_non_code_file_still_selects_its_project(self):
        """A fixture or an embedded resource is not formattable, but it is real.

        Scoping used to map only formattable files onto projects, which is the
        right question for `--format` and the wrong one for what to compile: a
        changed `.json` beside a project has to build and test that project.
        """
        self.repo.commit("src/Spatial.Maps/fixture.json", "{}\n")

        plan = self.repo.plan()

        self.assertEqual(plan.format_projects, ())
        self.assertIn(MAPS, plan.build_projects)
        self.assertIn(MAPS_TESTS, plan.test_projects)

    def test_the_build_covers_the_changed_projects_and_their_tests(self):
        """A scoped build compiles what changed even with no test of its own."""
        self.repo.commit("src/Spatial.Maps/Map.cs")

        self.assertEqual(self.repo.plan().build_projects, (MAPS, ARCHITECTURE, MAPS_TESTS))

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
        self.assertEqual(plan.build_projects, ())


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

    def _add_to_solution(self, *paths):
        """Put fixture projects on the solution's own list.

        `load_repository` reads the `.slnx` rather than globbing, so a fixture
        project that nothing references is never read and every assertion about
        it is a `KeyError` rather than a verdict.
        """
        solution = self.repo.root / verify_scope.SOLUTION_FILE
        listed = solution.read_text(encoding="utf-8")
        solution.write_text(
            listed.replace("</Solution>", "".join(
                f'  <Project Path="{p}" />\n' for p in paths) + "</Solution>"),
            encoding="utf-8")

    def test_a_project_that_declares_it_is_not_a_test_project_is_not_a_suite(self):
        """`IsTestProject=false` is a suite declining to be one, and it says so.

        `tests/conformance/Spatial.QueryConformance` is a library the store
        suites run; it carries `<IsTestProject>false</IsTestProject>` with the
        comment saying exactly that. Reading the *presence* of the element
        instead of its value put it in the CI lanes and in `dotnet test`'s
        scope, and `dotnet test` skips it and writes no trx — so the lanes'
        `--expect` counted a suite whose result file could never appear and
        every whole-solution run ended in
        `ERROR: 25 of 26 suite(s) in this run left a result file` on a green
        tree.
        """
        path = self.repo.root / CONFORMANCE
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(csproj(self.repo.root, CONFORMANCE, (CORE,), "false"),
                        encoding="utf-8")
        self._add_to_solution(CONFORMANCE)

        repository = verify_scope.load_repository(self.repo.root)

        self.assertFalse(repository.by_path()[CONFORMANCE].is_test)

    def test_every_msbuild_spelling_of_false_is_not_a_suite(self):
        """MSBuild reads `false`, `False`, `0` and `off` as false; so does this.

        The property is MSBuild's, so its truth test is MSBuild's: a project
        that says `<IsTestProject>0</IsTestProject>` is not a suite either, and
        a reader that only special-cases the exact string `false` would put it
        back in the lanes.
        """
        fakes = [f"tests/conformance/Fake{index}/Fake{index}.csproj"
                 for index in range(4)]
        for index, value in enumerate(("false", "False", "0", "off")):
            with self.subTest(value=value):
                path = self.repo.root / fakes[index]
                path.parent.mkdir(parents=True, exist_ok=True)
                path.write_text(csproj(self.repo.root, fakes[index], (CORE,),
                                       value), encoding="utf-8")
        # The scoper reads the solution's own list, so a fixture has to be on
        # it to be read at all.
        self._add_to_solution(*fakes)

        repository = verify_scope.load_repository(self.repo.root)

        for path in fakes:
            with self.subTest(project=path):
                self.assertFalse(repository.by_path()[path].is_test)

    def test_every_project_owns_its_own_files(self):
        repository = verify_scope.load_repository(self.repo.root)

        self.assertEqual(
            repository.owning_projects(["src/Spatial.Core/Envelope.cs"]),
            {CORE})

    def test_a_windows_separator_reference_still_resolves(self):
        """The real repository writes `..\\..\\src\\...`, and it has to count.

        MSBuild accepts either separator. A reference read with backslashes on
        Linux is one unresolvable filename, so the graph silently loses every
        edge: nothing reaches anything, the scoped lane selects no tests, and it
        reports success having run none. That is the worst failure this helper
        has, and it is invisible on a machine where the plan looks plausible.
        """
        contracts_dir = (self.repo.root / CONTRACTS).parent
        windows = csproj(self.repo.root, CONTRACTS, (CORE,), False).replace(
            os.path.relpath(self.repo.root / CORE, contracts_dir),
            f"..\\{Path(CORE).parent.name}\\{Path(CORE).name}")
        self.assertIn("\\", windows, "the fixture must carry a backslash")
        (self.repo.root / CONTRACTS).write_text(windows, encoding="utf-8")

        repository = verify_scope.load_repository(self.repo.root)

        self.assertEqual(repository.by_path()[CONTRACTS].references, (CORE,))
        self.assertIn(HOST_TESTS, repository.dependents_of({CORE}))


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
                            if p.is_test and p.in_solution)

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

    def test_a_suite_that_starts_no_container_runs_in_the_unit_lane(self):
        """The lane split is read off what a suite needs, not off how long it ran.

        `Spatial.Ingest.Codec.Tests` was listed in the integration lane because
        it was measured at 2m27 while the scoping work wrote ADR-0109
        (SpatialEngine-0dx). It starts no container, opens no socket, reads no
        file and reaches no process: eight classes, 103 cases, 3–6 s warm and
        17 s from a cold `obj/` including the build, against 77 s for the other
        18 unit projects combined. The cost was a fixture — a peak-heap sampler
        that spun on `GC.GetTotalMemory` for the length of two 40,000-feature
        decodes — and that fixture is gone (3308362, SpatialEngine-ivp): the
        assertion is now reachability, which needs no sampler and no machine.

        So the suite is a unit suite again, and the list it was in was a list of
        measurements rather than of needs. `lane_projects` now derives the split
        from each project's own `PackageReference`s, which is the property that
        actually put a container on the runner in the first place, and this
        fails until it does.
        """
        codec = "tests/unit/Spatial.Ingest.Codec.Tests/Spatial.Ingest.Codec.Tests.csproj"

        self.assertNotIn(codec, self.integration)
        self.assertIn(codec, self.unit)

    def test_every_suite_in_the_integration_lane_starts_a_container(self):
        """No suite reaches the integration lane without a container dependency.

        The three suites that genuinely need a Docker daemon each take a
        `Testcontainers*` package. Deriving the lane from that means a unit
        project cannot drift into it by being slow once — which is how the
        codec suite got there — and a new container-backed suite is placed by
        adding the package it already needs, not by editing a list.
        """
        by_path = self.repository.by_path()

        for path in self.integration:
            with self.subTest(lane="integration", project=path):
                packages = by_path[path].packages
                self.assertTrue(
                    any(p.startswith("Testcontainers") for p in packages),
                    f"{path} runs in the integration lane but takes no "
                    f"Testcontainers package: {packages}")

    def test_a_suite_outside_tests_integration_that_takes_testcontainers_is_in_the_integration_lane(self):
        """The rule cuts both ways: the dependency places the suite, not the folder."""
        repository = _repository_with_container_suite_under_tests_unit()

        self.assertIn("tests/unit/Spatial.Fake.Container.Tests/"
                      "Spatial.Fake.Container.Tests.csproj",
                      verify_scope.lane_projects(repository, "integration"))
        self.assertNotIn("tests/unit/Spatial.Fake.Container.Tests/"
                         "Spatial.Fake.Container.Tests.csproj",
                         verify_scope.lane_projects(repository, "unit"))

    def test_every_reference_in_the_real_repository_resolves(self):
        """The guard that would have caught the backslash defect immediately.

        Read against the real solution rather than a fixture: 175 of the
        references in this repository are written with Windows separators, and
        on Linux each one was an unresolvable filename — an empty reference
        graph, a scoped lane that selected no tests, and a green run that had
        verified nothing. A fixture written by this suite would not have shown
        it, because the fixture writes POSIX paths.
        """
        by_path = self.repository.by_path()
        dangling = sorted(
            f"{project.path} -> {reference}"
            for project in self.repository.projects
            for reference in project.references
            if reference not in by_path)

        self.assertEqual(dangling, [])

    def test_a_project_the_solution_omits_is_still_in_the_graph(self):
        """`clients/dotnet` is not in the slnx, but the host suite references it.

        Without closing the reference, an edge points at a project the graph has
        never heard of: a change to the CLI owns no project, selects no tests,
        and the lane reports success having run nothing.
        """
        cli = "clients/dotnet/Spatial.Cli/Spatial.Cli.csproj"

        self.assertIn(cli, self.repository.by_path())
        self.assertFalse(self.repository.by_path()[cli].in_solution)
        self.assertIn(
            "tests/unit/Spatial.Cli.Tests/Spatial.Cli.Tests.csproj",
            self.repository.dependents_of({cli}))

    def test_a_change_to_a_project_the_solution_omits_selects_its_tests(self):
        # The same hole, seen from the lane rather than the graph.
        plan = verify_scope.plan_for_files(
            self.repository, ["clients/dotnet/Spatial.Cli/Program.cs"])

        self.assertIn("tests/unit/Spatial.Cli.Tests/Spatial.Cli.Tests.csproj",
                      plan.test_projects)

    def test_a_change_reaches_real_test_projects_through_the_real_graph(self):
        # The end the previous test exists for: with the graph whole, a change
        # to Spatial.Core in *this* repository selects the suites that reach it.
        # An empty graph passes every scoping test written against a fixture
        # and selects none at all.
        closure = repository_test_closure(self.repository)

        self.assertIn(
            "tests/integration/Spatial.Host.Tests/Spatial.Host.Tests.csproj",
            closure)
        self.assertIn("tests/unit/Spatial.Core.Tests/Spatial.Core.Tests.csproj",
                      closure)

    def test_test_support_is_in_neither_lane(self):
        """`--expect` is the two lane lists added up, so it must be the suites.

        `eng/verify.sh` counts `--list lane --lane unit` plus
        `--list lane --lane integration` and hands the sum to
        `tools/skip_gate.py --expect`, and CI iterates the same two lists. A
        project that declares `IsTestProject=false` is run by another suite's
        tests, so it belongs in neither: listing it made the expectation
        unreachable and cost the whole-solution lane a red run on green
        (SpatialEngine-l2k).
        """
        self.assertNotIn(CONFORMANCE, self.unit)
        self.assertNotIn(CONFORMANCE, self.integration)
        by_path = self.repository.by_path()
        for lane, projects in (("unit", self.unit),
                               ("integration", self.integration)):
            for path in projects:
                with self.subTest(lane=lane, project=path):
                    self.assertTrue(by_path[path].is_test)

    def test_every_listed_suite_would_write_a_result_file(self):
        """The property a suite must carry for `dotnet test` to execute it.

        The lanes are read into `--expect`, and a suite `dotnet test` will not
        execute writes no trx, so the two have to be the same set by
        construction: one project per lane entry that a run reports on.
        """
        by_path = self.repository.by_path()
        listed = set(self.unit) | set(self.integration)

        self.assertEqual(
            listed,
            {path for path, project in by_path.items()
             if project.is_test and project.in_solution})

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
        self.output = result.stdout
        return [line for line in result.stdout.splitlines()
                if line.startswith(("dotnet ", "python3 "))]

    def scoped_solution(self, *arguments):
        """The `.verify-scoped.slnx` a plan generated, as it printed it.

        `--plan` writes the file and the lane's trap removes it on the way out,
        so the plan output is where a test can read the scope from.
        """
        self.plan(*arguments)
        return [line.strip() for line in self.output.splitlines()
                if line.strip().startswith("<Project Path=")]

    def test_the_default_lane_is_the_fast_gate(self):
        """Build what the change reaches, and run the tests that reach it.

        One scoped solution, built and then tested: a whole-solution build is
        1 m 07 s of MSBuild loading fifty projects where this is seconds. The
        test step asks for trx and reads the skips back through
        `tools/skip_gate.py`, because a suite that skipped most of itself exits
        0 and this is the lane that merges (SpatialEngine-8lj).
        """
        self.assertEqual(self.plan(), [
            "python3 tools/trailing_whitespace.py src/Spatial.Maps/Map.cs",
            CONFLICT_MARKER_GATE,
            DOC_SURFACE_GATE,
            DOC_GATE,
            "dotnet build .verify-scoped.slnx",
            "dotnet test .verify-scoped.slnx --no-build --logger trx "
            "--results-directory .verify-test-results",
            "python3 tools/skip_gate.py --results-dir .verify-test-results "
            "--expect 2",
        ])

    def test_the_scoped_solution_holds_the_changed_project_and_its_tests(self):
        """The generated solution is the plan; nothing is tested that is not in it."""
        scoped = self.scoped_solution()

        self.assertIn(f'<Project Path="{MAPS}" />', scoped)
        self.assertIn(f'<Project Path="{MAPS_TESTS}" />', scoped)
        self.assertIn(f'<Project Path="{ARCHITECTURE}" />', scoped)

    def test_the_default_lane_still_catches_a_broken_test_in_a_changed_project(self):
        """The under-run guard, through the script: the project is in the plan."""
        self.assertIn(f'<Project Path="{MAPS_TESTS}" />', self.scoped_solution())

    def test_the_default_lane_does_not_run_an_unchanged_projects_tests(self):
        """The reason the default lane is cheap at all."""
        scoped = " ".join(self.scoped_solution())

        self.assertNotIn(HOST_TESTS, scoped)
        self.assertNotIn(CORE_TESTS, scoped)

    def test_the_scoped_solution_does_not_outlive_the_lane(self):
        """A stale one in the repository root keeps building yesterday's change set."""
        self.plan()
        self.assertFalse((self.repo.root / ".verify-scoped.slnx").exists())

    def test_the_default_lane_never_formats(self):
        """Formatting is enforced by the format lane and by CI, not by the loop."""
        self.assertNotIn("dotnet format", " ".join(self.plan()))

    def test_the_default_lane_runs_the_tooling_tests_when_tools_moved(self):
        # Not `tools/verify_scope.py` itself: that is the program the lane
        # shells out to, and emptying it would answer nothing.
        self.repo.commit("tools/adr-next-number.py", "# edited\n")

        self.assertIn("python3 -m unittest discover --start-directory tools",
                      " ".join(self.plan()))

    def test_a_tooling_change_is_not_a_documentation_change(self):
        # `tools/**` owns no csproj, so nothing would be built for it; the
        # tooling tests are the lane's whole answer to that change set.
        self.repo.commit("tools/seed/fetch.py", "# edited\n")

        self.assertEqual(self.plan(), [
            "python3 tools/trailing_whitespace.py src/Spatial.Maps/Map.cs "
            "tools/seed/fetch.py",
            CONFLICT_MARKER_GATE,
            DOC_SURFACE_GATE,
            DOC_GATE,
            "dotnet build .verify-scoped.slnx",
            "dotnet test .verify-scoped.slnx --no-build --logger trx "
            "--results-directory .verify-test-results",
            "python3 tools/skip_gate.py --results-dir .verify-test-results "
            "--expect 2",
            "python3 -m unittest discover --start-directory tools --pattern test_*.py",
        ])

    def test_the_format_lane_is_scoped_to_the_changed_projects(self):
        self.assertEqual(self.plan("--format"), [
            "python3 tools/trailing_whitespace.py src/Spatial.Maps/Map.cs",
            CONFLICT_MARKER_GATE,
            DOC_SURFACE_GATE,
            DOC_GATE,
            f"dotnet format {MAPS} --verify-no-changes",
        ])

    def test_the_format_lane_never_builds_or_tests(self):
        plan = " ".join(self.plan("--format"))

        self.assertNotIn("dotnet build", plan)
        self.assertNotIn("dotnet test", plan)

    def test_the_full_lane_is_the_old_flat_gate(self):
        self.assertEqual(self.plan("--full"), [
            "python3 tools/trailing_whitespace.py",
            CONFLICT_MARKER_GATE,
            DOC_SURFACE_GATE,
            DOC_GATE,
            "dotnet format SpatialEngine.slnx --verify-no-changes",
            "dotnet build SpatialEngine.slnx",
            "dotnet test SpatialEngine.slnx --no-build --logger trx "
            "--results-directory .verify-test-results",
            "python3 tools/skip_gate.py --results-dir .verify-test-results "
            "--expect 4",
            "python3 -m unittest discover --start-directory tools --pattern test_*.py",
        ])

    def test_quick_is_a_synonym_for_the_default_lane(self):
        self.assertEqual(self.plan("--quick"), self.plan())

    def test_fast_is_the_name_the_merge_tool_uses(self):
        """Named, so CI=true cannot promote a merge to the 20-minute lane."""
        self.assertEqual(self.plan("--fast"), self.plan())

    def test_fast_is_not_promoted_by_ci_true(self):
        self.assertEqual(self.plan("--fast", ci="true"), self.plan())

    def test_ci_true_with_no_lane_named_runs_the_full_gate(self):
        """A workflow calling the bare script must not get the scoped lane.

        ADR-0118: the bare name is the build gate, so on a runner it means the
        gate until a lane says otherwise. Without this the next workflow
        written by someone not thinking about it silently under-runs.
        """
        self.assertEqual(self.plan(ci="true"), [
            "python3 tools/trailing_whitespace.py",
            CONFLICT_MARKER_GATE,
            DOC_SURFACE_GATE,
            DOC_GATE,
            "dotnet format SpatialEngine.slnx --verify-no-changes",
            "dotnet build SpatialEngine.slnx",
            "dotnet test SpatialEngine.slnx --no-build --logger trx "
            "--results-directory .verify-test-results",
            "python3 tools/skip_gate.py --results-dir .verify-test-results "
            "--expect 4",
            "python3 -m unittest discover --start-directory tools --pattern test_*.py",
        ])

    def test_a_named_lane_beats_ci_true(self):
        """The split CI jobs each own half of --full, so --format stays --format."""
        self.assertEqual(self.plan("--format", ci="true"), [
            "python3 tools/trailing_whitespace.py src/Spatial.Maps/Map.cs",
            CONFLICT_MARKER_GATE,
            DOC_SURFACE_GATE,
            DOC_GATE,
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
            "python3 tools/trailing_whitespace.py",
            CONFLICT_MARKER_GATE,
            DOC_SURFACE_GATE,
            DOC_GATE,
            "dotnet build SpatialEngine.slnx",
            "dotnet test SpatialEngine.slnx --no-build --logger trx "
            "--results-directory .verify-test-results",
            "python3 tools/skip_gate.py --results-dir .verify-test-results "
            "--expect 4",
            "python3 -m unittest discover --start-directory tools --pattern test_*.py",
        ])
    def test_an_unresolvable_base_falls_back_to_the_whole_formatter(self):
        self.assertEqual(self.plan("--format", base="origin/does-not-exist"), [
            "python3 tools/trailing_whitespace.py",
            CONFLICT_MARKER_GATE,
            DOC_SURFACE_GATE,
            DOC_GATE,
            "dotnet format SpatialEngine.slnx --verify-no-changes",
        ])

    def test_skip_tests_drops_a_named_suite_and_says_so(self):
        """A named skip is loud, or it is indistinguishable from scoping."""
        scoped = self.scoped_solution("--skip-tests=Maps")
        output = self.output

        self.assertNotIn(f'<Project Path="{MAPS_TESTS}" />', scoped)
        self.assertIn("skipped by --skip-tests Maps", output)
        self.assertIn(MAPS_TESTS, output)

    def test_skip_tests_never_drops_the_architecture_guard(self):
        # It is the one suite that is cheap and structural, so a skip pattern
        # that matched it would be a skip of the lane's whole point.
        scoped = self.scoped_solution("--skip-tests=Spatial.Architecture")

        self.assertIn(f'<Project Path="{ARCHITECTURE}" />', scoped)

    def test_an_unknown_argument_is_refused(self):
        with self.assertRaises(subprocess.CalledProcessError):
            self.plan("--everything")

    def test_the_script_is_valid_bash(self):
        subprocess.run(["bash", "-n", str(self.VERIFY)], check=True)

    def test_the_scoped_solution_does_not_outlive_the_lane(self):
        """A stale one in the repository root keeps building yesterday's change set."""
        self.plan()
        self.assertFalse((self.repo.root / ".verify-scoped.slnx").exists())


if __name__ == "__main__":
    unittest.main()
