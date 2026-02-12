#!/usr/bin/env bash

set -euo pipefail

usage() {
  cat <<'USAGE'
Usage: scripts/run-fsharp-web-hot-reload-demo.sh [options]

Creates (or refreshes) a minimal F# server-side HTML demo, configures the local
SDK/F# hot reload environment, and runs `dotnet watch run`.

Options:
  --demo-dir <path>        Demo directory (default: <repo>/HotReloadWebDemo)
  --project-name <name>    Project name (default: basename of demo dir)
  --port <number>          HTTP port (default: 5078)
  --respect-env-fcs-path   Respect DOTNET_WATCH_FSHARP_COMPILER_SERVICE_PATH from the shell
  --init-only              Scaffold files only; do not start watch
  --open-code              Open demo directory in VS Code before starting watch
  --open-browser           Open the demo URL in a browser before starting watch
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

DEMO_DIR="${REPO_ROOT}/HotReloadWebDemo"
PROJECT_NAME=""
PORT="5078"
PORT_USER_SPECIFIED=0
INIT_ONLY=0
OPEN_CODE=0
OPEN_BROWSER=0
BUILD_IF_MISSING=0
RESPECT_ENV_FCS_PATH=0

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
    --port)
      [[ $# -ge 2 ]] || { echo "error: --port requires a value" >&2; exit 2; }
      PORT="$2"
      PORT_USER_SPECIFIED=1
      shift 2
      ;;
    --respect-env-fcs-path)
      RESPECT_ENV_FCS_PATH=1
      shift
      ;;
    --init-only)
      INIT_ONLY=1
      shift
      ;;
    --open-code)
      OPEN_CODE=1
      shift
      ;;
    --open-browser)
      OPEN_BROWSER=1
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

is_port_in_use() {
  local port="$1"
  lsof -nP -iTCP:"$port" -sTCP:LISTEN >/dev/null 2>&1
}

if ! [[ "$PORT" =~ ^[0-9]+$ ]]; then
  echo "error: --port must be numeric" >&2
  exit 2
fi

if is_port_in_use "$PORT"; then
  if [[ "$PORT_USER_SPECIFIED" -eq 1 ]]; then
    echo "error: requested port '$PORT' is already in use" >&2
    exit 1
  fi

  original_port="$PORT"
  found_port=""
  for candidate in $(seq $((PORT + 1)) $((PORT + 50))); do
    if ! is_port_in_use "$candidate"; then
      found_port="$candidate"
      break
    fi
  done

  if [[ -z "$found_port" ]]; then
    echo "error: could not find a free port in range $((PORT + 1))-$((PORT + 50))" >&2
    exit 1
  fi

  PORT="$found_port"
  echo "[web-demo] NOTE: default port $original_port was in use; selected free port $PORT." >&2
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
if [[ "$RESPECT_ENV_FCS_PATH" -eq 1 ]]; then
  FCS_PATH="${DOTNET_WATCH_FSHARP_COMPILER_SERVICE_PATH:-${SDK_FCS_PATH_DEFAULT:-$FCS_PATH_FALLBACK}}"
else
  FCS_PATH="${SDK_FCS_PATH_DEFAULT:-$FCS_PATH_FALLBACK}"
fi

build_artifacts_if_missing() {
  if [[ ! -x "$HR_DOTNET" ]]; then
    echo "[web-demo] SDK dotnet not found at '$HR_DOTNET'. Building sdk..." >&2
    (cd "${REPO_ROOT}/sdk" && ./build.sh)
  fi

  if [[ ! -f "$FCS_PATH" ]]; then
    echo "[web-demo] FSharp.Compiler.Service not found at '$FCS_PATH'. Building fsharp..." >&2
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

if [[ "$RESPECT_ENV_FCS_PATH" -eq 0 ]] && [[ -n "${DOTNET_WATCH_FSHARP_COMPILER_SERVICE_PATH:-}" ]]; then
  if [[ -n "${SDK_FCS_PATH_DEFAULT:-}" ]]; then
    resolved_selected_path="$(cd "$(dirname "$FCS_PATH")" && pwd)/$(basename "$FCS_PATH")"
    resolved_env_path="$(cd "$(dirname "$DOTNET_WATCH_FSHARP_COMPILER_SERVICE_PATH")" && pwd)/$(basename "$DOTNET_WATCH_FSHARP_COMPILER_SERVICE_PATH")"
    if [[ "$resolved_selected_path" != "$resolved_env_path" ]]; then
      cat >&2 <<EOF_WARN
[web-demo] NOTE: ignoring shell DOTNET_WATCH_FSHARP_COMPILER_SERVICE_PATH to keep the demo SDK-matched.
  shell:    $resolved_env_path
  selected: $resolved_selected_path
[web-demo] Pass --respect-env-fcs-path if you intentionally want to use the shell override.
EOF_WARN
    fi
  fi
fi

if [[ -n "${SDK_FCS_PATH_DEFAULT:-}" ]] && [[ -f "$SDK_FCS_PATH_DEFAULT" ]]; then
  resolved_fcs_path="$(cd "$(dirname "$FCS_PATH")" && pwd)/$(basename "$FCS_PATH")"
  resolved_sdk_fcs_path="$(cd "$(dirname "$SDK_FCS_PATH_DEFAULT")" && pwd)/$(basename "$SDK_FCS_PATH_DEFAULT")"
  if [[ "$resolved_fcs_path" != "$resolved_sdk_fcs_path" ]]; then
    cat >&2 <<EOF_WARN
[web-demo] WARNING: DOTNET_WATCH_FSHARP_COMPILER_SERVICE_PATH is not SDK-matched:
  selected: $resolved_fcs_path
  sdk:      $resolved_sdk_fcs_path
[web-demo] This can cause runtime apply failures (for example TypeLoadException after "Hot reload succeeded").
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
export ASPNETCORE_URLS="http://localhost:${PORT}"
export PATH="$HR_DOTNET_ROOT:$PATH"

mkdir -p "$DEMO_DIR"

cat > "$DEMO_DIR/${PROJECT_NAME}.fsproj" <<'FSPROJ'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
  </PropertyGroup>
  <ItemGroup>
    <Compile Include="DemoContent.fs" />
    <Compile Include="Program.fs" />
  </ItemGroup>
</Project>
FSPROJ

cat > "$DEMO_DIR/DemoContent.fs" <<'DEMOCONTENT'
module DemoContent

module HtmlDsl =
    let element (name: string) (content: string) =
        "<" + name + ">" + content + "</" + name + ">"

    let elementWithAttributes (name: string) (attributes: string) (content: string) =
        "<" + name + " " + attributes + ">" + content + "</" + name + ">"

let private refreshScript =
    """
(() => {
  let current = null;

  async function tick() {
    try {
      const response = await fetch('/__hotreload/sentinel', { cache: 'no-store' });
      if (response.ok) {
        const next = (await response.text()).trim();
        if (current === null) {
          current = next;
        } else if (next !== current) {
          window.location.reload();
          return;
        }
      }
    } catch (_) {
      // Ignore transient polling failures.
    }

    setTimeout(tick, 1000);
  }

  tick();
})();
"""

type PageModel() =
    // Edit this method while dotnet watch is running.
    member _.Headline() =
        "Generation 1 - server-side F# HTML DSL"

    member this.Sentinel() =
        this.Headline()

    member this.RenderPage() =
        let headline = this.Headline()
        let sentinel = this.Sentinel()
        let pageTitle = HtmlDsl.element "title" "F# Server-Side Hot Reload Web Demo"
        let header = HtmlDsl.element "h1" headline
        let instructions = HtmlDsl.element "p" "Edit DemoContent.fs and change PageModel.Headline while dotnet watch is running."
        let sentinelLine = HtmlDsl.element "p" ("Sentinel: " + sentinel)
        let refreshLine = HtmlDsl.element "p" "Auto-refresh is driven by /__hotreload/sentinel polling."
        "<!doctype html>" +
        "<html lang=\"en\">" +
        "<head>" +
        "<meta charset=\"utf-8\"></meta>" +
        pageTitle +
        "</head>" +
        "<body>" +
        header +
        instructions +
        sentinelLine +
        refreshLine +
        "<script>" + refreshScript + "</script>" +
        "</body>" +
        "</html>"

let private pageModel = PageModel()

let renderSentinel () =
    pageModel.Sentinel()

let renderPage () =
    pageModel.RenderPage()
DEMOCONTENT

cat > "$DEMO_DIR/Program.fs" <<'PROGRAM'
module HotReloadWebDemo

open System
open System.IO
open System.Net
open System.Net.Sockets
open System.Text
open DemoContent

module Http =
    let private parsePath (requestLine: string) =
        if String.IsNullOrWhiteSpace(requestLine) then
            "/"
        else
            let parts = requestLine.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            if parts.Length >= 2 then parts[1] else "/"

    let rec private skipHeaders (reader: StreamReader) =
        let line = reader.ReadLine()
        if not (isNull line) && line <> "" then
            skipHeaders reader

    let private writeResponse (stream: Stream) (status: string) (contentType: string) (body: string) =
        let payload = Encoding.UTF8.GetBytes(body)
        let header =
            "HTTP/1.1 " + status + "\r\n" +
            "Content-Type: " + contentType + "; charset=utf-8\r\n" +
            "Content-Length: " + string payload.Length + "\r\n" +
            "Connection: close\r\n" +
            "Cache-Control: no-store\r\n\r\n"

        let headerBytes = Encoding.ASCII.GetBytes(header)
        stream.Write(headerBytes, 0, headerBytes.Length)
        stream.Write(payload, 0, payload.Length)
        stream.Flush()

    let handleClient (client: TcpClient) =
        use client = client
        client.ReceiveTimeout <- 5000
        client.SendTimeout <- 5000

        use stream = client.GetStream()
        use reader = new StreamReader(stream, Encoding.ASCII, false, 1024, true)

        let requestLine = reader.ReadLine()
        skipHeaders reader

        let path = parsePath requestLine
        if path.StartsWith("/__hotreload/sentinel", StringComparison.Ordinal) then
            writeResponse stream "200 OK" "text/plain" (renderSentinel ())
        elif path = "/" || path.StartsWith("/?", StringComparison.Ordinal) then
            writeResponse stream "200 OK" "text/html" (renderPage ())
        else
            writeResponse stream "404 Not Found" "text/plain" "Not found"

let private chooseBaseUrl () =
    let raw = Environment.GetEnvironmentVariable("ASPNETCORE_URLS")
    if String.IsNullOrWhiteSpace(raw) then
        "http://localhost:5078"
    else
        let parts = raw.Split(';', StringSplitOptions.RemoveEmptyEntries)
        if parts.Length = 0 then "http://localhost:5078" else parts[0]

let baseUrl = chooseBaseUrl ()
let uri = Uri(baseUrl)

if not (uri.Scheme.Equals("http", StringComparison.OrdinalIgnoreCase)) then
    failwith ("Only http:// URLs are supported in this demo. Received: " + baseUrl)

let listener = new TcpListener(IPAddress.Loopback, uri.Port)
listener.Start()

printfn "[web-demo] Listening on %s" baseUrl

while true do
    let client = listener.AcceptTcpClient()
    Http.handleClient client
PROGRAM

if [[ "$OPEN_CODE" -eq 1 ]]; then
  if command -v code >/dev/null 2>&1; then
    code "$DEMO_DIR" >/dev/null 2>&1 || true
  else
    echo "[web-demo] VS Code CLI ('code') not found; skipping --open-code" >&2
  fi
fi

DEMO_URL="http://localhost:${PORT}"
if [[ "$OPEN_BROWSER" -eq 1 ]]; then
  if command -v open >/dev/null 2>&1; then
    open "$DEMO_URL" >/dev/null 2>&1 || true
  elif command -v xdg-open >/dev/null 2>&1; then
    xdg-open "$DEMO_URL" >/dev/null 2>&1 || true
  else
    echo "[web-demo] No browser opener found; skipping --open-browser" >&2
  fi
fi

cat >&2 <<EOF_MSG
[web-demo] Environment configured:
  HR_DOTNET=$HR_DOTNET
  DOTNET_WATCH_FSHARP_COMPILER_SERVICE_PATH=$DOTNET_WATCH_FSHARP_COMPILER_SERVICE_PATH
  DOTNET_WATCH_FSHARP_USE_WORKSPACE_SNAPSHOTS=$DOTNET_WATCH_FSHARP_USE_WORKSPACE_SNAPSHOTS
  ASPNETCORE_URLS=$ASPNETCORE_URLS
[web-demo] Project: $DEMO_DIR/${PROJECT_NAME}.fsproj
[web-demo] Entry file: $DEMO_DIR/Program.fs
[web-demo] Edit file: $DEMO_DIR/DemoContent.fs
[web-demo] URL: $DEMO_URL
EOF_MSG

if [[ "$INIT_ONLY" -eq 1 ]]; then
  echo "[web-demo] Init complete (--init-only)." >&2
  exit 0
fi

cd "$DEMO_DIR"
echo "[web-demo] Starting watch: $HR_DOTNET watch run --non-interactive --verbose" >&2
exec "$HR_DOTNET" watch run --non-interactive --verbose
