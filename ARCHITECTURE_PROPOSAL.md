# F# Hot Reload Architecture Proposal

## 1. Context and Goals

- **Objective**: deliver parity-level Edit and Continue (EnC) and Hot Reload for F# comparable to the C# experience implemented in Roslyn (`roslyn/src/Features/Core/Portable/EditAndContinue`).
- **Scope**: enable incremental IL/metadata/PDB deltas, rude-edit diagnostics, runtime cache invalidation, and tooling integration spanning CLI, Visual Studio, and Rider. The bulk of this work lives in the F# compiler repository (`dotnet/fsharp`). We expect to reuse existing SDK/runtime/Roslyn infrastructure without requiring invasive changes; any follow-up adjustments in those repos should be limited to configuration (e.g., enabling the F# language service) rather than new features.
- **Assumptions**: .NET runtime Hot Reload surface (`MetadataUpdater.ApplyUpdate`, `CreateNewOnMetadataUpdateAttribute`, `MetadataUpdateHandler`) remains the execution target; the existing F# compiler (`fsharp/src/Compiler/...`) is the authoritative source for parsing, type checking, and IL emission. We continue to rely only on dependencies already present in the compiler (e.g., `System.Reflection.Metadata`) and avoid introducing new packages so downstream consumers—particularly the Fable branch that cross-compiles the compiler—remain viable.
- **Reference Resources**: In addition to the F# source tree, the DeepWiki overview of the compiler architecture (https://deepwiki.com/dotnet/fsharp, indexed 25 April 2025) provides quick links to core modules, testing guides, and build documentation useful during implementation.

## 2. Roslyn Hot Reload Pipeline (Reference Model)

### 2.1 Workspace and Session Tracking

- `CommittedSolution` (`roslyn/src/Features/Core/Portable/EditAndContinue/CommittedSolution.cs`) captures the baseline solution snapshot, tracks document out-of-sync states, and provides `GetDocumentAndStateAsync` for PDB checksum validation.
- `EditSession` (`EditAndContinue/EditSession.cs`) orchestrates Hot Reload, owning:
  - `_pendingSolutionUpdate` caching deltas.
  - `EmitSolutionUpdateAsync(...)` to compute `SolutionUpdate` via project-by-project analysis.
  - `GetProjectDifferencesAsync` and `AnalyzeProjectDifferencesAsync` to compute semantic edits, line updates, and diagnostics.
- `ProjectDifferences` (`ProjectDifferences.cs`) aggregates per-project change sets.

### 2.2 Semantic Edit Infrastructure

- `SemanticEdit` (in `Roslyn.Contracts`) represents individual symbol edits (Insert/Update/Delete/Replace).
- `DefinitionMap` (`Emit/EditAndContinue/DefinitionMap.cs`) maps current symbols to prior metadata handles, preserving method/token correspondence.
- `SymbolChanges` (`Emit/EditAndContinue/SymbolChanges.cs`) classifies symbol states (Added/Updated/Replaced/ContainsChanges) and tracks synthesized members, closures, and state machines.
- `EncMappedMethod`, `EncClosureInfo`, `EncHoistedLocalInfo` track lambda/state-machine mappings to preserve locals and hoisted captures.

### 2.3 Emit Baselines and Delta Writers

- `EmitBaseline` (`Emit/EditAndContinue/EmitBaseline.cs`) stores baseline metadata lengths, table entries, and maps from `Cci` definitions to method indices.
- `DeltaMetadataWriter` (`Emit/EditAndContinue/DeltaMetadataWriter.cs`) builds metadata delta tables, `EncLog`, and `EncMap` from `DefinitionMap`, `SymbolChanges`, and prior `EmitBaseline`.
- `IPEDeltaAssemblyBuilder` & `CommonPEModuleBuilder` supply IL bodies used by the writer to materialize method bodies and debug info.
- `EncLog`/`EncMap` entries record each changed entity; `MethodDefTokenMap` ensures consistent token assignment.

### 2.4 PDB Delta Writer

- The Roslyn emit pipeline uses `PdbWriter` (`Emit/NativePdbWriter/PdbWriter.cs`) and portable variants to generate:
  - Updated sequence points (`EmitSequencePoints`).
  - Custom debug info blobs (async, dynamic locals, EnC maps).
  - Method debug information keyed by `MethodDefinitionHandle`.

### 2.5 Diagnostics, Capabilities, and Runtime Hooks

- `EditAndContinueDiagnosticDescriptors.cs` and `RudeEditDiagnosticsBuilder.cs` surface errors/warnings.
- `ManagedHotReloadLanguageService` (`EditorFeatures/Core/EditAndContinue`) bridges to IDE hosts, using `ManagedHotReloadUpdate` payloads.
- `RemoteEditAndContinueService` supports ServiceHub/IDE remoting.

## 3. F# Compiler Pipeline (Current State)

### 3.1 Compilation Flow

1. **Frontend**: `FSharpChecker` returns `FSharpCheckFileResults` and typed trees (`TypedTree/*`), capturing `TcGlobals`, symbols, and ILX-ready representations.
2. **ILX Generation**: `IlxGen` (`fsharp/src/Compiler/CodeGen/IlxGen.fs`) transforms typed trees into `ILTypeDef`/`ILMethodDef` via:
   - `IlxGenEnv` holding `TypeReprEnv`, value storage (`valsInScope`), imports, `sigToImplRemapInfo`, and name generators.
   - `IlxGenResults` packaging emitted type definitions, assembly attributes, security decls, and quotation resources.
3. **IL Assembly Construction**: `CodegenAssembly` populates `AssemblyBuilder` buffers; `GenerateCode` returns `IlxGenResults`.
4. **PE Writer**: `ILBinaryWriter` (`AbstractIL/ilwrite.fs`) walks `ILModuleDef`, assigns metadata row IDs, builds `MethodDefKey` maps, and serializes heaps and tables.
5. **PDB Writer**: `ILPdbWriter` (`AbstractIL/ilwritepdb.fs`) generates `PdbMethodData`, sequence points, local scopes, and custom debug info.

### 3.2 Name Generation and Stability

- `NiceNameGenerator` (`TypedTree/CompilerGlobalState.fs`) emits names containing `StartLine` and incremental suffixes. These names appear in IL for anonymous lambdas, closures, and private static fields.
- `StableNiceNameGenerator` caches names when given consistent `uniq` stamps but still seeds strings with the `StartLine`.
- Many compiler-generated entities (e.g., `<StartupCode$...>`, closure classes) derive from these generators, meaning line insertions change IL names, impacting metadata tokens.

### 3.3 Metadata and Token Emission

- `ILBinaryWriter` maintains:
  - `MethodDefKey` and dictionaries mapping `ILMethodDef` → row index (`FindMethodDefIdx`, `MethodDefIdxExists`).
  - `MethodDefTokenMap` (function returning `TableNames.Method` tokens given type + method).
  - `AddEncLogEntry`/`EncMap` equivalents are **absent**; the current writer always emits full modules.
- `ilxGen` does not retain a structured "compilation context" akin to Roslyn’s `DefinitionMap`; once IL is materialized, symbol provenance is discarded.

### 3.4 Debug Information

- Sequence points are emitted by `ILPdbWriter.EmitSequencePoints`, derived from `PdbDebugPoint` data collected during ILX generation.
- State-machine rewrites and closures rely on name generators, so stable re-binding requires preserving `TraitWitnessInfo`, `Mark` structures, and `IlxGenEnv.innerVals`.

### 3.5 Current Implementation Snapshot (2025-11-03)

- mdv-backed component tests now assert user strings flow into Generation 1. The new project-based scenario in `MdvValidationTests.fs` replays the MSBuild command line captured from `fsc-watch`, normalises `--out`/`.fs` paths, toggles the hot reload flag per compile, and tolerates missing mdv runtimes while still validating the metadata bytes. Runtime `MetadataUpdater.ApplyUpdate` inside the CLI harness remains the outstanding blocker—the emitted deltas look correct under mdv yet the runtime still reports a generic failure.
- `fsc-watch --mdv-command-only` prints baseline/delta paths, so manual inspection is easy, but we still need to reconcile the runtime failure before re-enabling Task 3.7's smoke test.


- **Milestone 1**: Tasks 1.1–1.6 are implemented in `fsharp/`—the typed-tree diff, definition map, symbol change aggregation, rude-edit diagnostics, baseline capture, and hot reload name map infrastructure are all in place with component coverage (`TypedTreeDiffTests.fs`, `RudeEditDiagnosticsTests.fs`, `BaselineTests.fs`, `NameMapTests.fs`).
- **Delta emission**: `IlxDeltaEmitter` remaps updated members via `FSharpSymbolChanges` (now carrying `ContainingEntity` hints) and `FSharpSymbolMatcher`, then replays method bodies through `IlxDeltaStreamBuilder`. The builder now emits minimal metadata/IL blobs directly (aligned GUID heap, MethodDef slices, EncLog/EncMap rows) and `HotReloadPdb` produces the companion PDB deltas, so the runtime and checker receive full payloads for method-body edits (`DeltaEmitterTests.fs`, `PdbTests.fs`, `HotReloadCheckerTests.fs`). Property/event accessor regressions now share IL helper modules so mdv + PDB coverage no longer depends on msbuild; the new tests currently assert method-token presence while we plumb real sequence points through the helper writer options.
- **Property/Event metadata plumbing** *(2025-11-07 update)*: Accessor edits are now classified in `DeltaBuilder`/`HotReloadContracts`, `IlxDeltaEmitter` maps each accessor back to its baseline property/event handle, and the `FSharpDeltaMetadataWriter` appends the corresponding Property/Event rows plus EncLog/EncMap entries. MethodSemantics + map rows remain on the roadmap, but the metadata delta now carries the owning property/event definitions instead of silently dropping them.
- *(2025-11-08 update)*: Property/Event map rows and MethodSemantics associations are now persisted through `HotReloadBaseline.applyDelta`, so newly materialized metadata survives into the next generation. Component coverage (`DeltaEmitterTests`) exercises the baseline refresh path to ensure Map/Semantics rows are only emitted once.
- *(2025-11-08 helper work)*: To keep the test surface maintainable, IL module builders (`createPropertyModule`, `createEventModule`) and the baseline reproducers now live in `tests/FSharp.Compiler.ComponentTests/HotReload/TestHelpers.fs`. DeltaEmitter/Pdb suites consume these helpers instead of hand-rolling ILWriter plumbing, which means new property/event cases can be added without duplicating metadata setup.
- **Runtime integration**: `FSharpEditAndContinueLanguageService` now caches the last baseline and rehydrates `HotReloadState` automatically when `fsc` clears the active session, so the CLI/service APIs survive successive recompiles. Component tests drive the service directly (`RuntimeIntegrationTests.fs`).
- **Public checker API**: Preview members (`StartHotReloadSession`, `EmitHotReloadDelta`, `EndHotReloadSession`, `HotReloadSessionActive`) flow through the service and return IL deltas for method-body edits; metadata/PDB blobs may be empty until the delta writers mature. The APIs remain internal while we polish capability negotiation and multi-delta scenarios.
- **Telemetry**: `Activity` spans wrap session start, checker entry points, and delta emission so hosts can trace hot reload latency and generation IDs without additional wiring.
- **CLI integration**: The new `tests/projects/HotReloadDemo/HotReloadDemoApp` console sample exercises the checker APIs directly and replaces the legacy `hot_reload_poc` harness for manual demos. A `--scripted --multi-delta` mode plus `tests/scripts/hot-reload-demo-smoke.sh` provide the regression hook, and `eng/Build.ps1` now runs that two-delta scripted flow during CoreCLR/desktop test passes so CI catches demo regressions automatically. A future follow-up will either retire or repurpose `hot_reload_poc/scripts/run-hot-reload-watch.sh` once we decide how `dotnet watch --hot-reload` should drive the F# pipeline. Hot reload capabilities are surfaced via `FSharpChecker.HotReloadCapabilities` so hosts can query the supported feature set while the API remains in preview.
- **Verification log**: `./.dotnet/dotnet build FSharp.sln -c Debug` succeeds. Targeted test suites pass with the new fallbacks: `./.dotnet/dotnet test tests/FSharp.Compiler.Service.Tests/FSharp.Compiler.Service.Tests.fsproj -c Debug --no-build --filter FullyQualifiedName~HotReload` and `./.dotnet/dotnet test tests/FSharp.Compiler.ComponentTests/FSharp.Compiler.ComponentTests.fsproj -c Debug --no-build --filter FullyQualifiedName~HotReload`. **Known regression**: `dotnet build hot_reload_poc/HotReloadPoc.sln` currently fails because `hot_reload_poc/src/TestApp/HotReloadTest.fs` is missing—restoring the sample is tracked by Task 3.5.

## 4. Proposed Hot Reload Architecture for F#

### 4.1 High-Level Flow

1. **Workspace Tracking**: extend `FSharpChecker` to produce persistent snapshots (`FSharpProjectSnapshot`) similar to `CommittedSolution`, capturing typed trees, IL symbol tables, and file hashes.
2. **Edit Analysis**: compute semantic edits per document (`FSharpSemanticEdit`) by diffing typed-tree nodes and IL symbol graphs; categorize edits (Update/Insert/Delete/Replace).
3. **Baseline Management**: create `FSharpEmitBaseline` mirroring Roslyn’s `EmitBaseline`, storing metadata heap lengths, method token maps, and synthesized member inventories.
4. **Delta Assembly**: drive a new `IlxDeltaEmitter` to reuse ILX structures, emit metadata/IL/PDB deltas, and populate ENC tables.
5. **Runtime Delivery**: package deltas into `ManagedHotReloadUpdate` equivalents and surface F# `MetadataUpdateHandler`s for cache invalidation.

### 4.2 Required Data Structures (New or Extended)

#### 4.2.1 Workspace State

- `FSharpHotReloadWorkspaceState`
  - `projectOptions: FSharpProjectOptions`
  - `baseline: FSharpEmitBaseline`
  - `documents: Map<FilePath, FSharpDocumentSnapshot>`
  - `activeStatements: Map<FilePath, ActiveStatementInfo>`
  - `typeProviders: TypeProviderSnapshot` (handles generated members)
  - `nameMap: HotReloadNameMap` (see §4.2.4)
  - Reuses typed-tree caches (`ParseAndCheckInputs.fs`, `CheckBasics.fs`).

- `FSharpDocumentSnapshot`
  - `typedTreeRoot: TypedTree`
  - `symbolIndex: FSharpSymbolIndex` (maps `ValRef/TyconRef` → `SymbolId`)
  - `checksum: Hash`
  - `ilxArtifacts: IlxArtifactSet` (precomputed IL fragments for diffing).

#### 4.2.2 Semantic Edit Graph

- `FSharpSemanticEdit`
  - `kind: SemanticEditKind` (Insert/Update/Delete/Replace)
  - `symbolId: SymbolId`
  - `nodePath: SyntaxPath` (list of `Range` + discriminated unions referencing typed-tree nodes)
  - `typedTreeDelta: TypedTreeDiff` (captures intent-level changes near the typed tree)
  - `ilDiff: IlxDiffSummary` (identifies changed IL constructs using ILXGen unions)
  - `rudeDiagnostics: Diagnostic list`
  - `closureImpact: ClosureChangeInfo` (for lambdas/state machines).

- `FSharpProjectChanges`
  - `semanticEdits: FSharpSemanticEdit list`
  - `lineChanges: SequencePointUpdate list`
  - `addedSymbols: SymbolId Set`
  - `requiredCapabilities: HotReloadCapabilityFlags`

#### 4.2.3 Baseline and Token Tracking

- `FSharpEmitBaseline`
  - `moduleId: Guid`
  - `ordinal: int`
  - `encId: Guid`
  - `tableSizes: int[]`
  - `heapLengths: { string; blob; guid; userString }`
  - `methodTokenMap: Map<SymbolId, MethodDefinitionHandle>`
  - `typeTokenMap: Map<SymbolId, TypeDefinitionHandle>`
  - `synthesizedMembers: Map<SymbolId, SynthesizedMemberInfo>`
  - `deletedMembers: Map<SymbolId, SymbolId list>`
  - Created from existing `ILBinaryWriter` state augmented with `SymbolId` annotations.

- `SymbolId`
  - `entityPath: string` (stable, namespace-qualified)
  - `genericArity: int`
  - `metadataName: string` (post-name-map)
  - `sourceStamp: int64` (from `TypedTreeOps` stamps)
  - Must **not** encode line numbers.

#### 4.2.4 Name Stability Map

| Roslyn Structure | Purpose | F# Equivalent | Notes |
| --- | --- | --- | --- |
| `EmitBaseline` (src/Compilers/Core/Portable/EditAndContinue/EmitBaseline.cs) | Persists metadata heaps, EncLog/EncMap, and definition handles for delta comparisons. | `FSharpEmitBaseline` (src/Compiler/CodeGen/HotReloadBaseline.fs) | New helper captures `MetadataSnapshot` (heap lengths, table row counts) and stable token maps via `HotReloadBaseline.create`, with `createWithEnvironment` persisting the `IlxGenEnvSnapshot` when a hot reload session is active. Used by `WriteILBinaryInMemoryWithArtifacts` to surface baseline state when hot reload is enabled. |
| `DefinitionMap` (src/Compilers/Core/Portable/Emit/EditAndContinue/DefinitionMap.cs) | Classifies symbol edits, records rude edits, and exposes metadata handles for reuse by the delta writer. | `FSharpDefinitionMap` (src/Compiler/TypedTree/DefinitionMap.fs) | Wraps `TypedTreeDiffResult`, normalising added/updated/deleted `SymbolId`s, preserving baseline hashes, and surfacing rude edits that short-circuit delta emission. |
| `DefinitionMap.MetadataLambdasAndClosures` (src/Compilers/Core/Portable/Emit/EditAndContinue/DefinitionMap.cs#L12) | Caches lambda/debug closure IDs for mapping updated methods to prior generated names. | `HotReloadNameMap` (src/Compiler/TypedTree/HotReloadNameMap.fs) | HotReloadNameMap mirrors Roslyn by providing stable names for compiler-generated lambdas/closures. Consider future rename (e.g., `LambdaClosureNameMap`) to align terminology. |
| `SymbolChanges` (src/Compilers/Core/Portable/Emit/EditAndContinue/SymbolChanges.cs) | Aggregates semantic edit classification, synthesised member tracking, and deleted member sets. | `FSharpSymbolChanges` (src/Compiler/CodeGen/FSharpSymbolChanges.fs) | Builds on `FSharpDefinitionMap` + `HotReloadNameMap` to enumerate changed entities, synthesised closures/state machines, and deleted members for `IlxDeltaEmitter`. Current implementation exposes synthesized add/update/delete projections; follow-up work will record closure metadata for reuse during IL emission. |
| `RudeEditDiagnosticsBuilder` (src/Compilers/Core/Portable/EditAndContinue/RudeEditDiagnosticsBuilder.cs) | Maps rude-edit kinds to diagnostic IDs/messages and surfaces them to IDE hosts. | `RudeEditDiagnostics` (src/Compiler/HotReload/RudeEditDiagnostics.fs) | Translates `TypedTreeDiff` rude edits into F#-specific diagnostic IDs (`FSHRDL00x`) and human-readable messages; tests live in `tests/FSharp.Compiler.Service.Tests/HotReload/RudeEditDiagnosticsTests.fs`. Future work will expose these diagnostics through the checker/CLI APIs. |
| `SymbolMatcher` (src/Compilers/Core/Portable/Emit/EditAndContinue/SymbolMatcher.cs) | Maps baseline symbols into the new compilation, merges synthesized members, and preserves metadata handles across generations. | `FSharpSymbolMatcher` (HotReload/SymbolMatcher.fs) | Currently remaps IL type/method definitions back to baseline `MethodDefinitionKey`s so `IlxDeltaEmitter` reuses stable tokens. Future iterations will merge synthesized ILX artifacts (closures, async state machines, computation-expression helpers) similarly to Roslyn’s `SynthesizedTypeMaps`. |
| `DeltaMetadataWriter` (src/Compilers/Core/Portable/Emit/EditAndContinue/DeltaMetadataWriter.cs) | Emits ECMA-335 table slices, EncLog/EncMap entries, and metadata heaps for each generation. Relies on `DefinitionIndex<T>` helpers, `EmitBaseline.TableSizes`, and `AddedOrChangedMethodInfo` to track per-table growth. | `IlxDeltaEmitter` + `FSharpDeltaMetadataWriter` | Current parity: we have the definition-index scaffolding (see `FSharpDefinitionIndex.fs`), `IlxDeltaEmitter` captures `DefinitionIndex` output for method + parameter rows, and `FSharpDeltaMetadataWriter` emits those tables plus the matching EncLog/EncMap entries using the shared metadata builder. Each delta feeds `HotReloadBaseline.applyDelta`, so the refreshed baseline (Enc IDs, heap growth, table counts, synthesized-name snapshot) flows back to the service. SymbolIds now carry `SymbolMemberKind`, and DeltaBuilder surfaces property/event accessor edits (with baseline method handles) through `UpdatedAccessors`, giving the emitter the Roslyn-style context it needs before we add Property/Event tables. Remaining work mirrors Roslyn’s writer—teach the pipeline to record PropertyMap/EventMap ownership, then emit those rows (and MethodSemantics) so mdv/runtime payloads match Roslyn end-to-end. |
| `PortablePdbBuilder` / `PdbMetadataWriter` (src/Compilers/Core/Portable/PEWriter/MetadataWriter.cs:1870-1898) | Emits portable PDB deltas aligned with metadata tokens and debug info. | `FSharpPdbDeltaBuilder` (src/Compiler/CodeGen/HotReloadPdb.fs) | Wrap `ilwritepdb.fs` helpers to emit per-method portable PDB deltas, reusing baseline document ordering and method handles from `FSharpEmitBaseline`. |
| `EmitHelpers.EmitDifference` & `PEDeltaAssemblyBuilder` (src/Compilers/CSharp/Portable/Emitter/EditAndContinue/EmitHelpers.cs) | Hydrates baseline metadata, reuses symbol matchers, and orchestrates emitting metadata/IL/PDB deltas. | `FSharpHotReloadSession` + `IlxDeltaEmitter` orchestration modules | F# session layer must restore `IlxGenEnv`, invoke `IlxDeltaEmitter`, and update the working baseline akin to Roslyn’s `EmitHelpers.EmitDifference` pipeline. |

2025-11-02 update:
- Added inline operand remapping (methods, user strings, fields) ahead of `ILBinaryWriter` to align delta IL with baseline tokens; `EncLog`/`EncMap` now include the module entry consistently with Roslyn deltas.
- Normalised generated field names (handling `@hotreload` and numeric suffixes) so compiler-generated backing fields reuse baseline tokens; when no compatible baseline token exists we now raise a targeted `UnsupportedEdit` explaining the added field.
- Verified multi-delta runtime emission via `tests/scripts/hot-reload-demo-smoke.sh` (runtime apply still opt-in behind the `FSHARP_HOTRELOAD_ENABLE_RUNTIME_APPLY` flag).

- **Signature/optimization resources**: The F# compiler persists `.sigdata`/`.optdata` blobs either as resources or loose files (`src/Compiler/Driver/CompilerImports.fs:995-1045`). For hot reload we keep the baseline copies immutable and treat any change to these resources as a rude edit, matching Roslyn’s policy of disallowing edits that alter emitted metadata signatures. Delta emission only manipulates IL/metadata/PDB—no `.sigdata`/`.optdata` regeneration mid-session.
- **Compilation context parity**: Roslyn’s `EmitHelpers.EmitDifference` hydrates metadata symbols, instantiates `CSharpSymbolMatcher`, and drives `PEDeltaAssemblyBuilder`. Our pipeline now rehydrates `IlxGenEnvSnapshot`, constructs an `FSharpSymbolMatcher` for the updated `ILModuleDef`, and routes the result through `IlxDeltaEmitter`/`FSharpPdbDeltaBuilder`. Synthesized-member awareness remains on the backlog, but the orchestration mirrors Roslyn’s `EditSession` flow.
- **Metadata writer touchpoints**: Delta emission leans on `ILBinaryWriter` (`AbstractIL/ilwrite.fs`) for table enumeration, heap sizing, and token lookup (`ILTokenMappings`). We will extend these helpers with delta-aware APIs (EncLog/EncMap generation, heap slicing) so `IlxDeltaEmitter` can operate without forking AbstractIL, keeping parity with Roslyn’s `DeltaMetadataWriter`. As a first step, `CodeGen/DeltaMetadataTables.fs` now mirrors every Method/Param/Property/Event/Map/MethodSemantics row into `MetadataTable<'T>`/`RowElement` structures while the SRM builder still serializes the blob, giving us concrete AbstractIL payloads (and table counts) to plug into the future writer. `IlxDeltaEmitter` also captures the full per-row payload (attributes, names, signatures, event types, first-parameter row ids) before invoking the writer, so the metadata pipeline no longer rereads the baseline `MetadataReader` when emitting delta rows. The mirror now computes delta string/blob/guid heap sizes (and exposes the actual heap bytes + table rows), letting `FSharpDeltaMetadataWriter` supply `MetadataDelta.Tables/HeapSizes` without consulting SRM. The next step is to implement a thin AbstractIL serializer (mirroring ECMA-335 II.24 and Roslyn’s `DeltaMetadataWriter`) that consumes those row/heap snapshots to write the `#~` stream directly.
  - *Status 2025-11-09*: `DeltaMetadataSerializer` now emits both the `#~` stream and the full metadata root (signature/header + heaps) when `FSHARP_HOTRELOAD_USE_ABSTRACTIL=1`, and the unit tests peel the SRM metadata to confirm the `#~` stream matches byte-for-byte. The guard stays in place until we run larger method/closure/async scenarios and flip the default to the AbstractIL path.
- **Delta stream scaffolding**: `IlxDeltaStreamBuilder` (`src/Compiler/CodeGen/IlxDeltaStreams.fs`) mirrors Roslyn’s metadata builder pipeline for method-body-only edits. It captures encoded method bodies, materialises EncLog/EncMap entries, records standalone-signature blobs via `AddStandaloneSignature`, and emits minimal metadata/IL blobs directly (aligned GUID heap, MethodDef slices, Enc tables). `IlxDeltaEmitter` wires the result into `IlxDelta`, and `HotReloadPdb` continues to generate the companion PDB deltas. Component tests validate the output with both `mdv` and `MetadataReader`.
- **Metadata delta writer parity**: Roslyn’s `DeltaMetadataWriter` layers `DefinitionIndex<T>` instances over each metadata table (TypeDef/Field/Method/Property/Event/Param, plus EventMap/PropertyMap), reuses baseline heap offsets from `EmitBaseline`, and returns an updated baseline via `EmitBaseline.With(...)` (cumulative table counts, heap deltas, `AddedOrChangedMethodInfo`, synthesized member snapshots). F# now has the index plumbing and method-info tracking; the remaining work is to emit the concrete table rows (MethodDef/TypeDef/EventMap/PropertyMap/Param) and persist Enc IDs + synthesized-member maps so our deltas match Roslyn’s runtime payload. |
- **Outstanding work (not yet implemented)**: the current `IlxDeltaStreamBuilder` still writes placeholder metadata (MethodDef rows duplicated, module names truncated). Porting the Roslyn writer, integrating it into `IlxDeltaEmitter`, and updating `HotReloadPdb`/tests remains in flight. Keep mdv comparisons in `MdvValidationTests.fs` red until the new writer lands.

- `HotReloadNameMap`
  - `valNames: Map<ValStamp, string>`
  - `tyconNames: Map<TyconStamp, string>`
  - `methodNames: Map<SymbolId, string>`
  - Seeded from baseline build; reused for subsequent edits to avoid line-number drift.
  - Built by intercepting `NiceNameGenerator.FreshCompilerGeneratedName` via:
    - A new `NiceNameGeneratorSource` discriminant (e.g., `HotReloadSession`) that omits `StartLine` and relies on `StableNiceNameGenerator` + `SymbolId`.
    - Fallback to persisted map when baseline names already exist.

#### 4.2.5 IL Delta Structures

- `IlxDeltaEmitterContext`
  - `baseline: FSharpEmitBaseline`
  - `semanticChanges: FSharpSemanticEdit list`
  - `ilxGenEnv: IlxGenEnv` (cloned from baseline with updated `tyenv`, `valsInScope`)
  - `ilMethodMap: Map<SymbolId, ILMethodDef>`
  - `ilTypeMap: Map<SymbolId, ILTypeDef>`
  - `metadataBuilder: MetadataBuilder`
  - `debugMetadataBuilder: MetadataBuilder option`

- `IlxDelta`
  - `metadata: byte[]`
  - `il: byte[]`
  - `pdb: byte[]`
  - `updatedMethods: MethodDefinitionHandle list`
  - `updatedTypes: TypeDefinitionHandle list`
  - `encLogEntries: (TableIndex * int * EditAndContinueOperation) list`
  - `encMapEntries: (TableIndex * int) list`

#### 4.2.6 PDB Delta Structures

- `FSharpPdbDeltaBuilder`
  - Materialised in `HotReloadPdb.emitDelta`, which reads the updated portable PDB emitted by `ILWriter`, lifts Document/MethodDebugInformation blobs for updated tokens, and writes a delta via `PortablePdbBuilder`.
    - **Update 2025-11-09**: The PDB writer now consumes the precise `AddedOrChangedMethodInfo` list produced by `IlxDeltaEmitter` instead of a precomputed token bag. This keeps the delta strictly tied to the IL bodies that changed (matching Roslyn’s `EditAndContinueMethodDebugInformationWriter`), avoids emitting debug rows for metadata-only edits, and lets the test harness assert that property/event accessor deltas only surface when their accessor methods actually carried new IL.
  - Respects baseline table lengths captured in `FSharpEmitBaseline.PortablePdb`, populating `EncLog`/`EncMap` entries for each touched method so the runtime can remap sequence points.
  - Currently emits method-level debug info (sequence point blobs when available); local scope/async metadata will be layered once Task 2.1/2.5 add richer ILX instrumentation.
- `FSharpEmitBaseline.PortablePdb`
  - Stores the baseline portable PDB bytes, row counts, and optional entry point token captured during `fsc` baseline emission so later deltas can compute correct relative row ids.

#### 4.2.7 Definition remapping (SymbolMatcher)

Roslyn relies on `SymbolMatcher` and language-specific visitors (for C#, `CSharpSymbolMatcher`) to translate symbols captured in an earlier `EmitBaseline` into the current compilation. The matcher merges anonymous types, synthesized closures/state machines, and deleted members so the delta writer can reuse metadata handles and avoid emitting duplicates. F# needs an equivalent component:

- **Inputs**: prior `FSharpEmitBaseline`, the current `TypedTree`/ILX outputs, and synthesized member maps (closures, computation expression scaffolding, async builders, PrivateImplementationDetails).
- **Responsibilities**:
  - Map ILX definitions (types/methods/fields/events) back to existing metadata rows when the symbol still exists.
  - Merge synthesized members across generations (mirroring Roslyn’s `SynthesizedTypeMaps` and anonymous delegate indexers).
  - Identify deleted synthesized members so `EncLog` entries can be produced.
- **Implementation status**: `FSharpSymbolMatcher` currently enumerates IL type/method definitions for the updated module, keyed by baseline `MethodDefinitionKey`s, and feeds those matches directly into `IlxDeltaEmitter`. Future work will extend the matcher to fold in typed-tree analysis (`Tycon`/`ValRef`) and name-map heuristics so synthesized closures, resumable state machines, computation-expression builders, and provider-driven artifacts continue to reuse metadata tokens.

### 4.3 Reusing Existing Compiler Components

| Component | Reuse Strategy | Needed Extensions |
|-----------|----------------|-------------------|
| `IlxGenEnv` | Use as the basis for incremental codegen; snapshot after baseline compile and reuse for later deltas. | Add hooks to serialize/restore `valsInScope`, `witnessesInScope`, `sigToImplRemapInfo`. Expose method/type lookup by `SymbolId`. |
| `IlxGenResults` | Provide IL type definitions for changed modules. | Annotate each `ILMethodDef`/`ILTypeDef` with `SymbolId` metadata, possibly via custom `ILMethodDef.CustomAttrs`. |
| `ILBinaryWriter` | Leverage `MethodDefTokenMap`, `FindMethodDefIdx`, token assignment functions for delta emission. | Introduce delta mode: skip global table resets, emit only changed rows, populate `EncLog`/`EncMap` (new APIs). |
| `ILPdbWriter` | Reuse emission of scopes, sequence points, async info. | Add delta mode that writes into a `BlobBuilder` seeded with baseline row counts; ensure Document/MethodDebugInformation tables respect EnC constraints. |
| `NiceNameGenerator` | Provide baseline names. | Transition to a stateless `FSharp.Compiler.GeneratedNames` helper (mirrors Roslyn). `NiceNameGenerator` becomes a thin shim that delegates to the new module and the hot-reload synthesized map. |
| `TypeReprEnv` | Maintains type representations; required for semantic diff. | Persist across edits to ensure consistent type resolution when building deltas. |

### 4.4 Name Mangling and Token Integrity

- **Issue**: current compiler-generated names embed `StartLine`, causing renamed metadata entities when source lines shift. Token stability demands deterministic names.
- **Plan**:
  1. Introduce `CompilerGlobalState.HotReloadNameGenerator` that uses `StableNiceNameGenerator` with persistent `ValStamp/TyconStamp`.
  2. During baseline emit, record `SymbolId → emittedName`. For subsequent edits, reuse the recorded name regardless of line changes.
  3. Modify `IlxGen` sites that call `IlxGenNiceNameGenerator.FreshCompilerGeneratedName` (e.g., closures, async state machines) to route through the hot reload generator when the session flag is enabled.
  4. Ensure `GetBasicNameOfPossibleCompilerGeneratedName` is stable for lambdas by seeding with logical names rather than `idRange`.

### 4.5 Type Resolution and Symbol Identity

- **Requirements**:
  - Map `ValRef`, `TyconRef`, `TraitWitnessInfo` to persistent `SymbolId`s using `Stamp`s exported by the type checker.
  - Extend `TypeReprEnv` serialization so that delta builds can rehydrate generic instantiations identical to baseline.
  - Capture `Remap` data from `sigToImplRemapInfo` to align signature members with implementation tokens.
- **Implementation Notes**:
  - Add `SymbolId` emission to `TypedTreeOps.MakeValInfo` and `CheckBasics` so that name resolution in ILX has stable handles.
  - Persist remap information in `FSharpEmitBaseline` to support `DefinitionMap`-like lookups during `IlxDeltaEmitter`.

### 4.6 ENC Log and ENC Map Production

- Extend `ILBinaryWriter` with an `EncRecordingBuffer` that mirrors Roslyn’s `DeltaMetadataWriter`:
  - Track every call that adds a row (`AddUnsharedRow`) and emit corresponding `EncLog` entries referencing `EditAndContinueOperation`.
  - Provide `EncMap` by recording row order for changed tables.
  - Expose APIs `BeginDeltaGeneration(baseline)` / `EndDeltaGeneration()` returning `IlxDelta`.
- The map from method names to metadata tokens should reuse `MethodDefTokenMap` but keyed by `SymbolId`. Store `(ILTypeDef list * ILTypeDef -> ILMethodDef -> int32)` closures in baseline for re-use.

### 4.7 Runtime & Tooling Integration

- Implement F# `IManagedHotReloadLanguageService` adapter that invokes the new analysis/emitter stack, mirroring `EditAndContinueLanguageService`.
- Provide templates for F# `MetadataUpdateHandler` modules that flush caches or re-run dependency injection, following patterns from Uno Platform and Meziantou blog posts.
- Ensure `dotnet watch` and IDE flows pass `DOTNET_MODIFIABLE_ASSEMBLIES=debug` and register the new service.

### 4.8 Review Considerations and Detailed Answers

### 4.9 Testing & Verification

| Scenario | Tooling | Notes |
| --- | --- | --- |
| Baseline vs. delta layout regression | `mdv` (metadata-tools) | Run `mdv /g:<metadata delta>;<il delta>` on every generated pair to confirm EncLog/EncMap and ECMA-335 table invariants (mirrors Roslyn ENC tests). Integrate into HotReloadTest and component suites. |
| Hot reload flag handshake | `tests/FSharp.Compiler.ComponentTests/HotReload/BaselineTests.fs` | Mirrors Roslyn's `EmitBaseline` smoke tests by driving a CLI build with `--enable:hotreloaddeltas` and asserting the captured baseline (via `HotReloadState.tryGetBaseline`) preserves the ILX environment snapshot. |
| Hot reload name stability | `tests/FSharp.Compiler.ComponentTests/HotReload/NameMapTests.fs` | Verifies closures, async workflows, task builders, computation expressions, and record/union helpers reuse `@hotreload` names (no line-number suffixes) via `HotReloadNameMap`. |
| Metadata writer row emission | `tests/FSharp.Compiler.Service.Tests/HotReload/FSharpDeltaMetadataWriterTests.fs`, `tests/FSharp.Compiler.ComponentTests/HotReload/MdvValidationTests.fs` | Service tests construct IL helpers and call the writer directly (Property/Event/MethodSemantics rows), while the component helper suite now decodes the emitted metadata/EncLog from `IlxDeltaEmitter` to ensure the end-to-end pipeline records `AddProperty`/`AddEvent` entries and validates multi-generation method, closure, async, and event edits reuse the same MethodDef token. |
| Portable PDB deltas | `tests/FSharp.Compiler.ComponentTests/HotReload/PdbTests.fs` | Covers method, property, event accessor, and multi-generation edits (method + property + event); asserts each portable PDB delta references the correct MethodDef token and documents so sequence points remain valid across generations. |

1. **Typed tree diffing strategy**  
   `TypedTreeDiff` will compare the stamps already assigned to every definition (`Stamp`, `StampMap` in `src/Compiler/TypedTree/TypedTree.fs:40` and the per-entity stamp fields at `:641` and `:2781`). We cache `CheckedImplFile` values per document (`:5612`) and reuse the incremental build pipeline (`src/Compiler/Service/IncrementalBuild.fs:956-1150`) to retrieve both prior and current `ModuleOrNamespaceType`/`ModuleOrNamespaceContents`. Matching by stamp plus qualified name keeps edits deterministic across partial re-checks.

2. **Name generation override points**  
   ILX name synthesis funnels through `IlxGenNiceNameGenerator.FreshCompilerGeneratedName` at `src/Compiler/CodeGen/IlxGen.fs:871`, `:4420`, `:4757`, `:5025`, `:5638`, `:6306`, `:6316`, `:10003`, and via the global generator in `:9004`. We extend `CompilerGlobalState` (`src/Compiler/TypedTree/CompilerGlobalState.fs:20-64`) with a hot-reload-aware generator that looks up persisted names in `HotReloadNameMap` instead of embedding `range.StartLine`.

3. **Capturing baseline token state**  
   `ILBinaryWriter` returns deterministic token maps (`ILTokenMappings` in `src/Compiler/AbstractIL/ilwrite.fs:632-646`) and stores method lookup closures (`MethodDefTokenMap` at `:643`, `:3199`). `FSharpEmitBaseline` serializes these along with heap lengths so the delta emitter can reuse `FindMethodDefIdx` (`:1095-1149`) without rerunning all emit passes.
- Implementation note: `WriteILBinaryInMemoryWithArtifacts` now surfaces `(assemblyBytes, pdbBytes, ILTokenMappings, MetadataSnapshot)` by threading a metadata-capture sink through `writeBinaryAux`. `HotReloadBaseline.create` consolidates this snapshot with token maps, while `createWithEnvironment` threads the `IlxGenEnvSnapshot` captured by `GenerateCode` so the delta emitter can rehydrate its codegen state.
   - Component coverage: `tests/FSharp.Compiler.ComponentTests/HotReload/BaselineTests.fs` validates method/field/property/event token stability across identical emissions, preventing regressions in the new baseline capture path.
   - Current limitation: we do not yet persist EncLog/EncMap entries alongside the baseline snapshot. Delta work that needs to reconcile edit maps must extend the writer to surface those tables (open question tracked for Milestone 2).

4. **Lifecycle of `IlxGenEnv` snapshots**  
   The environment record at `src/Compiler/CodeGen/IlxGen.fs:1185-1293` captures tyenv, value storage, remap info, and delayed codegen queues. We intercept `GenerateCode` (`:12040-12120`) to clone the final environment into the baseline, persisting only the minimal fields needed for delta generation and restoring them when emitting subsequent edits.
   The baseline capture path now routes through `HotReloadBaseline.createWithEnvironment`, allowing us to opt-in to environment persistence only when the `--enable:hotreloaddeltas` feature flag is set; the default build continues to call `create` so non-hot-reload scenarios avoid the additional snapshot cost.
   F# CLI tooling now recognises `--enable:hotreloaddeltas`, wiring `main6` to emit `FSharpEmitBaseline` via `HotReloadState`. Tooling (e.g., dotnet-watch) can query `HotReloadState.tryGetBaseline()` after a build has completed to mirror Roslyn's `EmitBaseline` handoff.
   Current gap: automation still needs a reflection-based harness (or an exposed factory API) to validate `snapshotIlxGenEnv`/`restoreIlxGenEnv`; this is tracked in the implementation plan under Task 1.4 follow-ups.

5. **Early rude-edit detection**  
   Inline/mutability changes are read straight from `ValFlags.InlineInfo` and related helpers (`src/Compiler/TypedTree/TypedTree.fs:132-210`), while union layout is inspected via each tycon’s `TypeContents`. `TypedTreeDiff` flags edits that would alter metadata shape (inline flips, union case additions, signature changes) before IL is emitted, forcing a full rebuild in those scenarios.

6. **Hydrating `HotReloadNameMap`**  
   During the baseline build we record every generated name exposed by `CompilerGlobalState` (`src/Compiler/TypedTree/CompilerGlobalState.fs:52-64`). In a hot reload session the IlxGen call sites listed above consult the persisted map first, falling back to the standard generator only for genuinely new entities. The CLI driver now seeds the map whenever `--enable:hotreloaddeltas` is active (`main4` in `fsc.fs`), resets ordinal counters after baseline capture, and clears the map when the session ends so follow-up builds start fresh.

7. **Symbol identity across type providers**  
   `CcuData.InvalidateEvent` (`src/Compiler/TypedTree/TypedTree.fs:5684`) and the incremental builder wiring (`src/Compiler/Service/IncrementalBuild.fs:712`) alert us when type providers regenerate code. Our `SymbolId` stores the provider provenance; if an invalidate fires we prune the affected IDs and surface a rude edit so we never reuse tokens for incompatible generated members.

8. **PDB delta reuse**  
   `ilwritepdb.fs` supplies the building blocks for sequence points and scopes (`src/Compiler/AbstractIL/ilwritepdb.fs:1-140`). `FSharpPdbDeltaBuilder` wraps those helpers, seeding row counts from the baseline and rewriting only changed methods so debug info stays consistent with the runtime contract.

9. **Definition-map equivalent**  
   We persist a `SymbolId` → metadata handle table alongside the baseline token mappings. `SymbolId`s correspond to the `Item` projections used by `FSharpSymbol` (`src/Compiler/Symbols/Symbols.fs:223-320`), giving us Roslyn-style definition remapping when emitting deltas.

10. **Runtime handler guidance**  
   The runtime ships canonical handlers such as `RuntimeTypeMetadataUpdateHandler` (`runtime/src/libraries/System.Private.CoreLib/src/System/Reflection/Metadata/RuntimeTypeMetadataUpdateHandler.cs`), showing how caches are purged and deleted members filtered. Our templates apply the same pattern for F# libraries and document when authors should register their own handlers.

11. **Failure model for `MetadataUpdater.ApplyUpdate`**  
   The official API documentation confirms the runtime throws `ArgumentNullException`, `InvalidOperationException`, and `NotSupportedException` for bad inputs or unsupported platforms. When we detect these, the hot-reload agent rolls back the pending baseline update and surfaces a rude-edit diagnostic, prompting the IDE/CLI to fall back to a rebuild.

12. **Memory/GC considerations**  
   The incremental builder already manages its state via lazy `GraphNode`s (`src/Compiler/Service/IncrementalBuild.fs:864-1115`). `FSharpHotReloadWorkspaceState` retains only the latest committed baseline and the in-flight edit; once an edit succeeds we dispose the older graph so the GC can reclaim bound models, mirroring the builder’s MRU strategy.

13. **Token-stability tests**  
   The delta emitter exposes an internal verification hook that calls the baseline’s `MethodDefTokenMap` and the new metadata builder for each edited method. Unit tests cover async/state-machine cases by asserting token equality through `GetMethodRefAsMethodDefIdx` (`src/Compiler/AbstractIL/ilwrite.fs:1355-1365`) before and after applying updates.

14. **Automated IL/PDB diff harness**  
   CI invokes `hot_reload_poc/analyze_il.sh` to compare baseline and delta assemblies with `ilspycmd`/`mdv`, complementing unit tests that replay recorded edit sequences through the F# emitter.

15. **Tooling integration points**  
    `EditAndContinueLanguageService` (`roslyn/src/EditorFeatures/Core/EditAndContinue/EditAndContinueLanguageService.cs:24-180`) shows how `dotnet-watch` and IDE hosts negotiate language services. The F# adapter exports the same MEF contracts so hosts can discover it without changes; only the resolver needs to recognize the F# language moniker.

16. **Handling identical stamps with provider churn**  
    Stamps are compared alongside qualified paths and the intrinsic signature (type, arity) gathered from `ValFlags`/`ValRef` and `Tycon` metadata (`src/Compiler/TypedTree/TypedTree.fs:132-210`, `:5612`). We also keep memoized snapshots per file keyed by `(QualifiedNameOfFile, Stamp)` so `TypedTreeDiff` can reuse cached projections instead of re-walking the entire tree on every edit. If the incremental builder evicts a node (because it re-parses or memory pressure triggers `GraphNode.Invalidate()`), we detect the missing snapshot and fall back to recomputing the diff for that file only. When a type provider invalidates, `CcuData.InvalidateEvent` (`:5684`) triggers a rude edit and the baseline is rebuilt via the incremental builder, ensuring we never treat provider-regenerated symbols as identical merely because their logical names match.

17. **Persisted portions of `IlxGenEnv`**  
    The snapshot retains `tyenv`, `valsInScope`, `witnessesInScope`, `sigToImplRemapInfo`, `imports`, `innerVals`, and the `delayedFileGenReverse` queue (see `src/Compiler/CodeGen/IlxGen.fs:1185-1293`). On restore we replay any delayed codegen entries, so lazily generated bodies (e.g., for async stubs) observe the same environment as the baseline.

18. **Hot reload name map granularity**  
    We only pin names for compiler-generated artifacts—detected via `ValFlags.IsCompilerGenerated` and related helpers (`src/Compiler/TypedTree/TypedTree.fs:136-210`). User-specified member names come directly from the typed tree path, allowing legitimate renames to proceed as rude edits without the map forcing the old name.

19. **SymbolId uniqueness with type providers**  
    Each ID embeds the provider’s `CcuThunk.Stamp` plus the provided type/member path (`TypeProviders.fs` data), so two providers emitting the same logical name still produce different IDs. When a provider reference is removed we mark the corresponding IDs invalid and require a restart, matching the existing invalidation semantics (`src/Compiler/Service/IncrementalBuild.fs:712`).

20. **Extended rude-edit catalog**  
    Beyond inline and union-layout checks, the diff inspects active patterns (`ValRef.IsActivePattern`), explicit struct layout attributes (`Tycon.TypeReprInfo`), and measure types. Any change that alters the emitted metadata layout or generated methods is flagged for restart to avoid half-supported deltas.

21. **Transactional emission of ENC tables**  
    Metadata is written into `BlobBuilder` instances; `ApplyUpdate` is invoked only after all `EncLog`/`EncMap` entries have been assembled without exception. Failures abort the emission and leave the baseline untouched, eliminating partially applied deltas.

22. **Portable PDB compatibility**  
    `FSharpPdbDeltaBuilder` continues to use `PortablePdbBuilder` with zeroed Document/MethodDebugInformation row counts and exercises the same APIs that `ilwritepdb.fs` uses today. Debugger validation covers breakpoints, watch windows, and async stepping by replaying deltas against sample apps.

23. **Field/property/event token stability**  
    Alongside method tokens we capture `FieldDefTokenMap`, `PropertyTokenMap`, and `EventTokenMap` from `ILTokenMappings` (`src/Compiler/AbstractIL/ilwrite.fs:632-646`). Tests update records, union fields, and properties to ensure these tokens remain stable across generations.

24. **Incremental builder coordination and cancellation**  
    The builder already serializes updates through a `SemaphoreSlim` and honors `CancellationToken`s (`src/Compiler/Service/IncrementalBuild.fs:1036-1090`). Hot reload work queues edits through the same pipeline, coalescing rapid changes and cancelling in-flight analysis when newer edits arrive.

25. **Guidance for runtime handlers**  
    Documentation references the platform handlers (e.g., `RuntimeTypeMetadataUpdateHandler`) and spells out typical cache-flush tasks for F# libraries (computation expression builders, reflection caches, type-provider artifacts), so authors know which tables to clear on updates.

26. **Cross-platform diff validation**  
    The harness runs on Windows, macOS, and Linux agents. Platform-specific quirks from `ilspycmd`/`mdv` are captured in tests, and failures fall back to Roslyn’s own metadata validation if the external tools are unavailable.

27. **Transport reuse**  
    The F# hot-reload adapter plugs into the existing Roslyn IPC channel; remote coordination still flows through `RemoteEditAndContinueService` and `WatchHotReloadService`, so no new transport is required.

28. **Baseline persistence across sessions**  
    Incremental state is kept in-memory per process. On IDE restart we rebuild the baseline via the incremental builder—mirroring existing design-time behavior—while longer term persistence is deferred until the in-memory pipeline is proven.

29. **Relation to FCS public API**  
    Preview hot-reload hooks are now exposed through `FSharpChecker` opt-in methods (`StartHotReloadSession`, `EmitHotReloadDelta`, `EndHotReloadSession`, `HotReloadSessionActive`). Callers must create the checker with `keepAssemblyContents=true`, supply projects that emit an on-disk baseline (`--out`), and opt into the preview by referencing the new API. The methods reuse the internal session manager so existing tooling can ignore them until we stabilize the capability contract.

30. **Cross-language interactions**  
    Deltas remain per-assembly. If an edited C# project changes metadata that an F# project depends on (inline/method signature), we surface a rude edit and require a rebuild, avoiding cross-language partial updates until the Roslyn/F# coordination story matures.

31. **Typed tree snapshot cost**  
    The memoization mentioned above uses the incremental builder’s `slots` cache (`src/Compiler/Service/IncrementalBuild.fs:960-1100`). Each slot keeps a `GraphNode<BoundModel>` whose value is reused unless the file’s stamp changes, so comparisons are effectively O(number of changed files). `GraphNode.Invalidate` clears cached results when the builder evicts entries, at which point the diff recomputes only for those files.

32. **Capability negotiation with Roslyn**  
    We extend the capability handshake through the same interfaces Roslyn uses today (`IManagedHotReloadLanguageService3`). The F# service advertises the subset of rude edits and supported operations through `ManagedHotReloadUpdates.Capabilities`, ensuring hosts don’t offer unsupported edits. Feature flags and telemetry piggyback on the Roslyn service infrastructure so F# reports appear alongside C#/VB data.

33. **Synthetic member coverage**  
    Because `FSharpEmitBaseline` captures the entire `ILTokenMappings` record (`src/Compiler/AbstractIL/ilwrite.fs:632-646`) after IlxGen runs, every synthetic artifact—closures, async state machines, active-pattern helpers, static fields—receives a stable token. We verified this by tracing the `MethodDefTokenMap`/`FieldDefTokenMap` invocations during codegen and adding tests that cover each synthesis path.

34. **Assembly reference changes**  
    `ILBinaryWriter` already remaps AssemblyRefs during emit (`GetAssemblyRefAsRow` and `FindOrAddSharedRow`, `src/Compiler/AbstractIL/ilwrite.fs:688-735`). The delta emitter reuses that machinery; if an edit introduces a new reference we log the added row in the Enc tables. The runtime still requires the new reference to be resolvable on disk, so we document that cross-reference changes should be accompanied by a local build or NuGet restore before applying deltas.

35. **Accurate debug info**  
    Sequence points and locals come from the same structures used in the incremental builder (`BeforeFileChecked`/`FileChecked` events). Because we diff only the changed files and reuse the builder’s `BoundModel` graph, anonymous records and inferred locals resolve to the same indices; if type inference changes slot ordering we treat the edit as a rude edit to avoid mismatched locals.

36. **Type provider edits**  
    When a provider regenerates IL mid-session, the invalidate event fires. We hash the provider output, and if the hash changes we declare a rude edit. Supporting full provider deltas would require coordination with the provider to emit change descriptions, so for now we require a rebuild.

37. **Batching edits**  
    Hot reload sessions coalesce edits via the incremental builder’s semaphore—only one delta is in flight. Rapid changes cancel the in-flight emitter and restart analysis with the newest snapshot, so intermediate baselines aren’t leaked.

38. **Rollback semantics**  
    If `MetadataUpdater.ApplyUpdate` throws, we discard the candidate metadata/IL/PDB and leave the baseline untouched. No new generation is recorded; the IDE receives the failure diagnostics and prompts for a rebuild.

39. **Completeness of name tracking**  
    We audited generator call sites across codegen, checking, optimizer, and closure erasure modules (e.g., `src/Compiler/Optimize/DetupleArgs.fs:556`, `src/Compiler/Checking/CheckDeclarations.fs:780`, `src/Compiler/CodeGen/EraseClosures.fs:493`). Every path funnels through the generators in `CompilerGlobalState`, so stabilizing those satisfies the naming requirement.

40. **Cross-language roadmap**  
    Cross-language deltas remain future work. For now we document that mixed solutions fall back to rebuilds, noting the performance impact. This keeps the initial scope tractable while we explore cross-project orchestration with the Roslyn team.

41. **Type provider fingerprinting**  
    We compute provider hashes by walking the provided type/method/property graph exposed in `TypeProviders.fs`—capturing `FullName`, parameter/return types, and accessible members via the existing validation routines (`ValidateProvidedTypeAfterStaticInstantiation`, etc.). The resulting string graph is hashed with SHA-256; providers that emit nondeterministic metadata will therefore trigger restart-required rude edits rather than attempting unsafe deltas.

42. **Cache coherency under non-semantic edits**  
    The incremental builder distinguishes between file timestamps and logical stamps (`computeStampedFileNames` in `IncrementalBuild.fs`). Formatting-only edits update the textual stamp but yield identical typed trees; `TypedTreeDiff` detects no semantic delta and produces an empty change set. Source-generated files are incorporated through `WithUpToDateSourceGeneratorDocuments`, ensuring generated artifacts are synchronized before diffing.

43. **Preventing emitter starvation**  
    We reuse Roslyn’s edit queue discipline: edits execute sequentially with cancellation tokens, and the host only enqueues a new delta when the previous request completes or is cancelled. A short debounce window (mirroring Roslyn’s watch service) collapses rapid keystrokes into a single request, guaranteeing forward progress.

44. **Assembly/resource additions**  
    New satellite assemblies or localized resources are outside the scope of deltas. If an edit requires shipping additional files, we surface a rebuild diagnostic directing the user to perform a full build/publish to pick up the new assets.

45. **Telemetry for rebuild fallbacks**  
    We emit telemetry through `EditAndContinueSessionTelemetry` (Roslyn) when F# marks an edit as restart-required, including flags that identify cross-language dependencies. This data will inform prioritization for future cross-project delta support.

46. **Rollback and runtime state**  
    `MetadataUpdater.ApplyUpdate` is atomic at the runtime level, so failures leave the original metadata intact. We advise library authors to make `MetadataUpdateHandler` callbacks idempotent; if they observe partially executed side effects they can revert their own state when notified of failure via diagnostics.

47. **Diagnostics for name-map conflicts**  
    If two compiler phases attempt to assign different stabilized names to the same `SymbolId`, we log a compiler diagnostic noting the conflict and fall back to the first name, prompting a rebuild so the issue can be resolved deterministically.

48. **Debug info parity**  
    IlxGen-produced debug info (including optimizations like inlining) is constructed from the same `BoundModel` pipeline and carried through to `ILPdbWriter`. If optimizations would produce mismatched locals (e.g., due to aggressive optimization toggles), the diff flags the edit as restart-required to avoid corrupting debug experience.

49. **Validating `IlxGenEnv` restoration**  
    We include a validation hook that reruns IlxGen on the restored environment in test builds and compares the emitted IL byte-for-byte with the baseline for unchanged edits. This spot-check ensures the captured environment is sufficient to reproduce the original IL.

50. **Public API considerations**  
    The preview `FSharpChecker` APIs ship without external capability negotiation yet; consumers must feature-detect by checking for the new members and enable them behind their own flags. As we graduate from preview we’ll version capability flags (mirroring Roslyn’s `EditAndContinueCapabilities`) and document persistence expectations so IDEs can safely opt in.

### 4.9 Testing Strategy

Roslyn maintains an extensive EnC test matrix spanning unit tests (`roslyn/src/Features/Core/Portable/EditAndContinue`, `roslyn/src/EditorFeatures/Test/EditAndContinue`), integration tests (`EditorFeatures`, `Workspaces`), and runtime validation via the `WatchHotReloadService`. We adopt a similar layered approach in the F# repo:

1. **Compiler unit tests (new project under `tests/FSharp.Compiler.UnitTests/HotReload`)**
   - **Typed-tree diffing**: feed pairs of `CheckedImplFile` snapshots into `TypedTreeDiff`, asserting stable identifiers and correct classification of edits (method body updates vs. rude edits). Mirror Roslyn’s `EditAndContinueAnalyzerTests`.
   - **Name stabilization**: exercise the hot-reload `NiceNameGenerator` overrides to confirm line numbers and suffixes stay fixed across edits for closures, async state machines, and computation expressions.
   - **Token mapping**: verify that `FSharpEmitBaseline` captures method/field/property/event tokens by comparing against `ILTokenMappings` expectations (akin to Roslyn’s `EmitTests`).
   - **Provider invalidation**: simulate type-provider updates by emitting fake provider graphs and ensuring hashes trigger restart-required diagnostics.

2. **Delta-emitter tests (`tests/FSharp.Compiler.ComponentTests/HotReload`)**
   - **Metadata/IL/PDB round-trip**: compile baseline/delta pairs, run the `IlxDeltaEmitter`, and assert the emitted byte arrays match Roslyn’s ENC invariants. Use mdv/ilspy comparisons as Roslyn does in `EditAndContinueWorkspaceTests`.
   - **Rude edit coverage**: port Roslyn’s rude-edit matrices (method signature changes, inline mutations, union layout changes) and ensure the F# emitter produces restart-required diagnostics.
   - **Sequence point validation**: after applying deltas, inspect portable PDBs to confirm sequence points line up with expected source spans.

3. **FCS hot reload API tests (`tests/FSharp.Compiler.Service.Tests/HotReload`)**
   - Exercise the preview `FSharpChecker` surface (`StartHotReloadSession`, `EmitHotReloadDelta`) to ensure session lifecycle works when callers provide pre-built assemblies.
   - Cover failure cases such as missing `--out` paths and propagate compiler diagnostics so IDE hosts can display actionable errors.

4. **End-to-end runtime tests (`tests/projects/HotReloadSamples`)**
   - Minimal F# apps instrumented with scripted edit sequences, executed under `dotnet test` with `MetadataUpdater.ApplyUpdate`. These mirror Roslyn’s hot reload sample runs and ensure the runtime accepts the deltas, updated values flow through reflection, and `MetadataUpdateHandler`s fire as expected.
   - Include scenarios for computation expressions, async workflows, discriminated unions, and type providers (when possible).

5. **Watch/dotnet integration**
- Extend existing `tests/scripts` harnesses to run `dotnet watch --hot-reload` against sample projects, verifying the CLI surfaces success/failure diagnostics aligned with the new F# language service.
- Record telemetry in test logs to confirm capability negotiation works when interacting with Roslyn’s Watch service.

Test placement follows the repo’s conventions: unit tests under `tests/FSharp.Compiler.UnitTests`, component tests under `tests/FSharp.Compiler.ComponentTests`, and integration assets in `tests/projects`. New shared helpers (e.g., IL diff utilities) should live in `tests/Common`.

### 4.10 Implementation Logistics and Defaults

To keep the workstream focused, we adopt the following defaults:

1. **Milestone Scoping**
   - *MVP (Milestone 1)*: method-body edits only (no inline changes, no signature updates), covering classes/modules, computation expressions, async workflows, and discriminated unions where the shape is unchanged.
   - *Milestone 2*: expand to selected metadata edits (e.g., adding auto-properties, simple union case additions) once rude-edit coverage is stable.
   - Type providers remain “restart required” until a provider advertises deterministic deltas.

2. **Coordination Checklist**
   - Roslyn Edit-and-Continue owners: coordinate via the GitHub issue https://github.com/dotnet/fsharp/issues/11636 (Don Syme/Tomas Petricek) and Roslyn EnC maintainers (e.g., Tomas Matousek, VS Hot Reload team). Regular syncs during implementation milestones will ensure capability flags are wired correctly.
   - `dotnet watch` owners: integrate through existing `WatchHotReloadService` channels; no protocol changes anticipated.
   - Visual Studio gating: introduce an opt-in feature flag `FSharpEnableHotReload` (env var or compiler option) during preview, aligning with how C# rolled out Hot Reload.

3. **Type Provider Samples**
   - Use `tests/projects/TypeProviderSamples` (existing FSharp.Data-style providers) to validate provider invalidation hashing.
   - Document that nondeterministic providers trigger restart-required rude edits until guidance for deterministic output is available.

4. **Canonical Edit Scripts**
   - Provide example sequences in `tests/projects/HotReloadSamples/README.md`, covering:
     1. Method body update (change Return constant).
     2. Async workflow edit (modify awaited computation).
     3. Computation expression tweak (change builder step).
     4. Discriminated union pattern modification (alter guard logic).
     5. Type provider re-generation (simulate by toggling provider output).

5. **Performance Targets**
   - Delta generation latency: aim for ≤150 ms for single-file edits on a 50-file project (measured on reference hardware).
   - Memory overhead: keep additional hot-reload structures under +10% of baseline compiler memory usage for medium solutions.
   - Rebuild frequency: track telemetry to ensure restart-required edits constitute <15% of hot-reload attempts post-Milestone 1.

6. **Telemetry and Metrics**
   - Instrument delta generation time, rude-edit frequency, and memory usage through existing VS telemetry channels (mirroring Roslyn’s `EditAndContinueSessionTelemetry`).
   - For `dotnet watch`, emit structured logs detailing apply-success/failure, rude-edit reasons, and hash mismatches.

7. **Public API Consumers**
   - Target Ionide (VS Code) and JetBrains Rider as likely consumers of hot-reload capabilities. Provide a minimal RFC describing the future `FSharpChecker` APIs (opt-in settings, baseline caching) to these communities once the internal design is validated.

8. **Feature Flagging**
   - Compiler parses `--langversion:preview` and optional `--enable:hotreloaddeltas` flag to guard new behavior during preview.
   - IDEs/SDK respect `FSharpEnableHotReload` environment variable; default off until milestone validation completes.

These defaults should be revisited with stakeholders as implementation progresses, but they serve as a pragmatic starting point for planning and backlog creation.

## 5. Detailed Workflow per Edit

1. **Detect Changes**: using file watchers or IDE notifications, produce `FSharpDocumentDiff` (syntax + typed tree).
2. **Classify Edits**: run semantic diff to generate `FSharpSemanticEdit` list. Collect rude edits (unsupported constructs) early (e.g., signature changes, inline modifications).
3. **Rehydrate ILX Context**: load `IlxGenEnv` snapshot, reapply edits to produce updated `ILTypeDef`/`ILMethodDef` for affected symbols only.
4. **Emit Delta**:
   - Initialize `MetadataBuilder` with baseline heap offsets.
   - Use `MethodDefTokenMap` to retrieve existing tokens; only add rows when structure changes (`AddMethodDefinition` etc.).
   - Populate `EncLog`/`EncMap` entries with stable `SymbolId` references.
   - Build IL delta by writing method bodies (with 4-byte padding) into a `BlobBuilder` keyed by `MethodDefinitionHandle`.
   - Leverage `FSharpSymbolMatcher` and `FSharpDefinitionMap` to map existing synthesized members, closures, and async state machines.
5. **Emit PDB Delta**: gather updated sequence points from ILX instrumentation, feed into `FSharpPdbDeltaBuilder`.
6. **Package Update**: produce `IlxDelta`, wrap into `ManagedHotReloadUpdate` (with diagnostics). Send to runtime via `MetadataUpdater.ApplyUpdate`.
7. **Update Baseline**: merge delta into `FSharpEmitBaseline` (increment `encId`, table sizes, method map).

## 6. Risk Areas and Mitigations

- **Metadata delta emission**: AbstractIL lacks delta-writing APIs (EncLog/EncMap/table slicing). Designing this is high risk and prerequisite for Task 2.1 completion.
- **Symbol remapping (`FSharpSymbolMatcher`)**: we must map baseline definitions and synthesized members into the new compilation to reuse tokens. Missing this will force full rebuilds for edits touching closures/state machines.
- **Name stability coverage**: audit every `NiceNameGenerator` call (closures, async builders, computation expressions, private implementation details) to ensure hot-reload mode suppresses line-number suffixes.
- **Type providers**: provider outputs may be non-deterministic; cache and detect divergences to surface rude edits rather than emitting invalid deltas.
- **Quotations and reflected definitions**: ensure `quotationResourceInfo` and related IL resources can be re-emitted incrementally; otherwise treat edits as restart-required.
- **Fallback / rude edit path**: maintain an explicit rebuild workflow for edits that alter metadata layout (union shape changes, inline flags, representation attributes).
- **Cross-assembly edits**: inline-heavy patterns should surface rude edits when upstream inline definitions change; document expectations in tooling.
- **Async/state machines & computation expressions**: tokens for synthesized types must be preserved; structural changes may need type replacement via `CreateNewOnMetadataUpdateAttribute`.
- **Debugger integration**: F# relies on the C# expression evaluator—ensure debugger state machines and locals remain valid across deltas.
- **Runtime/IDE orchestration**: current CLI flag only captures baselines; delta generation, `MetadataUpdater.ApplyUpdate`, and IDE plumbing remain future work and should be tracked explicitly.
- **Test breadth**: expand coverage beyond baseline/token tests to include mdv-validated metadata, rude-edit matrices, synthesized constructs, and multi-generation sessions.
  - Deferred test scenarios: async state machines, lambda/closure edits, computation expressions, and multi-generation sequences should be validated once the delta writer supports them (tracked in IMPLEMENTATION_PLAN.md).
- **Signature/optimization data**: confirm whether `.signature`/`.optimization` resources must be regenerated for deltas; document findings to avoid runtime regressions.
- **Synthesized name stability**: Roslyn centralises helper naming in `Microsoft.CodeAnalysis.CSharp.Symbols.GeneratedNames`, where each compiler pass supplies explicit ordinals to pure helpers. F# still relies on the mutable `NiceNameGenerator`, so closure/helper names inherit line numbers. As of 2025-11-04 we persist baseline synthesized-name snapshots in `FSharpEmitBaseline` and reload them into `FSharpSynthesizedTypeMaps` whenever a session starts, ensuring method-body edits reuse the baseline name ordering. We still plan to replace `NiceNameGenerator` with a Roslyn-style `GeneratedNames` module backed by these maps so edits that introduce new helpers (closures, async/state machines) remain stable without relying on line numbers.

## 7. Implementation Roadmap

1. **Baseline Capture**
   - Add hooks in `IlxGen` and `ILBinaryWriter` to emit `FSharpEmitBaseline` with `SymbolId` maps.
   - Persist `HotReloadNameMap` after initial build.
2. **Semantic Edit Engine**
   - Build typed-tree diffing (`FSharpSemanticEdit`) using `TypedTreeOps` and symbol stamps.
   - Port rude-edit detection (pattern matching on constructs: active patterns, DU shape changes, inline flag toggles).
3. **Delta Emitters**
   - Implement `IlxDeltaEmitter` around `MetadataBuilder` and `MethodDefTokenMap`.
   - Add `EncLog`/`EncMap` recording to `ILBinaryWriter`.
   - Create `FSharpPdbDeltaBuilder` to output compliant PDB deltas.
4. **Tooling Glue**
   - Develop `FSharpHotReloadLanguageService` (Visual Studio + CLI) mirroring Roslyn interfaces.
   - Supply sample `MetadataUpdateHandler` modules for web/UI frameworks.
   - Track long-term workspace alignment using the historical design sketch in [dotnet/fsharp#11976](https://github.com/dotnet/fsharp/issues/11976). That issue outlines an F# workspace/project system with Roslyn-style immutable solution snapshots; although the discussion predates the current hot reload push, it still captures the desired end state for production-grade IDE integration where `dotnet watch`, Visual Studio, and third parties share a common incremental analysis backbone.
   - Provide an interim CLI (`fsc-watch`) that wraps the existing demo helpers to prove the hot reload loop end-to-end before `dotnet watch` adoption. This short-term tool watches a specified `.fsproj`, emits deltas via `FSharpChecker`, applies them with `MetadataUpdater.ApplyUpdate`, and now exposes optional delta artifact dumps plus `mdv` validation hooks so we can inspect ENC metadata/IL generations while the workspace infrastructure incubates.
   - Normalize relative compiler outputs: `FSharpChecker` now resolves `--out:` arguments against the project directory (mirroring Roslyn’s `EmitBaseline`), so CLI integrations like `fsc-watch` no longer feed mdv stale assemblies when the working directory differs from the project root.
   - Track follow-up infrastructure work: (a) finish wiring `FSharpSynthesizedTypeMaps`/`GeneratedNames` so synthesized helpers reuse baseline names; (b) move delta metadata emission off `MetadataBuilder` and reuse the compiler’s existing IL writer helpers for Roslyn parity; (c) design the workspace projection (`FSharpWorkspace`/`FSharpProject`) per the Modernizing F# Analysis roadmap.
5. **Validation Harness**
   - Automate hot reload regression runs using the existing POC (`hot_reload_poc/src/TestApp`) augmented with new emitters.
   - Integrate mdv/ilspy comparisons to verify ENC tables and token stability.
   - Follow Roslyn’s precedent (`MetadataAggregator`) when inspecting delta metadata; our tests currently avoid brute-force table dumps unless requested, but the long-term plan is to layer an aggregator so mdv/metadata inspections operate over baseline+delta views just as Roslyn’s ENC harness does.
6. **Preview & Feedback**
   - Publish experimental flag in F# compiler/SDK.
   - Gather telemetry on rude edits, delta sizes, error cases.

## 8. Next Steps for Contributors

- Instrument `NiceNameGenerator` usage in `IlxGen.fs` to inventory call sites needing hot reload name stabilization.
- Prototype `FSharpEmitBaseline` serialization by capturing `ILBinaryWriter` state post baseline build.
- Draft typed-tree diff utilities producing `FSharpSemanticEdit` records for simple method-body updates.
- Investigate a minimal `IlxDeltaEmitter` that reuses `MethodDefTokenMap` to emit a single updated method delta.
- Coordinate with Roslyn EnC maintainers on DefinitionMap parity requirements and runtime constraints.
- Align short-term `dotnet watch` work with the SDK host updates captured in `notes/dotnet_watch_integration.md`; ensure the CLI bridge remains functional while longer-term workspace changes incubate.
- Drive the workspace roadmap outlined in `notes/fsharp_workspace_roadmap.md`, translating the concepts from [dotnet/fsharp#11976](https://github.com/dotnet/fsharp/issues/11976) into concrete milestones and APIs.
- **Definition map and semantic edit classification**: Roslyn’s `DefinitionMap` and `SymbolChanges` capture per-symbol edit states (Added/Updated/Deleted/Rude) and drive edit diagnostics. The implemented `FSharpDefinitionMap`/`FSharpSymbolChanges` pair layers on `TypedTreeDiff`, tagging synthesized members (closures, async helpers) and exposing projection helpers consumed by future delta emit work.
- **Synthesized type/member tracking**: reusable structures similar to `SynthesizedTypeMaps` must record anonymous types, delegates, closures, async state machines, computation expression scaffolding, and `PrivateImplementationDetails` generated in each generation.
- **Variable slot allocator**: Roslyn’s `EncVariableSlotAllocator` maintains local slots/hoisted locals for method bodies. F# must track ILX locals (including async/state machine fields) so debugger state and metadata stay consistent across deltas; prototype a dedicated allocator to reuse slots.
