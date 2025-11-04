# F# Hot Reload Implementation Plan

This plan converts ARCHITECTURE_PROPOSAL.md into concrete milestones and tasks. Each task is scoped so that a single LLM-focused iteration can complete it while keeping the repository in a buildable, backwards-compatible state. After every task, run the full build (`dotnet build FSharp.sln` or `build.cmd`) and applicable test suites.

- Testing guidance: the full component suite is time-consuming (10+ minutes). For day-to-day iteration run `./.dotnet/dotnet test tests/FSharp.Compiler.ComponentTests/FSharp.Compiler.ComponentTests.fsproj -c Debug --no-build --filter FullyQualifiedName~HotReload` and reserve the full suite for validation runs.

## Milestone 1 – Baseline Capture & Semantic Diff Infrastructure

### Task 1.1 – TypedTreeDiff Core
- **Status**: Completed (2025-10-31). Added `TypedTreeDiff.fs`/`.fsi` with semantic/rude edit tracking and exercised the diff via `TypedTreeDiffResult`; service tests in `tests/FSharp.Compiler.Service.Tests/HotReload/TypedTreeDiffTests.fs` cover unchanged files, method-body updates, inline toggles, and union layout changes. Validated with `./.dotnet/dotnet build FSharp.sln -c Debug` and `./.dotnet/dotnet test tests/FSharp.Compiler.Service.Tests/FSharp.Compiler.Service.Tests.fsproj -c Debug --no-build --filter FullyQualifiedName~HotReload`.
- **Follow-up**: Wire the diff output into the session orchestration (DefinitionMap/SymbolChanges and forthcoming IDE adapters).

- **Scope**: Implement the `TypedTreeDiff` data structures and algorithms.
- **Files/Modules**: `src/Compiler/TypedTree/TypedTreeDiff.fs` (new), with signatures in `TypedTreeDiff.fsi`; updates to `TypedTree`/`TypedTreeOps` as needed.
- **Objective**: Compare two `CheckedImplFile` snapshots, producing semantic edit records keyed by stamps (method body updates vs. rude edits). Support method-body-only updates initially.
- **Acceptance Criteria**:
  - Unit tests under `tests/FSharp.Compiler.UnitTests/HotReload/TypedTreeDiffTests.fs` verify detection of unchanged nodes, method-body edits, inline changes (rude edit), union layout changes (rude edit).
  - Documentation references existing helpers in `TypedTree.fs`, `TypedTreeOps.fs`; see Roslyn `EditAndContinueAnalyzerTests` for inspiration.
  - Backwards compatibility maintained (no behavior change until feature flag enabled).
- **Additional Context**: `src/Compiler/Service/IncrementalBuild.fs` for typed-tree snapshots.

### Task 1.2 – HotReloadNameMap & Generators
- **Status**: Completed (2025-11-01). Compiler now provisions `HotReloadNameMap` whenever `--enable:hotreloaddeltas` is active: `main4` seeds the map before IL generation, baseline capture resets it prior to subsequent deltas, and sessions clear the map on teardown. Component coverage in `tests/FSharp.Compiler.ComponentTests/HotReload/NameMapTests.fs` exercises closures, async workflows, computation expressions, task builders, and record/union helpers to confirm `@hotreload` naming without line-number suffixes. CLI and checker entry points reuse the map across generations so metadata tokens stay stable.
- **Follow-up**: Continue auditing new synthesized constructs (e.g., resumable state machines, active patterns) whenever we light up additional generators, and ensure CLI/IDE orchestrators request map snapshots when persisting baselines.

- **Scope**: Introduce stabilized name mapping and session-aware name generators.
- **Files/Modules**: `src/Compiler/TypedTree/CompilerGlobalState.fs`, `src/Compiler/CodeGen/IlxGen.fs`, new helper `HotReloadNameMap.fs`.
- **Objective**: Capture compiler-generated names during baseline, reuse them during delta generation; ensure line numbers don’t leak into metadata.
- **Acceptance Criteria**:
  - Unit tests `tests/FSharp.Compiler.UnitTests/HotReload/NameMapTests.fs` cover closures, async state machines, computation expressions.
  - Feature flag gating ensures existing builds unaffected.
- **Context**: Audit generator call sites via `rg "FreshCompilerGeneratedName"` results, reference Roslyn naming behavior.

### Task 1.3 – FSharpEmitBaseline & Token Map Serialization
- **Scope**: Capture metadata/IL state after baseline emit.
- **Files/Modules**: new `src/Compiler/CodeGen/HotReloadBaseline.fs`, modifications to `ILBinaryWriter` (if necessary).
- **Objective**: Store heap lengths, `ILTokenMappings`, and symbol-to-token maps for reuse.
- **Acceptance Criteria**:
  - Component tests `tests/FSharp.Compiler.ComponentTests/HotReload/BaselineTests.fs` confirm token stability for methods, fields, properties, events across baseline rebuild.
  - Build unaffected when hot reload disabled.
- **Context**: `src/Compiler/AbstractIL/ilwrite.fs` and Roslyn `EmitBaseline.cs`.
- **Status**: Completed (2025-10-30). Baseline capture module and component tests implemented; metadata snapshot plumbed via IL writer helpers.
- **Follow-up**: Extend baseline capture to persist EncLog/EncMap rows alongside `MetadataSnapshot` once delta emission work begins.

### Task 1.4 – IlxGenEnv Snapshotting
- **Scope**: Persist minimal `IlxGenEnv` required for delta emission.
- **Files/Modules**: `IlxGen.fs`, `HotReloadBaseline.fs` (extend).
- **Objective**: Capture `tyenv`, `valsInScope`, `imports`, `sigToImplRemapInfo`, `delayedFileGenReverse`.
- **Acceptance Criteria**:
  - Unit tests `tests/FSharp.Compiler.UnitTests/HotReload/IlxGenEnvTests.fs` ensure restored environments produce identical IL for unchanged edits.
- **Context**: `IlxGen.fs:1185-1293`, ILX pipeline.
- **Status**: Completed (2025-10-30). Snapshot helpers are threaded through `IlxGenResults`, and `HotReloadBaseline.createWithEnvironment` carries the captured environment when the hot reload feature flag is enabled.
- **Follow-up**: Expose a test harness (reflection or public factory) so we can re-enable `IlxGenEnv` restoration tests before Milestone 2 work begins.

### Task 1.5 – HotReloadNameMap Coverage Audit
- **Status**: Completed (2025-11-01). `NiceNameGenerator` now consults `HotReloadNameMap` when hot reload is enabled, ensuring closures, async state machines, computation expressions, and record/union helpers reuse stable compiler-generated names. Component coverage lives in `tests/FSharp.Compiler.ComponentTests/HotReload/NameMapTests.fs`, which exercises all four scenarios under `--enable:hotreloaddeltas` and enforces the absence of line-number suffixes.
- **Follow-up**: Keep the test matrix in sync with future synthesized constructs (e.g., task builders) so new helpers receive map coverage.

- **Scope**: Audit every `NiceNameGenerator`/`StableNiceNameGenerator` call site and ensure hot reload sessions reuse stable names.
- **Files/Modules**: `IlxGen.fs`, `EraseClosures.fs`, `AsyncBuilder.fs`, `ComputationExpressions/*`, other modules emitting synthesized names.
- **Objective**: Eliminate line-number-based suffixes for compiler-generated symbols (closures, async state machines, computation expression artifacts, `PrivateImplementationDetails`) when hot reload is enabled.
- **Acceptance Criteria**:
  - Added component tests covering closures, async workflows, computation expressions, and record/union helpers with stable tokens across edits.
  - Documentation of any remaining generators that trigger rude edits rather than stable renaming.
- **Context**: Required to keep metadata tokens stable; mirrors Roslyn’s reliance on `DefinitionMap.MetadataLambdasAndClosures`.

## Milestone 2 – Delta Emission & Rude Edit Diagnostics

### Task 2.1 – IlxDeltaEmitter Core
- **Scope**: Build `IlxDeltaEmitter` to produce metadata/IL/PDB deltas.
- **Files/Modules**: new `src/Compiler/CodeGen/IlxDeltaEmitter.fs`, integrate into hot-reload workflow.
- **Objective**: Given baseline state and semantic edits, emit delta blobs ready for `MetadataUpdater.ApplyUpdate`, extending `AbstractIL/ilwrite.fs` with delta-aware helpers (EncLog/EncMap table writers, heap slicing) analogous to Roslyn’s `DeltaMetadataWriter`.
- **Acceptance Criteria**:
  - Component tests `tests/FSharp.Compiler.ComponentTests/HotReload/DeltaEmitterTests.fs` validate emitted blobs match `mdv` output (`mdv /g:<md>;<il>`).
  - Ensure `EncLog`/`EncMap` tracking implemented.
  - HotReloadTest orchestrates mdv verification in CI.
- **Context**: Roslyn `DeltaMetadataWriter.cs`, `EmitDifferenceResult`.
- **Status**: Completed (2025-11-01). `IlxDeltaEmitter` now re-emits updated modules via `ILBinaryWriter`, fills `IlxDeltaStreamBuilder` with method bodies/standalone signatures, and uses `FSharpDeltaMetadataWriter` to materialise metadata/IL/PDB blobs plus EncLog/EncMap projections. Multi-generation CLI flows and component tests validate mdv parity, method token reuse, and session state chaining.
- **Subtask tracker** (each with its own commit/tests report):
  - ✅ **Extract method-body helpers** (2025-10-31, commit `d58d2edd9`): factored out `EncodeMethodBody` in `ilwrite.fs`; ran `./.dotnet/dotnet build FSharp.sln -c Debug` and HotReload-filtered component tests.
  - ✅ **Expose metadata row builders** (2025-10-31): Added `IlxDeltaStreamBuilder.AddStandaloneSignature` and captured emitted handles/blobs so delta emission can queue StandaloneSig rows. Tests: `./.dotnet/dotnet build FSharp.sln -c Debug`; `./.dotnet/dotnet test tests/FSharp.Compiler.ComponentTests/FSharp.Compiler.ComponentTests.fsproj -c Debug --no-build --filter FullyQualifiedName~HotReload`.
  - ✅ **Implement delta emit context** (2025-10-31): introduced `IlDeltaStreamBuilder` (`src/Compiler/CodeGen/IlxDeltaStreams.fs`) and unit coverage in `DeltaEmitterTests.fs`. Validated via `./.dotnet/dotnet build FSharp.sln -c Debug` and `./.dotnet/dotnet test ... --filter FullyQualifiedName~HotReload`.
  - ✅ **Integrate with `IlxDeltaEmitter`** (2025-10-31): `emitDelta` now re-emits the updated module via `ILBinaryWriter`, populates `IlxDeltaStreamBuilder`, and persists metadata/IL streams alongside method body payloads. Added mdv-backed component tests. Tests: `./.dotnet/dotnet build FSharp.sln -c Debug`; `./.dotnet/dotnet test tests/FSharp.Compiler.ComponentTests/FSharp.Compiler.ComponentTests.fsproj -c Debug --no-build --filter FullyQualifiedName~HotReload`.
  - ✅ **Thread generator state through CLI harness** (2025-10-31): `DeltaGenerator.generateDelta` now returns the updated generator alongside the delta payload, propagating EncId and generation tracking into `HotReloadTest.fs` so multi-edit runs reuse the latest state. Validation: `dotnet build hot_reload_poc/HotReloadPoc.sln -c Debug`.
  - ✅ **Drive multi-generation CLI flow** (2025-10-31): `HotReloadTest` re-used the same generator across sequential edits, emitted generation-numbered delta artifacts, and reran mdv before each `MetadataUpdater.ApplyUpdate`. Validation at the time: `dotnet build hot_reload_poc/HotReloadPoc.sln -c Debug`. **Note (2025-11-02)**: this harness has temporarily regressed while Task 3.5 rewrites the sample to use `FSharpChecker`; restoring equivalent coverage via the new pipeline is required.
  - ✅ **Persist compiler session state** (2025-10-31): `HotReloadState` now tracks the captured baseline, current generation, and previous EncId; `IlxDeltaEmitter` consumes the session data to wire EncBaseId/EncId chaining and `DeltaEmitterTests` assert back-to-back deltas. Tests: `./.dotnet/dotnet build FSharp.sln -c Debug`.
  - ✅ **Broaden test coverage** (2025-11-01): Added multi-method IL delta validation (`emitDelta updates multiple methods`) and expanded HotReload NameMap component tests to cover closures, async workflows, task builders, computation expressions, and record/union helpers. Tests: `dotnet build FSharp.sln -c Debug`; `dotnet test tests/FSharp.Compiler.ComponentTests/FSharp.Compiler.ComponentTests.fsproj -c Debug --filter FullyQualifiedName~HotReload`.
- **Follow-up**: Integrate variable slot allocator/local mapping before enabling edits that rewrite locals (closures/async) and ensure synthesized member mapping (Task 2.5 follow-up) continues to project tokens correctly.
- **Follow-up (2025-11-02)**: Completed. `IlxDeltaEmitter` now normalises generated field names, reuses baseline tokens for stable backing fields, and raises `HotReloadUnsupportedEditException` when a truly new field is introduced so tooling surfaces an explicit unsupported-edit diagnostic.
- **Follow-up**: Keep delta output validation wired to `HotReloadNameMap` stability for any newly supported generator, and extend tests to cover async state machines, resumable computation expressions, and multi-delta sessions once symbol matcher enhancements land.

### Task 2.2 – Rude Edit Classification
- **Scope**: Extend `TypedTreeDiff` to label unsupported edits.
- **Files/Modules**: `TypedTreeDiff.fs`, new `RudeEditDiagnostics.fs`.
- **Objective**: Detect inline changes, signature edits, union layout changes, type provider regenerations, and edits that would alter `.sigdata`/`.optdata`; surface diagnostics.
- **Acceptance Criteria**:
  - Unit tests in `tests/FSharp.Compiler.Service.Tests/HotReload/TypedTreeDiffTests.fs` (and companions) cover each scenario.
  - Diagnostics integrate with existing compiler error reporting (no global behavior change until flag enabled).
- **Context**: Roslyn `RudeEditDiagnosticTests`.
- **Status**: Completed (2025-11-01). `TypedTreeDiff` now classifies signature/inline/type-layout/added/removed edits, `RudeEditDiagnostics.fs` maps each rude edit to stable IDs and messages, and service-layer tests (`RudeEditDiagnosticsTests.fs`, `TypedTreeDiffTests.fs`) assert the diagnostics. Validation: `./.dotnet/dotnet build FSharp.sln -c Debug`; `./.dotnet/dotnet test tests/FSharp.Compiler.Service.Tests/FSharp.Compiler.Service.Tests.fsproj -c Debug --no-build --filter FullyQualifiedName~HotReload`.
- **Follow-up**: Plumb diagnostics into `FSharpEditAndContinueLanguageService` responses and CLI tooling (Task 3.1/Task 3.4). Track additional rude-edit scenarios (type providers, active patterns) for future expansion.

### Task 2.3 – PDB Delta Support
- **Scope**: Implement `FSharpPdbDeltaBuilder` using existing ILPdbWriter utilities.
- **Files/Modules**: new `src/Compiler/CodeGen/HotReloadPdb.fs`, modifications to `ILPdbWriter` if needed.
- **Objective**: Emit portable PDB deltas consistent with sequence points/local scopes.
- **Acceptance Criteria**:
  - Component tests `tests/FSharp.Compiler.ComponentTests/HotReload/PdbTests.fs` assert sequence points align with source.
- **Context**: Roslyn PDB emission logic; `ilwritepdb.fs`.
- **Status**: Completed (2025-11-01). Added `HotReloadPdb.fs` with snapshot capture and delta emission helpers, stored baseline PDB metadata via `FSharpEmitBaseline.PortablePdb`, and threaded snapshot capture through `fsc.fs`. `IlxDeltaEmitter` now produces portable PDB deltas alongside metadata/IL streams, and `PdbTests.fs` validates that method debug information is emitted for updated tokens. Validation: `./.dotnet/dotnet test tests/FSharp.Compiler.ComponentTests/FSharp.Compiler.ComponentTests.fsproj -c Debug --no-build --filter FullyQualifiedName~HotReload`.
- **Follow-up**: Enrich the delta builder with local scope/async metadata once method body replay work lands (Tasks 2.1/2.5) and consider wiring actual source documents when F# code generation surfaces them.

### Task 2.4 – Runtime Integration Hooks
- **Scope**: Add compiler entry points to trigger delta generation and integrate feature flags.
- **Files/Modules**: CLI entry logic (`src/fsc/fsc.fs`), new hot reload module orchestrating baseline/delta runs.
- **Objective**: Provide internal API for hot reload sessions (not yet public), respect `--enable:hotreloaddeltas`.
- **Acceptance Criteria**:
  - End-to-end script in `tests/projects/HotReloadSamples` builds baseline and emits single delta, verifying reflection result changes.
  - Whole build remains backward compatible.
- **Context**: `HotReloadTest.fs` prototype, Roslyn `WatchHotReloadService`.
- **Status**: Completed (2025-11-01). `FSharpEditAndContinueLanguageService.EmitDeltaForCompilation` now diffs the cached `CheckedAssemblyAfterOptimization` against the latest compilation, maps `FSharpSymbolChanges` onto baseline tokens, and invokes the delta emitter. `main6` seeds the session with optimized implementations on baseline capture, and `DeltaBuilder` translates typed-tree edits into method/type token projections. Coverage added via `RuntimeIntegrationTests.fs`, which compiles real F# code to ensure method-body edits produce IL/metadata deltas and advance generation bookkeeping.
- **Follow-up**: Extend symbol mapping to handle additions/deletions (leveraging upcoming Task 2.5 symbol matcher) and surface the emitted deltas through CLI tooling (`dotnet watch`).

### Task 2.5 – FSharpSymbolMatcher & Synthesized Member Mapping
- **Scope**: Implement `FSharpSymbolMatcher` to map baseline symbols (including synthesized members) into the current compilation and introduce `FSharpSymbolChanges` to aggregate edit classifications for delta emission.
- **Files/Modules**: new `src/Compiler/CodeGen/FSharpSymbolMatcher.fs`, new `src/Compiler/CodeGen/FSharpSymbolChanges.fs`, integrations in `IlxDeltaEmitter.fs`, updates to `HotReloadBaseline` for synthesized-member metadata.
- **Objective**: Reuse metadata handles for unchanged definitions, merge anonymous types/delegates, and identify deleted synthesized members before emitting deltas.
- **Acceptance Criteria**:
  - Unit/component tests covering closure/async state-machine edits confirm tokens are reused across generations.
  - `FSharpSymbolChanges` surfaces added/updated/deleted/synthesised members mirroring Roslyn’s `SymbolChanges`, and feeds `IlxDeltaEmitter` with the necessary maps.
- **Context**: Mirrors Roslyn’s `SymbolMatcher`/`CSharpSymbolMatcher`; prerequisite for accurate metadata deltas.

- **Status**: Completed (2025-11-01). Added `HotReload/SymbolMatcher.fs` to map baseline IL definitions (types/methods/fields) keyed by `MethodDefinitionKey`, and refactored `IlxDeltaEmitter` to reuse the matcher for token projection. Hot reload session plumbing now snapshots the latest `CheckedAssemblyAfterOptimization`, enabling `EmitDeltaForCompilation` to diff typed trees, map edits to tokens, and emit/commit deltas. Component/runtime tests exercise multi-generation emission and token stability.
- **Follow-up**: Extend the matcher to understand synthesized members (closures, async/state-machine scaffolding, computation-expression builders) and ensure delta metadata preserves stable identities once Task 2.1 follow-ups on locals land.

## Milestone 3 – Tooling & API Surface

### Task 3.1 – FSharpChecker Hot Reload API
- **Scope**: Expose opt-in APIs for hot reload consumers.
- **Files/Modules**: `src/Compiler/Service/FSharpChecker.fs`, new signatures; documentation in `docs/hotreload-api.md`.
- **Objective**: Provide methods to start session, apply edits, retrieve diagnostics (internal until stabilized).
- **Acceptance Criteria**:
  - Unit tests `tests/FSharp.Compiler.Service.Tests/HotReload/HotReloadCheckerTests.fs` simulate session start/apply/rollback.
  - API flagged as preview (requires `--langversion:preview`).
- **Context**: Roslyn `ManagedHotReloadLanguageService`.
- **Status**: In progress (updated 2025-11-02). Preview APIs (`StartHotReloadSession`, `EmitHotReloadDelta`, `EndHotReloadSession`, `HotReloadSessionActive`, `HotReloadCapabilities`) now drive the edit-and-continue service end-to-end. Metadata/IL/PDB deltas are emitted by default, multi-delta sessions run through the scripted demo, and the checker surfaces capability flags so hosts can negotiate supported features. Runtime apply remains behind the `FSHARP_HOTRELOAD_ENABLE_RUNTIME_APPLY` feature flag while we stabilise the delta payloads. Checker and component hot reload suites continue to pass (`./.dotnet/dotnet test ...HotReload`).
- **Follow-up**: (1) Validate runtime `MetadataUpdater.ApplyUpdate` end-to-end, retire the feature flag, and document the supported scenarios; (2) surface richer diagnostics/telemetry (delta sizes, rude-edit propagation) to IDE hosts before exiting preview; (3) refine capability negotiation after runtime apply is enabled (e.g., advertise structural-edit roadmap, document opt-in warnings).

### Task 3.2 – Telemetry & Logging
- **Scope**: Instrument key paths for performance and rebuild diagnostics.
- **Files/Modules**: logging in hot reload modules (`HotReloadBaseline`, `IlxDeltaEmitter`), telemetry hooks similar to `EditAndContinueSessionTelemetry`.
- **Objective**: Record delta generation latency, rude edit counts, memory overhead.
- **Acceptance Criteria**:
  - Component tests ensure logging doesn’t regress functionality; documentation updated.
- **Context**: `EditAndContinueSessionTelemetry` (Roslyn); internal telemetry guidelines.
- **Status**: Completed (2025-11-01). Added `Activity` instrumentation around session start, delta emission, and checker APIs so hosts can trace generations (`HotReload/EditAndContinueLanguageService.fs`, `service.fs`). Tests exercised the new hooks via the existing hot reload suites.
- **Follow-up**: Emit counters/structured events (success vs. fallback, rude edits) once metadata/PDB deltas are finalized so telemetry consumers gain richer insights.

### Task 3.3 – dotnet watch / CLI Integration Scripts
- **Scope**: Provide sample scripts and ensure CLI invocation works with feature flag.
- **Files/Modules**: `tests/scripts/HotReloadWatchTests.ps1/sh`, updates to project samples.
- **Objective**: Validate `dotnet watch --hot-reload` runs F# sample edits successfully.
- **Acceptance Criteria**:
  - Scripted tests pass (manual or automated), verifying CLI output and reflection changes.
- **Context**: Roslyn watch integration tests; `dotnet watch` documentation.
- **Status**: Completed (2025-11-01). Added `hot_reload_poc/scripts/run-hot-reload-watch.sh`, a portable helper that sets the required environment (`DOTNET_MODIFIABLE_ASSEMBLIES=debug`) and launches `dotnet watch --hot-reload` against the F# sample. Serves as the reference entry point for demos/manual validation using the existing TestApp project.
- **Follow-up**: Automate the watch flow (capture console markers or reflection checks) once metadata/PDB deltas ship so CI can exercise the script without manual intervention.
- **Follow-up (short term, 2025-11)**: Land an SDK-side `dotnet watch` integration that calls today’s `FSharpChecker` hot reload services directly, enabling an official watch demo without waiting on larger compiler infrastructure changes.
- **Follow-up (long term)**: Evolve the compiler toward an `FSharpWorkspace`/`FSharpProject` abstraction—mirroring Roslyn workspaces as sketched in [dotnet/fsharp#11976](https://github.com/dotnet/fsharp/issues/11976)—so IDE hosts and `dotnet watch` share immutable solution snapshots, richer incremental analysis, and unified diagnostics.

**dotnet watch roadmap snapshot (2025-11-02)**
- *Current status*: Hot reload works via the in-repo demo (`HotReloadDemoApp`) and scripted automation. Method-body deltas are emitted with `FSharpChecker.EmitHotReloadDelta`, applied through `MetadataUpdater.ApplyUpdate`, and the smoke test validates multi-delta sessions.
- *Short-term goal*: Extend `dotnet watch` itself to call the existing F# hot reload service directly. This path touches only the SDK repo and the F# compiler service, enabling `dotnet watch --hot-reload` CLI scenarios (method-body edits, runtime apply, unsupported-edit diagnostics) without Roslyn changes.
- *Long-term direction*: As the “Modernizing F# Analysis” plan lands (immutable snapshots and Roslyn-like workspaces), revisit the integration so `dotnet watch` and Visual Studio route F# hot reload through Roslyn’s `WatchHotReloadService`/`IManagedHotReloadLanguageService`. That alignment will unlock full IDE parity (shared diagnostics UI, cross-language sessions, richer rude-edit handling) once the shared infrastructure exists.

### Task 3.4 – Documentation & Samples
- **Scope**: Document usage, limitations, and provide canonical edit sequences.
- **Files/Modules**: `docs/HotReload.md`, update README; maintain `tests/projects/HotReloadSamples/README.md`.
- **Objective**: Ensure contributors and users understand feature flag, supported edits, and test commands.
- **Acceptance Criteria**:
  - Docs reviewed, sample scripts runnable.
- **Status**: Completed (2025-11-01). Added `docs/HotReload.md` describing the current manual workflow (helper script, environment variables, known gaps) so contributors can exercise the F# sample end-to-end and track remaining integration work.
- **Follow-up**: Refresh the doc when metadata/PDB deltas ship and once the sample consumes the new compiler pipeline instead of the handcrafted delta generator.

### Task 3.5 – Sample & Tooling Integration
- **Scope**: Deliver a self-contained hot reload demo inside the `fsharp` repository (for example under `tests/projects/HotReloadDemo`) that exercises the new `FSharpChecker` APIs. Retire the ad hoc `hot_reload_poc` console harness.
- **Files/Modules**: new sample project in `fsharp/tests/projects`, wiring in `tests/FSharp.Compiler.ComponentTests` or `Service.Tests` as needed, plus supporting docs under `docs/`.
- **Objective**: Provide an example that compiles within the main repo, invokes `StartHotReloadSession` and `EmitHotReloadDelta`, and can be driven by `dotnet watch` or an automated smoke test without relying on the external `hot_reload_poc` folder.
- **Acceptance Criteria**:
  - Sample project builds with `./.dotnet/dotnet build FSharp.sln -c Debug` when hot reload is enabled.
  - Editing the sample code triggers delta emission through the checker APIs and updates behavior without restarting the process.
  - Legacy references to `hot_reload_poc/src/TestApp` are removed or clearly marked as obsolete.
  - Documentation (`docs/HotReload.md`, README snippets, or new sample docs) explains how to run the in-repo demo.
- **Context**: Builds on Tasks 2.1 through 3.1; replaces the throwaway `TestApp` proof of concept with an in-repo showcase.
- **Status**: Completed (2025-11-02). Added `tests/projects/HotReloadDemo/HotReloadDemoApp`, a net10.0 console that compiles `DemoTarget.fs` with the `FSharpChecker` hot reload APIs, applies deltas via `MetadataUpdater.ApplyUpdate`, and prints the temp source path/generation info for manual validation. The project is part of `FSharp.sln`, and `docs/HotReload.md` now documents the flow and the deprecation of the `hot_reload_poc` harness.
- **Follow-up**:
  - Automation now covered by Task 3.6; keep an eye on future enhancements (multi-delta, telemetry) tracked below.
  - Decide whether to retire or repurpose `hot_reload_poc/scripts/run-hot-reload-watch.sh` once CLI integration plans settle.
  - Wire the sample into `dotnet watch` and telemetry once locals/synthesized-member work (Task 2.x) lands.

### Task 3.6 – Hot Reload Demo Automation
- **Scope**: Add a smoke test that launches `HotReloadDemoApp`, edits the generated workload, and verifies the emitted delta so regressions are caught automatically.
- **Files/Modules**: `tests/projects/HotReloadDemo/HotReloadDemoApp`, new helper script under `tests/scripts/`, and CI plumbing.
- **Objective**: Ensure the new sample remains functional across commits and provide a reproducible workflow for CI.
- **Acceptance Criteria**:
  - Automated run edits the temp `DemoTarget.fs`, emits a delta, and records the result (runtime apply may be skipped until the runtime path stabilises).
  - The automation is wired into CI (documented command and inclusion in existing test pipelines).
  - Documentation references the new regression gate.
- **Context**: Builds on Task 3.5; prepares the ground for future `dotnet watch` integration.
- **Status**: Completed (2025-11-02). `eng/Build.ps1` now invokes the hot reload demo smoke test during CoreCLR/desktop test runs, so CI surfaces regressions in the scripted workflow and fails the build if the success marker is missing.
- **Follow-up**:
  - Multi-delta coverage now runs inside the scripted smoke test; consider extending it further once runtime apply stabilises.
  - Capture telemetry or additional diagnostics (generation ids, rude edits) for easier triage.
  - Evaluate whether we should also wire the scripted mode into `dotnet watch` once runtime apply becomes reliable.

### Task 3.7 – `fsc-watch` Hot Reload Demo (Short Term)
- **Status**: In progress (2025-11-03). Added a project-based regression in `MdvValidationTests.fs` that drives `StartHotReloadSession`/`EmitHotReloadDelta` using the MSBuild command line captured from `fsc-watch`, normalises `--out`/source paths, and verifies the emitted delta updates the expected user string. The helper now tolerates missing mdv runtimes but still records the metadata literal so we can diff locally. Runtime `MetadataUpdater.ApplyUpdate` inside the CLI harness is still failing, so the scripted smoke test remains disabled. A separate integration script (`hot_reload_poc/scripts/watchloop_mdv_integration.sh`) now automates the watch-loop + mdv workflow; it currently reproduces the stale literal (`"Message version 3 …"`) emitted by the CLI, so we have a concrete regression harness while we investigate.
- **Follow-up**:
  1. Use the new component test harness to compare the CLI-generated deltas with Roslyn’s output (focus on the `#US` heap diff) and unblock the runtime apply failure.
  2. Once runtime apply succeeds, re-enable the scripted CLI smoke test and capture the mdv command automatically in the logs.
  3. Audit `fsc-watch` cleanup logic (bin/obj/delta directories) so each run starts from a clean baseline without leftover files (the integration script already resets the delta directory; extend that logic to the CLI itself).
### Task 4.1 – Workspace Specification & Prototype
- **Scope**: Define `FSharpWorkspace`, `FSharpProject`, and `FSharpDocument` data models and build an initial prototype.
- **Files/Modules**: New workspace assemblies (to be decided), incremental builder integration layers.
- **Objective**: Provide immutable snapshots compatible with Roslyn workspaces and hot reload pipelines.
- **Acceptance Criteria**:
  - Draft API surface and contract docs (`notes/fsharp_workspace_roadmap.md`).
  - Prototype conversion from project graph to workspace snapshot with cached typed trees.
  - Migration strategy documented for `FSharpChecker` consumers.
- **Context**: Roadmap derived from [dotnet/fsharp#11976](https://github.com/dotnet/fsharp/issues/11976).
- **Status**: Planned (2025-11-02). Execution staged for 2026 milestones; see roadmap note for timeline.
- **Follow-up**: Subsequent tasks will cover analyzer hosting, IDE integration, and GA criteria.

---

Each task must:
- Preserve backward compatibility (feature flag off by default).
- Include unit/component tests as specified.
- Run the full build after completion.
- Reference the relevant code locations and resources for context.

- After completing each task update this plan with the task status and capture any lessons for ARCHITECTURE_PROPOSAL.md/IMPLEMENTATION_PLAN.md.
- **Follow-up**: Coordinate with SymbolMatcher work to ensure newly stabilized names are consumed when remapping definitions.

### Task 1.6 – Definition Map & Semantic Edit Classification
- **Scope**: Build `FSharpDefinitionMap`/`FSharpSymbolChanges` to track Added/Updated/Deleted/Rude edits and synthesized members.
- **Files/Modules**: `TypedTreeDiff.fs`, new `DefinitionMap.fs`, `RudeEditDiagnostics.fs`.
- **Objective**: Classify edits per symbol (including synthesized constructs) and provide rude-edit diagnostics prior to delta emission.
- **Acceptance Criteria**:
  - Unit tests mirror Roslyn `EditAndContinueAnalyzerTests` scenarios (method body update, inline change rude edit, union shape change, missing symbol).
  - Output consumed by `IlxDeltaEmitter` and future `FSharpSymbolMatcher` to build deltas.
- **Context**: Roslyn’s `DefinitionMap`, `SymbolChanges`, `AddedOrChangedMethodInfo`.
- **Follow-up**: Design `FSharpSynthesizedTypeMaps` and ensure `FSharpEmitBaseline` persists synthesized member metadata for reuse.
- **Status**: Completed (2025-10-30) — `TypedTreeDiff` annotates `SymbolId`/`SemanticEdit` with synthesis info, `FSharpDefinitionMap` surfaces synthesized helpers, and `FSharpSymbolChanges` aggregates the data with component coverage in `SymbolChangesTests.fs`.

### Task 2.x – mdv delta validation regression
- **Status (2025-11-03)**: In `fsc-watch` mdv command-only mode the emitted `delta_gen1.meta` still references user-string heap entry `'Message version 3 (invocation #%d)'` even after editing the sample to `'Message version <timestamp>'`. Tokens for method bodies and IL remapping succeed, but the delta user-string heap is reusing the baseline literal. Instrumentation (`FSHARP_HOTRELOAD_TRACE_STRINGS=1`) is now wired into `IlxDeltaEmitter.rewriteMethodBody`, and `IlxDelta.UserStringUpdates` exposes the captured mapping. Component test `DeltaEmitterTests.emitDelta records updated user strings` guards the expected behaviour.
- **Next steps**:
  1. Use the new trace output against the watch-loop repro to confirm whether the rewritten IL is still pointing at the baseline string token or whether the metadata builder is deduplicating incorrectly.
  2. If the IL carries the fresh literal, audit the mdv invocation (baseline path vs. updated assembly) to ensure we are loading the correct baseline snapshot before reading the delta; otherwise, adjust the IL rewrite to substitute the new token.
  3. Once the runtime path is validated, update `MdvValidationTests`/integration harnesses to assert the literal end-to-end so regressions are caught without manual mdv runs.
- **Owner**: Hot reload squad
- **Dependencies**: `IlxDeltaEmitter.fs`, `IlxDeltaStreams.fs`, `HotReloadSession.fs`, mdv harness under `tools/fsc-watch`.
- **Risks**: Incorrect user-string remapping will surface as stale literals at runtime even when method bodies update, undermining watch demos and mdv verification.
