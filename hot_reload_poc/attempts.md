# Hot Reload Delta Generation Attempts

## Attempt #1: Module + Refs + Minimal EnC

*   **Description:** Include `Module` definition row, `AssemblyRef` for `System.Runtime`, `TypeRef`s for `System.Object` and `System.Int32`. Populate `EncMap`/`EncLog` with `MethodDef` handle (from baseline token) and `StandAloneSig` handle (assumed handle 1). Do **not** explicitly add `TypeDef` or `MethodDef` rows.
*   **Expected Outcome:** Pass `ApplyUpdate` validation and potentially resolve `BadImageFormatException`.
*   **Result:** `ApplyUpdate` failed immediately (`System.InvalidOperationException: The assembly update failed.`). `mdv` failed to parse Gen 1.

## Attempt #2: Module + TypeDef + MethodDef + Refs + ExplicitSig EnC

*   **Description:** Include `Module`, `TypeDef`, `MethodDef` definitions, `AssemblyRef` for `System.Runtime`, `TypeRef`s for `System.Object` and `System.Int32`. Explicitly add empty `StandAloneSig` blob/row. Populate `EncMap`/`EncLog` with `MethodDef` handle and the explicitly added `StandAloneSig` handle.
*   **Expected Outcome:** Pass `ApplyUpdate` validation and potentially resolve `BadImageFormatException`.
*   **Result:** `ApplyUpdate` failed immediately (`System.InvalidOperationException: The assembly update failed.`). `mdv` failed to parse Gen 1.

## Attempt #3: Module + TypeDef + MethodDef + Refs + AssumedSig EnC

*   **Description:** Include `Module`, `TypeDef`, `MethodDef` definitions, `AssemblyRef` for `System.Runtime`, `TypeRef`s for `System.Object` and `System.Int32`. Populate `EncMap`/`EncLog` with `MethodDef` handle (from *added* definition) and `StandAloneSig` handle (assumed handle 1). Do **not** explicitly add the `StandAloneSig` row/blob.
*   **Expected Outcome:** Pass `ApplyUpdate` validation (like initial attempts) and potentially resolve `BadImageFormatException` by using the assumed correct EnC content.
*   **Result:** `ApplyUpdate` failed immediately (`System.InvalidOperationException: The assembly update failed.`). `mdv` failed to parse Gen 1.

## Attempt #4: Module + TypeDef + MethodDef + Refs + TypeMethod EnC

*   **Description:** Include `Module`, `TypeDef`, `MethodDef` definitions, `AssemblyRef` for `System.Runtime`, `TypeRef`s for `System.Object` and `System.Int32`. Populate `EncMap`/`EncLog` only with the handles for the **added `TypeDef`** and the **added `MethodDef`**.
*   **Expected Outcome:** Pass `ApplyUpdate` validation (as it includes all defs) and hopefully resolve `BadImageFormatException` by using the EnC content from the configuration that previously passed ApplyUpdate, but now with added Refs.
*   **Result:** `ApplyUpdate` **passed**, but invoke failed (`System.BadImageFormatException`). `mdv` failed to parse Gen 1.

## Attempt #5: Attempt #4 Defs/Refs + AssumedSig EnC

*   **Description:** Include `Module`, `TypeDef`, `MethodDef` definitions, `AssemblyRef` for `System.Runtime`, `TypeRef`s for `System.Object` and `System.Int32`. Populate `EncMap`/`EncLog` with `MethodDef` handle (from **added** definition) and `StandAloneSig` handle (assumed handle 1). (Combines structure of Att #4 with EnC content similar to Att #3/C# mdv output).
*   **Expected Outcome:** Pass `ApplyUpdate` validation (like Att #4) and potentially resolve `BadImageFormatException` by using the assumed correct EnC content.
*   **Result:** `ApplyUpdate` **passed**, but invoke failed (`System.BadImageFormatException`). `mdv` failed to parse Gen 1.

## Attempt #6: Attempt #4 Config + Correct Serialize Params

*   **Description:** Use configuration from Attempt #4 (Module+TypeDef+MethodDef+Refs added, EnC uses TypeDef+MethodDef handles). Correct the `MetadataRootBuilder.Serialize` call to use `baseMetadataLength = 0` and `generation = 1`.
*   **Expected Outcome:** Pass `ApplyUpdate` (like Att #4) and potentially fix `BadImageFormatException` by correcting the serialization parameters.
*   **Result:** Linter error prevented test run (Serialize overload mismatch). Code reverted to Att #4 state for Serialize call.

## Attempt #7: Module + MethodDef + Refs + AssumedSig EnC

*   **Description:** Include `Module` and `MethodDef` definitions, `AssemblyRef` for `System.Runtime`, `TypeRef`s for `System.Object` and `System.Int32`. **Exclude** `TypeDef` definition. Populate `EncMap`/`EncLog` with `MethodDef` handle (from added def) and `StandAloneSig` handle (assumed handle 1).
*   **Expected Outcome:** Align more closely with C# delta structure (no TypeDef row). Potentially pass `ApplyUpdate` and resolve `BadImageFormatException`.
*   **Result:** `ApplyUpdate` failed immediately (`System.InvalidOperationException: The assembly update failed.`). `mdv` failed to parse Gen 1.

## Attempt #8: Attempt #5 Config + Nil Base Type in TypeDef

*   **Description:** Use configuration from Attempt #5 (Module+TypeDef+MethodDef+Refs added, EnC uses MethodDef+AssumedSig(1)). Change `AddTypeDefinition` call to pass `EntityHandle()` (nil) as the `extends` parameter instead of a `TypeRef` to `System.Object`.
*   **Expected Outcome:** Pass `ApplyUpdate` (like Att #5) and potentially resolve `BadImageFormatException` by simplifying the `TypeDef` structure within the delta.
*   **Result:** `ApplyUpdate` failed immediately (`System.InvalidOperationException: The assembly update failed.`). `mdv` failed to parse Gen 1.

## Attempt #9: Module + TypeDef + MethodDef + ObjectRef + TypeMethod EnC

*   **Description:** Include `Module`, `TypeDef`, `MethodDef` definitions. Add **only** `TypeRef` for `System.Object` (needed for `TypeDef`). **Exclude** other Refs (`AssemblyRef`, `TypeRef` for `Int32`). Populate `EncMap`/`EncLog` with `TypeDef` and `MethodDef` handles.
*   **Expected Outcome:** Isolate if the extra references were causing the `BadImageFormatException` in Attempt #4/#5.
*   **Result:** `ApplyUpdate` **passed**, but invoke failed (`System.BadImageFormatException`). `mdv` failed to parse Gen 1.

## Attempt #10: Module Only + Minimal EnC (MethodSig)

*   **Description:** Include **only** `Module` definition. **Exclude** `TypeDef`, `MethodDef`, and all `Refs`. Populate `EncMap`/`EncLog` with `MethodDef` handle (using baseline token `0x06000001`) and `StandAloneSig` handle (assumed handle 1).
*   **Expected Outcome:** Test if minimal structure + C#-delta-like EnC content is sufficient.
*   **Result:** `ApplyUpdate` failed immediately (`System.InvalidOperationException: The assembly update failed.`). **`mdv` parsed Gen 1 correctly.**

## Attempt #11: Attempt #4 Config + Scrutinized Def Flags

*   **Description:** Use configuration from Attempt #4 (Module+TypeDef+MethodDef+Refs added, EnC uses TypeDef+MethodDef handles). Change `AddTypeDefinition` flags to `TypeAttributes.Public`. Change `AddMethodDefinition` flags to `MethodAttributes.Public ||| MethodAttributes.Static ||| MethodAttributes.HideBySig`.
*   **Expected Outcome:** Pass `ApplyUpdate` (like Att #4) and potentially fix `BadImageFormatException` by using correct/simpler flags for added definitions.
*   **Result:** `ApplyUpdate` **passed**, but invoke failed (`System.BadImageFormatException`). `mdv` failed to parse Gen 1.

## Attempt #12: Module + Refs + Minimal EnC (MethodSig) - Revisiting Attempt #1

*   **Description:** Include `Module` definition row, `AssemblyRef` for `System.Runtime`, `TypeRef`s for `System.Object` and `System.Int32`. Populate `EncMap`/`EncLog` with `MethodDef` handle (from baseline token) and `StandAloneSig` handle (assumed handle 1). Do **not** explicitly add `TypeDef` or `MethodDef` rows. (Re-testing Att #1 based on runtime analysis).
*   **Rationale:** Runtime (`encee.cpp`) analysis suggests deltas primarily *patch* existing rows based on `ENCLog`/`ENCMap`, potentially not requiring full `TypeDef`/`MethodDef` rows in the delta itself if core structure + refs + EnC tables are correct.
*   **Expected Outcome:** Test if runtime requires only Module+Refs+EnC for basic updates.
*   **Result:** `ApplyUpdate` failed immediately (`System.InvalidOperationException: The assembly update failed.`). `mdv` failed to parse Gen 1.

## Attempt #13: Use F# Static Class + Attempt #11 Config

*   **Description:** Change F# source template to use a static class (`type SimpleLib = static member GetValue() = ...`) instead of a module. Use delta generation logic from Attempt #11 (Module+TypeDef+MethodDef+Refs added, EnC uses TypeDef+MethodDef handles, adjusted flags).
*   **Rationale:** Align F# baseline structure more closely with C# test case to reduce variables.
*   **Expected Outcome:** Pass `ApplyUpdate` (like Att #11) and potentially resolve `BadImageFormatException` if module compilation was a factor.
*   **Result:** `ApplyUpdate` **passed**, but invoke failed (`System.BadImageFormatException`). **`mdv` parsed Gen 1 correctly.**

## Attempt #14: Module+MethodDef+Refs + AssumedSig EnC + Extra GUIDs

*   **Description:** Include `Module` and `MethodDef` definitions. **Exclude** `TypeDef`. Include `AssemblyRef`, `TypeRef`s, `MemberRef` for `.ctor`. Explicitly add ECMA Key GUID and unknown GUID (`FFB3...`) to `#GUID` heap using `Guid.Parse`. Populate `EncMap`/`EncLog` with `MethodDef` handle + assumed `StandAloneSig` handle (1).
*   **Rationale:** Attempt to match C# delta structure more closely based on byte analysis (No TypeDef, extra GUIDs) and mdv output (EnC tables).
*   **Expected Outcome:** Unknown. Test if this combination passes `ApplyUpdate` and resolves `BadImageFormatException`.
*   **Result:** Crashed during delta generation (`System.FormatException: Guid should contain 32 digits...`) due to incorrect GUID string parsing.

## Attempt #15: Module+MethodDef+Refs+EnC(AsmRef+TRef+MethodDef) (Based on C# mdv)

*   **Description:** Include `Module` and `MethodDef`. **Exclude** `TypeDef`. Include `AssemblyRef` (`System.Runtime`), `TypeRef` (`System.Object`). Exclude other Refs/MemberRefs. Populate `EncMap`/`EncLog` with handles for added `AssemblyRef`, added `TypeRef`, and baseline `MethodDef` token (matching C# mdv EnC content).
*   **Rationale:** Directly implements the EnC table structure observed in the C# `mdv` output for Generation 1.
*   **Expected Outcome:** Align with C# delta structure. Test if this combination passes `ApplyUpdate` and resolves `BadImageFormatException`.
*   **Result:** `ApplyUpdate` failed immediately (`System.InvalidOperationException: The assembly update failed.`). `mdv` parsed Gen 1.

## Attempt #16: Attempt #13 Config + Add Missing GUIDs

*   **Description:** Start with configuration from Attempt #13 (Module+TypeDef+MethodDef+Refs, EnC=TypeDef+MethodDef, Static Class). Add the two extra GUIDs observed in the C# delta's `#GUID` heap (`86d3b3ff-8f3c-44d6-bdea-dc76b2f7c2d1` and the ECMA Key GUID `b03f5f7f-11d5-0a3a-0000-000000000000`) using `GetOrAddGuid`.
*   **Rationale:** Test if adding the missing GUIDs resolves the `BadImageFormatException` for a configuration known to pass `ApplyUpdate`.
*   **Expected Outcome:** Pass `ApplyUpdate` (like Att #13) and potentially resolve `BadImageFormatException`.
*   **(Result will be added later)** 