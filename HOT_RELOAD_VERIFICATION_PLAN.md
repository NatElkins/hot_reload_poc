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
- [ ] Heap starts in PDB delta reuse baseline lengths (string/blob/guid/doc name). Add targeted debug-reader checks.

## Section 7 – Runtime Apply Pipeline
- [ ] End-to-end ApplyUpdate succeeds for supported edits (body-only, property/event add, interface impl, locals change) using demo/runtime smoke with mdv inspection of deltas.
- [ ] ApplyUpdate fails with expected rude-edit diagnostics for disallowed edits (signature change, instance field add, generic param change); capture HRESULT and diagnostic.
- [ ] Multi-generation apply: verify EncId chain, heap starts, EncLog/EncMap continuity, and live code changes applied across at least two generations.
- [ ] ALC correctness: ApplyUpdate targets correct assembly/ALC; multi-module assemblies rejected (negative test).

## Section 8 – Tooling and Verification
- [ ] mdv command templates exercised in CI/manual runs for baseline + /g:<md>;<il> (+PDB when present); ensure failures are actionable (heap starts, Enc tables, IL).
- [ ] compare_roslyn.fsx / inspect_delta.fsx produce parity reports for representative edits (body-only, property add, iterator/async, interface impl); flag deviations.
- [ ] HotReload demo smoke (`tests/scripts/hot-reload-demo-smoke.sh`) preserves delta-dump and logs HRESULT; inspect Enc tables/ParamList/heaps when runtime apply fails.
