#!/usr/bin/env bash
# One-command verification: formatting check, build, full test suite.
# Every change must pass this before it is considered done.
set -euo pipefail
cd "$(dirname "$0")/.."

echo "== format check =="
dotnet format SpatialEngine.slnx --verify-no-changes --no-restore

echo "== build =="
dotnet build SpatialEngine.slnx

echo "== tests =="
dotnet test SpatialEngine.slnx --no-build