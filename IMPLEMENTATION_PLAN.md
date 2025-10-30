# F# Hot Reload Implementation Plan

This plan converts ARCHITECTURE_PROPOSAL.md into concrete milestones and tasks. Each task is scoped so that a single LLM-focused iteration can complete it while keeping the repository in a buildable, backwards-compatible state. After every task, run the full build (`dotnet build FSharp.sln` or `build.cmd`) and applicable test suites.

- Reminder: After completing each task, update this plan and capture any follow-up adjustments in both IMPLEMENTATION_PLAN.md and ARCHITECTURE_PROPOSAL.md.

## Milestone 1 – Baseline Capture & Semantic Diff Infrastructure

### Task 1.1 – TypedTreeDiff Core
- **Scope**: Implement the `TypedTreeDiff` data structures and algorithms.
- **Files/Modules**: `src/Compiler/TypedTree/TypedTreeDiff.fs` (new), with signatures in `TypedTreeDiff.fsi`; updates to `TypedTree`/`TypedTreeOps` as needed.
- **Objective**: Compare two `CheckedImplFile` snapshots, producing semantic edit records keyed by stamps (method body updates vs. rude edits). Support method-body-only updates initially.
- **Acceptance Criteria**:
  - Unit tests under `tests/FSharp.Compiler.UnitTests/HotReload/TypedTreeDiffTests.fs` verify detection of unchanged nodes, method-body edits, inline changes (rude edit), union layout changes (rude edit).
  - Documentation references existing helpers in `TypedTree.fs`, `TypedTreeOps.fs`; see Roslyn `EditAndContinueAnalyzerTests` for inspiration.
  - Backwards compatibility maintained (no behavior change until feature flag enabled).
- **Additional Context**: `src/Compiler/Service/IncrementalBuild.fs` for typed-tree snapshots.

### Task 1.2 – HotReloadNameMap & Generators
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

### Task 1.4 – IlxGenEnv Snapshotting
- **Scope**: Persist minimal `IlxGenEnv` required for delta emission.
- **Files/Modules**: `IlxGen.fs`, `HotReloadBaseline.fs` (extend).
- **Objective**: Capture `tyenv`, `valsInScope`, `imports`, `sigToImplRemapInfo`, `delayedFileGenReverse`.
- **Acceptance Criteria**:
  - Unit tests `tests/FSharp.Compiler.UnitTests/HotReload/IlxGenEnvTests.fs` ensure restored environments produce identical IL for unchanged edits.
- **Context**: `IlxGen.fs:1185-1293`, ILX pipeline.

## Milestone 2 – Delta Emission & Rude Edit Diagnostics

### Task 2.1 – IlxDeltaEmitter Core
- **Scope**: Build `IlxDeltaEmitter` to produce metadata/IL/PDB deltas.
- **Files/Modules**: new `src/Compiler/CodeGen/IlxDeltaEmitter.fs`, integrate into hot-reload workflow.
- **Objective**: Given baseline state and semantic edits, emit delta blobs ready for `MetadataUpdater.ApplyUpdate`.
- **Acceptance Criteria**:
  - Component tests `tests/FSharp.Compiler.ComponentTests/HotReload/DeltaEmitterTests.fs` validate emitted blobs match `mdv` output (`mdv /g:<md>;<il>`).
  - Ensure `EncLog`/`EncMap` tracking implemented.
  - HotReloadTest orchestrates mdv verification in CI.
- **Context**: Roslyn `DeltaMetadataWriter.cs`, `EmitDifferenceResult`.

- **Scope**: Build `IlxDeltaEmitter` to produce metadata/IL/PDB deltas.
- **Files/Modules**: new `src/Compiler/CodeGen/IlxDeltaEmitter.fs`, integrate into hot-reload workflow.
- **Objective**: Given baseline state and semantic edits, emit delta blobs ready for `MetadataUpdater.ApplyUpdate`.
- **Acceptance Criteria**:
  - Component tests `tests/FSharp.Compiler.ComponentTests/HotReload/DeltaEmitterTests.fs` validate emitted blobs match expected mdv output for method-body edits.
  - Ensure `EncLog`/`EncMap` tracking implemented.
- **Context**: Roslyn `DeltaMetadataWriter.cs`, `EmitDifferenceResult`.

### Task 2.2 – Rude Edit Classification
- **Scope**: Extend `TypedTreeDiff` to label unsupported edits.
- **Files/Modules**: `TypedTreeDiff.fs`, new `RudeEditDiagnostics.fs`.
- **Objective**: Detect inline changes, signature edits, union layout changes, type provider regenerations; surface diagnostics.
- **Acceptance Criteria**:
  - Unit tests `tests/FSharp.Compiler.UnitTests/HotReload/RudeEditTests.fs` cover each scenario.
  - Diagnostics integrate with existing compiler error reporting (no global behavior change until flag enabled).
- **Context**: Roslyn `RudeEditDiagnosticTests`.

### Task 2.3 – PDB Delta Support
- **Scope**: Implement `FSharpPdbDeltaBuilder` using existing ILPdbWriter utilities.
- **Files/Modules**: new `src/Compiler/CodeGen/HotReloadPdb.fs`, modifications to `ILPdbWriter` if needed.
- **Objective**: Emit portable PDB deltas consistent with sequence points/local scopes.
- **Acceptance Criteria**:
  - Component tests `tests/FSharp.Compiler.ComponentTests/HotReload/PdbTests.fs` assert sequence points align with source.
- **Context**: Roslyn PDB emission logic; `ilwritepdb.fs`.

### Task 2.4 – Runtime Integration Hooks
- **Scope**: Add compiler entry points to trigger delta generation and integrate feature flags.
- **Files/Modules**: CLI entry logic (`src/fsc/fsc.fs`), new hot reload module orchestrating baseline/delta runs.
- **Objective**: Provide internal API for hot reload sessions (not yet public), respect `--enable:hotreloaddeltas`.
- **Acceptance Criteria**:
  - End-to-end script in `tests/projects/HotReloadSamples` builds baseline and emits single delta, verifying reflection result changes.
  - Whole build remains backward compatible.
- **Context**: `HotReloadTest.fs` prototype, Roslyn `WatchHotReloadService`.

## Milestone 3 – Tooling & API Surface

### Task 3.1 – FSharpChecker Hot Reload API
- **Scope**: Expose opt-in APIs for hot reload consumers.
- **Files/Modules**: `src/Compiler/Service/FSharpChecker.fs`, new signatures; documentation in `docs/hotreload-api.md`.
- **Objective**: Provide methods to start session, apply edits, retrieve diagnostics (internal until stabilized).
- **Acceptance Criteria**:
  - Unit tests `tests/FSharp.Compiler.Service.Tests/HotReloadCheckerTests.fs` simulate session start/apply/rollback.
  - API flagged as preview (requires `--langversion:preview`).
- **Context**: Roslyn `ManagedHotReloadLanguageService`.

### Task 3.2 – Telemetry & Logging
- **Scope**: Instrument key paths for performance and rebuild diagnostics.
- **Files/Modules**: logging in hot reload modules (`HotReloadBaseline`, `IlxDeltaEmitter`), telemetry hooks similar to `EditAndContinueSessionTelemetry`.
- **Objective**: Record delta generation latency, rude edit counts, memory overhead.
- **Acceptance Criteria**:
  - Component tests ensure logging doesn’t regress functionality; documentation updated.
- **Context**: `EditAndContinueSessionTelemetry` (Roslyn); internal telemetry guidelines.

### Task 3.3 – dotnet watch / CLI Integration Scripts
- **Scope**: Provide sample scripts and ensure CLI invocation works with feature flag.
- **Files/Modules**: `tests/scripts/HotReloadWatchTests.ps1/sh`, updates to project samples.
- **Objective**: Validate `dotnet watch --hot-reload` runs F# sample edits successfully.
- **Acceptance Criteria**:
  - Scripted tests pass (manual or automated), verifying CLI output and reflection changes.
- **Context**: Roslyn watch integration tests; `dotnet watch` documentation.

### Task 3.4 – Documentation & Samples
- **Scope**: Document usage, limitations, and provide canonical edit sequences.
- **Files/Modules**: `docs/HotReload.md`, update README; maintain `tests/projects/HotReloadSamples/README.md`.
- **Objective**: Ensure contributors and users understand feature flag, supported edits, and test commands.
- **Acceptance Criteria**:
  - Docs reviewed, sample scripts runnable.

---

Each task must:
- Preserve backward compatibility (feature flag off by default).
- Include unit/component tests as specified.
- Run the full build after completion.
- Reference the relevant code locations and resources for context.

- After completing each task update this plan with the task status and capture any lessons for ARCHITECTURE_PROPOSAL.md/IMPLEMENTATION_PLAN.md.
