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

The installed version is `0.1.0-preview.20260925.4`.
Its compiler source is `8b30815999b7f0f46bb8e66d4a533d59cd0075c1`.
Its SDK source is `8e1416d2f88b70a0767c4e36c9541cbac723fb42`.
The SDK fork includes the final prototype's transaction, capability, baseline, and ancestor-restart fixes.
The package size is 29,664,926 bytes.
Its SHA-256 is `f0c4d58ef5df980a17acbd4e4651680cd51318a2e78968ebcbd8cd5018a508cb`.

Both builds passed with zero warnings and errors.
The SDK bridge and reflection tests passed 22/22, with zero skips.
The test host and SDK redist watch DLL hashes matched.
The archive and global installation each passed all 300 payload hash checks.

The final smoke scenarios used project paths with spaces and no project modifications.
The results were:

| Scenario | First edit | Second edit | Error recovery | Rude-edit restart |
| --- | --- | --- | --- | --- |
| Single project | PID 80503, counter 14 | PID 80503, counter 26 | PID 80503, counter 46 | PID 81158, counter 1 |
| F# library and app | PID 81307, counter 17 | PID 81307, counter 28 | PID 81307, counter 47 | PID 81835, counter 1 |

Both scenarios applied two generations in place and preserved state after a compiler error.
A method removal triggered a rebuild and restart.
The multi-project scenario first edited the library, then the application.
Every observed application process stopped after the smoke driver sent Ctrl+C.

The transcripts and assertion results reside in these directories:

- `/tmp/fsharp-watch-final-single`
- `/tmp/fsharp-watch-final-multi`
- `/tmp/fsharp-watch-final-optout`

Release builds also passed with `SupportsHotReload=false` and with `--no-hot-reload`.
The launcher preserved the project files and stopped both applications.
A pinned `global.json` experiment verified that SDK selection follows the current directory, including with an external `--project` path.

The archive audit exposed stale dependencies under MSBuild's `PreserveNewest` copy rule.
Payload files now use `Always`, and `pack.py` rejects archive hashes that differ from the source payload.
The local version 0.1.0-preview.20260925.3 was superseded before the final smoke tests.

The launcher project cannot use `dotnet format`, which supports only C# and Visual Basic.
The SDK formatter reports one existing whitespace defect outside the edited lines in `CompilationHandler.cs`.
This local execution proves macOS ARM64 behavior. Windows and Linux execution remain unverified.
