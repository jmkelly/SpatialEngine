#!/usr/bin/env bash
# One-command verification: formatting check, build, full test suite.
# Every change must pass this before it is considered done.
set -euo pipefail
cd "$(dirname "$0")/.."

echo "== format check =="
# No --no-restore: a clean checkout has no project.assets.json yet, and the
# format check loads every project through MSBuild before the build step runs.
dotnet format SpatialEngine.slnx --verify-no-changes

echo "== build =="
dotnet build SpatialEngine.slnx

echo "== tests =="
dotnet test SpatialEngine.slnx --no-build

echo "== tooling tests =="
# The repo also carries Python tooling (tools/), and eng/verify.sh is the gate
# before done, so its unit tests run here too.
python3 -m unittest discover --start-directory tools --pattern 'test_*.py'