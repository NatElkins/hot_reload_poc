#!/usr/bin/env bash

set -euo pipefail

usage() {
  cat <<'USAGE'
Usage: scripts/run-fsharp-dotnet-watch-demo.sh [options]

Creates (or reuses) an F# console demo, writes a loop-based Program.fs,
sets hot-reload environment variables for the locally built SDK/F# compiler,
and starts `dotnet watch run`.

Options:
  --demo-dir <path>        Demo directory (default: <repo>/HotReloadDemo)
  --project-name <name>    Project name for `dotnet new` (default: basename of demo dir)
  --init-only              Scaffold and write Program.fs, but do not start watch
  --open-code              Open the demo directory in VS Code before starting watch
  --build-if-missing       Build sdk/fsharp artifacts if expected binaries are missing
  -h, --help               Show this help
USAGE
}

SCRIPT_SOURCE="${BASH_SOURCE[0]}"
while [[ -h "$SCRIPT_SOURCE" ]]; do
  SCRIPT_DIR="$(cd -P "$(dirname "$SCRIPT_SOURCE")" && pwd)"
  SCRIPT_SOURCE="$(readlink "$SCRIPT_SOURCE")"
  [[ "$SCRIPT_SOURCE" != /* ]] && SCRIPT_SOURCE="$SCRIPT_DIR/$SCRIPT_SOURCE"
done
SCRIPT_DIR="$(cd -P "$(dirname "$SCRIPT_SOURCE")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"

DEMO_DIR="${REPO_ROOT}/HotReloadDemo"
PROJECT_NAME=""
INIT_ONLY=0
OPEN_CODE=0
BUILD_IF_MISSING=0

while [[ $# -gt 0 ]]; do
  case "$1" in
    --demo-dir)
      [[ $# -ge 2 ]] || { echo "error: --demo-dir requires a value" >&2; exit 2; }
      DEMO_DIR="$2"
      shift 2
      ;;
    --project-name)
      [[ $# -ge 2 ]] || { echo "error: --project-name requires a value" >&2; exit 2; }
      PROJECT_NAME="$2"
      shift 2
      ;;
    --init-only)
      INIT_ONLY=1
      shift
      ;;
    --open-code)
      OPEN_CODE=1
      shift
      ;;
    --build-if-missing)
      BUILD_IF_MISSING=1
      shift
      ;;
    -h|--help)
      usage
      exit 0
      ;;
    *)
      echo "error: unknown argument '$1'" >&2
      usage >&2
      exit 2
      ;;
  esac
done

if [[ -z "$PROJECT_NAME" ]]; then
  PROJECT_NAME="$(basename "$DEMO_DIR")"
fi

HR_DOTNET_ROOT_DEFAULT="${REPO_ROOT}/sdk/artifacts/bin/redist/Debug/dotnet"
HR_DOTNET_DEFAULT="${HR_DOTNET_ROOT_DEFAULT}/dotnet"

HR_DOTNET_ROOT="${HR_DOTNET_ROOT:-$HR_DOTNET_ROOT_DEFAULT}"
HR_DOTNET="${HR_DOTNET:-$HR_DOTNET_DEFAULT}"

resolve_sdk_fcs_path() {
  local dotnet_path="$1"
  local dotnet_root="$2"
  local sdk_version=""
  local candidate=""

  if [[ -x "$dotnet_path" ]]; then
    sdk_version="$("$dotnet_path" --version 2>/dev/null || true)"
    if [[ -n "$sdk_version" ]]; then
      candidate="${dotnet_root}/sdk/${sdk_version}/FSharp/FSharp.Compiler.Service.dll"
      if [[ -f "$candidate" ]]; then
        printf "%s\n" "$candidate"
        return 0
      fi
    fi
  fi

  # Fallback: pick the newest available SDK entry under this dotnet root.
  local discovered=()
  while IFS= read -r line; do
    discovered+=("$line")
  done < <(find "${dotnet_root}/sdk" -mindepth 3 -maxdepth 3 -type f -name "FSharp.Compiler.Service.dll" 2>/dev/null | sort)

  if [[ ${#discovered[@]} -gt 0 ]]; then
    local last_index=$(( ${#discovered[@]} - 1 ))
    printf "%s\n" "${discovered[$last_index]}"
    return 0
  fi

  return 1
}

SDK_FCS_PATH_DEFAULT="$(resolve_sdk_fcs_path "$HR_DOTNET" "$HR_DOTNET_ROOT" || true)"
FCS_PATH_FALLBACK="${REPO_ROOT}/fsharp/artifacts/bin/FSharp.Compiler.Service/Debug/netstandard2.0/FSharp.Compiler.Service.dll"
FCS_PATH="${DOTNET_WATCH_FSHARP_COMPILER_SERVICE_PATH:-${SDK_FCS_PATH_DEFAULT:-$FCS_PATH_FALLBACK}}"

build_artifacts_if_missing() {
  if [[ ! -x "$HR_DOTNET" ]]; then
    echo "[demo] SDK dotnet not found at '$HR_DOTNET'. Building sdk..." >&2
    (cd "${REPO_ROOT}/sdk" && ./build.sh)
  fi

  if [[ ! -f "$FCS_PATH" ]]; then
    echo "[demo] FSharp.Compiler.Service not found at '$FCS_PATH'. Building fsharp..." >&2
    (cd "${REPO_ROOT}/fsharp" && ./.dotnet/dotnet build FSharp.sln -c Debug -v minimal)
  fi
}

if [[ "$BUILD_IF_MISSING" -eq 1 ]]; then
  build_artifacts_if_missing
fi

if [[ ! -x "$HR_DOTNET" ]]; then
  echo "error: SDK dotnet not found at '$HR_DOTNET'" >&2
  echo "hint: build sdk first: cd ${REPO_ROOT}/sdk && ./build.sh" >&2
  echo "      or rerun this script with --build-if-missing" >&2
  exit 1
fi

if [[ ! -f "$FCS_PATH" ]]; then
  echo "error: FSharp.Compiler.Service.dll not found at '$FCS_PATH'" >&2
  if [[ -n "${SDK_FCS_PATH_DEFAULT:-}" ]]; then
    echo "hint: expected SDK-matched FCS at '$SDK_FCS_PATH_DEFAULT'" >&2
  fi
  echo "      build fsharp fallback path with:" >&2
  echo "      cd ${REPO_ROOT}/fsharp && ./.dotnet/dotnet build FSharp.sln -c Debug -v minimal" >&2
  echo "      or rerun this script with --build-if-missing" >&2
  exit 1
fi

if [[ -n "${SDK_FCS_PATH_DEFAULT:-}" ]] && [[ -f "$SDK_FCS_PATH_DEFAULT" ]]; then
  resolved_fcs_path="$(cd "$(dirname "$FCS_PATH")" && pwd)/$(basename "$FCS_PATH")"
  resolved_sdk_fcs_path="$(cd "$(dirname "$SDK_FCS_PATH_DEFAULT")" && pwd)/$(basename "$SDK_FCS_PATH_DEFAULT")"
  if [[ "$resolved_fcs_path" != "$resolved_sdk_fcs_path" ]]; then
    cat >&2 <<EOF_WARN
[demo] WARNING: DOTNET_WATCH_FSHARP_COMPILER_SERVICE_PATH is not SDK-matched:
  selected: $resolved_fcs_path
  sdk:      $resolved_sdk_fcs_path
[demo] This can cause runtime apply failures (for example TypeLoadException after "Hot reload succeeded").
EOF_WARN
  fi
fi

export HR_REPO="$REPO_ROOT"
export HR_DOTNET_ROOT="$HR_DOTNET_ROOT"
export HR_DOTNET="$HR_DOTNET"
export DOTNET_ROOT="$HR_DOTNET_ROOT"
export DOTNET_MSBUILD_SDK_RESOLVER_CLI_DIR="$HR_DOTNET_ROOT"
export DOTNET_MULTILEVEL_LOOKUP=0
export DOTNET_WATCH_FSHARP_COMPILER_SERVICE_PATH="$FCS_PATH"
export DOTNET_WATCH_FSHARP_USE_WORKSPACE_SNAPSHOTS="${DOTNET_WATCH_FSHARP_USE_WORKSPACE_SNAPSHOTS:-1}"
export DOTNET_CLI_TELEMETRY_OPTOUT="${DOTNET_CLI_TELEMETRY_OPTOUT:-1}"
export PATH="$HR_DOTNET_ROOT:$PATH"

mkdir -p "$DEMO_DIR"

shopt -s nullglob
fsprojs=("$DEMO_DIR"/*.fsproj)
shopt -u nullglob

if [[ ${#fsprojs[@]} -eq 0 ]]; then
  echo "[demo] Creating new F# console project at '$DEMO_DIR'..." >&2
  "$HR_DOTNET" new console -lang F# -n "$PROJECT_NAME" -o "$DEMO_DIR"
elif [[ ${#fsprojs[@]} -gt 1 ]]; then
  echo "error: multiple .fsproj files found in '$DEMO_DIR'. Use --demo-dir with a single-project directory." >&2
  exit 1
else
  echo "[demo] Reusing existing project: ${fsprojs[0]}" >&2
fi

cat > "$DEMO_DIR/Program.fs" <<'PROGRAM'
open System

type Greeter() =
    let mutable count = 0

    member _.Message() =
        count <- count + 1
        sprintf "generation 1 (count: %d)" count

let greeter = Greeter()

while true do
    printfn "%s" (greeter.Message())
    Threading.Thread.Sleep(2000)
PROGRAM

if [[ "$OPEN_CODE" -eq 1 ]]; then
  if command -v code >/dev/null 2>&1; then
    code "$DEMO_DIR" >/dev/null 2>&1 || true
  else
    echo "[demo] VS Code CLI ('code') not found; skipping --open-code" >&2
  fi
fi

cat >&2 <<EOF_MSG
[demo] Environment configured:
  HR_DOTNET=$HR_DOTNET
  DOTNET_WATCH_FSHARP_COMPILER_SERVICE_PATH=$DOTNET_WATCH_FSHARP_COMPILER_SERVICE_PATH
  DOTNET_WATCH_FSHARP_USE_WORKSPACE_SNAPSHOTS=$DOTNET_WATCH_FSHARP_USE_WORKSPACE_SNAPSHOTS
[demo] Program file: $DEMO_DIR/Program.fs
EOF_MSG

if [[ "$INIT_ONLY" -eq 1 ]]; then
  echo "[demo] Init complete (--init-only)." >&2
  exit 0
fi

cd "$DEMO_DIR"
echo "[demo] Starting watch: $HR_DOTNET watch run --non-interactive --verbose" >&2
exec "$HR_DOTNET" watch run --non-interactive --verbose
