# Standalone F# watch, September 25, 2026

This note records the current upstream position and the standalone tool decision.

## Upstream position

The SDK team closed prototype PR #55128 on August 16.
The proposal in #55464 remains open, with its latest comment on August 17.
Tomas Matousek states that the protocol discussion remains internal and that further F# preparation is premature.
The planned watch architecture uses a language server and an RPC protocol for hot reload.
The FCS session API remains useful across those host designs.
Sources: [SDK prototype](https://github.com/dotnet/sdk/pull/55128), [provider proposal and maintainer response](https://github.com/dotnet/sdk/issues/55464#issuecomment-5321121712).

Roslyn PR #80556 merged in January.
It moved the watch integration into `Microsoft.CodeAnalysis.ExternalAccess.HotReload`.
This assembly remains an internal integration boundary, not a published F# provider contract.
Source: [Roslyn assembly separation](https://github.com/dotnet/roslyn/pull/80556).

SDK work continued after the discussion.
PR #55861 removed `DotNetWatchBuild` from hot reload design-time builds on August 25.
Its rationale requires design-time outputs to match regular builds.
Source: [SDK build-property change](https://github.com/dotnet/sdk/pull/55861).

September work also changed browser activation and process identification.
Those changes do not supply an F# provider contract.
The current public issue and targeted SDK/Roslyn PR searches expose no settled replacement contract.
Sources: [browser activation](https://github.com/dotnet/sdk/pull/56234), [process identity](https://github.com/dotnet/sdk/pull/56245).

## Tool decision

The global tool bundles the existing watch fork, its Roslyn dependencies, and a matched F# compiler.
It uses the selected installed SDK for MSBuild, restore, and application launch.
The package contains neither an SDK nor a runtime.
The launcher honors SDK selection from the current directory through `dotnet --info`, as `dotnet watch` does.

The tool requires .NET SDK 10.0.400 or later, plus the .NET 10 ASP.NET Core runtime.
The official current SDK is 10.0.401, released September 8.
The existing dnvm installation now contains that SDK beside its prior versions.
Source: [Microsoft's .NET 10 release manifest](https://dotnetcli.blob.core.windows.net/dotnet/release-metadata/10.0/releases.json).

The fork restores the prior POSIX signal helper because `SafeProcessHandle.Signal` requires .NET 11.
The package excludes `Microsoft.Build.Framework` at runtime so that MSBuildLocator loads one consistent SDK toolset.
The fork carries its existing metadata dependencies for the .NET 10 host.
The watch logger now displays F# compiler errors that the prototype previously discarded.
Source: [portable watch fork](https://github.com/NatElkins/sdk/tree/feature/fsharp-watch-global-tool).

The launcher sets compiler and target-import properties only in its child process.
The imported target adds the F# capability and the experimental compiler flag.
Both disk baselines and session compiles therefore use the same compiler lowering.
Project files and installed SDK files remain intact.

The package records both source revisions, build arguments, and SHA-256 hashes for every payload file.
The package includes upstream license and notice files.
The build script uses existing MSBuild 18.7.1 packages for the current compiler's .NET 10 host.

## Verification

The first full installed-tool smoke passed with the current compiler source at `b66ed5eec4`.
The SDK fork was `3094a2e441`.
The smoke used a project path with spaces and no project modifications.
The package measured approximately 28 MB.

The observed application evidence was:

- First edit: PID 68170, counter 13, message TWO.
- Second edit: PID 68170, counter 24, message THREE.
- Compiler-error recovery: PID 68170, counter 43, message RECOVERED.
- Method removal: PID 68673, counter 1, message RESTARTED.
- Cleanup: both application PIDs stopped.

The transcript is `/tmp/fsharp-watch-smoke-complete/watch.log`.
The assertions and results are in `/tmp/fsharp-watch-smoke-complete/result.json`.
The global installation is `fsharp-watch` version `0.1.0-preview.20260925.2`.
Both single-project and F# library/application scenarios passed against that command.
The fresh transcripts and results reside under `/tmp/fsharp-watch-global-single` and `/tmp/fsharp-watch-global-multi`.
Both scenarios preserve the PID through two edits and compiler-error recovery, then restart after a method removal.
The SDK bridge and reflection unit tests passed 13/13, with zero skipped tests.
The launcher project cannot use `dotnet format`, which supports only C# and Visual Basic.
The SDK formatter reports one existing whitespace defect outside the edited lines in `CompilationHandler.cs`.

Final packaging must repeat the smoke scenarios after the remaining compiler review fixes land.
This local execution proves macOS ARM64 behavior. Windows and Linux execution remain unverified.
