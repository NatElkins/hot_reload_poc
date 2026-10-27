#!/usr/bin/env python3
"""Verify installed watch behavior on Windows, macOS, or Linux."""
import argparse
import ctypes
import hashlib
import json
import os
from pathlib import Path
import re
import signal
import subprocess
import threading
import time

from smoke import APPLICATION, Watch, assert_edit, write_source


class WindowsJob:
    """Bound every descendant process to a job that terminates on close."""

    def __init__(self, child):
        from ctypes import wintypes

        class BasicLimits(ctypes.Structure):
            _fields_ = [("process_time", ctypes.c_int64), ("job_time", ctypes.c_int64),
                        ("flags", wintypes.DWORD), ("minimum", ctypes.c_size_t),
                        ("maximum", ctypes.c_size_t), ("active", wintypes.DWORD),
                        ("affinity", ctypes.c_size_t), ("priority", wintypes.DWORD),
                        ("scheduling", wintypes.DWORD)]

        class ExtendedLimits(ctypes.Structure):
            _fields_ = [("basic", BasicLimits), ("io", ctypes.c_uint64 * 6),
                        ("process_memory", ctypes.c_size_t), ("job_memory", ctypes.c_size_t),
                        ("peak_process", ctypes.c_size_t), ("peak_job", ctypes.c_size_t)]

        self.kernel = ctypes.WinDLL("kernel32", use_last_error=True)
        self.kernel.CreateJobObjectW.argtypes = [ctypes.c_void_p, wintypes.LPCWSTR]
        self.kernel.CreateJobObjectW.restype = wintypes.HANDLE
        self.kernel.SetInformationJobObject.argtypes = [wintypes.HANDLE, ctypes.c_int,
                                                        ctypes.c_void_p, wintypes.DWORD]
        self.kernel.AssignProcessToJobObject.argtypes = [wintypes.HANDLE, wintypes.HANDLE]
        self.kernel.OpenProcess.argtypes = [wintypes.DWORD, wintypes.BOOL, wintypes.DWORD]
        self.kernel.OpenProcess.restype = wintypes.HANDLE
        self.kernel.CloseHandle.argtypes = [wintypes.HANDLE]
        self.kernel.WaitForSingleObject.argtypes = [wintypes.HANDLE, wintypes.DWORD]
        self.handle = self.kernel.CreateJobObjectW(None, None)
        if not self.handle:
            raise ctypes.WinError(ctypes.get_last_error())
        limits = ExtendedLimits()
        limits.basic.flags = 0x2000  # JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE owns timeout cleanup.
        process = None
        try:
            if not self.kernel.SetInformationJobObject(self.handle, 9, ctypes.byref(limits), ctypes.sizeof(limits)):
                raise ctypes.WinError(ctypes.get_last_error())
            process = self.kernel.OpenProcess(0x0101, False, child.pid)
            if not process or not self.kernel.AssignProcessToJobObject(self.handle, process):
                raise ctypes.WinError(ctypes.get_last_error())
        except BaseException:
            self.close()
            raise
        finally:
            if process:
                self.kernel.CloseHandle(process)

    def alive(self, pid):
        """Inspect process lifetime without POSIX signal semantics."""
        handle = self.kernel.OpenProcess(0x100000, False, pid)
        if not handle:
            error = ctypes.get_last_error()
            if error == 87:  # ERROR_INVALID_PARAMETER means that the process no longer exists.
                return False
            raise ctypes.WinError(error)
        try:
            return self.kernel.WaitForSingleObject(handle, 0) == 258
        finally:
            self.kernel.CloseHandle(handle)

    def close(self):
        """Release the job and terminate descendants that survived graceful shutdown."""
        if self.handle:
            self.kernel.CloseHandle(self.handle)
            self.handle = None


class WindowsWatch(Watch):
    """Reuse output assertions with Windows process ownership and cancellation."""

    def __init__(self, command, cwd, env, output):
        self.lines = []
        self.output = output
        self.log = (output / "watch.log").open("w", encoding="utf-8")
        self.child = subprocess.Popen(command, cwd=cwd, env=env, stdout=subprocess.PIPE,
                                      stderr=subprocess.STDOUT, text=True, encoding="utf-8", errors="replace",
                                      creationflags=subprocess.CREATE_NEW_PROCESS_GROUP)
        try:
            self.job = WindowsJob(self.child)
        except BaseException:
            subprocess.run(["taskkill", "/PID", str(self.child.pid), "/T", "/F"], check=False)
            self.child.wait(timeout=10)
            self.log.close()
            raise
        self.reader = threading.Thread(target=self.read_output, daemon=True)
        self.reader.start()

    def close(self):
        pids = {int(match[1]) for line in self.lines if (match := re.search(APPLICATION, line))}
        forced = False
        try:
            if self.child.poll() is None:
                self.child.send_signal(signal.CTRL_BREAK_EVENT)
                try:
                    self.child.wait(timeout=15)
                except subprocess.TimeoutExpired:
                    forced = True
        finally:
            self.job.close()
            self.child.wait(timeout=10)
            self.reader.join(timeout=10)
            self.log.close()
        deadline = time.monotonic() + 5
        remaining = pids
        while remaining and time.monotonic() < deadline:
            remaining = {pid for pid in remaining if self.job.alive(pid)}
            time.sleep(0.1)
        assert not remaining, f"Application processes survived cleanup: {sorted(remaining)}"
        return {"watchExitCode": self.child.returncode, "applicationPidsStopped": sorted(pids),
                "forcedTreeCleanup": forced}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--tool", required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--expect-restart", action="store_true")
    parser.add_argument("--sdk-host", type=Path)
    parser.add_argument("--sdk-version")
    parser.add_argument("--framework", default="net10.0")
    args = parser.parse_args()
    output = args.output.resolve()
    project = output / "sample with spaces"
    project.mkdir(parents=True, exist_ok=True)
    env = dict(os.environ, DOTNET_WATCH_SUPPRESS_LAUNCH_BROWSER="1", DOTNET_WATCH_SUPPRESS_EMOJIS="1",
               DOTNET_WATCH_TRACE_FSHARP_HOTRELOAD="1", DOTNET_CLI_UI_LANGUAGE="en-US",
               DOTNET_ADD_GLOBAL_TOOLS_TO_PATH="0")
    for key in ("DotnetFscCompilerPath", "DOTNET_WATCH_FSHARP_COMPILER_SERVICE_PATH",
                "FSHARP_WATCH_ENABLE_HOT_RELOAD", "FSHARP_WATCH_ORIGINAL_COMMON_TARGETS",
                "CustomAfterMicrosoftCommonTargets"):
        env.pop(key, None)
    if args.sdk_host:
        host = args.sdk_host.resolve()
        env.update(DOTNET_HOST_PATH=str(host), DOTNET_ROOT=str(host.parent))
        env["PATH"] = str(host.parent) + os.pathsep + env.get("PATH", "")
    if args.sdk_version:
        (project / "global.json").write_text(json.dumps({
            "sdk": {"version": args.sdk_version, "rollForward": "disable"}}) + "\n")
    project_file = project / "Sample.fsproj"
    project_file.write_text(f'''<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><OutputType>Exe</OutputType><TargetFramework>{args.framework}</TargetFramework></PropertyGroup>
  <ItemGroup><Compile Include="Program.fs" /></ItemGroup>
</Project>
''')
    project_hash = hashlib.sha256(project_file.read_bytes()).hexdigest()
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
    source_file = project / "Program.fs"
    write_source(source_file, source)
    watch_type = WindowsWatch if os.name == "nt" else Watch
    watch = watch_type([args.tool, "--project", str(project_file), "--non-interactive", "--verbose"],
                       project, env, output)
    evidence = {"platform": os.name, "expectedMode": "restart" if args.expect_restart else "hot-reload"}
    try:
        current, _ = watch.application("ONE")
        evidence["initial"] = current
        if args.expect_restart:
            watch.wait_for(r"F# hot reload unavailable:.*Edits use rebuild and restart\.")
        for message in ("TWO", "THREE"):
            start = len(watch.lines)
            write_source(source_file, source.replace("ONE", message))
            updated, _ = watch.application(message, start)
            assert_edit(current, updated, not args.expect_restart)
            evidence[message] = updated
            current = updated
        start = len(watch.lines)
        write_source(source_file, source.replace("sprintf", "BROKEN"))
        _, index = watch.wait_for(r"error FS|FSHRDL|[Cc]ompil(?:e|ation).*fail", start)
        alive, index = watch.application(current["message"], index + 1)
        for _ in range(3):
            alive, index = watch.application(current["message"], index + 1)
            assert alive["pid"] == current["pid"], "A compiler error stopped the application."
        assert alive["count"] > current["count"], "The application stopped during a compiler error."
        evidence["duringCompilationError"] = alive
        start = len(watch.lines)
        write_source(source_file, source.replace("ONE", "RECOVERED"))
        recovered, _ = watch.application("RECOVERED", start)
        assert_edit(alive, recovered, not args.expect_restart)
        evidence["errorRecovery"] = recovered
        start = len(watch.lines)
        write_source(source_file, source.replace("ONE", "RESTARTED").replace("Message()", "Renamed()"))
        restarted, _ = watch.application("RESTARTED", start)
        assert restarted["pid"] != recovered["pid"], "A method removal did not restart the application."
        evidence["rudeEditRestart"] = restarted
        assert "FS1063" not in "".join(watch.lines), "The compiler rejected an experimental flag."
        assert hashlib.sha256(project_file.read_bytes()).hexdigest() == project_hash, "The tool changed the project."
        evidence["passed"] = True
    except BaseException as error:
        evidence["failure"] = repr(error)
        raise
    finally:
        try:
            evidence["cleanup"] = watch.close()
        finally:
            (output / "result.json").write_text(json.dumps(evidence, indent=2) + "\n")
    print(json.dumps(evidence, indent=2))


if __name__ == "__main__":
    main()
