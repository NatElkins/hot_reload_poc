# Hot Reload Delta Spec

## 1. Overview and Delta Artifacts
**What a .NET hot-reload delta contains**
- Three parallel byte streams per generation:
  1) **Metadata delta**: ECMA #~/#- root with tables/heaps containing only appended/updated rows plus EncLog/EncMap.
  2) **IL delta**: concatenation of updated/added method bodies; MethodDef.RVA in the delta points into this blob.
  3) **PDB delta (optional but expected for debugging)**: Portable PDB metadata delta with EncLog/EncMap and updated MethodDebugInformation.
- Identity chaining: Module row carries `Generation`, `EncId`, `EncBaseId`; Guid heap grows by exactly one GUID per generation (the EncId).
- Handles/offsets: All heap starts in the delta are absolute (baseline heap lengths); tokens inside metadata/IL/PDB are absolute to the post-apply metadata.
- Enc tables: EncLog records add/update operations; EncMap lists all updated/added definition tokens in table/RID order. Both must appear in metadata (and match the PDB copy).

**High-level emission flow (Roslyn)**
- Start from `EmitBaseline` with table/heap sizes and handle maps.
- Collect symbol changes (`DefinitionMap`/`SymbolChanges`); build per-table indices seeded with baseline row counts.
- Emit metadata delta via `DeltaMetadataWriter` (tables, heaps, EncLog/EncMap); emit IL bodies via `IlDeltaStreamBuilder`; emit PDB delta via `PdbDeltaBuilder`.
- Update `EmitBaseline` for the next generation (table sizes, heap lengths, EncId chain, added/changed methods).

**Runtime apply flow (CoreCLR)**
- `MetadataUpdater.ApplyUpdate` → `EditAndContinueModule::ApplyEditAndContinue`:
  - Merge metadata via `IMDInternalImportENC.ApplyEditAndContinue`.
  - Copy IL delta to RVA-backed memory; patch method desc RVAs.
  - Optionally load PDB delta for debugger consumption.

**Minimal correctness checklist per generation**
- Module row: correct Generation/EncId/EncBaseId; Name/Mvid unchanged.
- Heap starts = baseline lengths; row 0 entries present for #Strings/#Blob/#US.
- EncLog/EncMap present and consistent; Param rows include Seq#0; MethodDef ParamList non-zero.
- Table additions align with edits (TypeRef/MemberRef/TypeSpec/StandAloneSig/CustomAttribute/MethodImpl/etc. as needed).
- IL tokens resolve to existing/additional rows; StandAloneSig tokens match locals; Fat/Tiny headers per ECMA; EH clauses well-formed.
- PDB EncLog/EncMap identical to metadata; MethodDebugInformation present for updated methods.

## 2. Metadata Container and Heaps
### 2.1 Stream header and heap offsets
This section captures the *exact binary layout* of the metadata root + stream headers and the heap offset rules that every delta must follow.

**ECMA baseline (Partition II §24.2 & §24.2.3)**
- Metadata root (relative to start of the metadata blob):
  - `0x00` (4 bytes) **Signature** = `0x424A5342` (`"BSJB"` little‑endian).
  - `0x04` (2) **MajorVersion**; `0x06` (2) **MinorVersion**; both shall be `0x0001`.
  - `0x08` (4) **Reserved** = `0`.
  - `0x0C` (4) **VersionStringLength** = *m* (bytes), including the terminating 0.
  - `0x10` (m bytes, 4‑byte padded) **VersionString** (ASCII, e.g., `"v4.0.30319\0"`).
  - Next 2 bytes **Flags** (reserved, zero), next 2 bytes **Streams** (count *s*).
  - Followed by *s* **StreamHeaders**, each:
    - `Offset` (4): byte offset from metadata root to stream payload.
    - `Size`   (4): byte length of that stream (4‑byte aligned).
    - `Name`   (ASCII, null‑terminated, padded to 4 bytes). Legal names: `#~`, `#-`, `#Strings`, `#US`, `#Blob`, `#GUID`. At most one of each.
- Heap physical encodings (also §24.2.3):
  - **#Strings**: ASCII, null‑terminated; index = byte offset from heap start; index 0 is empty string.
  - **#US**: compressed‑uint length (UTF‑16 byte count + the trailing “kind” byte), UTF‑16 payload, 1 trailing byte reserved (currently 0 or 1 for special null handling); index 0 is empty user string.
  - **#Blob**: compressed‑uint length, raw bytes; index 0 is zero‑length blob.
  - **#GUID**: 16‑byte fixed entries; index 0 is “no GUID”.
  - All heap starts are recorded in the metadata header as the stream offsets; handles are simple byte offsets from the start of the corresponding heap stream.

**Roslyn implementation confirmation**
- `DeltaMetadataWriter.MakeTablesBuilder` seeds the per‑heap start offsets from the baseline lengths: `stringHeapStartOffset = previousGeneration.StringStreamLength`, `blobHeapStartOffset = previousGeneration.BlobStreamLength`, `guidHeapStartOffset = previousGeneration.GuidStreamLength`, `userStringHeapStartOffset = Math.Min(UserStringHeapCapacity, previousGeneration.UserStringStreamLength)` (Roslyn file `src/Compilers/Core/Portable/Emit/EditAndContinue/DeltaMetadataWriter.cs`, lines ~67–94). This preserves absolute handles for baseline rows and appends new literals after them.
- The metadata root + stream headers are emitted by the shared `MetadataWriter` base (`src/Compilers/Core/Portable/PEWriter/MetadataWriter.cs`), so deltas use the same physical layout as full builds.
- Roslyn test `EditAndContinueTests.DeltaHeapsStartWithEmptyItem` asserts that every delta still contains the row‑0 entries for `#Strings`, `#Blob`, `#US` (empty string/blob/user‑string) — proving Roslyn writes the heaps per ECMA (§24.2.3) even when emitting only a delta.

**Runtime enforcement**
- CoreCLR `EditAndContinueModule::ApplyEditAndContinue` (`runtime/src/coreclr/vm/encee.cpp`) passes the delta blob directly to `IMDInternalImportENC::ApplyEditAndContinue`. The loader expects the stream offsets in the root to *already* include prior heap lengths; it does not recompute or fix them. If a delta re-baselines the offsets to zero, handle translation fails and `MetadataUpdater.ApplyUpdate` returns `HR=0x80131509`.

**Concrete byte example (method-body‑only delta)**
- Suppose baseline heap sizes: `Strings=0x65`, `US=0x12`, `Blob=0xA8`, `GUID=0x10`.
- The delta metadata root will encode stream headers like:
  - `#~`: `Offset=0x30`, `Size=<tables size>` (aligned).
  - `#Strings`: `Offset=0x30 + tablesSizeAligned`, `Size=<delta strings>`; first usable handle is 0x65 because the start offset equals the baseline length.
  - `#Blob`: next aligned offset; first usable handle 0xA8.
  - `#GUID`: next aligned offset; first usable handle 0x10.
  - `#US`: next aligned offset; first usable handle 0x12.
  - Row 0 entries remain present; the first non‑empty string handle emitted in the delta is 0x65 + deltaOffset.

**How to self‑check with mdv**
- Command: `cd metadata-tools && dotnet run --project src/mdv/mdv.csproj --framework net10.0 -- <baseline.dll> "/g:<delta.meta>;<delta.il>" /stats+ /md+`
- Inspect `#Str/Blob/US/Guid` blocks: starting offsets should match baseline heap sizes; entry 0 should be empty.

**Roslyn/runtime tests touching this area**
- Roslyn: `EditAndContinueTests.DeltaHeapsStartWithEmptyItem`.
- Runtime: loader relies on `IMDInternalImportENC` (no explicit unit test; behavior observed via `MetadataUpdater.ApplyUpdate` failures when offsets are wrong).

### 2.2 String/Blob/UserString/Guid heap rules
**ECMA ground truth (Part II §24.2.3; Table intro §22)**
- All four heaps begin with a required “row 0” entry that represents “no value.” Any handle with index 0 (offset 0 for String/Blob/UserString; Guid = 0) is interpreted as empty.
- **#Strings** (UTF‑8, null‑terminated)
  - Physical layout: sequence of `bytes + 0x00` entries; no length prefix. Handle = byte offset from start of #Strings stream.
  - Alignment: none inside the heap; only the stream header is 4‑byte aligned.
- **#Blob** (arbitrary bytes)
  - Each entry: `CompressedUnsignedInteger length` followed by that many bytes. Handle = byte offset to the length prefix.
  - CompressedUnsignedInteger encoding (ECMA §23.2): 1, 2, or 4 bytes depending on top bits (00, 01, 11 patterns).
- **#US** (user strings, UTF‑16)
  - Each entry: `CompressedUnsignedInteger byteLengthIncludingTerminator` + UTF‑16LE payload + 1 trailing byte “kind” (bit 0 may mark special null handling; usually 0). Handle = byte offset to the length prefix.
  - Length is in **bytes**, not characters.
- **#GUID**
  - Sequence of 16‑byte records; handle index N refers to the N‑th GUID (1‑based); index 0 = no GUID. Stream itself is 4‑byte aligned by virtue of the metadata stream header.
- Handles inside tables are offsets (Strings/Blob/US) or 16‑byte indexes (Guid); coded indexes that can point to heaps (e.g., StringHandle) store these offsets directly. Size of each handle is decided by the table header heap-size bits (from the `#~/#-` header).

**Delta-specific rules (absolute suffixing)**
- Delta heap starts are the **baseline heap lengths** (captured from generation 0). New entries are appended after that offset; reused baseline handles keep their original offset (e.g., a baseline string at 0x2A remains handle 0x2A in all later generations).
- The delta streams must not re-emit baseline bytes; only the suffix is present, but the header offsets make handles absolute when interpreted by the runtime.
- Row 0 empties **must be present** in the delta heaps as well, so handle 0 remains valid when the delta blob is opened standalone (Roslyn enforces this via `DeltaHeapsStartWithEmptyItem`).

**Roslyn implementation checkpoints**
- `DeltaMetadataWriter.MakeTablesBuilder` seeds `stringHeapStartOffset`, `blobHeapStartOffset`, `guidHeapStartOffset`, `userStringHeapStartOffset` from the previous generation lengths, ensuring absolute handles (see `roslyn/src/Compilers/Core/Portable/Emit/EditAndContinue/DeltaMetadataWriter.cs`).
- `MetadataBuilder` (shared PE writer in `roslyn/src/Compilers/Core/Portable/PEWriter/MetadataWriter.cs`) writes the required row‑0 entries for #Strings, #Blob, and #US even in deltas; Guid row 0 is implicit (index 0).
- String/Blob handle sizes are taken from the ENC header heap-size bits, which Roslyn forces to “large” for ENC so offsets remain 32‑bit when needed (covered by multiple Roslyn ENC heap tests).

**Runtime consumption**
- CoreCLR metadata updater (`IMDInternalImportENC::ApplyEditAndContinue` reachable via `src/coreclr/vm/encee.cpp`) assumes heap offsets are absolute. It does not prepend baseline heaps; it reads handles using the delta’s stream offsets straight from the metadata root. Missing row‑0 entries or zeroed offsets will misindex into the heap and cause `ApplyUpdate` to fail.

**Concrete numeric example**
- Baseline sizes: `#Strings=0x65`, `#Blob=0xA8`, `#US=0x12`, `#GUID=0x10`.
- Delta adds one new string `"abc"` (ASCII): emit bytes `61 62 63 00` in #Strings; heap payload size = 4 bytes. The first non-empty string handle in the delta = baseline start 0x65. In a table, that handle is encoded as (offset) `0x00000065` because ENC keeps 32‑bit string indexes.
- Delta adds one new blob of 3 bytes `01 02 03`: emit length prefix `83 00 00 03`? (correct is `0x83 0x...` wait compressed). For length 3 the compressed uint is `0x03` (1 byte). Blob data = `01 02 03`; total 4 bytes. Blob handle = baseline start 0xA8.
- Delta adds one new user string `"X"`: length in bytes incl. terminator = 2 (UTF‑16 char) +1(kind)=3; compressed length = `0x03`; payload `58 00` + `00` kind; handle = baseline start 0x12.
- GUID heap unchanged: first usable GUID index remains 1 at offset 0x10; delta typically carries no new GUIDs unless a new EncId is emitted (Module table).

**Tests covering this behavior**
- Roslyn: `EditAndContinueTests.DeltaHeapsStartWithEmptyItem` validates row‑0 empties survive.
- Roslyn ENC heap-size forcing: multiple `FSharpDeltaMetadataWriterTests` analogues in this repo cover “ENC-sized indexes” (strings/blobs remain large); Roslyn C# equivalents live in `EditAndContinueTests` (heap size and index-size assertions).
- Runtime: no direct unit test, but any malformed handles surface as `MetadataUpdater.ApplyUpdate` failures (HR 0x80131509).
### 2.3 EncId/EncBaseId and Module metadata
**ECMA ground truth (Part II §22 Module table)**
- Module table columns (in order):
  1. `Generation` (UInt16)
  2. `Name` (String heap offset) – module logical name
  3. `Mvid` (Guid heap index) – module version ID
  4. `EncId` (Guid heap index) – *Edit-and-Continue ID for this generation*
  5. `EncBaseId` (Guid heap index) – *ID of the generation on which this delta is based*
- Rules:
  - For non-ENC metadata, `Generation = 0`, `EncId = 0`, `EncBaseId = 0` (zero = “no GUID”).
  - For ENC delta metadata, `Generation` is the delta ordinal (>0), `EncId` is a new GUID uniquely identifying the delta, and `EncBaseId` is the `EncId` of the generation immediately before it (forms a chain).
  - Mvid must stay the same across generations; Name is unchanged.
  - Guid handles are 1-based indexes into #GUID; handle 0 means “null”. With delta heap offsets set to baseline length, the new EncId is at index `baselineGuidCount + currentDeltaIndex`.

**Roslyn implementation checkpoints**
- `DeltaMetadataWriter` overrides:
  - `Generation` = `_previousGeneration.Ordinal + 1`.
  - `EncId` = newly supplied GUID for this delta (ctor parameter).
  - `EncBaseId` = `_previousGeneration.EncId` (Guid of prior generation, or Guid.Empty for the first delta).
- Module row emission: `MetadataWriter.PopulateModuleTableRow` (`roslyn/src/Compilers/Core/Portable/PEWriter/MetadataWriter.cs`):
  - `mvid` taken from `module.SerializationProperties.PersistentIdentifier` (the baseline MVID) and added to the Guid heap once; Guid heap start offset comes from baseline lengths, so the handle equals `baselineGuidCount` index.
  - Adds module row with `generation`, `moduleName` (String handle reused from baseline), `mvid`, `encId`, `encBaseId`.
- Resulting Guid heap growth: every delta appends exactly one new GUID entry (the delta’s EncId); EncBaseId reuses prior EncId handle, so no duplicate.

**Runtime consumption**
- CoreCLR ENC writer (`CMiniMdRW::SetOption` in `runtime/src/coreclr/md/enc/metamodelrw.cpp`) allocates a new GUID for `EncId` when the metadata emitter is in ENC update mode; the commented code shows intended propagation of prior `EncId` into `EncBaseId` (Roslyn already supplies both, so CLR accepts them).
- `EditAndContinueModule::ApplyEditAndContinue` (`src/coreclr/vm/encee.cpp`) does not rewrite Module rows; it trusts the EncId/EncBaseId chain in the delta. Wrong chaining or missing GUID entries will cause the CLR metadata merge to misidentify generations and can fail apply.

**Concrete numeric example (chain across 2 deltas)**
- Baseline module row (Generation=0):
  - Name handle = `0x00000020` (string “DemoTarget”), Mvid handle = Guid index `1`, EncId = 0, EncBaseId = 0.
  - Guid heap size = 16 bytes (one GUID entry).
- Delta 1:
  - Generation = 1; EncBaseId = 0; EncId = new GUID G1.
  - #GUID stream starts at offset 0x10 (baseline size); new entry G1 is written at offset 0x10 ⇒ Guid index 2.
  - Module row in delta: `Generation=0x0001`, `Name` = same handle `0x20`, `Mvid` = index 1, `EncId` = index 2, `EncBaseId` = 0.
- Delta 2:
  - Generation = 2; EncBaseId = handle 2 (G1); EncId = new GUID G2 at offset 0x20 ⇒ Guid index 3.
  - Module row: `Generation=0x0002`, `EncId`=3, `EncBaseId`=2; Name/Mvid unchanged.

**Handle sizing**
- Because ENC forces “big” index sizes, Guid handles in delta tables are encoded as 32-bit indexes when necessary; the heap-size bits in the `#~/#-` header reflect the baseline Guid heap size. Roslyn sets these through the shared `MetadataBuilder` initialized with `guidHeapStartOffset = previousGeneration.GuidStreamLength`.

**Tests / parity checks**
- Roslyn C#: implicit coverage via `EmitDifference` flows; chain exercised in multi-generation tests (e.g., `EditAndContinueTests` multi-delta scenarios; no dedicated Module-row assertion, but deltas would fail to apply if chain broke).
- Runtime: `CMiniMdRW::SetOption` ENC branch shows CLR expecting EncId growth per generation; failures surface as `MetadataUpdater.ApplyUpdate` errors if EncId/EncBaseId are malformed.
- Local mdv check: run mdv on consecutive deltas and verify Module table rows show increasing `Generation` and EncId/EncBaseId GUID handles (each delta adds one GUID row).

## 3. EncLog and EncMap Semantics
### 3.1 Operations and ordering
**ECMA ground truth (Part II §22 EncLog, §22 EncMap)**
- **EncLog table schema** (per-row fields, in order):
  1. `Token` (Type: `mdToken`, 4 bytes, little-endian) — points to the metadata entity being updated or added; combines table id (high byte) + row number (low 3 bytes).
  2. `FuncCode` (Type: `USHORT`, 2 bytes) — Edit operation code. ECMA defines `0` = default/update, `1` = add; other codes reserved. Rows are not sorted; the order emitted is preserved.
- **EncMap table schema**:
  - Single column: `Token` (`mdToken`, 4 bytes). Contains the handles of all definitions emitted in the delta, ordered by the row id within their tables (ascending within each table, tables appear in the order emitted by the compiler).
- Required invariants:
  - Every definition row added or updated must appear in both EncLog and EncMap.
  - EncLog may include reference rows (e.g., MemberRef) when the compiler needs to signal the runtime; EncMap typically includes only *definition* rows (though ECMA permits any handle).
  - Tokens are absolute: table id + row number as they will exist after applying the delta (baseline rows retain their baseline numbers).

**Roslyn implementation checkpoints**
- EncLog emission:
  - `DeltaMetadataWriter` builds EncLog via `DefinitionMap` and per-table writers; for each added/updated definition it logs `FuncCode=0` (update) or `1` (add) with the final token. Iterator/async cases add Param rows (Seq#0) and MethodDef rows; see `roslyn/src/Compilers/Core/Portable/Emit/EditAndContinue/DeltaMetadataWriter.cs` and helpers.
- EncMap emission:
  - Built from the same added/updated definitions; ordering mirrors table order and row ids. Accessed in tests with `GetEditAndContinueMapEntries()`.
- Test evidence:
  - `src/Compilers/CSharp/Test/Emit2/Emit/EditAndContinue/EditAndContinueTests.cs` and `EditAndContinueStateMachineTests.cs` use helpers `CheckEncLogDefinitions` / `CheckEncMapDefinitions`. Typical iterator add test expects:
    - EncLog: `[ (MethodDef,row?,Add), (Param,row1,Default) ]`
    - EncMap: `[ Param:1, MethodDef:token ]`
  - These tests prove Roslyn always appends Param rows and logs them when a method is added/updated.

**Runtime consumption**
- CoreCLR metadata merge (ENC writer) reads EncLog/EncMap to know which rows to copy/patch. Reader is in metadata ENC pipeline (`IMDInternalImportENC` inside `metamodelrw.cpp` / `enc` paths); it relies on tokens being absolute and FuncCode specifying add vs. update. Wrong tokens or missing rows lead to `ApplyUpdate` failure.

**Concrete numeric example (iterator method added)**
- Baseline: MethodDef count = 10, Param count = 20.
- Delta adds new iterator `C.<Iter>d__0.MoveNext`:
  - New Param row for return: row 21 (Seq#0).
  - New MethodDef row: row 11.
  - EncLog rows (in emission order):
    - `(Token=0x08000015, FuncCode=1)` → Param table (0x08) row 21, add.
    - `(Token=0x0600000B, FuncCode=1)` → MethodDef table (0x06) row 11, add.
  - EncMap rows (ordered):
    - `Param 0x08000015`
    - `MethodDef 0x0600000B`
- mdv will display:
  - EncLog:
    ```
    Row 1: Table=Param (0x08), Function=Add (1), Token=0x08000015
    Row 2: Table=MethodDef (0x06), Function=Add (1), Token=0x0600000B
    ```
  - EncMap:
    ```
    Row 1: 0x08000015
    Row 2: 0x0600000B
    ```

**Handle sizing**
- Tokens are always 4-byte (mdToken) regardless of heap-bit compression; ENC keeps table indexes “big” to avoid overflow in multi-gen scenarios.

**How to verify quickly**
- `cd metadata-tools && dotnet run --project src/mdv/mdv.csproj --framework net10.0 -- <baseline.dll> "/g:<delta.meta>;<delta.il>" /md+`
- Check EncLog/EncMap sections match the expected rows for the edit (method updates should show Param + MethodDef with correct FuncCode).

**Relevant Roslyn/runtime files**
- Roslyn: `DeltaMetadataWriter.cs`, `DefinitionMap.cs`, `SymbolMatcher.cs`, test helpers in `EditAndContinueTestBase.cs` (`CheckEncLog*`, `CheckEncMap*`).
- Runtime: metadata ENC reader/writer in `src/coreclr/md/enc/metamodelrw.cpp` (reads EncLog/EncMap tokens during merge), applied via `IMDInternalImportENC` from `src/coreclr/vm/encee.cpp`.
### 3.2 Token translation rules
**ECMA ground truth (Part II §22 EncMap)**
- EncMap table has one column: `Token` (`mdToken`, 4 bytes). Tokens are full metadata handles (table id << 24 | rowId).
- Ordering: entries are sorted first by table id in ascending order *as they appear in the metadata stream* and then by row id. ECMA notes that EncMap provides “the set of all updated or new metadata table rows ordered by (Table, RID).”
- Coverage: the EncMap should include *every definition row* that is updated or added in the delta. Reference rows are optional.

**Roslyn implementation checkpoints**
- The EncMap is populated from the same added/updated definitions the writer includes in EncLog. Roslyn emits them in table order with ascending row ids, which matches ECMA’s ordering requirement.
- When only method bodies change (no metadata shape change), Roslyn still emits the relevant MethodDef row into EncLog/EncMap to mark the updated RVA (via AddedOrChangedMethodInfo). Tests such as iterator/async edits prove Param + MethodDef appear together in EncMap.
- Token translation across generations is handled by Roslyn’s `MetadataAggregator` (not in this repo, but used by `EmitBaseline`) which maps deltas back to baseline tokens. EncMap tokens are already absolute for the target generation, so downstream consumers (mdv/runtime) read them directly.

**Runtime consumption**
- CoreCLR ENC merge reads EncMap to know which rows in the delta correspond to metadata definitions; it then uses EncLog to determine whether to append or update. Because tokens are absolute (include table id and target RID), the runtime can place incoming rows correctly without recomputing RID positions. Wrong ordering would only affect diagnostics, but missing tokens causes failed applies.

**Concrete numeric example (update-only edit)**
- Baseline: MethodDef rows = 10. Delta updates MethodDef row 5 (body change) and adds no other rows.
- EncMap contents (sorted): one entry `0x06000005`.
- EncLog entry: `(Token=0x06000005, FuncCode=0)` update.
- mdv will show:
  ```
  EncMap
    Row 1: 0x06000005
  EncLog
    Row 1: Table=MethodDef, Function=Update, Token=0x06000005
  ```

**How to verify**
- `dotnet run --project metadata-tools/src/mdv/mdv.csproj --framework net10.0 -- <baseline.dll> "/g:<delta.meta>;<delta.il>" /md+`
- Confirm EncMap rows are sorted by table then RID, and all updated/added definition tokens appear.

**Tests touching this**
- Roslyn C#: `EditAndContinueTests` and `EditAndContinueStateMachineTests` use `CheckEncMapDefinitions` to assert expected tokens/order; iterator add/update scenarios validate Param + MethodDef ordering.
- Runtime: implicitly exercised by `MetadataUpdater.ApplyUpdate`; malformed EncMap causes apply failure.

## 4. Table-by-Table Delta Rules
### 4.1 Module
**ECMA ground truth (Part II §22 “Module” table)**
- Columns (in order): `Generation` (UInt16), `Name` (String heap), `Mvid` (Guid), `EncId` (Guid), `EncBaseId` (Guid).
- Rules:
  - Exactly one row for a module; multi-module assemblies unsupported in Hot Reload.
  - `Generation` must be 0 for baseline, >0 for deltas.
  - `Name` must stay identical across generations (same String handle).
  - `Mvid` must stay identical across generations (same Guid handle); changing it is invalid.
  - `EncId` and `EncBaseId` chain generations: Gen1 sets `EncId=G1`, `EncBaseId=0`; Gen2 sets `EncId=G2`, `EncBaseId=G1`, etc. EncBaseId references the prior EncId.
  - Guid handle 0 means “no GUID”; Baseline EncId/EncBaseId are 0.

**Roslyn implementation checkpoints**
- `MetadataWriter.PopulateModuleTableRow` (`roslyn/src/Compilers/Core/Portable/PEWriter/MetadataWriter.cs`, ~2725):
  - Uses `this.Generation` (overridden to 0 in full writer, N in `DeltaMetadataWriter`).
  - `module.ModuleName` string handle is reused; Roslyn captures module name early and adds it via `GetOrAddString`.
  - `Mvid` taken from `module.SerializationProperties.PersistentIdentifier`; preserved across deltas.
  - `EncId` = new GUID passed into `DeltaMetadataWriter`; `EncBaseId` = previous generation’s `EncId`.
  - Guid heap start offset comes from baseline lengths (`MakeTablesBuilder`), so handles remain absolute; only one new Guid per delta (EncId).
- Capacity: Roslyn enforces a single Module row; adding more would violate ECMA and is not emitted.

**Runtime expectations**
- CoreCLR ENC metadata writer (`CMiniMdRW::SetOption` in `runtime/src/coreclr/md/enc/metamodelrw.cpp`) allocates a new EncId for ENC updates and stores it in the Module row; Roslyn pre-fills EncId/EncBaseId and the CLR accepts them.
- `EditAndContinueModule::ApplyEditAndContinue` (`src/coreclr/vm/encee.cpp`) does not rewrite the Module table; it relies on the supplied row being consistent. Wrong chaining (EncBaseId not equal to prior EncId) or changed Mvid/Name can make the delta invalid.

**Concrete numeric example (Gen1)**
- Baseline Module row:
  - `Generation=0x0000`
  - `Name` = String handle `0x00000020` (“DemoTarget”)
  - `Mvid` = Guid handle `0x00000001` (guid at offset 0x00 in #GUID)
  - `EncId=0`, `EncBaseId=0`
- Delta 1 metadata header sets #GUID start offset to baseline size (0x10). Add new Guid G1 at offset 0x10 → Guid handle `0x00000002`.
- Delta Module row (in the delta tables):
  - `Generation=0x0001`
  - `Name` = `0x00000020` (reused)
  - `Mvid` = `0x00000001` (reused)
  - `EncId` = `0x00000002` (G1)
  - `EncBaseId` = `0x00000000`
- mdv will display Module: `Generation=1`, `Name="DemoTarget"`, `Mvid=<guid1>`, `EncId=<guid2>`, `EncBaseId=00000000-0000-0000-0000-000000000000`.

**Handle sizing**
- ENC forces large index sizes; Guid and String handles are encoded per heap-size bits in the `#~/#-` header. With baseline GUID count ≥1, Guid indexes are at least 1 byte; ENC keeps them 32-bit-safe.

**Tests / verification**
- Roslyn: `EditAndContinueTests.Delta_AssemblyDefTable` confirms AssemblyDef is absent in deltas, implicitly verifying Module remains and is single-row; multi-gen ENC flows depend on valid Module chaining.
- Runtime: ENC helper in `metamodelrw.cpp` actively sets EncId for ENC; a bad chain leads to `ApplyUpdate` failure.
- mdv check: first delta of any edit should show Module row with Generation=1, EncId set, EncBaseId zero (or prior EncId for later gens).
### 4.2 TypeDef and NestedClass
**ECMA ground truth (Part II §22 TypeDef & NestedClass)**
- **TypeDef table columns (in order)**: 
  1. `Flags` (UInt32, `TypeAttributes`)
  2. `TypeName` (String)
  3. `TypeNamespace` (String)
  4. `Extends` (TypeDefOrRef coded index)
  5. `FieldList` (Simple index into Field table) — points to the first field of this type; last field of the type is *before* the next type’s FieldList.
  6. `MethodList` (Simple index into MethodDef table) — similar rule for methods.
- Ordering rule: TypeDef rows are sorted by increasing RID; FieldList/MethodList provide the ranges. A new TypeDef typically appends to the table; its FieldList/MethodList must be ≥ previous type’s list values and ≤ (next type’s list or end+1).
- **NestedClass table columns**: `NestedClass` (TypeDef handle of nested type), `EnclosingClass` (TypeDef handle of containing type). Each nested type must also appear as a TypeDef row; ordering not constrained but RIDs must be valid.

**Roslyn implementation checkpoints**
- Type discovery and change tracking: `DeltaMetadataWriter` builds `_typeDefs` via `Changes`/`DefinitionMap`. For `SymbolChange.Added` or `Updated`, it assigns new row ids (`_typeDefs.GetRowId`) and records updates in EncLog/EncMap.
- FieldList/MethodList wiring: `GetFirstFieldDefinitionHandle` / `GetFirstMethodDefinitionHandle` override uses `_fieldDefs.GetRowId` / `_methodDefs.GetRowId`; for added types, the first field/method row id reflects the first field/method emitted for that type in the delta (or previous rows if none added).
- Nested types: nested definitions are visited via `Visit(INestedTypeDefinition)`; the NestedClass map is populated so that for any new nested type, a NestedClass row is emitted with `NestedClass = nested TypeDef token`, `EnclosingClass = parent TypeDef token`.
- EncLog/EncMap: `PopulateEncLogTableRows(_typeDefs, TableIndex.TypeDef)` ensures TypeDef adds/updates are logged; NestedClass rows are also included through the general table-writer path.

**Runtime expectations**
- CoreCLR applies TypeDef and NestedClass rows as-is; new types are appended. The runtime requires consistent FieldList/MethodList ranges; incorrect ranges cause metadata import failures during `ApplyEditAndContinue`.
- NestedClass rows must reference valid TypeDef tokens; otherwise, runtime type loading for nested additions fails when `JitUpdatedFunction` resolves the declaring type.

**Concrete numeric example (add nested type)**
- Baseline: TypeDef count = 10; Field count = 20; MethodDef count = 30.
- Add nested type `C+N` with one method and no fields:
  - New MethodDef row assigned RID 31 (since baseline had 30).
  - New TypeDef row assigned RID 11 with:
    - `Flags` = `0x00000001` (not public, nested private, etc., per source)
    - `TypeName` = handle for `"N"`
    - `TypeNamespace` = handle `0` (nested types have empty namespace)
    - `Extends` = TypeRef for `System.Object`
    - `FieldList` = `0x00000015`? Wait: first field; since no fields, FieldList points to next available field RID = 21 (end+1 sentinel).
    - `MethodList` = `0x0000001F` (31 decimal) — first method of this type.
  - NestedClass row: `(NestedClass = 0x0200000B, EnclosingClass = 0x02000002)` if `C` is TypeDef RID 2.
  - EncLog rows include `(TypeDef 0x0200000B, Add)`, `(MethodDef 0x0600001F, Add)`, `(Param row for return, Add)`, `(NestedClass 0x2900000B, Add)`.
  - EncMap entries (sorted) include tokens for Param, MethodDef, TypeDef, NestedClass.

**Handle sizing**
- FieldList/MethodList are simple indexes; under ENC, Roslyn keeps index sizes “big” (32-bit) by setting heap-size bits accordingly, avoiding overflow as counts grow.

**Tests / verification**
- Roslyn C#: `EditAndContinueTests.AddType` and nested-type scenarios use `CheckEncLogDefinitions`/`CheckEncMapDefinitions` to assert TypeDef + NestedClass rows appear with correct ordering.
- Roslyn VB tests (closure/nested) provide additional EncLog/NestedClass coverage.
- mdv check: run mdv on a delta that adds a nested type; ensure TypeDef table grew by 1, NestedClass table has the new pair, FieldList/MethodList point to the new ranges, EncLog/EncMap contain TypeDef + NestedClass + members.
### 4.3 TypeRef / MemberRef / AssemblyRef
**ECMA ground truth (Part II §22 tables)**
- **TypeRef** columns: `ResolutionScope` (coded index: Module/ModuleRef/AssemblyRef/TypeRef), `TypeName` (String), `TypeNamespace` (String). Each row identifies a type not defined in the current module.
- **MemberRef** columns: `Class` (coded index: TypeDef/TypeRef/TypeSpec/ModuleRef/MethodDef), `Name` (String), `Signature` (Blob). Used for member references to external or generic instantiations.
- **AssemblyRef** columns: `MajorVersion, MinorVersion, BuildNumber, RevisionNumber` (UInt16 each), `Flags` (UInt32), `PublicKeyOrToken` (Blob), `Name` (String), `Culture` (String), `HashValue` (Blob). Identifies referenced assemblies.
- Handles are simple indexes into their respective tables; coded indexes use the ECMA encoding with tag bits in the low bits (see §24.2.6, §II.23.2 for compressed integers).

**Delta rules**
- Deltas may append new TypeRef/MemberRef/AssemblyRef rows as needed by newly emitted IL or signatures.
- No updates-in-place: references are always additions; existing rows are reused when structurally identical (same scope/name/signature).
- Tokens are stable across generations: a reused TypeRef keeps its original token; newly added rows get the next RID.
- EncLog/EncMap usually do **not** list reference rows (Roslyn omits them from definition-only checks); they can be present but are not required by ECMA for references.

**Roslyn implementation checkpoints**
- Reference indices are rehydrated from baseline sizes:
  - `_typeRefIndex = new TypeReferenceIndex(this, lastRowId: sizes[TypeRef])`
  - `_memberRefIndex = new InstanceAndStructuralReferenceIndex<ITypeMemberReference>(..., lastRowId: sizes[MemberRef])`
  - `_assemblyRefIndex = new HeapOrReferenceIndex<AssemblyIdentity>(..., lastRowId: sizes[AssemblyRef])`
  - All in `DeltaMetadataWriter` constructor (`roslyn/src/Compilers/Core/Portable/Emit/EditAndContinue/DeltaMetadataWriter.cs`, top section).
- When emitting IL/signatures, Roslyn calls `GetOrAddTypeReferenceHandle`, `GetOrAddMemberReferenceHandle`, `GetOrAddAssemblyReferenceHandle`, which:
  - Reuse existing rows if structurally equal to a baseline row.
  - Append new rows and assign consecutive RIDs when a new reference shape appears.
- Table sizes in the delta are set to the *added* count for these tables (`GetDeltaTableSizes`), and EncLog rows for references are emitted via `PopulateEncLogTableRows(TableIndex.TypeRef/MemberRef/AssemblyRef, ...)` when rows are added.
- Signatures for MemberRef are emitted into #Blob; string names into #Strings. Handle sizes stay “big” in ENC (index-size bits).

**Runtime expectations**
- CoreCLR metadata merge treats new reference rows as append-only; tokens are translated through the ENC metadata aggregator. Incorrect reuse (e.g., duplicating an existing TypeRef instead of reusing it) increases table size but normally still applies; however, missing references needed by IL would fail verification/JIT.
- Token remapping during IL translation relies on consistent RID stability across generations; reusing reference rows avoids token churn.

**Concrete numeric example (method body references System.Console.WriteLine)**
- Baseline already has `AssemblyRef` for `System.Console` and a `MemberRef` for `WriteLine(string)`.
- Delta edits method body only:
  - No new TypeRef/MemberRef/AssemblyRef rows should be added.
  - Table counts in the delta for these tables remain 0; EncLog/EncMap omit reference rows.
- If a new overload is used (e.g., `WriteLine(int)` not previously referenced):
  - Add one MemberRef row:
    - `Class` = TypeRef token for `System.Console` (reused).
    - `Name` = String handle `"WriteLine"`.
    - `Signature` = new Blob handle for `(int32)->void`.
  - MemberRef RID grows by 1 (e.g., baseline 25 → delta RID 26).
  - EncLog includes `(MemberRef 0x0A00001A, Add)`, EncMap may include it (Roslyn often filters references out of definition-only asserts).
  - String/Blob heaps grow by the new signature/name handles (name likely reused; blob new).

**How to verify**
- mdv: `dotnet run --project metadata-tools/src/mdv/mdv.csproj --framework net10.0 -- <baseline.dll> "/g:<delta.meta>;<delta.il>" /md+` and inspect TypeRef/MemberRef/AssemblyRef tables and counts; confirm RIDs only increase when genuinely new references are needed.

**Tests / references**
- Roslyn C#: `EditAndContinueTests` TypeRef/MemberRef reuse scenarios (look for `TypeRefReuse`-style assertions) and iterator/async tests that implicitly check reference stability via mdv comparisons.
- Runtime: general ENC merge in `IMDInternalImportENC` tolerates appended reference rows; correctness is observed via successful `MetadataUpdater.ApplyUpdate`.
### 4.4 MethodDef (RVA, ImplFlags, ParamList)
**ECMA ground truth (Part II §22 “MethodDef”)**
- Columns (in order):
  1. `RVA` (UInt32): relative virtual address of method body (0 if abstract or PInvoke).
  2. `ImplFlags` (UInt16): `MethodImplAttributes`.
  3. `Flags` (UInt16): `MethodAttributes`.
  4. `Name` (String handle).
  5. `Signature` (Blob handle, MethodDefSig).
  6. `ParamList` (Simple index into Param table): points to first Param row for this method; params for a method occupy a contiguous range ending just before the next method’s ParamList or table end.
- Ordering: MethodDef rows are sorted by RID; ParamList values must be non-decreasing and within table bounds.

**Delta rules**
- Updates to an existing method (body-only) still produce a MethodDef row in the delta so the runtime can capture the new RVA and any signature/impl flags that changed. `FuncCode=Update` in EncLog; token is the existing MethodDef token (RID unchanged).
- Adding a method appends a new MethodDef row; ParamList points at the new Param rows (including Seq#0 return param).
- RVA in deltas is the RVA of the new IL body *within the delta IL stream*, not the baseline PE RVA. The runtime patches the method descriptor to the delta RVA.
- ParamList must reference the first Param row (Seq#0 return param) emitted for that method in the delta; if no parameters existed before, a synthesized Seq#0 row must be added so ParamList is not 0.

**Roslyn implementation checkpoints**
- Method change detection: `DeltaMetadataWriter.AddDefIfNecessary` with `SymbolChange.Updated` or `Added` queues MethodDef rows; Added/Changed method bodies are recorded in `_addedOrChangedMethods` with their new IL and debug info.
- ParamList wiring: `GetFirstMethodDefinitionHandle` supplies the starting method RID for MethodList ranges; for parameter rows, `DefinitionMap.GetParameters` feeds `_parameterDefs` and `ParamList` uses the first parameter RID (baseline or newly added Seq#0) via `_firstParamRowMap`.
- RVA/ImplFlags/Signature come from the newly emitted body/signature. Even for body-only edits, Roslyn emits a MethodDef update so the new RVA is recorded.
- EncLog/EncMap: `PopulateEncLogTableRows(_methodDefs, TableIndex.MethodDef)` logs add/update; Param rows are logged separately (see 4.5).

**Runtime expectations**
- CoreCLR ENC (`EditAndContinueModule::UpdateMethod` in `src/coreclr/vm/encee.cpp`) uses the MethodDef token to locate the method descriptor and patch its code pointer to the new IL RVA from the delta. If ParamList is 0 or invalid, the metadata import fails before JIT patching.
- ImplFlags/Flags are respected; sudden signature shape changes are considered rude edits unless combined add+replace strategies are used.

**Concrete numeric example (body update)**
- Baseline: MethodDef RID 5 (`C.F`), ParamList = 12 (Seq#0 return param), RVA = 0x2000 in baseline PE.
- Delta (body change only):
  - New IL body placed at offset 0x20 in delta IL stream ⇒ RVA in delta metadata = 0x00000020.
  - MethodDef row in delta table (RID still 5):
    - `RVA=0x00000020`
    - `ImplFlags` unchanged (e.g., `0x0000`)
    - `Flags` unchanged (e.g., `0x0006` private/public bits)
    - `Name` handle reused (baseline string handle)
    - `Signature` handle reused (baseline blob)
    - `ParamList=0x0000000C` (unchanged, points to baseline Seq#0 row)
  - EncLog: `(MethodDef 0x06000005, Update)`
  - EncMap: `0x06000005`
  - Param table in delta gains zero rows (since reusing baseline); valid because ParamList still points to existing row.

**Concrete numeric example (add new method with no parameters)**
- Baseline MethodDef count = 10; Param count = 20.
- New method appended: RID 11.
  - Create Seq#0 Param row RID 21 ⇒ ParamList = 21.
  - MethodDef row:
    - `RVA=<delta IL offset>`
    - `ImplFlags/Flags` per source
    - `Name` = handle for method name
    - `Signature` = new method signature blob
    - `ParamList = 0x00000015`? (decimal 21)
  - EncLog: `(Param 0x08000015, Add)`, `(MethodDef 0x0600000B, Add)`
  - EncMap: Param then MethodDef.

**Handle sizing**
- MethodDef tokens are simple indexes; ENC keeps table index size large enough (set by `#~` header). ParamList is a simple (non-coded) index into Param table and must fit the declared index size; ENC uses the enlarged `big` width.

**Tests / verification**
- Roslyn: iterator/async tests in `EditAndContinueStateMachineTests` and method update tests in `EditAndContinueTests` validate MethodDef + Param rows in EncLog/EncMap; component tests also assert RVA updates.
- mdv: run mdv and check MethodDef table shows updated RVAs and correct ParamList, with EncLog/EncMap entries present for updated/added methods.
- Runtime: failures in ApplyUpdate from bad ParamList or missing MethodDef row manifest as `HR=0x80131509` during `MetadataUpdater.ApplyUpdate`.
### 4.5 Param (Seq#0 return, names, flags)
**ECMA ground truth (Part II §22 “Param”)**
- Columns (in order):
  1. `Flags` (UInt16) — `ParamAttributes` (In, Out, Optional, HasDefault, etc.).
  2. `Sequence` (UInt16) — 0 for return parameter; 1-based for formal parameters in signature order.
  3. `Name` (String handle) — may be 0 (unnamed).
- Ordering: Param rows for a method are contiguous and sorted by Sequence; a method’s ParamList in MethodDef points to its first Param row. Sequence values must be strictly increasing starting at 0 (return) if present.

**Delta rules**
- Every method must have a Seq#0 row when present in the delta (even if it had none before) to keep ParamList valid. Parameterless methods still use a Seq#0 return row.
- Added methods: emit Param rows for Seq#0 and each parameter; EncLog logs both the MethodDef (Add) and Param rows (Default). For new methods Roslyn also logs `MethodDef AddParameter` entries.
- Updated methods (body-only): reuse baseline Param rows when possible; if none existed, synthesize a Seq#0 row so ParamList is non-zero. EncLog logs Param rows with `Default` (update) even if unchanged.
- Names: if reusing an existing parameter, reuse the baseline String handle (no new string in delta). New parameters use new String handles as needed.

**Roslyn implementation checkpoints**
- Parameter discovery: `DefinitionMap.GetParameters` feeds `_parameterDefs`; `_parameterDefList` maps each Param to its MethodDef for EncLog purposes.
- Row IDs: `_parameterDefs` seeded with baseline count; added rows get consecutive RIDs. `_firstParamRowMap` captures the first Param RID per method; used to set MethodDef.ParamList.
- EncLog/EncMap emission (`PopulateEncLogTableParameters` in `DeltaMetadataWriter.cs`):
  - For added methods: emit `AddParameter` EncLog entry for the MethodDef **and** a `Default` entry for each Param row; EncMap gets the Param RIDs.
  - For updated methods: emit `Default` EncLog for each Param row (using existing or new RIDs); EncMap includes Param rows.
- Name reuse: `DeltaMetadataTables.AddParameterRow` chooses existing String handles when provided; recent fixes ensure baseline handles propagate so string heap doesn’t grow for unchanged parameter names.

**Runtime expectations**
- CoreCLR metadata merge uses Param rows to rebuild method signatures and map IL arguments/return types. Missing Seq#0 rows or incorrect ParamList ranges cause metadata import failure before JIT patching.
- Flags/Sequence must match the method signature; mismatches can produce invalid metadata or runtime verification failures.

**Concrete numeric examples**
- *Parameterless method update (baseline had no Param rows)*:
  - Baseline Param count = 20; MethodDef RID 5 has ParamList=0.
  - Delta: add Seq#0 Param row RID 21: `Flags=0`, `Sequence=0`, `Name=0`.
  - MethodDef.ParamList updated to 21.
  - EncLog: `(Param 0x08000015, Default)`; `(MethodDef 0x06000005, Update)` already emitted. EncMap: `Param 0x08000015`, `MethodDef 0x06000005`.
- *Method with two parameters added*:
  - New MethodDef RID 11; Param rows RID 21..23:
    - RID 21: Seq=0 (return), Name=0, Flags=0
    - RID 22: Seq=1, Name="x", Flags=In
    - RID 23: Seq=2, Name="y", Flags=In
  - MethodDef.ParamList=21.
  - EncLog: MethodDef Add; Param rows Default; MethodDef AddParameter (Roslyn emits for new methods). EncMap: Param rows then MethodDef.

**Handle sizing**
- Param simple indexes respect ENC large-index mode; Name is a String handle (offset) and also benefits from 32-bit string index sizing under ENC.

**Tests / verification**
- Roslyn: `EditAndContinueStateMachineTests.AddIteratorMethod` expects Param rows/EncLog/EncMap (Seq#0 + parameters); ctor rewrite tests in `EditAndContinueTests` similarly assert Param presence.
- This repo’s added tests (service + component) ensure Seq#0 rows for parameterless updates and name-handle reuse.
- mdv: check Param table and EncLog/EncMap sections for Seq#0 rows and correct sequencing; ParamList in MethodDef should point to the first Param row.
### 4.6 StandAloneSig (locals, lambdas)
**ECMA ground truth (Part II §22 “StandAloneSig”)**
- Columns: single `Signature` (Blob handle). Each row represents a standalone signature used by IL instructions like `locals` or `calli`.
- Handle: simple index into StandAloneSig table; referenced by IL via `StandAloneSig` tokens (`0x110000xx`).
- Signatures follow ECMA method signature encoding (for locals, use LocalVarSig per §II.23.2.6).

**Delta rules**
- Any change that introduces a new locals signature (e.g., adding a local variable, closure, or state-machine hoisted locals) must add a new StandAloneSig row and corresponding blob.
- Reuse baseline StandAloneSig when the exact signature matches; otherwise append a new row. Token remains stable for reused rows.
- EncLog/EncMap include StandAloneSig rows when added (FuncCode=Add) or, rarely, updated (FuncCode=Default when reused but logged).
- Blob heap grows by the local signature blob; string heap typically unchanged.

**Roslyn implementation checkpoints**
- Indices: `_standAloneSignatureIndex` seeded with baseline row count; `GetOrAddStandaloneSignatureHandle` appends or reuses rows. (DeltaMetadataWriter constructor lines ~120–170 and method emission paths.)
- Emission site: `SerializeLocalVariablesSignature` builds a locals signature blob for each method body; adds EncLocalInfo and records `AddedOrChangedMethodInfo`. The blob is added to #Blob, and a StandAloneSig row is added via `_standAloneSignatureIndex`.
- EncLog: StandAloneSig table included in `PopulateEncLogTableRows(TableIndex.StandAloneSig, ...)`, so added rows appear with `Add` codes; EncMap will list the new StandAloneSig token.
- Handle sizes: ENC keeps blob indexes “big” so StandAloneSig tokens remain valid as tables grow.

**Runtime expectations**
- CoreCLR uses StandAloneSig tokens when relinking IL that references `locals` signatures or `calli`. Missing or mismatched signatures cause IL verification or JIT failures during ApplyUpdate.
- The delta IL must refer to the correct StandAloneSig token (either baseline RID or new RID); token translation assumes stable re-use when possible.

**Concrete numeric example (add one local)**
- Baseline StandAloneSig count = 5.
- Method body gains a new local `int x`:
  - Emit locals signature blob: `0x07 0x01 0x08` (LOCAL_SIG, count=1, I4) — actual bytes depend on encoder; length prefix in Blob heap.
  - Blob handle = baselineBlobSize + offset; new StandAloneSig row RID 6 holding that blob handle.
  - IL method header references token `0x11000006` for `locals`.
  - EncLog: `(StandAloneSig 0x11000006, Add)`; EncMap: `0x11000006`.

**Tests / verification**
- Roslyn C#: `EditAndContinueTests` has `LocalSignature` scenarios (`VerifyLocalSignature`) ensuring new StandAloneSig rows and blobs are emitted when locals change.
- State-machine tests (`EditAndContinueStateMachineTests`) generate locals for hoisted state; implicitly verify StandAloneSig emission.
- mdv: inspect StandAloneSig table; a locals edit should add one row with the expected blob, and EncLog/EncMap should include the StandAloneSig token.
### 4.7 MethodSemantics / Property / Event / Maps
**ECMA ground truth (Part II §22)**
- **PropertyMap**: columns `Parent` (TypeDef), `PropertyList` (Property table index to first property of the type). Ranges work like FieldList/MethodList.
- **Property**: `Flags` (UInt16), `Name` (String), `Type` (Blob — PropertySig per §23.2.5). Properties do not carry bodies; accessors are linked via MethodSemantics.
- **MethodSemantics**: `Semantics` (UInt16 flags: Setter=0x0001, Getter=0x0002, Other=0x0004, AddOn=0x0008, RemoveOn=0x0010, Fire=0x0020), `Method` (MethodDef), `Association` (coded index: HasSemantics → Property/Event).
- **EventMap**: `Parent` (TypeDef), `EventList` (Event table index).
- **Event**: `EventFlags` (UInt16), `Name` (String), `EventType` (TypeDefOrRef coded index).
- Ordering: PropertyMap/EventMap rows appear once per parent type; PropertyList/EventList define contiguous ranges. MethodSemantics rows can be in any order but typically group by association.

**Delta rules**
- Adding a property/event in a delta:
  - Append Property/Event row(s).
  - Ensure PropertyMap/EventMap row exists for the parent TypeDef (added if missing).
  - Add MethodSemantics rows linking accessor MethodDefs to the Property/Event.
  - EncLog entries for Property/Event, MethodSemantics, and the maps; EncMap includes the added definition rows.
- Updating a getter/setter body (no signature change):
  - MethodDef row gets updated (see 4.4); Property/Event rows typically unchanged.
  - MethodSemantics rows are usually unchanged; still listed in EncLog/EncMap only if added/re-emitted (Roslyn may not re-emit on body-only edits).

**Roslyn implementation checkpoints**
- Indices: `_propertyMap`, `_eventMap`, `_propertyDefs`, `_eventDefs`, `_methodDefs` seeded with baseline counts; changes tracked via `SymbolChange`.
- Map creation: during type visit, Roslyn adds map rows if any property/event is present/changed and the map entry didn’t previously exist (`_propertyMap.Add(typeRowId)` / `_eventMap.Add(typeRowId)`).
- MethodSemantics rows are added through `PopulateEncLogTableEventsOrProperties`, which walks properties/events and their accessors, emitting MethodSemantics entries with correct `Semantics` flags and associations.
- EncLog covers maps, properties/events, method semantics; EncMap includes new definition rows (maps + property/event + semantics). Reference rows omitted unless needed.

**Runtime expectations**
- CLR uses MethodSemantics to bind accessors to properties/events for dispatch and reflection. Missing or mismatched associations break property/event invocation after ApplyUpdate.
- PropertyMap/EventMap must keep contiguous ranges; bad `PropertyList/EventList` causes metadata validation errors.

**Concrete numeric example (add auto-property)**
- Baseline: PropertyMap row for TypeDef RID 2 exists; Property table count = 5; MethodDef count = 30.
- Delta adds `int P { get; set; }` to type RID 2:
  - Property RID 6: `Flags=0`, `Name="P"`, `Type` = PropertySig for `int`.
  - Accessor methods: MethodDef RIDs 31 (get_P), 32 (set_P). Each has Param rows (Seq#0 return / value).
  - MethodSemantics rows:
    - Row A: `Semantics=Getter (0x0002)`, `Method=0x0600001F`, `Association=Property 0x17000006`
    - Row B: `Semantics=Setter (0x0001)`, `Method=0x06000020`, `Association=Property 0x17000006`
  - PropertyMap row unchanged (if already present) but `PropertyList` for TypeDef 2 now points to first property of the type (still valid). If map was missing, delta adds a new PropertyMap row referencing TypeDef 2.
  - EncLog:
    - Property (Add) 0x17000006
    - MethodSemantics (Add) rows for getter/setter
    - MethodDef rows (Add) for accessors
    - Param rows (Add/Default) for accessor parameters
    - Possibly PropertyMap (Add) if newly created
  - EncMap includes Property, MethodSemantics, MethodDef, Param (+ PropertyMap if added).

**Handle sizing**
- Map indexes (`PropertyList/EventList`) and MethodSemantics coded indexes rely on ENC large-index mode; Roslyn ensures heap-size bits keep these uncompressed when tables grow.

**Tests / verification**
- Roslyn C#: property/event add tests (`EditAndContinueTests.PropertyAdd`, `EventAdd`) assert Property/MethodSemantics/Map rows via `CheckEncLogDefinitions` and `CheckEncMapDefinitions`.
- This repo’s component tests (`MdvValidationTests`) include mdv validations for property/event accessor edits and ensure EncLog/EncMap rows exist.
- mdv: verify Property/Event tables grew, MethodSemantics rows tie accessor methods to the new association, and maps are present/updated.
### 4.8 CustomAttribute (parent ordering)
**ECMA ground truth (Part II §22 “CustomAttribute”)**
- Columns: `Parent` (coded index: HasCustomAttribute), `Type` (coded index: CustomAttributeType → MethodDef/MemberRef), `Value` (Blob handle).
- Ordering constraint: CustomAttribute table must be sorted by `Parent` (ascending); within a parent, stable order is recommended but not enforced by ECMA. Duplicate parents must appear contiguously.
- Blob format: `Value` is a fixed prolog `0x01 0x00` followed by serialized arguments (per ECMA §II.23.3).

**Delta rules**
- Adding attributes appends rows; they must be inserted in the correct Parent order even in the delta. If the parent already had attributes, new rows must follow the existing ones for that parent (baseline + prior deltas) to keep grouping.
- `Type` typically a MemberRef to the attribute constructor; reused when possible. `Parent` is the entity being annotated (TypeDef, MethodDef, Param, Property, Event, Field, etc.).
- EncLog/EncMap include CustomAttribute rows when added; updates are uncommon (attributes usually append).

**Roslyn implementation checkpoints**
- Custom attributes are deferred, then sorted by Parent before finalizing (`FinalizeCustomAttributeTableRows` in `DeltaMetadataWriter.cs`). The algorithm:
  - Captures existing attribute order for each parent (baseline + previous deltas) to preserve grouping.
  - Adds new rows after existing ones for the same parent, maintaining overall Parent sort.
  - Records added row IDs per parent in `_customAttributesAdded` for future generations.
- EncLog/EncMap: `PopulateEncLogTableRows` includes CustomAttribute, adding EncLog entries for each new row; EncMap includes the added CA row tokens via `AddRowNumberTokens`.
- Baseline handle reuse: MemberRef to ctor and Value blob reuse where identical; otherwise new Blob rows are added. String heap unchanged unless attribute arguments introduce strings.

**Runtime expectations**
- CLR merge assumes CustomAttribute table remains parent-sorted so lookups via `GetCustomAttributeByParent` function correctly. Misordered rows can produce invalid metadata during ApplyUpdate.
- Attributes on method bodies (e.g., `NullableContext`, `AsyncStateMachine`) are expected in state-machine deltas; missing rows can cause runtime semantic mismatches but usually won’t crash apply unless metadata is invalid.

**Concrete numeric example (add `[Obsolete]` to a method)**
- Baseline: MethodDef RID 5 has no attributes.
- Delta:
  - Parent = MethodDef token `0x06000005`.
  - Type = MemberRef token to `System.ObsoleteAttribute..ctor()` (may already exist; reuse RID).
  - Value blob: `01 00 00 00` (prolog + no ctor args + no named args).
  - CustomAttribute RID appended (e.g., baseline count 100 → delta RID 101).
  - EncLog: `(CustomAttribute 0x0C000065, Add)`; EncMap: `0x0C000065`.
  - Ordering: if MethodDef 4 already had attributes, MethodDef 5’s new entry appears after all parent=4 attributes and before any parent>5 attributes.

**Handle sizing**
- Parent is a coded index (HasCustomAttribute) and uses ENC large-index mode when needed; Type is a coded index (CustomAttributeType). Blob handle follows blob-index sizing; ENC keeps blob indexes large.

**Tests / verification**
- Roslyn C#: `EditAndContinueTests.AddAttribute`, `ModifyDelegateInvokeMethod_AddAttributes`, and state-machine tests (nullable/async) check that added attributes appear and are ordered by parent.
- mdv: inspect CustomAttribute table and verify parent grouping/order; EncLog/EncMap should list new CA rows. Confirm Value blob matches expected prolog/args.
  
### 4.9 MethodImpl (interface impl changes)
**ECMA ground truth (Part II §22 “MethodImpl”)**
- Columns:
  1. `Class` (TypeDef handle) — the type providing the implementation.
  2. `MethodBody` (coded index: MethodDefOrRef) — implementing method.
  3. `MethodDeclaration` (coded index: MethodDefOrRef) — the declaration being implemented (interface method or base virtual).
- Each MethodImpl row connects an implementing method to a declaration for a specific class. Multiple rows per class are allowed.

**Delta rules**
- Adding a new interface or override implementation appends MethodImpl rows; tokens use existing MethodDef/MemberRef where possible.
- MethodImpl rows are typically added when:
  - A new class implements an interface method.
  - An existing class gains an explicit interface implementation method.
  - An override is added for a virtual in a base.
- EncLog/EncMap include MethodImpl rows when added; updates are rare (replacing MethodBody token).

**Roslyn implementation checkpoints**
- Method implementations gathered per type during visit: `_methodImpls` tracks `(MethodDef RID, slot index)` pairs to avoid duplicates, assigning row IDs for new implementations (`MethodImplIndex`).
- Added rows recorded in `_methodImpls` and emitted via `PopulateEncLogTableRows(TableIndex.MethodImpl, ...)`.
- `MethodImplKey` uses MethodDef RID + “index” to distinguish multiple implementations for the same method; ensures stable RIDs across generations when already present.
- Member tokens come from method definitions (MethodDef) or references (MethodRef) resolved through existing indices; signature reuse prevents churn.

**Runtime expectations**
- CLR uses MethodImpl to update interface dispatch maps; missing rows for new implementations make interface calls route to stubs or throw `MissingMethodException`. Incorrect tokens (wrong declaration) corrupt dispatch.
- ApplyUpdate relies on MethodImpl rows being appended correctly; the runtime merges them into the EEClass’s method table for updated types.

**Concrete numeric example (add explicit interface method)**
- Interface `I.M()`; class `C` lacked implementation. Delta adds `C.M` MethodDef RID 40.
  - MethodImpl row appended (baseline count 5 → delta RID 6):
    - `Class` = TypeDef token for `C` (`0x02000003`).
    - `MethodBody` = `0x06000028` (MethodDef RID 40).
    - `MethodDeclaration` = MemberRef to `I.M` (`0x0A000012`) or MethodDef in interface (`0x06000007`) depending on signature availability.
  - EncLog: `(MethodImpl 0x19000006, Add)`; EncMap: `0x19000006`.
  - No new Param rows unless the method is new; MethodDef/Param logged separately.

**Handle sizing**
- MethodImpl uses coded indexes; ENC large-index mode ensures enough bits for growing MethodDef/MemberRef counts. Roslyn maintains large index sizes in ENC header.

**Tests / verification**
- Roslyn: `EditAndContinueTests.ImplementInterface` scenarios assert MethodImpl rows/EncLog/EncMap when new interface implementations are added.
- mdv: inspect MethodImpl table to confirm new row with correct Class/MethodBody/MethodDeclaration tokens; EncLog/EncMap entries should be present.
- Runtime: interface calls after ApplyUpdate should succeed; failures here usually surface as missing-interface-method errors at runtime.
### 4.10 TypeSpec / GenericParam / Constraints
**ECMA ground truth (Part II §22 tables)**
- **TypeSpec**: single column `Signature` (Blob). Used for instantiated generic types and array/pointer/byref specs in metadata. Referenced via TypeSpec tokens (`0x1B0000xx`).
- **GenericParam**: `Number` (UInt16, 0-based per owner), `Flags` (UInt16), `Owner` (coded index: TypeOrMethodDef), `Name` (String), `Kind` (optional, later editions) not in v2; entries describe type/method generic parameters.
- **GenericParamConstraint**: `Owner` (GenericParam handle), `Constraint` (coded index: TypeDef/TypeRef/TypeSpec).
- Ordering: TypeSpec rows appear where needed; GenericParam rows are grouped by Owner with increasing Number; constraints grouped by Owner.

**Delta rules**
- Body-only edits should reuse existing TypeSpec/GenericParam rows (no new instantiations). Adding or changing generic instantiations or constraints appends rows:
  - New closed constructed types/methods emit TypeSpec rows (new signature blobs).
  - Adding a generic parameter (usually a rude edit) would require GenericParam rows; generally unsupported at runtime.
  - Adding constraints emits GenericParamConstraint rows tied to existing GenericParam tokens.
- EncLog/EncMap include added TypeSpec/GenericParam/Constraint rows when present; updates are rare.

**Roslyn implementation checkpoints**
- `_typeSpecIndex` seeded with baseline count; `GetOrAddTypeSpecificationHandle` reuses existing TypeSpec rows when signatures match, otherwise appends.
- Generic parameters/constraints are typically stable across edits; Roslyn treats signature changes as rude edits instead of emitting new GenericParam rows.
- EncLog covers TypeSpec via `PopulateEncLogTableRows(TableIndex.TypeSpec, ...)`; GenericParam/Constraint would flow similarly if changes occurred, but Edits that alter them are generally blocked earlier (rude edits).
- Signature blobs for TypeSpec are emitted to the Blob heap; ENC keeps blob indexes large.

**Runtime expectations**
- CLR uses TypeSpec to resolve instantiated types in IL and metadata; incorrect reuse or missing TypeSpec causes token resolution failures during ApplyUpdate.
- Generic parameter shape changes are often rejected as rude edits; if emitted incorrectly, ApplyUpdate fails during metadata validation.

**Concrete numeric example (new generic instantiation usage)**
- Baseline has no `List<int>` TypeSpec; only `List<T>` TypeRef.
- Delta edits a method body to instantiate `new List<int>()` for the first time:
  - Emit TypeSpec signature blob for `class [System.Collections]System.Collections.Generic.List<int32>`; blob handle = new blob offset; TypeSpec RID appended (baseline 4 → delta RID 5).
  - IL in delta refers to token `0x1B000005`.
  - EncLog: `(TypeSpec 0x1B000005, Add)`; EncMap: `0x1B000005`.
- No GenericParam rows are added (existing generic type unchanged); constraints unchanged.

**Tests / verification**
- Roslyn: generic addition scenarios in `EditAndContinueTests.GenericAddMethod` and state-machine tests that introduce new closed instantiations implicitly validate TypeSpec addition; rude-edit tests cover attempts to change generic params.
- mdv: ensure TypeSpec table grows only when new instantiations appear; EncLog/EncMap include TypeSpec tokens when added; GenericParam/Constraint tables should remain unchanged for body-only edits.
### 4.11 Field / FieldRVA
**ECMA ground truth (Part II §22 “Field” / “FieldRVA”)**
- **Field** columns: `Flags` (UInt16, FieldAttributes), `Name` (String), `Signature` (Blob — FieldSig per §23.2.4).
- **FieldRVA** columns: `RVA` (UInt32), `Field` (FieldDef handle). Links a static field with explicit data to its initial value in the PE data section.
- Ordering: Field rows are contiguous per owning TypeDef via TypeDef.FieldList ranges. FieldRVA rows can appear in any order but typically match the Field token.

**Delta rules**
- Adding a new field appends a Field row and, if it has an explicit RVA (e.g., `static int x = 1;` with initdata), a FieldRVA row.
- Field additions require updating the TypeDef’s FieldList range implicitly (FieldList of the owning TypeDef remains the starting RID; subsequent FieldList values for later types must still be monotonic).
- EncLog/EncMap include Field rows (Add) and FieldRVA rows when present.
- Body-only edits should not add fields; attempting to change a field shape is usually a rude edit.

**Roslyn implementation checkpoints**
- Fields are collected per type; `_fieldDefs` assigns row IDs seeded with baseline count. FieldRVA entries are added via `PopulateEncLogTableFieldsOrMethods` helpers when a field has data.
- EncLog includes Field rows (`AddField` operation) and FieldRVA rows (`Add`); EncMap lists Field and FieldRVA tokens when added.
- Signatures and names reuse baseline handles when unchanged; new fields add new Blob/String handles as needed.

**Runtime expectations**
- CLR merges Field and FieldRVA; new static fields with RVA cause loader to allocate and initialize new storage. Incorrect FieldRVA mapping (wrong Field token or misordered data) leads to invalid metadata or runtime type load failure.
- Non-RVA fields simply extend the field list; layout recalculation for instance fields is generally not supported mid-run (treated as rude edit), but static fields without layout impact are acceptable.

**Concrete numeric example (add static int field with initdata)**
- Baseline: Field count = 20; owning TypeDef RID 3 has FieldList=10, its last field RID 12.
- Delta adds `static int Y = 42;`
  - Field row RID 21: `Flags=0x0010 (Static)`, `Name="Y"`, `Signature` = FieldSig for int32.
  - FieldRVA row RID 1 (delta table counts from 1): `RVA=<data offset in delta PE>`, `Field=0x04000015` (FieldDef RID 21).
  - EncLog: `(Field 0x04000015, Add)`, `(FieldRVA 0x1D000001, Add)`.
  - EncMap: `FieldRVA`, then `Field` (sorted by table/RID).
  - TypeDef.FieldList for TypeDef 3 remains 10; since this is appended after its existing fields, no FieldList change is required for previous types. If TypeDef 3 is last, no later FieldList to adjust.

**Handle sizing**
- FieldList is a simple index; ENC uses large index sizes. Field tokens are simple indexes; FieldRVA uses a coded reference to Field (actually a direct Field handle column).

**Tests / verification**
- Roslyn: `EditAndContinueTests.AddField` verifies Field/FieldRVA emission and EncLog/EncMap entries.
- mdv: check Field table growth and FieldRVA entries; ensure RVA points into the delta data, and EncLog/EncMap contain the added rows.

## 5. IL Delta Stream
### 5.1 Body formats, EH tables, RVA layout
**ECMA ground truth (Partition III, §II.25 “CIL instruction set”, §II.12.4 “Method bodies”)**
- Two method body formats:
  - **Tiny**: 1-byte header: bits 0–1 = `0b10` (Tiny), bits 2–7 = code size / 4 (must fit <64 bytes), max stack = 8, no local sig, no EH.
  - **Fat**: 4-byte aligned header, first 2 bytes: low 12 bits = header size in dwords (usually 3 for no EH, 4+ with EH); bits 12–15: `0b0011`; bits 16–31: max stack; next 4 bytes: code size; next 4 bytes: local sig token (StandAloneSig) or 0. If EH present, header size ≥4 and an SEH/Clause section follows (type=0x01, fat or small clauses).
- IL stream layout for each method: [header][IL bytes][optional padding to 4 bytes if EH][EH clauses].
- RVA in MethodDef points to the start of the header.

**Delta rules**
- Delta IL stream contains only updated/added method bodies, back-to-back. Each MethodDef row in the delta holds the RVA (offset into the delta IL stream) for its new body.
- Header choice must follow ECMA rules; EH-containing bodies must use Fat headers (or Tiny + separate EH section? actually Tiny cannot have EH). Body size and local sig token must match the delta’s StandAloneSig rows.
- Exception clause offsets (try/start, handler/start, filter) within EH sections are **relative to the method body start** (as per ECMA) and thus unchanged by delta concatenation; RVA only relocates the body as a whole.

**Roslyn implementation checkpoints**
- IL emission for deltas uses `MethodBodyStreamEncoder` / `ILBuilder` scoped to the delta; `DeltaMetadataWriter.SerializeLocalVariablesSignature` supplies the StandAloneSig token for locals.
- `IlDeltaStreamBuilder` writes bodies sequentially and calculates RVA offsets; `AddedOrChangedMethodInfo` captures new bodies. Roslyn uses fat headers for most hot-reload bodies, matching baseline behavior.
- Tokens inside the body are fixed up after metadata handle translation (see 5.2).

**Runtime expectations**
- CoreCLR ENC (`encing/encil.cpp` IL map handling) copies delta IL into module RVA-able memory and uses the RVA from MethodDef to redirect execution. Body headers and EH data are consumed as-is; malformed headers or EH clauses cause `ApplyUpdate` failure or JIT verification errors.
- RVA is relative to the delta IL blob supplied with the update; runtime does not relocate individual method internals beyond what the header encodes.

**Concrete numeric example (fat header, no EH)**
- MaxStack=8, code size=25 bytes, locals present (StandAloneSig token `0x11000002`):
  - Fat header (3 dwords): 
    - Bytes 0–1: `0x13 0x30`? (bits: size=3 dwords → 0x3, flags=Fat 0x3, combined = 0x30? actual little-endian: 0x30 0x03). For clarity: word = `0x3003`.
    - Bytes 2–3: MaxStack = `0x0008`.
    - Bytes 4–7: CodeSize = `0x00000019`.
    - Bytes 8–11: LocalSig = token `0x11000002`.
  - IL bytes follow (25 bytes), padded to 4-byte boundary => total body size = header (12) + code (25) + pad (3) = 40 bytes. Next method RVA starts at previous RVA + 40.

**Tests / verification**
- Roslyn: `EditAndContinueTests.UpdateMethodBody` and EH-specific tests exercise IL body emission; mdv `/il+` displays headers and EH sections.
- mdv usage: `dotnet run --project metadata-tools/src/mdv/mdv.csproj --framework net10.0 -- <baseline.dll> "/g:<delta.meta>;<delta.il>" /il+` — inspect per-method RVA, header type, code size, EH clauses.
### 5.2 Token fixups and baseline token reuse
**ECMA ground truth**
- IL instruction operands that reference metadata (MemberRef/MethodDef/FieldDef/TypeRef/TypeSpec/String/UserString/StandAloneSig) encode a 4-byte metadata token (table id + RID) in little-endian (see Partition III §II.25 “Instruction Encoding”).
- Correctness requires tokens to resolve in the *current* metadata context when executed.

**Delta rules**
- Tokens inside delta IL must refer to the target tokens **as they will exist after applying the delta**, not to temporary delta-relative row numbers. For unchanged symbols, reuse the baseline token. For newly added rows in the delta, use the new tokens assigned in the delta tables.
- String/UserString tokens point into the combined heaps using absolute offsets (delta heaps start at baseline lengths).
- StandAloneSig tokens used in locals headers must match the StandAloneSig rows emitted in the delta (or baseline if reused).
- No indirection/translation at runtime for in-body tokens—runtime applies the delta metadata and expects tokens to already match the merged tables.

**Roslyn implementation checkpoints**
- Token reuse: `EmitBaseline` and `DefinitionMap` translate symbols to baseline handles; `ILBuilder.FixupTokens` (in Roslyn IL pipeline) uses these handles when emitting IL so unchanged references keep baseline tokens.
- Added references: when a new TypeSpec/MemberRef/etc. is needed, Roslyn first asks the delta metadata writer for the handle (which may reuse or append); the IL encoder then emits that token directly into the IL stream.
- Method bodies captured in `AddedOrChangedMethodInfo` carry already-fixed tokens; no further translation occurs after metadata blobs are produced.

**Runtime expectations**
- `MetadataUpdater.ApplyUpdate` copies IL and metadata; at execution time the CLR resolves tokens using the updated metadata. If IL contains a token that points to a non-existent RID (e.g., because the delta used delta-local numbering instead of absolute), JIT will fail with invalid metadata or `MissingMethodException`.
- Token stability for unchanged members is important to avoid unnecessary remapping and to keep reflection/debug info consistent.

**Concrete numeric examples**
- *Body-only edit reusing tokens*: A call to `System.Console.WriteLine(string)` in baseline used token `0x0A000019`. Delta body should still emit `0x0A000019` (MemberRef RID unchanged). No new MemberRef row added.
- *New instantiation token*: If delta introduces `List<int>` for the first time, IL uses TypeSpec token `0x1B000005` (as added in the delta). The corresponding TypeSpec row is present in the delta metadata; runtime resolves it after apply.
- *Locals sig token*: Fat header LocalSig token might be `0x11000006` for a StandAloneSig row (RID 6) added in the delta. IL header must encode that exact token.

**Tests / verification**
- Roslyn: `EditAndContinueTests.TokenReuse`-style cases (and general `UpdateMethodBody` tests) ensure tokens for unchanged references are reused; generic/instantiation tests validate new TypeSpec tokens are emitted when required.
- mdv: use `/il+` to inspect IL operands; cross-check tokens against the delta metadata tables. Ensure calls/ldtoken/box operands align with rows present in baseline+delta.
- Runtime: invalid tokens surface during ApplyUpdate or JIT with bad token errors; stable tokens keep ApplyUpdate succeeding.

## 6. PDB / Debug Delta
### 6.1 Portable PDB delta structure
**Spec references**
- Portable PDB is documented in the ECMA Portable PDB annex; key ENC delta tables mirror metadata ENC tables: `EncLog`, `EncMap`, `MethodDebugInformation`, `LocalScope`, `LocalVariable`, `LocalConstant`, `Document`, `CustomDebugInformation`, etc.
- PDB deltas are emitted as a separate metadata stream (`#Pdb`) with its own heap offsets; they are conceptually independent of the IL/metadata delta but must stay token-consistent.

**Delta rules**
- Each generation emits a PDB delta containing only changed method debug info plus ENC tables:
  - `EncLog`/`EncMap` duplicated in PDB to allow debugger to map tokens.
  - `MethodDebugInformation` rows for updated/added methods; sequence points may be updated.
  - Documents are typically reused; new documents add rows with stable checksums.
- Tokens in PDB delta must match the updated metadata tokens (baseline+delta absolute values).
- PDB delta includes a `#~`-style header for debug tables and separate heaps (UserString, Blob, Guid, DocumentName). Heap offsets in the PDB delta are absolute to prior PDB generations (Portable PDB uses similar start-offset semantics).

**Roslyn implementation checkpoints**
- PDB delta builder: `PdbDeltaBuilder`/`PortablePdbBuilder` produce the delta debug metadata; `DeltaMetadataWriter` plugs in EncLog/EncMap duplication for PDB.
- Method debug info is regenerated for changed methods; unchanged methods are omitted.
- Heap starts for PDB delta are seeded from previous PDB sizes (similar to metadata) so handles remain stable across generations.
- `EditAndContinuePdbTests` verify that EncLog/EncMap in PDB match the metadata Enc tables and that method debug info is present for updated methods.

**Runtime/debugger expectations**
- CLR runtime largely ignores PDB deltas; debugger uses them to map new IL to source. Visual Studio expects EncLog/EncMap consistency between metadata and PDB.
- Mismatched tokens or heap offsets can cause debugger failures or incorrect stepping but usually won’t block `ApplyUpdate`.

**Concrete numeric example**
- Baseline PDB has Document rows D1..D3; MethodDebugInformation row for MethodDef 5.
- Delta updates MethodDef 5 and adds MethodDef 11:
  - PDB delta EncMap: tokens 0x06000005, 0x0600000B.
  - EncLog: entries for MethodDef updates, Param updates, etc., mirroring metadata.
  - MethodDebugInformation rows for 5 (updated) and 11 (new), with new sequence points; LocalScope/LocalVariable rows added as needed.
  - Heaps: #Blob/#Guid start at baseline sizes; new sequence point blobs appended.

**Verification**
- Use `dotnet-symbol`/`dotnet-dump` or Roslyn test helpers; in this repo use Roslyn `EditAndContinuePdbTests` as reference. PDB deltas aren’t inspected by mdv; instead use debug readers to confirm Enc tables/token alignment.
### 6.2 EncLog/EncMap duplication in PDB
**What must match**
- The PDB delta carries its own `EncLog` and `EncMap` tables, and they must be byte-for-byte consistent (same rows, same order) with the ENC tables in the metadata delta for that generation.
- Tokens in the PDB Enc tables use the same absolute metadata tokens (post-apply values), not PDB-local numbering.

**Roslyn implementation**
- `PdbDeltaBuilder` populates PDB EncLog/EncMap from the same data structures used by the metadata writer. There is no independent computation—rows are copied to ensure identity.
- Tests `EditAndContinuePdbTests` (e.g., `LogMatchesMetadata`) assert equality between metadata Enc tables and PDB Enc tables for each delta, covering ordering and content.

**Runtime/debugger expectations**
- The CLR uses metadata Enc tables; the debugger uses PDB Enc tables. Any divergence can lead to mismatched stepping spans or missing updated methods in the debugger, even if `ApplyUpdate` succeeds.

**Concrete check**
- After emitting a delta, read both the metadata and PDB EncLog/EncMap:
  - They should have identical row counts and tokens.
  - EncLog FuncCodes should match; ordering identical.
  - EncMap token sequences identical.
- In Roslyn tests this is done via `CheckEncLog`/`CheckEncMap` against both readers.

## 7. Runtime Apply Pipeline
### 7.1 MetadataUpdater.ApplyUpdate flow and errors
**Runtime entrypoint**
- Managed API: `System.Reflection.Metadata.MetadataUpdater.ApplyUpdate(Assembly, ReadOnlySpan<byte> metadataDelta, ReadOnlySpan<byte> ilDelta, ReadOnlySpan<byte> pdbDelta = default)`.
- CoreCLR native path: `assemblynative.cpp` → `EditAndContinueModule::ApplyEditAndContinue` (`src/coreclr/vm/encee.cpp`) handles the deltas.
- Steps:
  1. Verify module is EnC-capable; ensure metadata is read-write (`ConvertMDInternalToReadWrite`).
  2. Call `IMDInternalImportENC.ApplyEditAndContinue` with metadata delta; this merges tables/heaps (including EncLog/EncMap).
  3. Copy IL delta into RVA-mapped memory; update method descs with new RVAs.
  4. Apply PDB delta if provided (debugger consumption).
  5. Increment apply count; possibly dump deltas if `COMPlus_EncDumpApplyChanges` is set.
- HRESULT failure commonly observed: `0x80131509` (`COR_E_INVALIDOPERATION`) when metadata is invalid (bad handles, missing Param rows, mismatched heaps) or when runtime validation rejects the update.

**Error sources to guard against**
- Invalid metadata after merge (e.g., ParamList 0, out-of-range tokens, unsorted CustomAttributes).
- Unsupported edits (signature or layout changes classified as rude edits).
- Missing heap offset chaining (string/blob offsets reset to 0).
- Unmatched EncId/EncBaseId chain.

**Roslyn diagnostics**
- Roslyn surfaces rude edits before emission via `EditAndContinueDiagnosticAnalyzer`; ApplyUpdate failures downstream usually indicate serializer/metadata mistakes, not analyzer gaps.

**Tests**
- Runtime repo: `MetadataUpdateTests` cover successful and failing ApplyUpdate scenarios, including error codes.
- Repo smoke: `tests/scripts/hot-reload-demo-smoke.sh` (when runtime-apply flag enabled) exercises the end-to-end ApplyUpdate; failures are captured with HRESULT.

**Verification**
- On failure, capture HRESULT and inspect merged metadata with mdv; check EncLog/EncMap, ParamList, heap offsets, and CustomAttribute ordering.
### 7.2 CLR validation rules and rude edits
**Runtime validation (CoreCLR enc.cpp/encee.cpp)**
- Signature and shape checks:
  - Method signature changes to existing methods are generally rejected; runtime expects updated bodies to match existing signatures. Edits that would require slot re-layout are rude edits.
  - Instance field layout changes (adding/removing instance fields) are not supported during EnC; static fields may be added with caution.
  - Generic parameter additions/constraint changes are rejected.
- Table consistency checks:
  - ParamList must be valid and point to existing Param rows.
  - CustomAttribute must remain parent-ordered.
  - EncMap/EncLog tokens must resolve; heap offsets must align with prior lengths.
- Slot mapping:
  - Virtual method slots are preserved; adding an override where none existed can be allowed via MethodImpl, but changing existing virtual signatures is disallowed.
  - Interface implementation additions are allowed via MethodImpl rows; removal is not.

**Roslyn alignment (rude edits)**
- Roslyn’s `EditAndContinueDiagnosticAnalyzer` classifies disallowed edits, mirroring CLR constraints: signature changes, field layout changes, generic parameter moves, etc.
- Body-only edits and additive property/event/interface implementations are allowed and emitted.

**Tests**
- Roslyn: `EditAndContinueTests.RudeEdits` covers prohibited scenarios (e.g., changing method signature, adding fields in structs).
- Runtime: validation is exercised indirectly via ApplyUpdate; invalid metadata triggers `COR_E_INVALIDOPERATION`.
### 7.3 ALC/module identity considerations
**Runtime constraints**
- ApplyUpdate operates on the specific `Assembly` instance within its `AssemblyLoadContext` (ALC). Deltas must be generated against that loaded module (matching MVID and assembly identity).
- Multi-module assemblies are not supported for Hot Reload; only the primary module is eligible.
- Assembly identity (AssemblyDef) must not change; Module Name/Mvid stay constant (see 4.1).

**Implications**
- If the same assembly is loaded into multiple ALCs, each ALC requires a separate ApplyUpdate call with deltas compiled for that instance.
- Attempting to update a different module or mismatched MVID will fail ApplyUpdate (invalid operation).

**Tests / references**
- Runtime: `MetadataUpdateTests.MultiModule` verifies multi-module is rejected; other tests exercise ApplyUpdate in default ALC.
- API surface: `System.Reflection.Metadata.MetadataUpdater.ApplyUpdate` takes the target `Assembly`, ensuring correct ALC routing.

## 8. Tooling and Verification
### 8.1 mdv usage and expected outputs
**Tool location**: `metadata-tools/src/mdv` (dotnet console). Build/run: `cd metadata-tools && dotnet run --project src/mdv/mdv.csproj --framework net10.0 -- ...`

**Baseline + delta command template**
```
dotnet run --project src/mdv/mdv.csproj --framework net10.0 -- \
  <baseline.dll> \
  "/g:<delta.meta>;<delta.il>" \
  /stats+ /md+ /il+   # add /pdb:<delta.pdb> if you have a PDB delta
```
- `/g:` expects semicolon-separated metadata + il paths; optional PDB via `/pdb:` or embedded in `/g:...;...;pdb`.
- Useful switches: `/md+` dumps metadata tables/heaps; `/il+` disassembles delta IL with RVAs; `/stats+` shows heap sizes/row counts.

**Expected outputs to check**
- Stream offsets: #Strings/#Blob/#US/#GUID should start at baseline lengths (see 2.1/2.2).
- EncLog/EncMap: rows for MethodDef/Param/etc. per edits (3.1/3.2).
- Table growth: TypeDef/MethodDef/Param/StandAloneSig/etc. rows match the scenario.
- IL: RVA matches MethodDef rows; tokens resolve to rows present in baseline+delta.

**Examples**
- Property edit delta: see prior run captured at `.../delta-dump/gen-001/metadata.bin;.../il.bin`.
- C# harness deltas: `mdv hot_reload_poc/src/csharp_delta_test/bin/Debug/net9.0/csharp_delta_test.dll.1.dmeta ...`

**Troubleshooting**
- If mdv reports “bad token range” or missing EncMap rows, cross-check sections 2–4 of this spec for required rows and heap offsets.

### 8.2 compare_roslyn / inspect_delta scripts
**Scripts**
- `tools/hot-reload/compare_roslyn.fsx`: compares F# delta tables against Roslyn baselines; use for parity checks on specific edits. Run with `dotnet fsi tools/hot-reload/compare_roslyn.fsx -- <baseline> <delta> <roslyn-dmeta>`.
- `inspect_delta.fsx` (root): quick dump of table counts, heap sizes, EncLog/EncMap for any delta. Run: `dotnet fsi inspect_delta.fsx -- <baseline> <delta.meta>`.

**Use cases**
- Quickly spot missing tables (e.g., CustomAttribute/StandAloneSig) or heap growth.
- Validate EncLog/EncMap parity against Roslyn output for the same scenario.
- Feed edits from C# delta harness or scripted F# demo for side-by-side comparison.

### 8.3 HotReload demo smoke flows
**Script**: `tests/scripts/hot-reload-demo-smoke.sh`
- Default mode runs the sample end-to-end without runtime apply (ensures emit succeeds).
- Enable runtime apply: `HOTRELOAD_SMOKE_RUNTIME_APPLY=1 HOTRELOAD_SMOKE_KEEP_WORKDIR=1 tests/scripts/hot-reload-demo-smoke.sh`
  - Keeps `delta-dump/` with `metadata.bin`/`il.bin` (and optional PDB) for mdv inspection.
  - Logs HRESULT on failure (`HR=0x80131509` is common when metadata is invalid).

**What to inspect when it fails**
- EncLog/EncMap present and ordered; Param rows (Seq#0) exist; MethodDef ParamList non-zero.
- CustomAttribute ordering; StandAloneSig rows for locals; TypeRef/MemberRef reuse vs. append.
- Heap starts match baseline sizes (string/blob/us/guid).

## 9. References and Parity Anchors
### 9.1 ECMA clause mapping
- Metadata streams/header: §24.2 (physical layout), §24.2.3 (heaps), compressed integers §23.2.
- EncLog/EncMap: §22 (tables), token format §24.2.6.
- Table definitions: Module/TypeDef/Field/MethodDef/Param/Property/Event/MethodSemantics/PropertyMap/EventMap/CustomAttribute/StandAloneSig/MethodImpl/TypeRef/MemberRef/AssemblyRef/TypeSpec/GenericParam/GenericParamConstraint → all in §22.
- IL bodies: Partition III §12.4 (method bodies), §25 (instruction encoding), LocalVarSig in §23.2.6.
- CustomAttribute blob encoding: §23.3.
- Portable PDB: Portable PDB ECMA annex (separate from Part II/III).

### 9.2 Roslyn revision and files
- Commit used for parity: `511a87e0653b62d58b486ef0860dc684d4423d1d`.
- Core ENC files: `src/Compilers/Core/Portable/Emit/EditAndContinue/DeltaMetadataWriter.cs`, `EmitBaseline.cs`, `DefinitionMap.cs`, `SymbolMatcher.cs`, `PEWriter/MetadataWriter.cs`, `PEWriter/FullMetadataWriter.cs`.
- PDB delta builders: `PdbDeltaBuilder`, `PortablePdbBuilder`.
- Tests: `src/Compilers/CSharp/Test/Emit2/Emit/EditAndContinue/*` (state-machine, property/event, attribute, locals, token reuse, PDB ENC) plus VB equivalents.

### 9.3 Runtime source touchpoints
- CoreCLR ENC apply: `src/coreclr/vm/encee.cpp`, `assemblynative.cpp`; IL map handling `encil.cpp`.
- Metadata merge/writer: `src/coreclr/md/enc/metamodelrw.cpp` (EncId/EncBaseId, heap handling, EncLog/EncMap merge paths).
- Managed API bridge: `System.Reflection.Metadata.MetadataUpdater`.
- Runtime commit for parity: `b02019ab18b6126bcf2def992fba2ac85069a65b`.
- Tests: runtime `MetadataUpdateTests` (including MultiModule rejection and ApplyUpdate HRESULT paths).
