#!/usr/bin/env python3
"""Verify real edits through an installed tool, then stop every process from the test."""
import argparse
import json
import os
from pathlib import Path
import re
import signal
import subprocess
import threading
import time


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--tool", default="fsharp-watch")
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--multi-project", action="store_true", help="Alternate edits between an F# library and its application.")
    args = parser.parse_args()
    args.output.mkdir(parents=True, exist_ok=True)
    project = args.output / "sample with spaces"
    project.mkdir(exist_ok=True)
    project_text = '''<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework></PropertyGroup>
  <ItemGroup><Compile Include="Program.fs" /></ItemGroup>
</Project>
'''
    if args.multi_project:
        library = project / "Library"
        library.mkdir(exist_ok=True)
        (library / "Library.fsproj").write_text('''<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
  <ItemGroup><Compile Include="Library.fs" /></ItemGroup>
</Project>
''')
        (library / "Library.fs").write_text('namespace WatchLibrary\nmodule Message =\n    let text () = "ONE"\n')
        project_text = project_text.replace('</Project>', '<ItemGroup><ProjectReference Include="Library/Library.fsproj" /></ItemGroup></Project>')
    (project / "Sample.fsproj").write_text(project_text)
    original_project = (project / "Sample.fsproj").read_bytes()
    source = '''open System
open System.Runtime.CompilerServices

type Counter() =
    let mutable count = 0
    [<MethodImpl(MethodImplOptions.NoInlining)>]
    member _.Message() =
        count <- count + 1
        sprintf "pid:%d count:%d message:ONE" Environment.ProcessId count

let counter = Counter()
while true do
    printfn "%s" (counter.Message())
    Threading.Thread.Sleep 200
'''
    if args.multi_project:
        source = source.replace('message:ONE" Environment.ProcessId count', 'message:ONE-%s" Environment.ProcessId count (WatchLibrary.Message.text())')
    file = project / "Program.fs"
    file.write_text(source)
    env = dict(os.environ, DOTNET_WATCH_SUPPRESS_LAUNCH_BROWSER="1", DOTNET_WATCH_SUPPRESS_EMOJIS="1",
               DOTNET_WATCH_TRACE_FSHARP_HOTRELOAD="1", DOTNET_CLI_UI_LANGUAGE="en-US")
    lines = []
    log = (args.output / "watch.log").open("w")
    child = subprocess.Popen([args.tool, "--project", str(project / "Sample.fsproj"), "--non-interactive", "--verbose"],
                             cwd=project, env=env, stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
                             text=True, start_new_session=True)
    def read_output():
        for line in child.stdout:
            lines.append(line)
            log.write(line)
            log.flush()
    reader = threading.Thread(target=read_output, daemon=True)
    reader.start()
    def wait_for(pattern, start=0, timeout=90):
        deadline = time.monotonic() + timeout
        while time.monotonic() < deadline:
            for index, line in enumerate(lines[start:], start):
                match = re.search(pattern, line)
                if match:
                    return match, index
            if child.poll() is not None:
                raise RuntimeError(f"Watch exited with {child.returncode}. See {args.output / 'watch.log'}")
            time.sleep(0.1)
        raise TimeoutError(f"No output matched {pattern!r}. See {args.output / 'watch.log'}")
    evidence = {}
    try:
        initial, _ = wait_for(r"pid:(\d+) count:(\d+) message:ONE")
        first_pid = initial[1]
        for generation, message in enumerate(["TWO", "THREE"], 1):
            before = len(lines)
            if args.multi_project and generation == 1:
                (library / "Library.fs").write_text('namespace WatchLibrary\nmodule Message =\n    let text () = "TWO"\n')
                expected = "ONE-TWO"
            else:
                file.write_text(source.replace("ONE", message))
                expected = f"{message}-TWO" if args.multi_project else message
            match, _ = wait_for(rf"pid:(\d+) count:(\d+) message:{expected}", before)
            assert match[1] == first_pid, "A body edit restarted the application."
            assert int(match[2]) > int(initial[2]), "The application state did not survive."
            evidence[f"generation{generation}"] = match.group(0)
        before = len(lines)
        file.write_text(source.replace('"pid:', 'BROKEN "pid:'))
        wait_for(r"error FS|FSHRDL|[Cc]ompil(?:e|ation).*fail", before)
        before = len(lines)
        file.write_text(source.replace("ONE", "RECOVERED"))
        recovered, _ = wait_for(r"pid:(\d+) count:(\d+) message:RECOVERED", before)
        assert recovered[1] == first_pid, "Correction of a compiler error restarted the application."
        evidence["errorRecovery"] = recovered.group(0)
        before = len(lines)
        file.write_text(source.replace("ONE", "RESTARTED").replace("_.Message()", "_.Renamed()").replace("counter.Message()", "counter.Renamed()"))
        restarted, _ = wait_for(r"pid:(\d+) count:(\d+) message:RESTARTED", before)
        assert restarted[1] != first_pid, "A method removal did not restart the application."
        evidence["rudeEditRestart"] = restarted.group(0)
        assert (project / "Sample.fsproj").read_bytes() == original_project, "The tool changed the project."
    finally:
        if child.poll() is None:
            os.killpg(child.pid, signal.SIGINT)
            try:
                child.wait(timeout=15)
            except subprocess.TimeoutExpired:
                os.killpg(child.pid, signal.SIGKILL)
                child.wait(timeout=10)
        reader.join(timeout=5)
        log.close()
    assert evidence, "No application evidence was collected."
    pids = {int(match[1]) for line in lines if (match := re.search(r"pid:(\d+) count:", line))}
    for pid in pids:
        try:
            os.kill(pid, 0)
        except ProcessLookupError:
            continue
        raise AssertionError(f"Application process {pid} survived cleanup.")
    evidence["cleanup"] = {"watchExitCode": child.returncode, "applicationPidsStopped": sorted(pids)}
    (args.output / "result.json").write_text(json.dumps(evidence, indent=2) + "\n")
    print(json.dumps(evidence, indent=2))


if __name__ == "__main__":
    main()
