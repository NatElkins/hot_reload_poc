# Hot Reload Verification Plan

Goal: for each spec section (2–8), define actionable checks to prove the F# implementation matches HOT_RELOAD_SPEC.md. Each item should culminate in automated or semi-automated tests (service/component/runtime/mdv) and references to Roslyn/runtime behavior.

## Section 2 – Metadata Container and Heaps
- [x] Verify metadata root/header emits BSJB signature, stream headers, and heap offsets exactly per spec (baseline lengths used as starts; row-0 entries present) via mdv on baseline and deltas.
- [x] Add/confirm tests that string/blob/user-string/guid heap starts equal baseline sizes in every delta (service + component HotReload tests; mdv parsing).
- [x] Confirm EncId/EncBaseId chaining in Module row across multi-gen deltas; ensure one new GUID per generation and stable Name/Mvid (service/component tests).
- [x] Ensure heap suffix only contains new literals/blobs; verify bounds with mdv and existing heap-growth guards.

## Section 3 – EncLog and EncMap Semantics
- [x] Ensure EncLog rows emitted with correct FuncCode (add/update) for all added/updated definitions; compare against Roslyn iterator/async expectations.
- [x] Ensure EncMap contains all definition tokens in table/RID order; no missing Param/MethodDef/TypeDef entries on updates/adds.
 - [x] Add/verify tests that metadata and PDB EncLog/EncMap are identical per generation. (component test now compares metadata vs PDB EncLog/EncMap for method update; PDB emission mirrors metadata Enc tables)

## Section 4 – Table-by-Table Delta Rules
- [x] Module: single row per generation; correct Generation/EncId/EncBaseId; Name/Mvid reused; Guid heap grows by one. Guid columns encoded as byte offsets (baseline GuidHeapStart + (index-1)*16), no sentinel in delta Guid heap. Gen2 BaseId = Gen1 EncId. Covered by `FSharpDeltaMetadataWriterTests.module rows chain enc ids...` (service HotReload filter).
- [ ] ~~TypeDef/NestedClass: added types update FieldList/MethodList ranges; NestedClass rows present for nested additions. mdv compares against Roslyn add-nested-type scenario.~~ (Deferred: not needed for method-body-only demo; emitter lacks TypeDef/NestedClass/Field plumbing.)
- [ ] ~~TypeRef/MemberRef/AssemblyRef: reused when structurally identical; new rows only when needed. Add test that body-only edits do not grow these tables; new reference scenario grows exactly once.~~ (Deferred: baseline reuse suffices for method-body-only demo.)
- [x] MethodDef: updates emit MethodDef rows with EncLog/EncMap entries (async multi-gen delta uses Default op). (RVA/ParamList spot-check deferred to Param item.) Verified via service test `method update emits MethodDef row with ParamList and RVA`.
- [x] Param: ParamList non-zero/Param rows preserved; EncLog/EncMap include Param when emitted (validated with async/event deltas). (Seq#0 optional for void-return adds; return param synthesized for updates.) Covered by new service test `added method emits Param seq0 and enc entries`.
- [x] StandAloneSig: locals/calli edits add rows/blobs; EncLog/EncMap contain StandAloneSig row. Covered by service test `local signature delta emits standalone signature rows`.
- [ ] ~~MethodSemantics/Property/Event/Maps: accessor adds wire MethodSemantics + Property/Event + Map rows; body-only accessor edits don’t add maps. Tests for property/event add/edit.~~ (Deferred: not needed for simple method body edit demo.)
- [ ] ~~CustomAttribute: parent-sorted rows; new attributes appended after existing parent group; EncLog/EncMap include CA rows; value blobs correct. Include NullableContext/Async attributes scenarios.~~ (Deferred: no CA changes for body-only demo.)
- [ ] ~~MethodImpl: added interface/override implementations append rows with correct Class/MethodBody/MethodDecl tokens; EncLog/EncMap include. Test interface-impl add scenario.~~ (Deferred: not needed for body edit.)
- [ ] ~~Field/FieldRVA: appended Field rows (and FieldRVA when RVA present); validate TypeDef field range intact; RVA points into delta data.~~ (Deferred.)
- [ ] ~~TypeSpec/GenericParam/Constraints: new instantiations add TypeSpec rows; generic param changes rejected (rude edit). Test new closed generic use; ensure no spurious growth on body-only edits.~~ (Deferred.)

## Section 5 – IL Delta Stream
- [x] Validate fat/tiny headers and RVAs per method; EH sections well-formed; mdv /il+ matches MethodDef RVA. (Covered via component test `DeltaEmitterTests.IL delta fat header matches method body length`; RVA-to-body offset also covered by `MethodDef RVA matches emitted method body offset`.)
- [x] Ensure IL tokens reuse baseline tokens for unchanged refs; new-reference growth deferred (needs MemberRef/TypeRef add scenario). Covered by component test `emitDelta reuses MemberRef tokens for unchanged external call`.
- [x] Confirm StandAloneSig tokens in headers match emitted StandAloneSig rows; locals edits create sig rows for updated methods and header tokens point at the emitted sig. Covered by component tests `emitDelta reuses StandAloneSig token when locals unchanged` and `emitDelta adds new StandAloneSig and header token when locals change` (delta currently re-emits the locals sig even when unchanged; header/token blob alignment verified).

## Section 6 – PDB / Debug Delta
- [x] PDB EncLog/EncMap identical to metadata EncLog/EncMap; add comparison test. Covered by component test `PDB EncLog/EncMap matches metadata Enc tables` (updates check equality across sorted rows).
- [x] Updated methods include MethodDebugInformation/sequence points; unchanged methods omitted. Assert documents stable unless new files introduced. Covered by component test `PDB delta includes only updated methods and stable documents` (two-method baseline, single-method edit).
- [x] Heap starts in PDB delta reuse baseline lengths (string/blob/guid/doc name). Covered by component test `PDB heap offsets start at baseline sizes` (logs/guards when no doc/seqpoint blobs emitted).

## Section 7 – Runtime Apply Pipeline
- [x] End-to-end ApplyUpdate succeeds for supported edits (body-only) using demo with `--runtime-apply` flag. GUID heap format fix and multi-generation session state fix completed; runtime accepts F# deltas. Covered by `HotReloadDemoApp --runtime-apply` and component tests.
- [x] ApplyUpdate fails with expected rude-edit diagnostics for disallowed edits. Covered by FSharpChecker API tests:
  - `EmitHotReloadDelta rejects signature change`
  - `EmitHotReloadDelta rejects record field addition`
  - `EmitHotReloadDelta rejects new function addition`
  - `EmitHotReloadDelta rejects union case addition`
- [x] Multi-generation apply: EncId chain verified, heap starts correct, live code changes applied across multiple generations. Covered by `HotReloadDemoApp --multi-delta --runtime-apply` and service test `module rows chain enc ids and reuse name/mvid across generations`.
- [x] ALC correctness: MVID consistency validated across generations. Multi-module assemblies N/A (F# never produces multi-module assemblies). Covered by `module rows chain enc ids and reuse name/mvid across generations` assertions on gen1MvidGuidValue/gen2MvidGuidValue matching baseMvidGuid.

## Section 8 – Tooling and Verification
- [x] mdv command templates exercised in CI/manual runs for baseline + /g:<md>;<il> (+PDB when present); ensure failures are actionable (heap starts, Enc tables, IL). Smoke test outputs correct mdv command templates (e.g., `mdv <baseline.dll> "/g:<meta>;<il>"`) and supports `FSHARP_HOTRELOAD_MDV_PATH` for custom mdv location. Heap starts and Enc tables validated via mdv when SDK compatible.
- [x] compare_roslyn.fsx / inspect_delta.fsx produce parity reports for representative edits (body-only, property add, iterator/async, interface impl); flag deviations. `tools/hot-reload/compare_roslyn.fsx` compares table row counts against `tools/baselines/roslyn_tables.json` scenarios. `inspect_delta.fsx` (root) dumps Param/MethodDef rows for quick inspection.
- [x] HotReload demo smoke (`tests/scripts/hot-reload-demo-smoke.sh`) preserves delta-dump and logs HRESULT; inspect Enc tables/ParamList/heaps when runtime apply fails. Script supports `HOTRELOAD_SMOKE_KEEP_WORKDIR=1` to preserve `delta-dump/gen-NNN/{metadata,il,pdb}.bin` files, `HOTRELOAD_SMOKE_RUNTIME_APPLY=1` for MetadataUpdater.ApplyUpdate, and logs HRESULT on failures.
