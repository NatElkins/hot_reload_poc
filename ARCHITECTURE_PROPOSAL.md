# F# Hot Reload Architecture Proposal

## 1. Context and Goals

- **Objective**: deliver parity-level Edit and Continue (EnC) and Hot Reload for F# comparable to the C# experience implemented in Roslyn (`roslyn/src/Features/Core/Portable/EditAndContinue`).
- **Scope**: enable incremental IL/metadata/PDB deltas, rude-edit diagnostics, runtime cache invalidation, and tooling integration spanning CLI, Visual Studio, and Rider. The bulk of this work lives in the F# compiler repository (`dotnet/fsharp`). We expect to reuse existing SDK/runtime/Roslyn infrastructure without requiring invasive changes; any follow-up adjustments in those repos should be limited to configuration (e.g., enabling the F# language service) rather than new features.
- **Assumptions**: .NET runtime Hot Reload surface (`MetadataUpdater.ApplyUpdate`, `CreateNewOnMetadataUpdateAttribute`, `MetadataUpdateHandler`) remains the execution target; the existing F# compiler (`fsharp/src/Compiler/...`) is the authoritative source for parsing, type checking, and IL emission. We continue to rely only on dependencies already present in the compiler (e.g., `System.Reflection.Metadata`) and avoid introducing new packages so downstream consumers—particularly the Fable branch that cross-compiles the compiler—remain viable.
- **Reference Resources**: In addition to the F# source tree, the DeepWiki overview of the compiler architecture (https://deepwiki.com/dotnet/fsharp, indexed 25 April 2025) provides quick links to core modules, testing guides, and build documentation useful during implementation.
- **2025-11-12 update**: Instrumentation plus a Roslyn baseline (`hotreload-utils`/`csharp_delta_test`) confirmed our ENC deltas still emit bloated `#Strings`/`#Blob` heaps (hundreds of empty entries) whereas Roslyn only serializes the handful of handles touched by each edit. This remaining mismatch likely explains the `MetadataUpdater.ApplyUpdate failed` error we see in the F# demo; shrinking our heaps/EncLog rows to Roslyn parity is now a top priority.
- **2025-11-12 update +5**: `FSharpMetadataAggregator` now maps delta string handles back to baseline handles using the literal value, so we can safely re-base the string/blob heaps (Roslyn-style) without breaking downstream components that expect translated handles to point at generation 0.
- **2025-11-15 update**: Reference tables now capture baseline handles as well—`IlxDeltaEmitter` records the original `StringHandle`/`BlobHandle` data for every emitted `TypeRef`, `MemberRef`, and `AssemblyRef`, `DeltaMetadataTables` reuses those offsets, and the hot reload metadata tests assert the new tables show up for property/event/async scenarios. A lightweight CLI (`tools/hot-reload/compare_roslyn.fsx`) compares captured deltas with `tools/baselines/roslyn_tables.json`; today’s async runtime-apply dump still lacks Roslyn’s `CustomAttribute`/`StandAloneSig` rows so we remain focused on wiring those tables next.
- **2025-11-18 update**: Added Roslyn baselines for the closure scenario (`Closure`/`ClosureUpdate`) and wired both the service regressions (`RoslynBaselineComparisons.fs`) and the mdv component suite to enforce matching Module/TypeRef/Param/MemberRef/StandAloneSig counts. This keeps the closure helper in lockstep with the C# lambda edit while we continue chasing the EncMap/NullableContext issues that show up in the runtime demo.
- **2025-11-16 update**: The delta emitter now feeds the reference tables unconditionally (even when the referenced type already lives in an assembly reference), and the compiler tests plus `compare_roslyn.fsx` confirm the async scenario only emits the two TypeRef rows Roslyn expects. Runtime ApplyUpdate in `tests/scripts/hot-reload-demo-smoke.sh` still fails with `HR=0x80131509`, though; the captured metadata lacks the custom-attribute and stand-alone signature rows Roslyn produces, so the next iteration needs to project those tables alongside the new reference rows.
- **2025-11-17 update**: `IlxDeltaEmitter` now injects a default stand-alone signature blob (`0x07 0x00`) whenever the rewritten IL body exposes no local signature token, ensuring Roslyn-style `StandAloneSig` rows materialize even for trivial method edits. The metadata writer also logs the module header into EncLog/EncMap before any per-table entries, eliminating the ordering failures in the HotReload metadata tests. Runtime deltas now match Roslyn’s async baseline on every table except `CustomAttribute`; the emitter’s state-machine detector was expanded to look for the `@statemachine` suffix Roslyn uses so we can synthesize the missing `AsyncStateMachineAttribute` rows once a qualifying nested type is present.
- **2026-02-07 update**: Ongoing implementation tracking is anchored to `NatElkins/fsharp#1` (`hot-reload@c454aeb`); treat branch commits and local docs as canonical whenever the PR description lags behind current behavior. Re-verified today with `./.dotnet/dotnet build FSharp.sln -c Debug -v minimal`, service hot reload tests (`258` passed), and component hot reload tests (`101` passed).
- **2026-02-07 verification addendum**: Verified non-interactive runtime behavior using the in-repo demo harness. `tests/scripts/hot-reload-demo-smoke.sh` passes in both metadata-only mode and `HOTRELOAD_SMOKE_RUNTIME_APPLY=1`; runtime-apply mode reports `Hot reload applied (delta #1)` and `Hot reload applied (delta #2)` with `Hello from generation 0 -> 1 -> 2` message transitions. A direct run of `./.dotnet/dotnet run --project tests/projects/HotReloadDemo/HotReloadDemoApp/HotReloadDemoApp.fsproj -- --scripted --multi-delta --runtime-apply` reproduces the same transitions without manual edits, and enabling `FSHARP_HOTRELOAD_TRACE_RUNTIME_APPLY=1` confirms explicit `[hotreload-runtime] applying delta ...` traces for both generations.
- **2026-02-07 deep architecture review (F# vs Roslyn vs SDK)**: A cross-repo audit of `fsharp/`, `roslyn/`, and `sdk/` identified four primary integration risks. First, SDK capability negotiation is incomplete for F#: C#/VB opt into `SupportsHotReload`, but F# does not, while `dotnet-watch` gates managed hot reload on that capability. Second, `dotnet-watch` remains Roslyn-centric (`WatchHotReloadService` + C#/VB workspace loading), and F#-specific handling is currently diagnostic/logging only. Third, F# hot reload session state is duplicated across `HotReloadState`, `FSharpEditAndContinueLanguageService` backup state, and checker-local state (`currentBaselineState`), unlike Roslyn's explicit committed/pending solution model. Fourth, commit/discard semantics are not fully symmetric (`DiscardPendingUpdate` is currently a no-op), which increases multi-generation recovery risk.
- **2026-02-07 execution strategy (risk-first)**: Apply changes in four phases: (1) semantic hardening (commit/discard correctness, stale-baseline detection hardening, and removal of unconditional console logging in metadata/PDB emit paths), (2) internal deduplication (single baseline-construction factory + single hot reload session owner), (3) host integration (F# `SupportsHotReload` capability and `dotnet-watch` F# managed update path), and (4) parity guardrails (mdv + runtime-apply multi-generation validation for every phase). Each phase should land in small commits with explicit Roslyn parity notes in commit messages.
- **2026-02-07 Task 0.1 completion (session semantics hardening)**: `HotReloadState` now tracks pending updates explicitly and only advances generation on `recordDeltaApplied`, with `discardPendingUpdate` clearing staged deltas to mirror Roslyn's commit/discard lifecycle. Checker emission now snapshots output fingerprints and rejects semantic updates when build output is unchanged (stale binary guard), metadata/PDB emit logging is env-gated, and `DeltaMetadataTables` routes user-string heap tokens through #US offsets instead of #Strings. New regression coverage exercises commit/discard behavior and stale-output rejection in both component and service hot reload suites.
- **2026-02-07 Task 0.2 completion (baseline/session deduplication)**: Baseline construction is now centralized in `HotReloadBaseline.createFromEmittedArtifacts`, shared by both checker and `fsc` capture flows so module-id/metadata snapshot/token-handle hydration stays aligned. Session ownership was also collapsed into `HotReloadState`: committed snapshots are restored through `TryRestoreSession`, explicit teardown uses `ResetSessionState`, and checker/service no longer maintain duplicate backup state copies.
- **2026-02-07 Task 0.3 completion (SDK SupportsHotReload wiring)**: `Microsoft.NET.Sdk.FSharp.targets` now advertises `SupportsHotReload` with the same conditions as C#/VB (`.NETCoreApp`, TFM >= 6.0, `SupportsHotReload != false`), and SDK tests were extended to verify the enabled, pre-net6, and explicit opt-out cases for F# projects.
- **2026-02-07 Task 0.4 completion (dotnet-watch F# managed path)**: `dotnet-watch` now feeds F# projects into the managed update flow, combines Roslyn + F# updates before applying deltas, preserves Roslyn-style commit/discard semantics, and uses language-neutral managed-update messaging. An F# bridge service attempts checker-driven deltas when compatible FCS APIs are available and intentionally falls back to restart/rebuild behavior otherwise to keep runtime semantics safe.
- **2026-02-07 Task 0.5 completion (validation discipline closure)**: Milestone 0 landed as separate task-scoped commits (`63d4fcc32`, `a8a7e58d4`, `54871557ab`, `01196e0834`, `ae9b3ba0f`) and closed with a full `tests/scripts/hot-reload-verify.sh` pass (`259` service tests, `107` component tests, smoke default/runtime-apply + direct runtime-apply trace markers).
- **2026-02-07 Milestone 0 follow-up hardening**: Live `dotnet watch` validation found additional integration gaps after the initial Task 0.4 closeout. Follow-up commits (`ac239b8e11` in `sdk`, `6662b3c68` in `fsharp`) addressed empty F# design-time command lines via forced compile capture, quoted `FscToolPath` normalization, changed-file notifications into FCS before emit, compile-before-emit to satisfy stale-output checks, and short output switch parsing (`-o:<path>`) in F# hot-reload sessions. This preserves Roslyn-style host semantics while closing watch-loop-specific failure modes.
- **2026-02-07 Milestone 0 final verification rerun**: Re-executed `tests/scripts/hot-reload-verify.sh` after the follow-up hardening commits; all stages remained green with `260` service tests, `107` component tests, smoke default/runtime-apply, and direct runtime-apply trace assertions.
- **2026-02-07 dotnet-watch F# test gate hardening**: SDK test coverage in `dotnet-watch.Tests` now exercises F# watch-loop edits and rude-edit restarts (`68de339706`). The assertions intentionally model Roslyn-style host semantics by accepting either managed apply or deterministic restart fallback when the F# bridge cannot call `FSharpChecker.StartHotReloadSession`, while still requiring explicit restart markers for rude edits.
- **2026-02-07 workspace snapshot bridge update**: `FSharpChecker` now accepts `FSharpProjectSnapshot` inputs for `StartHotReloadSession`/`EmitHotReloadDelta` (`04e92d03a`), and `dotnet-watch` prefers an `FSharpWorkspace` snapshot bridge when those APIs exist (`4947ff9558`), with a safe fallback to the existing project-options reflection path. This narrows the remaining gap between the current host path and the long-term Roslyn-style workspace architecture.
- **2026-02-08 workspace support completion**: The remaining workspace blocker is now closed across compiler + SDK. `fsharp` commit `ed382175f` routes snapshot session APIs through real snapshot parse/check flows (`ParseAndCheckProject(projectSnapshot)`) and adds a regression covering full `FSharpWorkspace` baseline/update snapshot lifecycle with successful delta emission. `sdk` commit `d53ea81e6d` fixes `FSharpOption` reflection for static accessor shapes, updates workspace change propagation to use `Files.Edit(Uri,string)` when available, keeps checker notifications aligned in workspace mode, and gates workspace-snapshot preference to custom-FCS/explicit env opt-in to avoid bundled-FCS instability. Validation remains green across required gates (`fsharp` build + component filter), service hot-reload tests (`262`), SDK F# watch tests (`5`), and full `tests/scripts/hot-reload-verify.sh`.
- **2026-02-09 symbol-driven finalization for method identity**: Follow-up work removed output-diff evidence fallback so updated-method selection remains symbol-driven end-to-end (Roslyn-parity intent). `service.fs` now relies solely on symbol-derived updates, `EditAndContinueLanguageService.fs` emits from symbol mapping directly, and `TypedTreeDiff.SymbolId` now carries compiled-name/arity data consumed by `DeltaBuilder` for method-key resolution. A local/parameter hashing fix (logical identity instead of stamp) eliminated false positives across recompiles; new overload regression coverage now ensures unchanged overload bodies are not emitted as updates.
- **2026-02-10 same-arity overload disambiguation hardening**: `fsharp` commit `15b872436` extends `SymbolId` with canonical parameter-type identities and updates `DeltaBuilder` to apply signature-based matching only when name/arity resolution is ambiguous. This mirrors Roslyn’s “full signature to disambiguate semantic edit targets” intent while keeping existing single-candidate behavior unchanged. Added regression coverage in `HotReloadCheckerTests` (`Same-arity overloaded method-body edit updates matching overload token`) to ensure edits now target the correct overload token without forcing restart.
- **2026-02-10 generic-signature + watch-loop parity closure**: `fsharp` commit `b694b01e6` completed generic overload identity matching by deriving canonical parameter/return identities from compiled-form method signatures (`GetValReprTypeInCompiledForm`) so typar/generic-arity ambiguities resolve to the authored method token. SDK commit `826e7e359c` then hardened `dotnet-watch` session bootstrap/runtime targeting for F# by pre-caching project inputs before first edit and applying deltas against the loaded module MVID across forced compiles, with updated F# watch tests covering first-edit in-place apply and rude-edit restart sequencing. Validation snapshot: SDK F# watch filter (`6` passed), `fsharp` build (`FSharp.sln`), and `fsharp` component HotReload filter (`107` passed).
- **2026-02-11 Roslyn no-change parity fix**: SDK commit `38fad1b60c` aligns F# no-op edit handling with Roslyn: `FSharpManagedUpdateStatus.NoChanges` now flows through dotnet-watch as “no managed changes” (or benign in-place apply) and no longer forces restart/rebuild. Added regression coverage in `dotnet-watch.Tests` (`ChangeFileInFSharpProject_WhitespaceOnlyEditDoesNotRestart`) asserting that insignificant edits never emit `RestartNeededToApplyChanges`; validation passed for both the focused test (`1`) and the full F# watch filter (`7`).
- **2026-02-11 UI-focused parity backlog update**: Captured a Roslyn-inspired gap matrix in `IMPLEMENTATION_PLAN.md` to prioritize UI iteration risk: (1) computation-expression/DSL usage edits with explicit end-to-end coverage, (2) non-`.fs` dependency edit routing in `dotnet watch`, (3) workspace snapshot freshness for tracked non-source inputs, and (4) type-provider-oriented invalidation semantics. Lower-priority follow-ups cover richer rude-edit taxonomy and debugger active-statement parity.
- **2026-02-11 matrix follow-up implementation**: Completed the remaining P0 slices from that matrix. Compiler commit `fc189a911` fixed tracked-input parsing for resource options with logical-name suffixes (for example `--resource:path,logicalName`) so workspace config hashes now react to non-source dependency edits; service tests now verify workspace snapshot version changes and dependency-invalidation-to-source-delta continuity. SDK commit `3e15e57016` added end-to-end F# computation-expression usage coverage in `dotnet-watch.Tests` with in-place apply assertions. Rude-edit taxonomy also tightened in `fsharp` commit `02a3ca211` by classifying explicit interface insertion as `InsertExplicitInterface` (with matching diagnostics/test coverage).
- **2026-02-11 lowered-shape rude-edit taxonomy expansion**: `TypedTreeDiff` now emits dedicated rude-edit categories for lowered lambda/state-machine/query shape changes (`LambdaShapeChange`, `StateMachineShapeChange`, `QueryExpressionShapeChange`) instead of collapsing these into coarse fallback behavior. Diagnostics now expose stable IDs `FSHRDL012`-`FSHRDL014`, and service tests lock this behavior through targeted diff and diagnostic assertions.
- **2026-02-11 strict async lowered-shape classification follow-up**: Tightened `TypedTreeDiff` matching so async resource-scope edits no longer surface as declaration-churn fallback for the same user-authored method identity. Canonical symbol-signature matching now keeps equivalent bindings paired across recompiles, and the resource-scope regression asserts `StateMachineShapeChange` without `DeclarationAdded`/`DeclarationRemoved`.
- **2026-02-11 parity closure (provider + CE matrix)**: Completed the remaining Milestone-0-adjacent parity items with additional test coverage. `HotReloadCheckerTests` now includes a real `TestTP` provider matrix (erased usage apply, generative usage apply, generative static-arg no-delta, and dependency invalidation + subsequent source delta). CE/desugaring output-shape validation is now explicitly covered end-to-end in runtime tests (`Computation-expression output shape is preserved across desugaring variants`) and reinforced by mdv async metadata validation after MethodSpec/MemberRef remapping parity fixes.
- **2026-02-12 demo reliability update (toolset skew root cause)**: Live `dotnet watch` demo failures with `System.TypeLoadException` after a reported successful apply were traced to an FCS/FSharp.Core skew when `DOTNET_WATCH_FSHARP_COMPILER_SERVICE_PATH` pointed at `fsharp/artifacts` while the app/runtime used SDK-bundled F# bits. Controlled repro runs show: custom-path FCS crashes after apply; SDK-matched FCS applies in place and continues execution. `scripts/run-fsharp-dotnet-watch-demo.sh` now defaults to the SDK-matched `FSharp.Compiler.Service.dll` for the selected `dotnet`, while still allowing explicit override with a skew warning.

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
- *(2025-11-11 service helper + gaps)*: Mirrored that helper strategy inside the service tests via `tests/FSharp.Compiler.Service.Tests/HotReload/MetadataDeltaTestHelpers.fs`. Both the metadata-writer parity suite and the new `FSharpMetadataAggregatorTests` now share IL builders, mdv assertions, and baseline reproducers, so `dotnet build tests/FSharp.Compiler.Service.Tests/FSharp.Compiler.Service.Tests.fsproj -c Debug` is green again. However, the hot-reload-focused filter still fails: `MetadataReaderProvider` throws `BadImageFormatException` because the `MetadataDelta.Metadata` blob lacks the stream headers/#~ padding, and every “AbstractIL serializer matches metadata builder output” assertion reports a one-byte discrepancy (11 vs 12). Fixing the serializer to emit the full metadata root is now the top blocker for validating the aggregator path.
- *(2025-11-18 update)*: `DeltaMetadataSerializer.tableRowsByIndex` now feeds `CustomAttribute` rows into the delta table stream, so `MetadataReaderProvider`/`mdv` can reopen async deltas without `BadImageFormatException`. The remaining roslyn-parity gaps are limited to the missing TypeRef/Param/StandAloneSig rows and EncLog ordering tracked under Task 2.3, plus the outstanding runtime apply failure.
- **Runtime integration**: `FSharpEditAndContinueLanguageService` now caches the last baseline and rehydrates `HotReloadState` automatically when `fsc` clears the active session, so the CLI/service APIs survive successive recompiles. Component tests drive the service directly (`RuntimeIntegrationTests.fs`).
- **Public checker API**: Preview members (`StartHotReloadSession`, `EmitHotReloadDelta`, `EndHotReloadSession`, `HotReloadSessionActive`) flow through the service and return IL deltas for method-body edits; metadata/PDB blobs may be empty until the delta writers mature. The APIs remain internal while we polish capability negotiation and multi-delta scenarios.
- **Telemetry**: `Activity` spans wrap session start, checker entry points, and delta emission so hosts can trace hot reload latency and generation IDs without additional wiring.
- **CLI integration**: The new `tests/projects/HotReloadDemo/HotReloadDemoApp` console sample exercises the checker APIs directly and replaces the legacy `hot_reload_poc` harness for manual demos. A `--scripted --multi-delta` mode plus `tests/scripts/hot-reload-demo-smoke.sh` provide the regression hook, and `eng/Build.ps1` now runs that two-delta scripted flow during CoreCLR/desktop test passes so CI catches demo regressions automatically. A future follow-up will either retire or repurpose `hot_reload_poc/scripts/run-hot-reload-watch.sh` once we decide how `dotnet watch --hot-reload` should drive the F# pipeline. Hot reload capabilities are surfaced via `FSharpChecker.HotReloadCapabilities` so hosts can query the supported feature set while the API remains in preview.
- **Verification log**: `./.dotnet/dotnet build FSharp.sln -c Debug` succeeds. The latest service-suite run (`./.dotnet/dotnet test tests/FSharp.Compiler.Service.Tests/FSharp.Compiler.Service.Tests.fsproj -c Debug --no-build --filter FullyQualifiedName~HotReload`) now fails in two places: `FSharpMetadataAggregatorTests` hits `BadImageFormatException: Not enough space for stream header name` when parsing the delta metadata blob, and every “AbstractIL serializer matches metadata builder output” assertion reports a one-byte padding mismatch (expected 12 bytes in the #~ stream, actual 11). The component hot-reload filter was not re-run in this iteration; its last passing run is still the 2025‑11‑02 baseline. **Known regression**: `dotnet build hot_reload_poc/HotReloadPoc.sln` currently fails because `hot_reload_poc/src/TestApp/HotReloadTest.fs` is missing—restoring the sample is tracked by Task 3.5.

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
  - *2025-11-11 note*: moving the `TableRows`/`RowElementData` record to module scope (required by the .NET 9 nested-type restriction) currently runs into FS0039 because `ILBinaryWriter.MetadataTable<'T>` and `UnsharedRow` are only visible while we stay inside `DeltaMetadataTables`. To unblock that, we need to materialize the AbstractIL rows as plain `{ Tag; Value }` arrays before exposing them through `DeltaMetadataTypes`, so no external file references `MetadataTable<'T>` directly.
  - *2025-11-12 status*: We temporarily exposed `RowElementTags`, `RowElement`, and `MetadataTable<'T>` through `ilwrite.fsi` so `DeltaMetadataTables` could keep compiling, but that surfaced new issues: the signature now needs explicit `[<Struct>]`/constructor annotations (FS0938), the tables module imports the wrong namespace (`ILBinary` instead of `BinaryConstants`) and forgets to pull in `IlxDeltaStreams`, and helper `let` bindings sit after `member` definitions, triggering FS0960. The immediate priority is to fix those mechanical build breaks (see Implementation Plan Task 2.3 micro-steps) so we can resume the deeper refactor toward pure `RowElementData` snapshots without leaking additional AbstractIL implementation types.
  - *Status 2025-11-11*: `DeltaMetadataSerializer` now emits both the `#~` stream and the full metadata root (signature/header + heaps) unconditionally (the `FSHARP_HOTRELOAD_USE_ABSTRACTIL` guard and SRM fallback have been removed). The service tests still compare the AbstractIL bytes against a reference `MetadataRootBuilder` blob to keep Roslyn parity coverage.
  - *2025-11-11 fix*: `FSharp.Compiler.Service.fsproj` now lists `DeltaMetadataTables.fs` ahead of `DeltaMetadataSerializer.fs`/`FSharpDeltaMetadataWriter.fs`, ensuring the shared `TableRows` DTO resolves before the serializer compiles.
  - *2025-11-11 fix*: `DeltaMetadataSerializer.align4` now masks with `~~~3`, matching Roslyn’s helper and restoring correct `#~` padding on net9+/ARM builds.
  - *2025-11-11 blocker*: Adding aggregator coverage highlighted that `DeltaMetadataTables.fs` still instantiates `ILBinaryWriter.MetadataTable<'T>`/`UnsharedRow` and helper constructors (`UShort`, `HasSemantics`, etc.) that are not exported via `ilwrite.fsi`. Clean netstandard builds therefore fail as soon as we force a rebuild (which the new tests do). We need to clone just the small slice of functionality we rely on—string/blob/guid heap builders + row accumulators emitting `RowElementData`—so the hot reload pipeline no longer depends on IL writer internals before we can proceed with additional metadata tests.
  - *2025-11-12 update*: The bespoke string/blob heap builders now accept baseline offsets, copy the literal bytes back into the delta heaps when we reuse existing handles, and prime the header flags with `StringHeapLarge|GuidHeapLarge|BlobHeapLarge|EncDeltas|DeletedMarks` so CLR readers treat the output as a proper Enc delta. `FSharpMetadataAggregatorTests` now parse the deltas without `BadImageFormatException`, and setting `FSHARP_HOTRELOAD_TRACE_METADATA=1` dumps the custom vs. SRM metadata blobs for quick diffing.
  - *2025-11-13 update*: The SRM `MetadataBuilder` pipeline is now optional (`FSHARP_HOTRELOAD_USE_SRM_TABLES=1`). By default we skip emitting Method/Param/Property/Event rows to SRM and rely entirely on the AbstractIL mirror + serializer, keeping only the EncLog/EncMap bookkeeping active. This removes the double-write cost and keeps the writer aligned with the “pure AbstractIL” goal without regressing the existing tooling hook.
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
    - **Update 2025-11-11**: Property/event accessor deltas now consult the inverse method-token remap captured by `IlxDeltaEmitter`. The portable PDB reader first translates the delta token (e.g., `0x06000002`) back to the token emitted by the temporary IL module (often `0x06000001`) before fetching sequence points, fixing the missing `MethodDebugInformation` rows that previously caused component tests to fail for added accessors.
  - Respects baseline table lengths captured in `FSharpEmitBaseline.PortablePdb`, populating `EncLog`/`EncMap` entries for each touched method so the runtime can remap sequence points.
  - Currently emits method-level debug info (sequence point blobs when available); local scope/async metadata will be layered once Task 2.1/2.5 add richer ILX instrumentation.
- `FSharpEmitBaseline.PortablePdb`
  - Stores the baseline portable PDB bytes, row counts, and optional entry point token captured during `fsc` baseline emission so later deltas can compute correct relative row ids.

#### 4.2.8 Remaining Metadata/PDB Work (2025-11-13)

- **DeltaIndexSizing / DeltaTableLayout parity** – Replace the remaining AbstractIL sizing helpers with computations derived from the mirror data so coded-index flags/bitmasks match Roslyn’s `MetadataSizes` (see `roslyn/src/Compilers/Core/Portable/PEWriter/MetadataSizes.cs`). This affects `DeltaIndexSizing.fs`, `DeltaTableLayout.fs`, `DeltaMetadataSerializer.fs`, and the writer tests.
- **Aggregator + mdv multi-generation coverage** – Use the new `MetadataDeltaTestHelpers.fs` builders to emit Gen1/Gen2 property + event deltas, then extend `FSharpMetadataAggregatorTests.fs`, `MdvValidationTests.fs`, and `PdbTests.fs` to assert EncLog/EncMap/mdv output across generations.
- **Runtime demo validation** – Update `tests/projects/HotReloadDemo/HotReloadDemoApp` (and its scripted smoke test) to emit real metadata/IL/PDB blobs, invoke `MetadataUpdater.ApplyUpdate`, and log the mdv command + before/after literals. This becomes the acceptance gate showing that the emitted deltas can be applied by the runtime without restarting the process.

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

## 9. Investigation Update (2026-02-08)

- Root-cause investigation for `dotnet watch` F# managed updates showed the failure is in symbol-diff classification, not in runtime apply transport.
- `service.fs` currently feeds `HotReloadOptimizationData` into diffing (`src/Compiler/Service/service.fs:619`, `src/Compiler/Service/service.fs:652`), and `EditAndContinueLanguageService` maps those edits directly to delta method keys (`src/Compiler/HotReload/EditAndContinueLanguageService.fs:177`, `src/Compiler/HotReload/EditAndContinueLanguageService.fs:184`).
- With optimized implementation snapshots, edits to user code were observed as changes to lowered bindings (`main` or `greeter`) rather than the edited method/member, leading to:
  - no-op applied deltas (`main` updated while `main` stays active in an infinite loop), or
  - `NoChanges` fallback when lowered symbol names do not resolve to baseline method keys.
- Added trace instrumentation (env-gated) in:
  - `src/Compiler/TypedTree/TypedTreeDiff.fs` for body-hash edit classification (`[fsharp-hotreload][typed-diff] ...`),
  - `src/Compiler/HotReload/DeltaBuilder.fs` for symbol-to-method resolution (`[fsharp-hotreload][delta-builder] ...`),
  - `src/BuiltInTools/dotnet-watch/HotReload/FSharp/FSharpHotReloadService.cs` for module-id/token summaries,
  - `src/BuiltInTools/HotReloadAgent/HotReloadAgent.cs` for module-match diagnostics.
- Resolution implemented:
  - `service.fs` now computes symbol updates and output-diff evidence together, then selects updated methods using:
    1. direct symbol/evidence matches when present,
    2. non-generated evidence methods when symbol classification only points at noisy callers,
    3. symbol-only fallback when evidence is synthesized-only (for lowered async edits).
  - `EditAndContinueLanguageService.fs` now mirrors the same selection policy at emit time, preventing drift between stale-output prechecks and actual emitted method rows.
  - `TypedTreeDiff.fs` now hashes member references by stable identity, reducing stamp-churn caller false positives.
- Current validated behavior:
  - Module-function loop edit updates `Demo::message` token and applies in-place.
  - Property loop edit updates `Demo.Greeter::get_Message` token and applies in-place.
  - Async mdv regression no longer emits invalid synthesized updates (`Handle belongs to a future generation` resolved).
- 2026-02-09 follow-up:
  - Removed output-diff evidence fallback from method selection to match Roslyn's symbol-driven model more closely. `service.fs` now emits from symbol-derived updates only, and `EditAndContinueLanguageService.fs` no longer filters by output evidence.
  - Extended `TypedTreeDiff.SymbolId` with compiled-name and arity metadata consumed by `DeltaBuilder` during method-key resolution.
  - Extended `TypedTreeDiff.SymbolId` again with canonical parameter-type identities and applied them in `DeltaBuilder` as an ambiguity-only tie-breaker, so same-arity overload edits resolve to the intended method token without changing single-candidate behavior.
  - Fixed local/parameter stamp churn by hashing local `Expr.Val` references via logical identity (name + type), preventing unchanged overloads from being marked as method-body updates.
  - Added service regression coverage for both overload shapes (`Overloaded method-body edit updates matching overload token` and `Same-arity overloaded method-body edit updates matching overload token`) to lock in this behavior.
- 2026-02-10 follow-up:
  - Closed the generic-typar ambiguity gap by deriving symbol signature identity from compiled-form typars (`GetValReprTypeInCompiledForm`) instead of dropping `TType_var` during symbol encoding.
  - `TypedTreeDiff.SymbolId` now carries canonical parameter and return identities plus method generic arity, so `DeltaBuilder` can resolve overloaded methods by authored signature intent rather than lowered-binding heuristics.
  - Added targeted checker regressions for generic overload resolution:
    - typar-parameter ambiguity (`Compute('T)` vs `Compute(string)`)
    - generic-arity ambiguity (`Compute(int)` vs `Compute<'T>(int)`)
  - Verification for this change set:
    - `./.dotnet/dotnet build FSharp.sln -c Debug -v minimal` passed.
    - HotReload component filter passed (`107` tests).
    - HotReload checker filter passed (`17` tests).

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
