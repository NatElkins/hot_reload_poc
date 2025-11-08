#!/usr/bin/env bash

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$(cd "${SCRIPT_DIR}/.." && pwd)"

export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_MODIFIABLE_ASSEMBLIES=debug

PROJECT="${ROOT}/src/TestApp/TestApp.fsproj"

if [[ ! -f "${PROJECT}" ]]; then
  echo "error: unable to locate TestApp.fsproj at ${PROJECT}" >&2
  exit 1
fi

echo "Running dotnet watch with hot reload for ${PROJECT}" >&2
echo "Press Ctrl+C to stop." >&2

dotnet watch --project "${PROJECT}" --hot-reload run "$@"
