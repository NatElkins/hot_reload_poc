# F# Hot Reload Implementation Plan

This plan converts ARCHITECTURE_PROPOSAL.md into concrete milestones and tasks. Each task is scoped so that a single LLM-focused iteration can complete it while keeping the repository in a buildable, backwards-compatible state. After every task, run the full build (`dotnet build FSharp.sln` or `build.cmd`) and applicable test suites. Complete outstanding work in earlier milestones before tackling later items—e.g., finish the Task 2.x synthesized-name/metadata plumbing prior to expanding tooling scenarios in Milestone 3.

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

### Task 2.3 – Metadata & PDB Delta Writers
- **Scope**: Mirror Roslyn’s `DeltaMetadataWriter`/`EmitBaseline` pipeline so the F# compiler records table/heap deltas, EncLog/EncMap entries, and portable PDB slices without re-emitting full assemblies.
- **Files/Modules**:
  - `src/Compiler/CodeGen/IlxDeltaEmitter.fs`: route metadata/PDB generation through the new writer and persist per-generation artifacts.
  - **New** `src/Compiler/CodeGen/FSharpDeltaMetadataWriter.fs` (or similar) encapsulating delta table/heap serialization.
  - `src/Compiler/CodeGen/HotReloadBaseline.fs`: extend `FSharpEmitBaseline` with cumulative table row counts, heap growth, synthesized/deleted member maps, and method-body metadata (Roslyn’s `AddedOrChangedMethodInfo` equivalent).
  - `src/Compiler/AbstractIL/ilwrite.fs`: expose delta-aware helpers (e.g., enumerate table entries, copy heap suffixes, provide stable token lookups) analogous to Roslyn’s `DefinitionIndex<T>` utilities.
  - `src/Compiler/CodeGen/HotReloadPdb.fs`: continue emitting portable PDB deltas, but consume the new baseline metadata to align method debug handles.
- **Objective**:
  1. Build per-table “definition indexes” for the updated module so we can append only the rows that changed.
  2. Serialize table and heap suffixes using baseline offsets from `MetadataSnapshot`, keeping EncLog/EncMap generation in one place.
  3. Return an updated `FSharpEmitBaseline` mirroring Roslyn’s `EmitBaseline.With(...)`: update table/heap cumulative counts, synthesized/deleted member state, Enc IDs, and per-method delta info.
  4. Ensure PDB deltas remain aligned with the metadata handles produced by the new writer.
- **Acceptance Criteria**:
  - Unit coverage for the writer (e.g., `FSharpDeltaMetadataWriterTests.fs`) verifies table/heap deltas and EncLog/EncMap entries match expectations for simple edits.
  - Component tests (`HotReload/DeltaEmitterTests.fs`, `HotReload/PdbTests.fs`) validate that mdv and portable PDB deltas remain correct across multiple generations using the new writer.
  - `FSharpEmitBaseline` persists cumulative table sizes/heap lengths and synthesized/deleted member sets, and the next delta can be emitted solely from baseline data plus the new edits.
- **Context**: Roslyn `DeltaMetadataWriter`, `EmitBaseline.With(...)`, `SymbolChanges`, and `SynthesizedTypeMaps`.
- **Status**: In progress (definition indexes and method-info bookkeeping landed 2025-11-06; table-row emission, Enc IDs, and synthesized-member tracking outstanding).
- **Status**: In progress (definition indexes and method-info bookkeeping landed 2025-11-06; 2025-11-07 update: property/event map rows now emitted end-to-end, method-semantics rows carry association metadata, and the baseline refresh logic persists the new rows so subsequent generations no longer try to re-add them).
- **Status 2025-11-07**: Helper-driven component tests (`tests/FSharp.Compiler.ComponentTests/HotReload/MdvValidationTests.fs`, `.../PdbTests.fs`) now exercise property/event accessor edits without going through msbuild. mdv asserts that accessor deltas log the expected method tokens, and the PDB suite confirms the portable PDB delta references those tokens (sequence points are still missing for these IL-built helpers—captured as a follow-up below).
- **Status 2025-11-11 (am)**: HotReload PDB tests started failing for added/edited property accessors: the delta contained MethodDef token `0x06000002`, but the temporary IL module only emitted one method, so its portable PDB only had row `0x06000001`. `HotReloadPdb.emitDelta` tried to read `MethodDebugInformation` by the delta token and printed the “missing method debug row 2” warning while returning `None`.
- **Status 2025-11-11 (pm)**: Root cause is the missing inverse method-token map. `IlxDeltaEmitter` already builds `methodTokenMap` (`updated token -> baseline/delta token`) so metadata references stay stable, but the PDB writer never saw the reverse lookup. Action item: capture `delta token -> updated token` after emitting method bodies, pass it to `HotReloadPdb.emitDelta`, and have the PDB reader fetch sequence points via the updated token while still logging Enc entries with the delta token.
- **Status 2025-11-11 (late)**: Implemented the inverse token remap plus test cleanup (`PdbTests` no longer fails intentionally). `dotnet test ... --filter FullyQualifiedName~HotReload` is green again, confirming that added/edited property and event accessors now produce portable PDB deltas with the expected MethodDef tokens and sequence points. Next regression: cover multi-generation accessor edits with the mdv helper to ensure the remap survives successive deltas.
- **Status 2025-11-11**: Attempted to hoist `TableRows`/`RowElementData` out of `DeltaMetadataTables` to satisfy FS0058 (nested type ban). Exposing those records at module scope currently fails because `ILBinaryWriter.MetadataTable<'T>`/`UnsharedRow` are only usable inside the AbstractIL writer; referencing them from a top-level record trips FS0039 before we even serialize rows. We need to mirror the AbstractIL rows into a simple `RowElementData[][]` representation first, then expose that data through the new module so no external file touches `MetadataTable` directly.
- **Status 2025-11-12**: Exported `RowElement`/`RowElementTags`/`MetadataTable<'T>` via `ilwrite.fsi` and rewired `DeltaMetadataTables` plus the metadata writer tests to consume them. This punts the nested-type warning but leaves the tree unbuildable on netstandard: the signature now exposes hidden struct representations (FS0938), `DeltaMetadataTables.fs` imports the wrong namespace (`ILBinary` instead of `BinaryConstants`) and forgets to open `IlxDeltaStreams`, and helper `let` bindings (`compressedLength`, heap caches) sit after member definitions (FS0960). Until those compile issues are fixed, `FSharpMetadataAggregatorTests` cannot even run because `dotnet build` fails.
- **Status 2025-11-11 (late)**: Landed `tests/FSharp.Compiler.Service.Tests/HotReload/MetadataDeltaTestHelpers.fs` and pointed both the writer and aggregator suites at it so the IL module builders, metadata diff helpers, and mdv assertions live in one place. `./.dotnet/dotnet build tests/FSharp.Compiler.Service.Tests/FSharp.Compiler.Service.Tests.fsproj -c Debug` is green again, but `./.dotnet/dotnet test … --filter FullyQualifiedName~HotReload` still fails because (a) `FSharpMetadataAggregatorTests.aggregator translates handles to owning generation` throws `BadImageFormatException: Not enough space for stream header name` when feeding `MetadataReaderProvider` with `DeltaWriter.emit`’s `Metadata` blob, and (b) every “abstract metadata serializer matches metadata builder output” case reports a one-byte discrepancy in the #~ stream length (expected 12 bytes of padding, actual 11). These regressions confirm the new helper is wired up correctly yet the AbstractIL serializer still needs to emit the full metadata root (stream headers + padding) before the aggregator/parity tests will pass.
- **Next steps (execute in order)**:
  1. **Baseline extensions (Enc IDs + synthesized maps)** – ✅ `FSharpEmitBaseline.applyDelta` now persists `EncId`, `EncBaseId`, `NextGeneration`, and refreshes the synthesized-name snapshot so later deltas resume cleanly (2025-11-06).
  2. **Populate definition indexes with real rows** – the helper plumbing is in place, but today we never enqueue new rows. Mirroring Roslyn’s `DefinitionIndex<T>` means:
     - projecting the current `ILModuleDef` through `ILTokenMappings` to resolve baseline row IDs, then pushing any *added* rows into the index so we can assign contiguous row numbers (`NextRowId`).
     - threading per-table indexes for MethodDef, Param, Property/Event, EventMap/PropertyMap just like Roslyn does, so later steps can ask “is this handle added or updated?” and log the correct Enc operation.
     - capturing parameter handle order (Roslyn’s `FirstParamRowMap`) so we can emit `AddParameter` operations when new parameters appear on a method.
     - *Update 2025-11-07*: Introduced `FSharpDefinitionIndex.fs` plus `DefinitionIndexTests.fs`, which mirrors Roslyn’s ordering/freeze semantics for rows and verifies we can distinguish added vs. baseline entries.
     - *Update 2025-11-07 (pm)*: `IlxDeltaEmitter` now instantiates `DefinitionIndex<'MethodDefinitionKey>` using the baseline table counts, records the resulting row order on `IlxDelta`, and the new `DeltaEmitter` regression asserts the indices for multi-method edits. Remaining work: extend the same plumbing to Param/Property/Event tables and feed those snapshots into the metadata writer.
     - *Update 2025-11-08*: Reordered the emitter so the MethodDef index (and `addedMethodDeltaTokens`) is populated before `methodUpdateInputs` are built, enabling us to stream metadata for newly added methods. Added a `DeltaEmitterTests` fact (`emitDelta adds method metadata rows for new method`) that exercises the scenario end-to-end and confirmed the EncLog now records the expected `AddMethod` entry.
     - *Update 2025-11-08 (pm)*: Added `createModuleWithParameterizedMethod` + `emitDelta adds parameter metadata rows for new method`, which verifies we emit `AddParameter` EncLog entries (and tokens) when a newly added method introduces parameters. This proves the Parameter definition index + metadata writer path can hydrate the row payload from the updated assembly before we tackle property/event rows.
     - *Update 2025-11-08 (late pm)*: Introduced a `createPropertyHostBaselineModule` helper plus a new regression (`emitDelta adds property metadata rows for new property`). `IlxDeltaEmitter` now tracks `addedPropertyTokens`, wires them through the property definition index, registers accessor metadata even when the method is new, updates the baseline’s `PropertyTokens`, and emits the corresponding Property/PropertyMap rows so the mdv dump shows the expected `AddProperty` operation.
     - *Update 2025-11-10*: Attempted to lift the row-info records (`MethodDefinitionRowInfo`, `ParameterDefinitionRowInfo`, etc.) into a shared `DeltaMetadataTypes.fs` module so the emitter, metadata tables, and writer all reference the same definitions. The build immediately failed because `DeltaMetadataTables.fs` still exposes its nested `TableRows` record directly via AbstractIL types (`UnsharedRow`), which .NET 9 no longer accepts outside the module. **Follow-up plan**: (1) Refactor `DeltaMetadataTables.fs` to emit a Roslyn-style `RowElementData` snapshot (each row expressed as tag/value pairs) while keeping AbstractIL details private. (2) Once that compiles, add `DeltaMetadataTypes.fs` with the shared row-info definitions and update `FSharpDeltaMetadataWriter`, `IlxDeltaEmitter`, and the metadata writer tests to consume them. (3) Re-run `…FSharpDeltaMetadataWriterTests` and commit.
     - *Update 2025-11-10*: Tried extracting the row-info records (`MethodDefinitionRowInfo`, `ParameterDefinitionRowInfo`, etc.) into a new module so `IlxDeltaEmitter`, `DeltaMetadataTables`, and `FSharpDeltaMetadataWriter` could share them. The compilation order/preamble checks (nested `TableRows` type + AbstractIL helpers) caused the build to fail, so the experiment was rolled back. We still need a safe way to hoist the row-info types without touching the existing AbstractIL table mirrors.
     - *Update 2025-11-09*: Mirrored the same flow for events. Added `createEventHostBaselineModule` and `emitDelta adds event metadata rows for new event`, plumbed `addedEventTokens`/`EventMap` rows through the emitter/baseline, and verified EncLog now records `AddEvent` + `AddEventMap` when an event (and its add/remove accessors) is introduced mid-session.
     - *Update 2025-11-09 (pm)*: MethodSemantics rows now light up for newly added property/event accessors. `IlxDeltaEmitter` resolves tokens for methods introduced in the current delta, emits MethodSemantics entries, and the new component asserts `AddMethod` operations appear in EncLog for both property and event additions.
     - *Update 2025-11-09 (pm)*: Portable PDB coverage now spans accessor additions, and the mdv helper suite mirrors those scenarios. `HotReload.PdbTests` plus the new mdv helper facts cover property/event additions starting from host baselines (no accessor) and verify the metadata/PDB outputs contain the expected documents, sequence points, and generation markers for the newly added accessor tokens.
  3. **Rewrite `FSharpDeltaMetadataWriter` to append table rows** – ⚙️ *in progress*: Roslyn’s `DeltaMetadataWriter` (`roslyn/src/Compilers/Core/Portable/Emit/EditAndContinue/DeltaMetadataWriter.cs`) copies each updated method from the baseline reader, swaps in the new RVA/local-signature token, and appends MethodDef/Param/Event/Property rows plus EncLog entries. To reach parity:
     - **Definition indexes** – extend `FSharpDefinitionIndex` so Property/Event/MethodSemantics rows are tracked the same way Roslyn tracks them via `DefinitionIndex<PropertyDefinitionHandle>`/`DefinitionIndex<EventDefinitionHandle>`. Each index needs to return `(rowId,isAdded)` so the writer knows whether to emit `EditAndContinueOperation.AddProperty/AddEvent` vs `Default`.
     - **Map tables** – mirror Roslyn’s `PopulateEventMapTableRows`/`PopulatePropertyMapTableRows` by queueing `(typeRowId, firstPropertyRowId)` snapshots inside `IlxDeltaEmitter` whenever a type gets its first property/event. `FSharpDeltaMetadataWriter` should then call `metadataBuilder.AddEventMap/AddPropertyMap` and log the `AddEvent`/`AddProperty` operations.
     - **EncLog/EncMap** – replicate Roslyn’s `PopulateEncLogTableRows`/`PopulateEncMapTableRows`: iterate every definition index (Method/Param/Property/Event/MethodSemantics/Map) and push `(table,row,operation)` tuples through `metadataBuilder.AddEncLogEntry` and `AddEncMapEntry`. Return the resulting `(EncLog, EncMap, TableRowCounts, HeapSizes, EncId, EncBaseId)` so `HotReloadBaseline.applyDelta` can persist cumulative counts/guids like Roslyn’s `EmitBaseline.With(...)`.
     - **IlxDeltaEmitter integration** – once the writer emits all table types, feed `MetadataDelta.TableRowCounts` and the new `EncLog` back into `IlxDelta` so mdv/PDB tests (and fsc-watch) can assert the rows exist. Persist the returned Enc IDs/Synthesized snapshots through `HotReloadBaseline` so subsequent deltas start from the updated counts.
     - *Update 2025-11-07*: MethodDef + Param rows now flow through the new writer. `IlxDeltaEmitter` captures parameter row order per method, the metadata writer appends those rows (along with Module/Method EncLog entries), and the returned table counts feed directly into `HotReloadBaseline.applyDelta`. Property/Event(+map) rows remain TODO.
     - *Update 2025-11-09*: Introduced `CodeGen/DeltaMetadataTables.fs`, a lightweight wrapper around AbstractIL’s `MetadataTable`/`RowElement` primitives. `FSharpDeltaMetadataWriter` now mirrors every Method/Param/Property/Event/Map/MethodSemantics row into the new helper while still emitting SRM metadata, and derives `TableRowCounts` from the helper so future work can serialize the AbstractIL tables directly.
     - *Update 2025-11-09 (pm)*: `IlxDeltaEmitter` now captures the full metadata payload (attributes, names, signatures, event types, first-param row ids) when it records definition-index snapshots, and `FSharpDeltaMetadataWriter` consumes those snapshots instead of rereading the baseline `MetadataReader`. This removes reader dependencies from the writer’s row emission loop and aligns the data model with Roslyn’s `DefinitionIndex<T>`/`AddedOrChangedMethodInfo` pipeline, paving the way for a pure AbstractIL delta writer.
     - *Update 2025-11-09 (late pm)*: `DeltaMetadataTables` now computes the delta string/blob/guid heap sizes directly from the mirrored rows (and surfaces the actual heap byte arrays), and the metadata writer feeds those sizes into `MetadataDelta.HeapSizes` without consulting the SRM `MetadataReader`. This validates that the AbstractIL mirror contains enough information to update baseline heap lengths once we cut over to a pure AbstractIL serializer.
     - *Update 2025-11-09 (late pm)*: `FSharpDeltaMetadataWriter` no longer depends on `MetadataReader`; it consumes the captured row payloads (names, signatures, attributes) supplied by `IlxDeltaEmitter`, while the emitter provides the module name directly. This keeps the writer’s inputs aligned with Roslyn’s `DefinitionIndex<T>` pipeline and clears the way for a future AbstractIL serializer.
     - **Upcoming (new breakdown, 2025-11-09)**: To finish the Roslyn parity work we need a thin AbstractIL serializer that emits the `#~` stream using `DeltaMetadataTables.TableRows` + heap blobs (no SRM involvement). Break this into the following substeps:
       1. *Table ordering & bitvectors*: build a helper (`DeltaTableLayout.fs`) that takes `TableRows`, filters out empty tables, computes the `valid`/`sorted` bit masks, and produces the per-table row counts exactly like Roslyn’s `DeltaMetadataWriter.PopulateEncLogTableRows` and AbstractIL’s `tableSize` computation. Validate the output against several mdv tests (method-only, property, event scenarios).
       2. *Coded-index sizing*: compute the “big” flags for heaps and every coded token kind (TypeDefOrRef, HasSemantics, etc.), reusing the formulas from `ilwrite.fs` (`tdorBigness`, `hsBigness`, …). Unit-test this helper to ensure it matches the SRM path for our existing deltas.
       3. *Heap stream writer*: wrap the mirrored `StringHeap`/`BlobHeap`/`GuidHeap` bytes in stream headers (`#Strings`, `#Blob`, `#GUID`, optional empty `#US`) with proper 4-byte padding, mirroring ECMA-335 II.24.2 and the logic in `ilwrite.fs:3310-3380`.
       4. *Table stream writer*: implement `DeltaMetadataSerializer` that writes the `#~` header, row counts, and row bodies using the same row-element encoding as `ilwrite.fs:3440-3605`. This component should accept `TableRows`, heap-address helpers, and the coded-index sizing info from step 2.
     5. *Integration & dual-path tests*: teach `FSharpDeltaMetadataWriter` to call the new serializer (behind a feature flag). Extend `FSharpDeltaMetadataWriterTests` to assert that the AbstractIL-serialized metadata matches the current SRM output byte-for-byte before making it the default. Once validated, remove the SRM dependency entirely so future deltas are emitted via the AbstractIL writer.
     - *Update 2025-11-09 (late pm)*: `FSharpDeltaMetadataWriter` now emits the AbstractIL `#~` stream snapshot on every delta, and `FSharpDeltaMetadataWriterTests.fs` extracts the SRM `#~` stream to assert byte-for-byte parity for the property/event scenarios. This work originally lived behind the `FSHARP_HOTRELOAD_USE_ABSTRACTIL` guard; we removed that guard on 2025-11-11 once the metadata-root serializer proved stable across larger scenarios.
     - *Update 2025-11-09 (late pm)*: `DeltaMetadataSerializer` now assembles the entire metadata root (signature, version header, stream headers, #~ + heap streams). Before today it only activated when `FSHARP_HOTRELOAD_USE_ABSTRACTIL=1`; with the guard gone, the AbstractIL path is now unconditional and the SRM serializer is no longer part of the hot reload pipeline.
     - *Update 2025-11-10*: Added `FSharpDeltaMetadataWriterTests` coverage for single-parameter methods, closure-like helpers, and async/state-machine pairs. Those tests used to toggle `FSHARP_HOTRELOAD_USE_ABSTRACTIL`; they now call a helper that serializes the mutated `MetadataBuilder` via `MetadataRootBuilder` so we continue to compare the AbstractIL bytes against an SRM reference without touching environment variables.
     - *Micro-steps 2025-11-12*: Before chasing more parity scenarios, fix the immediate build blockers introduced by the new exports. Concrete steps:
       1. Update `ilwrite.fsi` so every newly exposed type (`RowElementTags`, `RowElement`, `UnsharedRow`, `MetadataTable<'T>`) carries the same `[<Struct>]`/constructor shape as the implementation, and add summaries so future exports stay intentional.
       2. In `DeltaMetadataTables.fs`, import `FSharp.Compiler.AbstractIL.BinaryConstants` (not `ILBinary`), `FSharp.Compiler.IlxDeltaStreams`, and move helper `let` bindings (`compressedLength`, heap cache builders) to the top of the type before any `member` definitions to satisfy FS0960.
       3. Rebuild the service/test projects (`./.dotnet/dotnet build tests/FSharp.Compiler.Service.Tests/FSharp.Compiler.Service.Tests.fsproj -c Debug`) and then run the new aggregator unit tests with `./.dotnet/dotnet test ... --filter FullyQualifiedName~FSharpMetadataAggregatorTests` to prove the fix before proceeding to the serializer refactor.
  4. **Portable PDB sequence points for helper modules** – ✅ helper modules now flow `ILSourceDocument` instances into the writer (via `allGivenSources`) and seed each accessor body with a `DebugRange`. The component tests were updated to (a) verify the baseline portable PDB actually records the expected document names (`PropertyDemo.fs`, `EventDemo.fs`) and (b) assert the delta PDB exposes non-nil `Document` handles plus real sequence points/line info. Delta PDBs only carry references to baseline heap entries, so the tests decode doc names by opening the captured baseline snapshot while still ensuring the delta payload keeps the associations alive.
  5. **Emitter integration** – refactor `IlxDeltaEmitter.emitDelta` to feed the richer writer:
     - capture the rewritten `ILMethodDef` structures (RVA, locals blob, signature tokens) needed for MethodDef/Param rows.
     - detect added methods/fields/props/events (currently flagged as rude edits) and queue them for actual table emission.
     - thread the resulting `MetadataDelta` (including new Enc IDs) into `IlxDelta`.
  6. **Baseline refresh** – extend `FSharpEmitBaseline`/`HotReloadState` to commit the new Enc IDs and synthesized-member snapshots after each delta so subsequent generations rehydrate directly from baseline state. ✅ `IlxDeltaEmitter` now applies each metadata delta to the baseline (updating table counts, heap lengths, Enc IDs, synthesized-name snapshot) and surfaces the refreshed baseline through `UpdatedBaseline` so the service can keep chaining generations.

  6. **Event/Property metadata parity** – Remaining steps before we can emit the Event/Property tables:
    - *Update 2025-11-07*: `SymbolId` now carries a `SymbolMemberKind` discriminator and `FSharpSymbolChanges` exposes helpers for property/event accessor edits, so DeltaBuilder can distinguish which metadata tables need updates.
    - *Update 2025-11-07*: DeltaBuilder now groups property/event accessor symbols, resolves baseline method handles where available, and surfaces them via `UpdatedAccessors` on `DeltaEmissionRequest`/`IlxDeltaRequest`. IlxDeltaEmitter can now inspect accessor metadata when emitting MethodSemantics/property rows.
     - *Update 2025-11-07 (late pm)*: IlxDeltaEmitter walks the IL property/event accessors, remaps them to baseline handles, and feeds the property/event definition indexes so the metadata writer can append the corresponding rows + EncLog entries.
     - *Update 2025-11-08*: MethodSemantics rows are now emitted with association metadata, and `FSharpEmitBaseline.applyDelta` consumes that snapshot together with the new PropertyMap/EventMap rows so future deltas can resolve the new handles without adding duplicate rows.
     - *Update 2025-11-08 (pm)*: Created `tests/FSharp.Compiler.ComponentTests/HotReload/TestHelpers.fs` so property/event module builders, baseline reproducers, and accessor utilities live in one place. DeltaEmitter/Pdb tests now rely on these helpers, and every `IlxDeltaRequest` explicitly sets `UpdatedAccessors` to avoid future signature drift.
     - Record PropertyMap/EventMap ownership in `FSharpEmitBaseline` (e.g., map from TypeDef name -> first property/event row id). Once baseline tracking exists the emitter can instantiate DefinitionIndex instances for PropertyDef/EventDef plus their map tables, just like we now do for MethodDef/Param.
     - After the above, extend `FSharpDeltaMetadataWriter` to append Property/Event/MethodSemantics/Map rows and update EncLog/EncMap accordingly, then broaden `MdvValidationTests` + PDB tests with scenarios that add parameters/properties/events to ensure the runtime payload matches Roslyn’s output. (Property/event coverage landed in `DeltaEmitterTests`; helpers are now shared with `PdbTests`, and the mdv suite is queued next.)
     - *Update 2025-11-08 (late pm)*: Reintroduced `IlDeltaStreamBuilder` component tests that exercise the new builder API (method-body emission + standalone signatures) so we keep regression coverage on the raw stream encoder.
  7. **PDB alignment** – update `HotReloadPdb.emitDelta` to consume the enriched baseline (`AddedOrChangedMethodInfo`, Enc IDs, synthesized member map) when it emits sequence points. Roslyn reference: `EditAndContinueMethodDebugInformationWriter` and `PortablePdbBuilder` in `MetadataWriter.cs`.
     - *Update 2025-11-09*: `HotReloadPdb.emitDelta` now takes the concrete `AddedOrChangedMethodInfo` list instead of a raw token bag. `IlxDeltaEmitter` computes the list up front (using the IL delta stream builder’s method bodies) and threads it both into the baseline refresh logic and the PDB writer. The delta writer deduplicates the tokens from that list, reuses the existing document copier, and only emits debug rows when a method’s IL actually changed. This mirrors Roslyn’s Edit-and-Continue writer, prevents stale tokens from being enqueued when metadata-only edits occur, and keeps the component PDB regressions focused on true IL edits.
  8. **Tests & validation** – once rows are fully emitted:
    - *Update 2025-11-08*: Added the first service-level metadata writer fact in
      `tests/FSharp.Compiler.Service.Tests/HotReload/FSharpDeltaMetadataWriterTests.fs`. The helper now
      emits a minimal property host module, invokes `FSharpDeltaMetadataWriter.emit`, and asserts the
      resulting Property/PropertyMap table counts plus EncLog entries. This ensures regressions in the
      writer surface immediately without running the heavier component suites. Expand this suite as we
      light up Event/MethodSemantics rows.
    - *Update 2025-11-08 (pm)*: Extended the service tests with an event/add accessor scenario so the
      writer now has unit coverage for Event/EventMap rows and MethodSemantics entries (Adder). This keeps
      the fast suite aligned with the emitter as we continue enabling runtime apply.
    - *Update 2025-11-08 (late pm)*: The helper-based component regressions (`mdv helper validates added property/event metadata`)
      now decode the emitted metadata and assert Property/Event/Map row counts while also checking the
      `IlxDelta.EncLog` payload for the matching `AddProperty`/`AddEvent` operations. This ensures the
      full IlxDeltaEmitter → metadata writer pipeline produces Roslyn-parity rows, not just the unit-level
      writer tests.
    - *Update 2025-11-08 (late pm)*: Added `mdv helper validates multi-generation method metadata`, which
      replays two sequential method-body edits via `IlxDeltaEmitter`, verifies both deltas retain the same
      MethodDef token, and asserts their EncLog slices only contain the expected Module/Method entries.
      This covers the multi-generation scenario without depending on mdv CLI output.
    - *Update 2025-11-08 (late pm)*: `PdbTests` now include a helper-based multi-generation scenario that
      emits two method-body deltas and asserts each portable PDB delta still references the baseline
      MethodDef token, proving sequence-point data chains cleanly across generations.
    - *Update 2025-11-08 (late pm)*: Added a companion PDB regression for property getters, mirroring the
      multi-generation method test so accessor edits now verify portable PDB chaining as well.
    - *Update 2025-11-08 (late pm)*: Added the matching event-accessor scenario so `PdbTests` covers multi-
      generation add-handlers (reuse of the `add_OnChanged` MethodDef token) in addition to methods and
      property accessors.
    - *Update 2025-11-08 (late pm)*: Added `mdv helper validates multi-generation event accessor metadata`,
      ensuring the Ilx metadata pipeline logs the correct Enc operations for event add-handlers across
      sequential deltas.
    - *Update 2025-11-08 (late pm)*: Added `mdv helper validates multi-generation closure metadata`, giving
      the IL helper suite parity with the FSharpChecker closure tests so method/closure/async/event
      scenarios all have multi-generation coverage without relying on the checker pipeline.
    - *Update 2025-11-08 (late pm)*: Added `mdv validates consecutive closure edits`, which drives
      `FSharpChecker` through two closure updates, captures both metadata blobs, and asserts `mdv`
      outputs the updated literals for generations 1 and 2. This raises confidence in closure-heavy
      scenarios (beyond simple method bodies) without relying on helper IL modules.
    - *Update 2025-11-08 (late pm)*: Added `mdv validates consecutive async method edits`, exercising the
      async workflow path across two generations via `FSharpChecker` and ensuring mdv reports the updated
      literals for both deltas. Async scenarios now have the same multi-generation coverage as baseline
      and closure cases.
     - broaden `MdvValidationTests.fs` to cover multi-generation changes (closure, async, add/remove) and assert mdv output and EncLog/EncMap contents match the target Roslyn dump.
     - update `HotReload/PdbTests.fs` (and the CLI smoke tests) to ensure PDB deltas reuse method handles across generations.
     - rerun `fsc-watch` with and without runtime apply to verify `MetadataUpdater.ApplyUpdate` succeeds once the writer parity work is complete.
  10. **New action (2025-11-11)** – Capture the AbstractIL table snapshots as plain `RowElementData[][]` structures inside `DeltaMetadataTables` so module-scope `TableRows` no longer depend on `ILBinaryWriter.MetadataTable<'T>`. Once that mirror exists we can reapply the FS0058 fix without fighting internal-type visibility.
  9. **Status (2025-11-05)** – Roslyn parity work in progress. mdv component tests remain red because the new writer is not implemented; keep this item open until the new infrastructure ships.
- **Follow-up**: Once the metadata writer lands, audit `IlxDeltaEmitter` callers (CLI, checker API) to consume the richer baseline state and remove legacy `MetadataBuilder` fallbacks.
- **Micro-plan for metadata-row refactor (2025-11-11)**:
  1. 🟢 **Done** – Introduce `DeltaMetadataTypes.fs` with only `RowElementData` so the project knows about the shared module but no behaviour changes occur.
  2. 🟢 **Done (2025-11-11)** – Move one record at a time from `FSharpDeltaMetadataWriter.fs` into `DeltaMetadataTypes.fs`, updating each dependent file (`DeltaMetadataTables.fs`, `IlxDeltaEmitter.fs`, tests) and re-running `dotnet test ...FSharpDeltaMetadataWriterTests` after every move. Method, parameter, property, event, property-map, event-map, and MethodSemantics rows now live in the shared module.
  3. 🟢 **Done (2025-11-11)** – Expose the `TableRows` DTO in `DeltaMetadataTypes` so `DeltaMetadataTables` projects its `MetadataTable` contents into the shared `{ Tag; Value }` representation that downstream components can consume without touching AbstractIL internals.
  4. 🟢 **Done (2025-11-11)** – Update `DeltaMetadataSerializer` to consume the shared `TableRows`/`RowElementData` snapshots (no direct dependency on `UnsharedRow`), keeping the serialization logic identical while aligning with the new abstraction.
  5. 🟢 **Done (2025-11-11)** – The AbstractIL serializer is now the default (and only) metadata path. `DeltaMetadataSerializer` always emits the metadata root, `FSharpDeltaMetadataWriter` no longer calls `MetadataRootBuilder.Serialize`, and the old `FSHARP_HOTRELOAD_USE_ABSTRACTIL` flag has been removed. Parity tests now derive their SRM baseline by serializing the mutated `MetadataBuilder` inside the test harness.
  6. 🟢 **Done (2025-11-11)** – Reordered `FSharp.Compiler.Service.fsproj` so `DeltaMetadataTables.fs` compiles before `DeltaMetadataSerializer.fs`/`FSharpDeltaMetadataWriter.fs`, preventing undefined-type errors now that `TableRows` lives in a shared module.
  7. 🟢 **Done (2025-11-11)** – Updated `DeltaMetadataSerializer.align4` to mask with `~~~3`, matching Roslyn’s helper and restoring correct padding on net9+/ARM builds.
  8. 🔴 **Blocked (2025-11-11)** – While attempting to flesh out `FSharpMetadataAggregatorTests`, the netstandard build surfaced that `CodeGen/DeltaMetadataTables.fs` still depends on `ILBinaryWriter.MetadataTable<'T>`, `UnsharedRow`, and helper functions (`UShort`, `HasSemantics`, etc.) that are not exposed through `ilwrite.fsi`. This means any clean build targeting netstandard fails before we can exercise the new aggregator coverage. **Plan:**
      1. Introduce a lightweight `RowElement`/`RowTable` helper in `CodeGen` that mirrors the handful of APIs we use from `ILBinaryWriter` (shared string/blob/guid heaps + row accumulators) but materialises rows directly as `RowElementData[]`.
      2. Update `DeltaMetadataTables.fs` to use the new helper (no `open FSharp.Compiler.AbstractIL.ILBinaryWriter`) and regenerate the heap/table bytes solely from `RowElementData`, removing the last dependency on IL writer internals.
      3. Add targeted unit tests that build a synthetic module, invoke the rewritten tables, and assert the resulting metadata blobs match the previous output so we can confidently remove the old dependency.
      4. Once this refactor lands, re-run the aggregator metadata tests so their coverage becomes unblocked.
  9. 🟡 **New (2025-11-11)** – Emit a full metadata root (stream headers + padded #~ stream) from `DeltaMetadataSerializer` so downstream consumers can treat `MetadataDelta.Metadata` as a standalone blob. Today the returned bytes contain only the compressed table stream, which causes `MetadataReaderProvider` to throw `BadImageFormatException` in `FSharpMetadataAggregatorTests` and makes the parity tests fail with a 12-byte vs 11-byte mismatch. Completion = aggregator test + all `abstract metadata serializer matches …` cases pass using the shared helper.

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
- **Status**: Completed (2025-11-04). The CLI now applies deltas at runtime; the manual loop and the scripted harness both rebind the invocation target with the updated literal, the new `--clean-build` option keeps runs deterministic, and mdv command lines are captured alongside the emitted artifacts. Component coverage includes single-, multi-generation, and closure edits.
- **Follow-up**:
  - None (roll remaining coverage expansion into Task 2.x and the symbol-mapping workstream).
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
- Document the new tests in this plan so future steps keep adding regression coverage rather than relying on ad-hoc mdv runs.

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
- **Status (2025-11-04)**: Baseline snapshots now come from the PE reader (matching Roslyn’s `EmitBaseline`), both the single-edit and multi-generation mdv regressions pass, and the runtime CLI consumes the same metadata/IL blobs without stale literals.
- **Next steps (execute in order)**:
1. **Stabilise synthesized helpers**
    - ✅ Persist baseline synthesized-name snapshots in `FSharpEmitBaseline` and reload them into `FSharpSynthesizedTypeMaps` when sessions start; `FSharpChecker` now resets the map before each delta emission.
    - ✅ Wired `FSharpSynthesizedTypeMaps` through `NiceNameGenerator`, the symbol matcher, and `IlxDeltaEmitter` so closures/async helpers reuse baseline names; alias-aware token remapping now keeps the async state-machine helpers stable and the mdv regression verifies the split literals (`"Integration async "`/`"updated"`) instead of the stale baseline string.
    - ✅ Added regression coverage (`mdv validates method-body edit with closure`, `mdv validates method-body edit with async state machine`) that checks the updated IL reuses the baseline synthesized names; continue adding tests alongside every hot-reload change so we never regress this behaviour.
 2. **Replace `NiceNameGenerator` with `GeneratedNames`**
     - ✅ Introduced `FSharp.Compiler.GeneratedNames`, mirroring Roslyn’s helper set, and rewired `NiceNameGenerator`/`FSharpSynthesizedTypeMaps` to delegate to it when generating hot-reload-safe names.
     - ✅ Updated the compiler-global name generator to allocate ordinals explicitly (rather than relying on source ranges), so every lowering/codegen call site inherits the stateless scheme while we continue migrating individual helpers.
  3. **Move metadata deltas off `MetadataBuilder`**
     - Extract the minimal table-writing primitives from `ilwrite.fs` and use them to build delta metadata in pure F#.
     - Flesh out `FSharpMetadataAggregator` once the new writer is in place so tests can inspect multi-generation metadata.
  4. **Broaden validation**
     - Once the above steps land, extend coverage beyond simple method-body edits (closures, async/state machines) and keep diffing Roslyn ENC output on larger samples to vet blob/table shape.
- **Owner**: Hot reload squad
- **Dependencies**: `IlxDeltaEmitter.fs`, `IlxDeltaStreams.fs`, `HotReloadSession.fs`, mdv harness under `tools/fsc-watch`.
- **Risks**: Incorrect user-string remapping will surface as stale literals at runtime even when method bodies update, undermining watch demos and mdv verification.
- **Implementation Sketch**:
  - Extend `IlxDeltaRequest` with an optional `SynthesizedNames` map and thread it from both the checker and CLI entry points (`service.fs`, `fsc.fs`).
  - Teach `FSharpSymbolMatcher` to import the map (mirroring Roslyn’s `SynthesizedTypeMaps`) so nested helper types resolve to baseline definitions.
  - Update `IlxDeltaEmitter` to consult the map before classifying helpers as new entities, keeping closure/async state machines aligned with baseline metadata.
  - Tests: keep the existing closure regression, add an async state-machine mdv regression, and add a checker-level unit test that ensures the map flows through public APIs.
