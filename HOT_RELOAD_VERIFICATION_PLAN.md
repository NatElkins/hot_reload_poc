# Hot Reload Verification Plan

Goal: for each spec section (2–8), define actionable checks to prove the F# implementation matches HOT_RELOAD_SPEC.md. Each item should culminate in automated or semi-automated tests (service/component/runtime/mdv) and references to Roslyn/runtime behavior.

## Section 2 – Metadata Container and Heaps
- [x] Verify metadata root/header emits BSJB signature, stream headers, and heap offsets exactly per spec (baseline lengths used as starts; row-0 entries present) via mdv on baseline and deltas.
- [x] Add/confirm tests that string/blob/user-string/guid heap starts equal baseline sizes in every delta (service + component HotReload tests; mdv parsing).
- [x] Confirm EncId/EncBaseId chaining in Module row across multi-gen deltas; ensure one new GUID per generation and stable Name/Mvid (service/component tests).
- [x] Ensure heap suffix only contains new literals/blobs; verify bounds with mdv and existing heap-growth guards.

## Section 3 – EncLog and EncMap Semantics
- [ ] Ensure EncLog rows emitted with correct FuncCode (add/update) for all added/updated definitions; compare against Roslyn iterator/async expectations.
- [ ] Ensure EncMap contains all definition tokens in table/RID order; no missing Param/MethodDef/TypeDef entries on updates/adds.
- [ ] Add/verify tests that metadata and PDB EncLog/EncMap are identical per generation.

## Section 4 – Table-by-Table Delta Rules
- [ ] Module: single row per generation; correct Generation/EncId/EncBaseId; Name/Mvid reused; Guid heap grows by one. Test multi-gen mdv output.
- [ ] TypeDef/NestedClass: added types update FieldList/MethodList ranges; NestedClass rows present for nested additions. mdv compares against Roslyn add-nested-type scenario.
- [ ] TypeRef/MemberRef/AssemblyRef: reused when structurally identical; new rows only when needed. Add test that body-only edits do not grow these tables; new reference scenario grows exactly once.
- [ ] MethodDef: updates emit MethodDef rows with new RVA; ParamList non-zero. Validate RVA matches delta IL offsets; Param reuse preserved.
- [ ] Param: Seq#0 return row always present; sequences strictly increasing; name handle reuse. Tests for parameterless updates and added params.
- [ ] StandAloneSig: locals or calli edits add rows/blobs; reused when identical. Validate tokens in IL headers and EncLog/EncMap rows.
- [ ] MethodSemantics/Property/Event/Maps: accessor adds wire MethodSemantics + Property/Event + Map rows; body-only accessor edits don’t add maps. Tests for property/event add/edit.
- [ ] CustomAttribute: parent-sorted rows; new attributes appended after existing parent group; EncLog/EncMap include CA rows; value blobs correct. Include NullableContext/Async attributes scenarios.
- [ ] MethodImpl: added interface/override implementations append rows with correct Class/MethodBody/MethodDecl tokens; EncLog/EncMap include. Test interface-impl add scenario.
- [ ] Field/FieldRVA: appended Field rows (and FieldRVA when RVA present); validate TypeDef field range intact; RVA points into delta data.
- [ ] TypeSpec/GenericParam/Constraints: new instantiations add TypeSpec rows; generic param changes rejected (rude edit). Test new closed generic use; ensure no spurious growth on body-only edits.

## Section 5 – IL Delta Stream
- [ ] Validate fat/tiny headers and RVAs per method; EH sections well-formed; mdv /il+ matches MethodDef RVA.
- [ ] Ensure IL tokens reuse baseline tokens for unchanged refs; new tokens correspond to added rows (TypeSpec/MemberRef/etc.). Add token-reuse tests.
- [ ] Confirm StandAloneSig tokens in headers match emitted StandAloneSig rows; locals edits create new sigs only when needed.

## Section 6 – PDB / Debug Delta
- [ ] PDB EncLog/EncMap identical to metadata EncLog/EncMap; add comparison test.
- [ ] Updated methods include MethodDebugInformation/sequence points; unchanged methods omitted. Assert documents stable unless new files introduced.
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
