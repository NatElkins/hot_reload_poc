#!/usr/bin/env python3
"""Verify an installed tool through real SDK compilers and bounded application edits."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import signal
import shlex
import sys
import subprocess
import tempfile
import threading
import time
import xml.etree.ElementTree as ET


APPLICATION = r"pid:(\d+) count:(\d+) message:"
FRESH_SESSION = "fsharp-watch: SDK or compiler changed. Starting a fresh watch session."


def digest(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def compiler_records(text, compiler, sdk):
    return [line for line in text.splitlines()
            if "FSHARP_WATCH_EXISTING_IMPORT Sample flags=" in line
            and str(compiler) in line and f"sdk={sdk}" in line]


def write_source(path, text):
    temporary = path.with_suffix(path.suffix + ".new")
    temporary.write_text(text)
    temporary.replace(path)


def processes(group):
    output = subprocess.check_output(["ps", "-axo", "pid=,pgid=,args="], text=True)
    result = {}
    for line in output.splitlines():
        fields = line.strip().split(None, 2)
        if len(fields) == 3 and int(fields[1]) == group:
            result[int(fields[0])] = fields[2]
    return result


class Watch:
    """Own the process group and retain output after failures for diagnosis."""

    def __init__(self, command, cwd, env, output):
        self.lines = []
        self.output = output
        self.log = (output / "watch.log").open("w")
        self.child = subprocess.Popen(command, cwd=cwd, env=env, stdout=subprocess.PIPE,
                                      stderr=subprocess.STDOUT, text=True, start_new_session=True)
        self.reader = threading.Thread(target=self.read_output, daemon=True)
        self.reader.start()

    def read_output(self):
        for line in self.child.stdout:
            self.lines.append(line)
            self.log.write(line)
            self.log.flush()

    def wait_for(self, pattern, start=0, timeout=120):
        deadline = time.monotonic() + timeout
        while time.monotonic() < deadline:
            for index, line in enumerate(self.lines[start:], start):
                match = re.search(pattern, line)
                if match:
                    return match, index
            if not any(re.search(APPLICATION, line) for line in self.lines) and any(
                    re.search(r"\berror (?:MSB|FS)\d+:", line) for line in self.lines):
                raise RuntimeError(f"Startup compilation failed. See {self.output / 'watch.log'}")
            if self.child.poll() is not None:
                raise RuntimeError(f"Watch exited with {self.child.returncode}. See {self.output / 'watch.log'}")
            time.sleep(0.1)
        raise TimeoutError(f"No output matched {pattern!r}. See {self.output / 'watch.log'}")

    def application(self, message, start=0, previous_pid=None):
        prefix = APPLICATION if previous_pid is None else rf"pid:(?!{previous_pid}\b)(\d+) count:(\d+) message:"
        match, index = self.wait_for(prefix + re.escape(message) + r"\s*$", start)
        return {"pid": int(match[1]), "count": int(match[2]), "message": message}, index

    def engines(self):
        result = sorted(pid for pid, command in processes(self.child.pid).items()
                        if "dotnet-watch.dll" in command and "--fsharp-watch-probe" not in command)
        assert result, "No live watch engine was found in the process group."
        return result

    def close(self):
        before = processes(self.child.pid)
        for sig, timeout in [(signal.SIGINT, 15), (signal.SIGKILL, 5)]:
            try:
                os.killpg(self.child.pid, sig)
            except ProcessLookupError:
                break
            deadline = time.monotonic() + timeout
            while time.monotonic() < deadline:
                self.child.poll()
                if not processes(self.child.pid):
                    break
                time.sleep(0.1)
            if not processes(self.child.pid):
                break
        self.child.wait(timeout=5)
        self.reader.join(timeout=5)
        self.log.close()
        remaining = processes(self.child.pid)
        assert not remaining, f"Processes survived cleanup: {remaining}"
        app_pids = {int(match[1]) for line in self.lines if (match := re.search(APPLICATION, line))}
        for pid in app_pids:
            try:
                os.kill(pid, 0)
            except ProcessLookupError:
                continue
            raise AssertionError(f"Application process {pid} survived cleanup.")
        return {"watchExitCode": self.child.returncode, "stoppedProcessIds": sorted(before),
                "applicationPidsStopped": sorted(app_pids)}


def assert_stopped(pids):
    remaining = set(pids)
    deadline = time.monotonic() + 5
    while remaining and time.monotonic() < deadline:
        for pid in tuple(remaining):
            try:
                os.kill(pid, 0)
            except ProcessLookupError:
                remaining.remove(pid)
        if remaining:
            time.sleep(0.1)
    assert not remaining, f"Old processes remain alive after restart: {sorted(remaining)}"


def assert_edit(previous, current, reload):
    if reload:
        assert current["pid"] == previous["pid"], "A body edit restarted the application."
        assert current["count"] > previous["count"], "The application state did not survive."
    else:
        assert current["pid"] != previous["pid"], "Fallback did not restart the application."
        assert_stopped([previous["pid"]])


def cancel(_signal, _frame):
    raise KeyboardInterrupt("Acceptance was cancelled.")


def main():
    signal.signal(signal.SIGTERM, cancel)
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--tool", default="fsharp-watch")
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--sdk-host", type=Path, help="Select an isolated dotnet installation through DOTNET_HOST_PATH.")
    parser.add_argument("--sdk-version", help="Pin normal SDK selection through a fixture global.json.")
    parser.add_argument("--multi-project", action="store_true")
    parser.add_argument("--framework", default="net10.0", help="Target framework for the plain acceptance application.")
    parser.add_argument("--expect-restart", action="store_true", help="Require fallback for a real unsupported SDK.")
    parser.add_argument("--opt-out", choices=["argument", "project", "environment", "release"])
    parser.add_argument("--hold-preflight", action="store_true", help="Correct a source edit during a held fallback build.")
    parser.add_argument("--touch-compiler", action="store_true", help="Require identical compiler bytes to preserve the session.")
    parser.add_argument("--compiler-replacement", type=Path, help="Replace private fixture FCS with a compatible real build.")
    parser.add_argument("--next-sdk-version", help="Change global.json to another SDK in the same private installation.")
    args = parser.parse_args()
    args.output = args.output.resolve()
    args.output.mkdir(parents=True, exist_ok=True)
    project = args.output / "sample with spaces"
    project.mkdir(exist_ok=True)
    env = dict(os.environ, DOTNET_WATCH_SUPPRESS_LAUNCH_BROWSER="1", DOTNET_WATCH_SUPPRESS_EMOJIS="1",
               DOTNET_WATCH_TRACE_FSHARP_HOTRELOAD="1", DOTNET_CLI_UI_LANGUAGE="en-US")
    # Ambient compiler overrides cannot establish SDK ownership.
    for name in ["DotnetFscCompilerPath", "DOTNET_WATCH_FSHARP_COMPILER_SERVICE_PATH",
                 "FSHARP_WATCH_ENABLE_HOT_RELOAD", "FSHARP_WATCH_ORIGINAL_COMMON_TARGETS"]:
        env.pop(name, None)
    if args.sdk_host:
        args.sdk_host = args.sdk_host.resolve()
        env["DOTNET_HOST_PATH"] = str(args.sdk_host)
        env["DOTNET_ROOT"] = str(args.sdk_host.parent)
        env["PATH"] = str(args.sdk_host.parent) + os.pathsep + env.get("PATH", "")
    host = str(args.sdk_host or env.get("DOTNET_HOST_PATH", "dotnet"))
    protected = {}
    if args.next_sdk_version:
        assert args.sdk_version, "SDK changes require an initial SDK version."
        ancestor_pin = args.output / "global.json"
        ancestor_pin.write_text(json.dumps({"sdk": {"version": args.sdk_version, "rollForward": "disable"}}) + "\n")
        protected[ancestor_pin] = digest(ancestor_pin)
    if args.sdk_version:
        pin = project / "global.json"
        pin.write_text(json.dumps({"sdk": {"version": args.sdk_version, "rollForward": "disable"}}) + "\n")
        protected[pin] = digest(pin)
    project_text = '''<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework></PropertyGroup>
  <ItemGroup><Compile Include="Program.fs" /></ItemGroup>
</Project>
'''
    project_text = project_text.replace("net10.0", args.framework)
    if args.opt_out == "project":
        project_text = project_text.replace("</PropertyGroup>", "<SupportsHotReload>false</SupportsHotReload></PropertyGroup>")
    library = project / "Library"
    if args.multi_project:
        library.mkdir(exist_ok=True)
        library_project = library / "Library.fsproj"
        library_project.write_text('''<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
  <ItemGroup><Compile Include="Library.fs" /></ItemGroup>
</Project>
'''.replace("net10.0", args.framework))
        protected[library_project] = digest(library_project)
        write_source(library / "Library.fs", 'namespace WatchLibrary\nmodule Message =\n    let text () = "ONE"\n')
        project_text = project_text.replace('</Project>', '<ItemGroup><ProjectReference Include="Library/Library.fsproj" /></ItemGroup></Project>')
    project_file = project / "Sample.fsproj"
    project_file.write_text(project_text)
    protected[project_file] = digest(project_file)
    sentinel = args.output / "existing import.targets"
    import_receipt = args.output / "import-observations.log"
    import_receipt.unlink(missing_ok=True)
    sentinel.write_text('''<Project><Target Name="FSharpWatchAcceptanceImport" AfterTargets="CoreCompile">
<Message Importance="high" Text="FSHARP_WATCH_EXISTING_IMPORT $(MSBuildProjectName) flags=$(OtherFlags) capability=@(ProjectCapability, ',') compiler=$(DotnetFscCompilerPath) sdk=$(MSBuildToolsPath)" />
</Target></Project>
''')
    document = ET.parse(sentinel)
    target = document.getroot().find("Target")
    message_task = target.find("Message")
    property_group = ET.Element("PropertyGroup")
    ET.SubElement(property_group, "_FSharpWatchAcceptanceObservation").text = message_task.attrib["Text"]
    target.insert(0, property_group)
    message_task.set("Text", "$(_FSharpWatchAcceptanceObservation)")
    ET.SubElement(target, "WriteLinesToFile", File=str(import_receipt),
                  Lines="$(_FSharpWatchAcceptanceObservation)", Overwrite="false")
    document.write(sentinel, encoding="unicode")
    preflight_gate = args.output / "preflight.hold"
    if args.hold_preflight:
        assert args.expect_restart or args.opt_out, "A held preflight requires restart mode."
        helper = args.output / "hold-preflight.py"
        helper.write_text("from pathlib import Path\nimport time\n"
                          f"gate = Path({str(preflight_gate)!r})\n"
                          "print('FSHARP_WATCH_PREFLIGHT_HELD', flush=True)\n"
                          "deadline = time.monotonic() + 30\n"
                          "while gate.exists():\n"
                          "    if time.monotonic() >= deadline: raise TimeoutError('Preflight release did not arrive.')\n"
                          "    time.sleep(0.05)\n")
        document = ET.parse(sentinel)
        target = ET.SubElement(document.getroot(), "Target", Name="FSharpWatchHoldPreflight", BeforeTargets="CoreCompile",
                               Condition=f"'$(ArtifactsPath)' != '' and Exists('{preflight_gate}')")
        ET.SubElement(target, "Exec", Command=shlex.join([sys.executable, str(helper)]))
        document.write(sentinel, encoding="unicode")
        protected[helper] = digest(helper)
    protected[sentinel] = digest(sentinel)
    env["CustomAfterMicrosoftCommonTargets"] = str(sentinel)
    if args.opt_out == "environment":
        env["DOTNET_WATCH_FSHARP_HOTRELOAD"] = "0"
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
    write_source(file, source)
    evaluation = subprocess.check_output([host, "msbuild", str(project_file),
        "-getProperty:DotnetFscCompilerPath,MSBuildToolsPath"], cwd=project, env=env, text=True)
    properties = json.loads(evaluation)["Properties"]
    compiler = Path(properties["DotnetFscCompilerPath"].strip('"')).parent / "FSharp.Compiler.Service.dll"
    mutable_compiler = args.touch_compiler or args.compiler_replacement
    if mutable_compiler:
        assert args.sdk_host, "Compiler mutation requires an explicit private SDK host."
        assert (args.sdk_host.parent / ".fsharp-watch-private-sdk").is_file(), "Private SDK marker is absent."
        assert compiler.resolve().is_relative_to(args.sdk_host.parent), "The compiler escapes the private SDK fixture."
    original_compiler = compiler.read_bytes() if args.compiler_replacement else None
    compiler_times = compiler.stat().st_atime_ns, compiler.stat().st_mtime_ns
    command = [args.tool, "--project", str(project_file), "--non-interactive", "--verbose"]
    if args.opt_out == "argument":
        command.append("--no-hot-reload")
    if args.opt_out == "release":
        command.extend(["--configuration", "Release"])
    evidence = {"compiler": str(compiler), "compilerSha256": digest(compiler), "sdk": properties["MSBuildToolsPath"],
                "expectedMode": "restart" if args.expect_restart or args.opt_out else "hot-reload"}
    watch = Watch(command, project, env, args.output)
    library_message = "ONE"
    app_message = "ONE"

    def message_text(message):
        return f"{message}-{library_message}" if args.multi_project else message

    error = None
    try:
        initial, _ = watch.application(message_text("ONE"))
        assert import_receipt.is_file(), "The existing target import did not execute."
        initial_imports = compiler_records(import_receipt.read_text(), compiler.parent / "fsc.dll", properties["MSBuildToolsPath"])
        assert initial_imports, "The initial build did not use the evaluated SDK compiler."
        evidence["initialCompilerImport"] = initial_imports[-1]
        if args.expect_restart:
            watch.wait_for(r"F# hot reload unavailable:.*Edits use rebuild and restart\.")
        elif not args.opt_out:
            watch.wait_for(re.escape(f"F# hot reload uses SDK compiler '{compiler.parent / 'fsc.dll'}'."))
        current = initial
        evidence["initial"] = initial
        reload = not (args.expect_restart or args.opt_out)
        for generation, message in enumerate(["TWO", "THREE", "FOUR"] if args.multi_project else ["TWO", "THREE"], 1):
            before = len(watch.lines)
            if args.multi_project and generation in (1, 3):
                library_message = message
                write_source(library / "Library.fs", f'namespace WatchLibrary\nmodule Message =\n    let text () = "{message}"\n')
            else:
                app_message = message
                write_source(file, source.replace("ONE", message))
            expected = message_text(app_message)
            updated, _ = watch.application(expected, before)
            assert_edit(current, updated, reload)
            evidence[f"generation{generation}"] = updated
            current = updated
        before = len(watch.lines)
        write_source(file, source.replace('"pid:', 'BROKEN "pid:'))
        _, failed_index = watch.wait_for(r"error FS|FSHRDL|[Cc]ompil(?:e|ation).*fail", before)
        alive, alive_index = watch.application(current["message"], failed_index + 1)
        first_error_count = alive["count"]
        while alive["count"] < first_error_count + 3:
            os.kill(current["pid"], 0)
            alive, alive_index = watch.application(current["message"], alive_index + 1)
            assert alive["pid"] == current["pid"], "A compiler error stopped the running application."
        assert alive["count"] > current["count"], "The application stopped while the source contained an error."
        evidence["duringCompilationError"] = alive
        before = len(watch.lines)
        write_source(file, source.replace("ONE", "RECOVERED"))
        recovered, _ = watch.application(message_text("RECOVERED"), before)
        assert_edit(alive, recovered, reload)
        evidence["errorRecovery"] = recovered
        current = recovered
        if args.hold_preflight:
            before = len(watch.lines)
            preflight_gate.write_text("Wait for the correction.\n")
            write_source(file, source.replace('"pid:', 'BROKEN "pid:'))
            _, held_index = watch.wait_for("FSHARP_WATCH_PREFLIGHT_HELD", before)
            alive, _ = watch.application(current["message"], held_index + 1)
            assert alive["pid"] == current["pid"], "Preflight stopped the application before validation."
            before = len(watch.lines)
            write_source(file, source.replace("ONE", "CONCURRENT"))
            preflight_gate.unlink()
            updated, _ = watch.application(message_text("CONCURRENT"), before)
            assert_edit(current, updated, False)
            evidence["editDuringPreflight"] = updated
            current = updated
        if args.touch_compiler:
            before = len(watch.lines)
            engines = watch.engines()
            old_hash = digest(compiler)
            os.utime(compiler, None)
            time.sleep(2)
            write_source(file, source.replace("ONE", "TOUCHED"))
            current, _ = watch.application(message_text("TOUCHED"), before)
            assert_edit(recovered, current, reload)
            assert watch.engines() == engines, "A compiler timestamp change restarted the watch host."
            assert FRESH_SESSION not in "".join(watch.lines[before:]), "Identical compiler bytes triggered a fresh session."
            assert digest(compiler) == old_hash
            evidence["compilerTimestamp"] = {**current, "enginePids": engines, "sha256": old_hash}
        if args.compiler_replacement:
            replacement = args.compiler_replacement.resolve()
            assert replacement.is_file(), "The replacement must be a real FSharp.Compiler.Service.dll."
            replacement_hash = digest(replacement)
            assert replacement_hash != digest(compiler), "The replacement compiler is byte-identical."
            before = len(watch.lines)
            engines = watch.engines()
            with tempfile.NamedTemporaryFile(dir=compiler.parent, suffix=".dll", delete=False) as temporary:
                temporary.write(replacement.read_bytes())
                replacement_temp = Path(temporary.name)
            replacement_temp.replace(compiler)
            # A source edit also tests identity checks before the next delta.
            write_source(file, source.replace("ONE", "NEWCOMPILER"))
            watch.wait_for(re.escape(FRESH_SESSION), before)
            updated, _ = watch.application(message_text("NEWCOMPILER"), before)
            assert_edit(current, updated, False)
            assert not set(engines) & set(watch.engines()), "The old watch host survived a compiler change."
            assert_stopped(engines)
            evidence["compilerChange"] = {**updated, "enginePids": watch.engines(), "sha256": replacement_hash}
            before = len(watch.lines)
            write_source(file, source.replace("ONE", "AFTERCOMPILER"))
            current, _ = watch.application(message_text("AFTERCOMPILER"), before)
            assert_edit(updated, current, reload)
            evidence["newCompilerBaseline"] = current
        if args.next_sdk_version:
            assert args.sdk_host and args.sdk_version, "SDK changes require an explicit host and initial SDK version."
            pin = project / "global.json"
            for action, message in [("modify", "NEWSDK"), ("delete", "UNPINNED"), ("create", "REPINNED")]:
                before = len(watch.lines)
                engines = watch.engines()
                receipt_offset = len(import_receipt.read_text())
                if action == "delete":
                    pin.unlink()
                    protected.pop(pin)
                else:
                    write_source(pin, json.dumps({"sdk": {"version": args.next_sdk_version, "rollForward": "disable"}}) + "\n")
                    protected[pin] = digest(pin)
                selected_version = subprocess.check_output([host, "--version"], cwd=project, env=env, text=True).strip()
                selected_sdk = args.sdk_host.parent / "sdk" / selected_version
                watch.wait_for(re.escape(FRESH_SESSION), before)
                if reload:
                    watch.wait_for(re.escape(f"F# hot reload uses SDK compiler '{selected_sdk / 'FSharp/fsc.dll'}'."), before)
                updated, _ = watch.application(current["message"], before, previous_pid=current["pid"])
                new_imports = compiler_records(import_receipt.read_text()[receipt_offset:], selected_sdk / "FSharp/fsc.dll", selected_sdk)
                assert new_imports, "The restarted build did not produce a receipt for the selected SDK compiler."
                assert not set(engines) & set(watch.engines()), "The old watch host survived SDK selection changes."
                assert_stopped([current["pid"], *engines])
                evidence[f"sdkInput_{action}"] = {**updated, "enginePids": watch.engines(), "version": selected_version,
                                                 "previousApplicationStopped": current["pid"], "previousEnginesStopped": engines,
                                                 "newImportObservations": new_imports}
                before = len(watch.lines)
                write_source(file, source.replace("ONE", message))
                current, _ = watch.application(message_text(message), before)
                assert_edit(updated, current, reload)
                evidence[f"sdkInput_{action}_nextUpdate"] = current
        before = len(watch.lines)
        write_source(file, source.replace("ONE", "RESTARTED").replace("_.Message()", "_.Renamed()").replace("counter.Message()", "counter.Renamed()"))
        restarted, _ = watch.application(message_text("RESTARTED"), before)
        assert restarted["pid"] != current["pid"], "A method removal did not restart the application."
        assert_stopped([current["pid"]])
        evidence["rudeEditRestart"] = restarted
        assert "FS1063" not in "".join(watch.lines), "An unsupported compiler received the experimental flag."
        imports = [line for line in import_receipt.read_text().splitlines() if "FSHARP_WATCH_EXISTING_IMPORT Sample flags=" in line]
        assert imports, "The existing target import did not execute for the application."
        if not reload:
            assert all("HotReloadDeltas" not in line for line in imports), "An opt-out or unsupported compiler received the flag."
            assert all("SupportsHotReload" not in line for line in imports), "Fallback advertised the hot reload capability."
        for path, original in protected.items():
            assert digest(path) == original, f"The tool changed {path}."
        evidence["projectHashes"] = {str(path.relative_to(args.output)): value for path, value in protected.items()}
        evidence["importObservations"] = imports
    except BaseException as failure:
        error = failure
        evidence["failure"] = repr(failure)
    finally:
        preflight_gate.unlink(missing_ok=True)
        try:
            evidence["cleanup"] = watch.close()
        finally:
            if original_compiler is not None:
                compiler.write_bytes(original_compiler)
            if mutable_compiler:
                os.utime(compiler, ns=compiler_times)
            (args.output / "result.json").write_text(json.dumps(evidence, indent=2) + "\n")
    if error:
        raise error
    print(json.dumps(evidence, indent=2))


if __name__ == "__main__":
    main()
