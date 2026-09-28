#!/usr/bin/env python3
"""Run installed-tool acceptance against stock and private future-SDK fixtures."""
import argparse
import json
from pathlib import Path
import subprocess
import signal
import sys


def cancel(_signal, _frame):
    raise KeyboardInterrupt("Acceptance was cancelled.")


def main():
    signal.signal(signal.SIGTERM, cancel)
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--tool", required=True)
    parser.add_argument("--case", action="append", help="Run only these named cases. Repeat to select several cases.")
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--stock-host", type=Path, required=True)
    parser.add_argument("--stock-version", default="10.0.401")
    parser.add_argument("--stock-second-host", type=Path, required=True, help="Unchanged stock SDK11 installation.")
    parser.add_argument("--private-host", type=Path, required=True)
    parser.add_argument("--first-sdk", default="10.0.401")
    parser.add_argument("--second-sdk", default="11.0.100-rc.1.26420.103")
    parser.add_argument("--replacement-compiler", type=Path, required=True)
    args = parser.parse_args()
    args.output = args.output.resolve()
    args.output.mkdir(parents=True, exist_ok=True)
    assert (args.private_host.resolve().parent / ".fsharp-watch-private-sdk").is_file(), "The private SDK marker is absent."
    smoke = Path(__file__).with_name("smoke.py")
    cases = [
        ("stock-fallback", args.stock_host, args.stock_version, ["--expect-restart", "--hold-preflight"]),
        ("stock-sdk11-fallback", args.stock_second_host, args.second_sdk,
         ["--expect-restart", "--hold-preflight", "--framework", "net11.0"]),
        ("sdk10-single", args.private_host, args.first_sdk, []),
        ("sdk10-multi", args.private_host, args.first_sdk, ["--multi-project"]),
        ("sdk11-single-identity", args.private_host, args.second_sdk,
         ["--touch-compiler", "--compiler-replacement", str(args.replacement_compiler.resolve())]),
        ("sdk11-multi", args.private_host, args.second_sdk, ["--multi-project"]),
        ("sdk-selection-change", args.private_host, args.first_sdk, ["--next-sdk-version", args.second_sdk]),
        ("opt-out-argument", args.private_host, args.second_sdk,
         ["--opt-out", "argument", "--hold-preflight", "--compiler-replacement", str(args.replacement_compiler.resolve()),
          "--next-sdk-version", args.first_sdk]),
        *[(f"opt-out-{mode}", args.private_host, args.second_sdk, ["--opt-out", mode])
          for mode in ["project", "environment", "release"]],
    ]
    if args.case:
        unknown = set(args.case) - {case[0] for case in cases}
        if unknown:
            parser.error("Unknown acceptance cases: " + ", ".join(sorted(unknown)))
        cases = [case for case in cases if case[0] in args.case]
    results = []
    for name, host, version, options in cases:
        command = [sys.executable, str(smoke), "--tool", args.tool, "--output", str(args.output / name),
                   "--sdk-host", str(host.resolve()), "--sdk-version", version, *options]
        print(f"Run {name}", flush=True)
        with (args.output / f"{name}.log").open("w") as log:
            child = subprocess.Popen(command, stdout=log, stderr=subprocess.STDOUT)
            try:
                exit_code = child.wait()
            except BaseException:
                child.send_signal(signal.SIGINT)
                child.wait(timeout=30)
                raise
        results.append({"case": name, "exitCode": exit_code, "command": command})
        (args.output / "matrix.json").write_text(json.dumps(results, indent=2) + "\n")
        print(f"{name}: {'passed' if exit_code == 0 else 'failed'}", flush=True)
        if exit_code != 0:
            break
    failed = [result["case"] for result in results if result["exitCode"] != 0]
    if failed:
        raise SystemExit("Acceptance failed: " + ", ".join(failed))
    print(f"Passed {len(results)} installed-tool acceptance cases.")


if __name__ == "__main__":
    main()
