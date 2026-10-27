# F# watch maintenance, October 2026

The `fsharp-watch` tool uses the compiler and compiler service from the selected SDK.
The package contains the watch engine and Roslyn dependencies.
It contains no F# compiler, compiler service, or compiler build task.
Nelknet owns the NuGet trusted publisher policy.

## Source status

The compiler branches include F# main `fe180deb55`.
The watch engine includes SDK main `082c56168c`.
The package pins engine `b6f301717c` and compiler fixture `74df1854e3`.

| Pull request | Purpose |
| --- | --- |
| [dotnet/fsharp#20025](https://github.com/dotnet/fsharp/pull/20025) | Typed-tree comparison |
| [dotnet/fsharp#20027](https://github.com/dotnet/fsharp/pull/20027) | Delta emission |
| [dotnet/fsharp#20030](https://github.com/dotnet/fsharp/pull/20030) | Hot reload sessions |
| [dotnet/fsharp#20031](https://github.com/dotnet/fsharp/pull/20031) | In-process compilation |
| [dotnet/fsharp#19941](https://github.com/dotnet/fsharp/pull/19941) | Complete compiler implementation |
| [NatElkins/sdk#1](https://github.com/NatElkins/sdk/pull/1) | SDK prototype |
| [NatElkins/sdk#2](https://github.com/NatElkins/sdk/pull/2) | Standalone watch engine |

The compiler PRs remain open for maintainer review.
The historical foundations fork PR remains a reference, because merged upstream PRs replace its design.
The contributor-owned expression-evaluator prototype remains separate from the CLI release.

## Corrections and verification

The compiler refresh fixes cross-project capture leakage and omitted runtime-async lowering.
It also corrects delta parameter metadata to match Roslyn.
The metadata helper now fails when an installed visualizer cannot execute.
The session slice removes duplicate filesystem probes from ordinary project snapshots.

The complete compiler verifier passed all 11 steps.
Its suites passed 595 service tests, 255 component tests, and 29 strict metadata tests.
The component suite skips two manual hosts.
Release EmittedIL passed 1,660 tests, with zero failures and three existing skips.
Independent review covered complete F# and C# metadata dumps and runtime application of the exact deltas.

The corrected watch engine passed 62 focused tests locally and in hosted Linux CI.
The package support scripts passed 16 unit tests.
The [final source gate](https://github.com/NatElkins/hot_reload_poc/actions/runs/40533012274) passed on Linux, macOS, and Windows.
Each platform verified stock-SDK fallback and private-compiler hot reload.
Linux and macOS also verified multiple projects.
Local checks verified SDK selection, compiler replacement, opt-outs, and process cleanup.

## Feedback corrections

The audit includes resolved, outdated, and historical review threads.
Every applicable request has a source correction or an explicit disposition with evidence.
The deferred upstream SDK adapter requirements remain separate from this standalone preview.

The corrected watch engine sends F# diagnostics through the existing browser payload and clears them after recovery.
The console and browser use an immutable documentation link because the upstream-main link returned 404.
The regression failed before the fix and passed afterward, with Roslyn diagnostics preserved.

The compiler driver now uses its emission hook for module debug information.
The expanded guard catches fully qualified references that previously escaped detection.
The compiler shares environment names and Boolean parsing, restores the requested translations, and removes intermediate list allocations.
The emitter separates definition matching, row construction, and sequence-point analysis behind typed boundaries.
Independent review confirms unchanged emitted IL and metadata outside generation GUIDs.

## Release automation

The [tool PR](https://github.com/NatElkins/hot_reload_poc/pull/1) merged into main.
Both weekly workflows are active and passed their first invocations.
The SDK workflow proposes upstream merges into the maintained engine branch.
The package workflow proposes immutable engine and compiler fixture revisions.
Conflicts require a source fix before an update can merge.

The release workflow verifies one archive on all three platforms before publication.
NuGet trusted publishing uses the release workflow identity and stores no API key.
The `nuget` environment permits only main.
The [first release run](https://github.com/NatElkins/hot_reload_poc/actions/runs/40535078132) passed.
NuGet published `0.2.0-preview.20261027.405350781321` under Nelknet.
The public archive matches the verified artifact, and public installation passed.
A subsequent preview will include the additional feedback corrections.

Official SDKs must distribute the compiler changes before the tool can provide F# hot reload with those SDKs.
SDKs without compatible session APIs use rebuild and restart.
Private fixtures provide positive test coverage without changes to the user's SDK installation.
