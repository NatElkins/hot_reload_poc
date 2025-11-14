#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
REPO_ROOT=$(cd "$SCRIPT_DIR/../.." && pwd)
WATCH_PROJECT="$REPO_ROOT/tools/fsc-watch/fsc_watch.fsproj"
SAMPLE_PROJECT="$REPO_ROOT/tools/fsc-watch/sample/WatchLoop/WatchLoop.fsproj"
MESSAGE_FILE="$REPO_ROOT/tools/fsc-watch/sample/WatchLoop/Message.fs"
MDV_EXE=${MDV_EXE:-mdv}
TMP_DIR=$(mktemp -d /tmp/watchloop-mdv-XXXXXX)
LOG_FILE="$TMP_DIR/fsc_watch.log"
DELTA_DIR="$TMP_DIR/deltas"
RESTORE_FILE="$TMP_DIR/Message.fs.bak"
KEEP_TMP=${KEEP_TMP:-0}

echo "[watchloop-mdv] temp dir: $TMP_DIR"
echo "[watchloop-mdv] log file: $LOG_FILE"
if [[ "$KEEP_TMP" != "1" ]]; then
    echo "[watchloop-mdv] set KEEP_TMP=1 to preserve artifacts after the run"
fi
if [[ "${FSHARP_HOTRELOAD_TRACE_STRINGS:-0}" != "1" ]]; then
    echo "[watchloop-mdv] hint: export FSHARP_HOTRELOAD_TRACE_STRINGS=1 to log literals from fsc-watch"
fi

cleanup() {
    if [[ "$KEEP_TMP" == "1" ]]; then
        echo "Preserving temp directory: $TMP_DIR"
        return
    fi
    if [[ -f "$RESTORE_FILE" ]]; then
        cp "$RESTORE_FILE" "$MESSAGE_FILE"
    fi
    rm -rf "$TMP_DIR"
}
trap cleanup EXIT

cp "$MESSAGE_FILE" "$RESTORE_FILE"

DOTNET_MODIFIABLE_ASSEMBLIES=debug \
FSHARP_HOTRELOAD_ENABLE_RUNTIME_APPLY=1 \
FSHARP_HOTRELOAD_TRACE_STRINGS=1 \
dotnet run --project "$WATCH_PROJECT" -- "$SAMPLE_PROJECT" --mdv-command-only --dump-deltas "$DELTA_DIR" >"$LOG_FILE" 2>&1 &
WATCH_PID=$!

for _ in {1..60}; do
    if grep -q "Watching" "$LOG_FILE" 2>/dev/null; then
        break
    fi
    if ! kill -0 "$WATCH_PID" 2>/dev/null; then
        cat "$LOG_FILE" >&2
        exit 1
    fi
    sleep 1
done

MESSAGE_FILE="$MESSAGE_FILE" python <<'PY'
import os
from pathlib import Path
path = Path(os.environ["MESSAGE_FILE"])
text = path.read_text()
if "hashwait" not in text:
    raise SystemExit("Expected baseline marker 'hashwait' not found in Message.fs")
path.write_text(text.replace("hashwait", "mdv-cli-delta"))
PY

wait "$WATCH_PID"

CMD_SCRIPT="$DELTA_DIR/gen1/mdv-gen1.cmd"
if [[ -f "$CMD_SCRIPT" ]]; then
    CMD_LINE=$(cat "$CMD_SCRIPT")
else
    CMD_LINE=$(grep '"/g:' "$LOG_FILE" | tail -1)
fi

if [[ -z "$CMD_LINE" ]]; then
    echo "Failed to locate mdv command (checked $CMD_SCRIPT and log)." >&2
    cat "$LOG_FILE" >&2
    exit 1
fi

CMD_LINE="$CMD_LINE" MDV_EXE="$MDV_EXE" python <<'PY'
import os
import shlex
import subprocess
import sys

line = os.environ["CMD_LINE"]
mdv = os.environ["MDV_EXE"]
parts = shlex.split(line)
try:
    idx = parts.index('mdv')
except ValueError:
    print(f"Unable to parse mdv command line: {line}", file=sys.stderr)
    sys.exit(1)
try:
    baseline, delta = parts[idx + 1], parts[idx + 2]
except IndexError:
    print(f"Incomplete mdv command line: {line}", file=sys.stderr)
    sys.exit(1)
proc = subprocess.run([mdv, baseline, delta], capture_output=True, text=True)
if proc.returncode != 0:
    print(proc.stdout, file=sys.stderr)
    print(proc.stderr, file=sys.stderr)
    sys.exit(proc.returncode)
output = proc.stdout
if "mdv-cli-delta" not in output:
    print("mdv output did not contain updated literal", file=sys.stderr)
    print(output, file=sys.stderr)
    sys.exit(1)
print(output)
PY

echo "mdv validation succeeded"
