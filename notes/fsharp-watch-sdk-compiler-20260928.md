# SDK-owned compiler in fsharp-watch

The selected SDK supplies the F# compiler and compiler service.
The external tool supplies the watch engine, project discovery, update delivery, and restart behavior.
The package contains no `fsc`, compiler service, or F# build task.
The launcher retains its ordinary FSharp.Core dependency.

## Compatibility contract

Tool and SDK versions do not require matching version numbers.
The tool checks the compiler API before it enables `--test:HotReloadDeltas`.
The initial build and subsequent deltas use one SDK compiler implementation per session.
The compiler service owns edit classification, baseline state, and delta emission.

The watch engine loads the compiler service and its dependencies in a private assembly context.
The launcher selects installed .NET and ASP.NET Core runtimes that satisfy both the watch engine and the selected SDK.
An absent compatible runtime produces a diagnostic before the engine starts.

The engine enables MSBuild task dependency isolation before MSBuild initializes.
This permits the SDK's NuGet assemblies and the watch engine's NuGet assemblies to coexist.
SDK 11 exposed this conflict with NuGet 7.11 and the watch package's NuGet 7.10.
The [MSBuild loader](https://github.com/dotnet/msbuild/blob/335551904e6674972a48d4532eb2dc46ebd95bd6/src/Framework/Loader/MSBuildLoadContext.cs#L97-L102) provides the isolation switch.

An absent or incompatible compiler API selects rebuild and restart.
An isolated build verifies the replacement before the current application stops.
Errors from hot reload compilation or the isolated build leave the current application alive.
Unsupported edits follow the watch restart policy.

Fallback then performs the normal project build and restart.
A later source change or build failure can interrupt that restart.
The tool does not promote artifacts from the isolated build into the live output directory.

Content hashes identify the compiler and adjacent dependencies.
Before another update, a compiler replacement or SDK selection change starts a fresh engine and application baseline.
A timestamp change with identical bytes retains the current session.
Custom compiler paths and graphs with disabled hot reload use rebuild and restart.

## Distribution boundary

The tested stock SDKs, `10.0.401` and `11.0.100-rc.1.26420.103`, lack the hot reload session API.
Positive acceptance uses private SDK fixtures with the reviewed compiler changes.
These fixtures verify the normal SDK layout and selection path.
They do not represent an official SDK release.

The five F# PRs require no additional source changes for this tool architecture.
An official SDK must distribute those compiler changes before users receive SDK-owned hot reload.
The external package carries the watch integration, so this distribution path does not require an upstream SDK watch merge.

The [tool PR](https://github.com/NatElkins/hot_reload_poc/pull/1) contains the package and acceptance scripts.
The [watch engine PR](https://github.com/NatElkins/sdk/pull/2) contains the engine changes.
Both PRs remain drafts.

## Verification

The Release watch build and the test-project build pass without warnings or errors.
All 36 focused bridge, reflection, identity, and file watcher tests pass without skips.
The watch engine revision is `d8af3dd4962dea7fdacbab9919a7c5454a007e84`.
The positive compiler fixtures use revision `8b30815999b7f0f46bb8e66d4a533d59cd0075c1`.

The installed package is `0.2.0-preview.20260928.3`.
All 11 installed-tool scenarios pass against that exact package on macOS ARM64.
The scenarios include stock SDK 10 and 11 fallback, supported single/multiple projects, compiler replacement, SDK selection, and four opt-outs.
Modification, deletion, and creation of `global.json` each select a fresh host without a source edit.
The existing imported target records the current compiler and SDK after each build.
All application and watch processes stop after each scenario.

All 238 payload hashes and 259 installed tool files match the archive.
Package SHA256: `d9b7b76af6887339d79bd55ece1590990652aab5e37f73002c3919d1f4e14225`.
All 57 recorded stock compiler files and 12 private compiler files remain unchanged after verification.
The package remains unpublished to NuGet.
Windows and Linux execution remain unverified.
The local evidence directory is `.audit/sdk-owned-watch-20260925/`.
The acceptance driver exercises stock fallback, SDK 10 and 11, multiple projects, compiler replacement, SDK selection, and opt-outs.
Each edit scenario verifies compiler-error recovery and process cleanup.

The previous [bundled preview](fsharp-watch-research-20260925.md) remains historical evidence.
It does not satisfy the SDK-owned distribution contract.
