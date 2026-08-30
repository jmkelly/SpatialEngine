#!/usr/bin/env bash
# Format the solution. Pass --verify-no-changes to check without editing.
set -euo pipefail
cd "$(dirname "$0")/.."

if [[ "${1:-}" == "--check" ]]; then
  dotnet format SpatialEngine.slnx --verify-no-changes --no-restore
else
  dotnet format SpatialEngine.slnx --no-restore
fi