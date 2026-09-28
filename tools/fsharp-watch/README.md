# F# watch preview

`fsharp-watch` coordinates F# hot reload through the compiler service in the selected .NET SDK.
The package includes the watch engine and its Roslyn dependencies.
The SDK supplies MSBuild, the F# compiler, and the compiler service.
The current directory determines SDK selection, including `global.json`, as it does for `dotnet watch`.
The engine requires a compatible installed .NET runtime and ASP.NET Core runtime, version 10 or later.
The tool selects a runtime that also satisfies the selected SDK.

## Run a project

```sh
fsharp-watch --project path/to/App.fsproj
```

Use Debug configuration with portable PDBs.
Use `--non-interactive` for automatic restart after unsupported edits.
Press Ctrl+C to stop watch and its application.
Run `fsharp-watch --tool-info` to display the watch source revision and payload hashes.

## Compiler compatibility

The tool probes the selected compiler before it enables experimental hot reload flags.
The compiler must expose compatible F# hot reload session APIs.
Tool and SDK versions do not need matching version numbers.
The initial build and all updates in one session use the same compiler implementation.

An SDK without compatible APIs uses rebuild and restart, with an explanation in the console.
Errors from hot reload compilation or the fallback build check leave the current application alive.
Before another update, an SDK or compiler change starts a fresh watch host and application baseline.
The tool does not require a compiler replacement or project-file edits.
Projects that select custom compiler paths use rebuild and restart.
The tested stock SDKs, `10.0.401` and `11.0.100-rc.1.26420.103`, lack this hot reload API.
Hot reload requires an SDK distribution that contains the F# compiler changes.

Fallback verifies an isolated build before it stops the current application.
It then performs the normal project build and restart.
A later source change or build failure can interrupt that restart.

## Package the tool

Install the SDK checkout's pinned build SDK through its standard bootstrap command.
Use the SDK fork branch `feature/fsharp-watch-global-tool` from `NatElkins/sdk`.
Run the package script with that checkout path.

```sh
python3 tools/fsharp-watch/pack.py --sdk /path/to/sdk
dotnet tool install --global fsharp-watch --prerelease --add-source tools/fsharp-watch/artifacts
```

The script builds the watch engine before package creation.
It verifies every packaged payload file against its provenance hash.
It rejects packages that contain an F# compiler, compiler service, or compiler build task.
The F# launcher retains its ordinary FSharp.Core runtime dependency.

## Scope

This preview carries the watch integration from dotnet/sdk#55128 while the upstream protocol design remains open.
The compiler service owns edit classification, baselines, and delta emission.
The tool owns file observation, project discovery, update delivery, and restart decisions.
Its child process imports `FSharpWatch.targets` through `CustomAfterMicrosoftCommonTargets`.
That target adds compiler flags only after a successful compatibility probe.
An existing environment value for `CustomAfterMicrosoftCommonTargets` remains part of the import chain.
The package retains the upstream watch arguments.

## Verify a local installation

If the selected SDK contains the hot reload API, run both smoke scenarios against the installed command.

```sh
python3 tools/fsharp-watch/smoke.py --output /tmp/fsharp-watch-single
python3 tools/fsharp-watch/smoke.py --multi-project --output /tmp/fsharp-watch-multi
```

Each scenario verifies edits, preserved state, compiler-error recovery, restart after a method removal, and process cleanup.
The script uses POSIX signals and requires macOS or Linux.

If the selected SDK lacks the API, verify restart fallback.

```sh
python3 tools/fsharp-watch/smoke.py --expect-restart --output /tmp/fsharp-watch-fallback
```

The acceptance driver also verifies SDK selection, compiler replacement, and hot reload opt-outs.
Private SDK fixtures provide positive cases until an official SDK contains the compiler changes.
