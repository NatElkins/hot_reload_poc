# F# watch preview

`fsharp-watch` applies supported F# edits to a live application through a standalone fork of `dotnet watch`.
The package includes the watch engine, its Roslyn dependencies, and the experimental F# compiler.
The tool uses the installed .NET SDK for MSBuild and project restore.
The current directory determines SDK selection, including `global.json`, as it does for `dotnet watch`.
The tool requires .NET SDK 10.0.400 or later and the .NET 10 ASP.NET Core runtime, which the SDK includes.

## Run a project

```sh
fsharp-watch --project path/to/App.fsproj
```

Use Debug configuration with portable PDBs.
Use `--non-interactive` for automatic restart after unsupported edits.
Press Ctrl+C to stop watch and its application.
Run `fsharp-watch --tool-info` to display the source revisions and payload hashes.

## Package the tool

Install each checkout's pinned SDK through its standard bootstrap command.
Use the SDK fork branch `feature/fsharp-watch-global-tool` from `NatElkins/sdk`.
Run the package script with both checkout paths.

```sh
python3 tools/fsharp-watch/pack.py --sdk /path/to/sdk --fsharp /path/to/fsharp
dotnet tool install --global fsharp-watch --prerelease --add-source tools/fsharp-watch/artifacts
```

The script builds both source checkouts before package creation.
It verifies every packaged payload file against its provenance hash.
The compiler build targets .NET 10 with MSBuild 18.7.1 dependencies.
Use a dedicated compiler checkout because these framework properties differ from its default build.

## Scope

This preview carries the prototype from dotnet/sdk#55128 while the upstream protocol design remains open.
It does not change the installed SDK or project files.
Its child process selects the compiler through `DotnetFscCompilerPath` and imports `FSharpWatch.targets` through `CustomAfterMicrosoftCommonTargets`.
That target adds the F# hot reload capability and preserves existing compiler flags.
Projects that override those properties require separate integration.
An existing environment value for `CustomAfterMicrosoftCommonTargets` remains part of the import chain.
The package retains the upstream watch arguments and restart behavior.

## Verify a local installation

Run both smoke scenarios against the installed command.

```sh
python3 tools/fsharp-watch/smoke.py --output /tmp/fsharp-watch-single
python3 tools/fsharp-watch/smoke.py --multi-project --output /tmp/fsharp-watch-multi
```

Each scenario verifies edits, preserved state, compiler-error recovery, restart after a method removal, and process cleanup.
The script uses POSIX signals and requires macOS or Linux.
