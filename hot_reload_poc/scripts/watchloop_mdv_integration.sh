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

cleanup() {
    if [[ -f "$RESTORE_FILE" ]]; then
        cp "$RESTORE_FILE" "$MESSAGE_FILE"
    fi
    rm -rf "$TMP_DIR"
}
trap cleanup EXIT

cp "$MESSAGE_FILE" "$RESTORE_FILE"

DOTNET_MODIFIABLE_ASSEMBLIES=debug \
FSHARP_HOTRELOAD_ENABLE_RUNTIME_APPLY=1 \
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

CMD_LINE=$(grep ' mdv ' "$LOG_FILE" | tail -1)
if [[ -z "$CMD_LINE" ]]; then
    echo "Failed to locate mdv command in log:" >&2
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
