# F# Hot Reload Delta Format Specification

## 1. Introduction

This specification defines the format and requirements for generating delta metadata, IL, and PDB information to support hot reload functionality for F# code in the .NET runtime. The specifications detailed
here are based on empirical findings from comparing successful C# delta formats with F# equivalents, and reference the ECMA-335 Common Language Infrastructure (CLI) standard where appropriate.

Hot reloading allows for updating the implementation of methods in a running application without restarting the process. In .NET, this is facilitated by the
`System.Reflection.Metadata.MetadataUpdater.ApplyUpdate` API, which takes three components:
1. Metadata Delta: Changes to metadata tables
2. IL Delta: Updated method implementation code
3. PDB Delta: Updated debugging information

## 2. Prerequisites

For hot reload to function, the environment must be properly configured:

```bash
DOTNET_MODIFIABLE_ASSEMBLIES=debug
```

This environment variable must be set before the application is launched.

## 3. Example F# Code

For clarity, we will use a simple F# example throughout this specification:

### Original F# Code (Version 0)

```fsharp
namespace TestApp

type SimpleLib =
    static member GetValue() = 42
```

### Updated F# Code (Version 1)

```fsharp
namespace TestApp

type SimpleLib =
    static member GetValue() = 43
```

## 4. Components of a Delta Update

### 4.1 Delta Structure

The delta consists of three primary components, all provided as byte arrays to the `MetadataUpdater.ApplyUpdate` method:

```fsharp
type Delta = {
    ModuleId: Guid               // Must match the target assembly's ModuleVersionId
    MetadataDelta: byte[]        // Metadata tables in ECMA-335 format
    ILDelta: byte[]              // IL instructions with proper header
    PdbDelta: byte[]             // Debug information in Portable PDB format
    UpdatedMethods: int[]        // Tokens of methods being updated
    UpdatedTypes: int[]          // Tokens of types being updated
}
```

## 5. Metadata Delta Format

The metadata delta must conform to the ECMA-335 CLI metadata format with specific adaptations for EnC (Edit and Continue) scenarios.

### 5.1 Metadata Heap Offsets

A critical requirement for successful delta generation is to use the correct heap offsets from the baseline assembly:

```fsharp
// Read baseline heap sizes
let userStringHeapSize = reader.GetHeapSize(HeapIndex.UserString)
let stringHeapSize = reader.GetHeapSize(HeapIndex.String)
let blobHeapSize = reader.GetHeapSize(HeapIndex.Blob)
let guidHeapSize = reader.GetHeapSize(HeapIndex.Guid)

// Create MetadataBuilder with these offsets
let deltaBuilder = MetadataBuilder(
    userStringHeapStartOffset = userStringHeapSize,
    stringHeapStartOffset = stringHeapSize,
    blobHeapStartOffset = blobHeapSize,
    guidHeapStartOffset = guidHeapSize)
```

These offsets ensure that all references in the delta metadata correctly resolve against the baseline assembly.

### 5.2 Module Table

The `Module` table in a delta requires specific values for its generation-tracking columns: `Generation`, `EncId` (Edit and Continue ID), and `EncBaseId` (Edit and Continue Base ID).

-   **`Generation`**: Incremented for each delta (1 for the first delta, 2 for the second, etc.).
-   **`EncId`**: A new, unique GUID generated for each specific delta generation.
-   **`EncBaseId`**: Links the delta to the previous generation. Our interpretation, confirmed by analyzing C# deltas using `mdv`, is as follows:
    -   For the **first delta (Generation 1)** applied to a baseline assembly, `EncBaseId` **must be null**.
    -   For **subsequent deltas (Generation 2+)**, `EncBaseId` **must be the `EncId` of the immediately preceding delta generation** (e.g., Gen 2's `EncBaseId` is Gen 1's `EncId`).

This specification and the accompanying `DeltaGenerator.fs` example focus on generating the *first* delta (Generation 1).

```fsharp
// Create required GUIDs
let mvidHandle = deltaBuilder.GetOrAddGuid(moduleId) // Baseline MVID
let deltaEncId = Guid.NewGuid()                      // New unique GUID for this delta (Gen 1)
let encIdHandle = deltaBuilder.GetOrAddGuid(deltaEncId)

// Determine EncBaseId based on generation
let encBaseIdHandle = 
    match generation, previousEncId with // Assuming generation=1, previousEncId=None here
    | 1, _ -> 
        // Generation 1: EncBaseId MUST be null.
        GuidHandle() // Use NULL handle
    | g when g > 1, Some prevId ->
        // Generation 2+: Use previous generation's EncId.
        deltaBuilder.GetOrAddGuid(prevId) 
    | _, _ -> 
        // Handle error/unexpected cases
        GuidHandle() 

// Add Module row
deltaBuilder.AddModule(
    1,                 // Generation number (1 for the first delta)
    moduleNameHandle,  // From baseline
    mvidHandle,        // Baseline MVID
    encIdHandle,       // EncId for this delta (Gen 1)
    encBaseIdHandle)   // NULL EncBaseId for Gen 1
|> ignore
```

### 5.3 Method Attributes

When updating a method, match the original attributes but add `HideBySig` if the method is static:

```fsharp
let methodAttributes =
    if baselineMethodAttributes.HasFlag(MethodAttributes.Static) then
        // Add HideBySig flag to match C# method attributes
        baselineMethodAttributes ||| MethodAttributes.HideBySig
    else
        baselineMethodAttributes
```

### 5.4 Method Row Relative Virtual Address (RVA)

When adding the `MethodDef` row, set the `bodyOffset` (RVA) to 4:

```fsharp
let methodDefHandle = deltaBuilder.AddMethodDefinition(
    attributes = methodAttributes,
    implAttributes = baselineImplAttributes,
    name = methodNameHandle,
    signature = signatureBlobHandle,
    bodyOffset = 4,             // CRITICAL: RVA must be 4, not 1 or other values
    parameterList = ParameterHandle())
```

The value 4 correlates with the 4-byte padding at the start of the IL delta.

### 5.5 EnC Tables

The Edit and Continue (EnC) tables must record all entities involved in the update:

```fsharp
// For AssemblyRef added in delta
deltaBuilder.AddEncLogEntry(assemblyRefEntityHandle, EditAndContinueOperation.Default)
deltaBuilder.AddEncMapEntry(assemblyRefEntityHandle)

// For TypeRef added in delta
deltaBuilder.AddEncLogEntry(typeRefEntityHandle, EditAndContinueOperation.Default)
deltaBuilder.AddEncMapEntry(typeRefEntityHandle)

// For baseline MethodDef being updated
let methodEntityHandle = MetadataTokens.EntityHandle(methodToken)
deltaBuilder.AddEncLogEntry(methodEntityHandle, EditAndContinueOperation.Default)
deltaBuilder.AddEncMapEntry(methodEntityHandle)
```

All entities in the delta must be logged, including both new entities (`AssemblyRef`, `TypeRef`) and entities being updated (`MethodDef`).

## 6. IL Delta Format

The IL delta format is critical for successful hot reload. The format must exactly match the C# delta format.

### 6.1 IL Delta Structure

The IL delta for a method update must have this exact format:

```text
[4-byte padding][IL Method Header][IL Instructions]
```

More specifically:

1. 4 null bytes (`0x00`, `0x00`, `0x00`, `0x00`)
2. Method header (tiny or fat format as per ECMA-335)
3. IL instructions

### 6.2 Tiny Method Format

For methods with less than 64 bytes of IL and no local variables or exception handlers:

```fsharp
// 4-byte padding
ilBuilder.WriteInt32(0)  // Four null bytes: 00 00 00 00

// Constants from ECMA-335 specification
let ILTinyFormat = 0x02uy     // Format flag for tiny method body
let ILTinyFormatSizeShift = 2 // Number of bits to shift the size

// Tiny format header: (codeSize << 2) | 0x02
let headerByte = byte ((codeSize <<< ILTinyFormatSizeShift) ||| int ILTinyFormat)

// Write header followed by IL instructions
ilBuilder.WriteByte(headerByte)
ilBuilder.WriteBytes(ilInstructions)
```

For example, with the IL code `ldc.i4.s 43; ret` (3 bytes: `0x1F`, `0x2B`, `0x2A`), the header would be:
`(3 << 2) | 0x2 = 0x0E`

### 6.3 Fat Method Format

For methods with 64+ bytes of IL, locals, or exception handlers:

```fsharp
// 4-byte padding
ilBuilder.WriteInt32(0)

// Fat format header (12 bytes total)
ilBuilder.WriteUInt16(0x3003us) // Flags=0x03 (fat format) | Size=0x30 (3 dwords = 12 bytes)
ilBuilder.WriteUInt16(8us)      // MaxStack
ilBuilder.WriteInt32(codeSize)  // CodeSize
ilBuilder.WriteInt32(0)         // LocalVarSigTok (0 = no locals)

// Write IL instructions
ilBuilder.WriteBytes(ilInstructions)
```

### 6.4 Complete IL Delta Example

For our example where we change the return value from 42 to 43:

**IL Instruction Changes:**

- Original: `ldc.i4.s 42` (`0x1F`, `0x2A`); `ret` (`0x2A`)
- Updated: `ldc.i4.s 43` (`0x1F`, `0x2B`); `ret` (`0x2A`)

**IL Delta Hex Dump:**

```hex
00 00 00 00 0E 1F 2B 2A
```

Breakdown:
- `00 00 00 00`: 4-byte padding (RVA offset)
- `0E`: Tiny method header `((3 << 2) | 0x2)`
- `1F 2B`: `ldc.i4.s 43` instruction
- `2A`: `ret` instruction

## 7. PDB Delta Format

The PDB delta contains debugging information and must be in the Portable PDB format.

### 7.1 PDB Structure

```fsharp
// Create document entry
let documentName = debugMetadataBuilder.GetOrAddDocumentName("SimpleTest.fs")
let languageGuid = debugMetadataBuilder.GetOrAddGuid(Guid("3F5162F8-07C6-11D3-9053-00C04FA302A1")) // F# language GUID
let hashAlgorithm = debugMetadataBuilder.GetOrAddGuid(Guid("8829d00f-11b8-4213-878b-770e8597ac16")) // SHA256
let documentHandle = debugMetadataBuilder.AddDocument(
    documentName,
    hashAlgorithm,
    debugMetadataBuilder.GetOrAddBlob(Array.empty), // No hash
    languageGuid)

// Add method debug information
let methodHandle = MetadataTokens.MethodDefinitionHandle(methodToken % 0x01000000)
let emptySequencePoints = debugMetadataBuilder.GetOrAddBlob(BlobBuilder())

debugMetadataBuilder.AddMethodDebugInformation(
    documentHandle,
    emptySequencePoints) |> ignore

// For EnC PDB deltas, certain tables must have zero rows
let rowCountsArray = Array.zeroCreate<int>(64)
let methodTableIndex = 6  // MethodDef table index
rowCountsArray.[methodTableIndex] <- MetadataTokens.GetRowNumber(methodHandle)
let documentTableIndex = 48  // Document table index
rowCountsArray.[documentTableIndex] <- 0  // Must be 0 for EnC deltas
let methodDebugInfoTableIndex = 49  // MethodDebugInformation table index
rowCountsArray.[methodDebugInfoTableIndex] <- 0  // Must be 0 for EnC deltas

let typeSystemRowCounts = ImmutableArray.Create<int>(rowCountsArray)

// Create PDB builder and serialize
let pdbBuilder = new PortablePdbBuilder(
    debugMetadataBuilder,
    typeSystemRowCounts,
    MethodDefinitionHandle(),  // No entry point
    null)  // No ID provider

pdbBuilder.Serialize(pdbBlob) |> ignore
```

### 7.2 PDB Delta Requirements

1. The `Document` table and `MethodDebugInformation` table must have zero rows (as shown in the row counts)
2. Language GUID must be correct for F# (`3F5162F8-07C6-11D3-9053-00C04FA302A1`)
3. Even for methods with no sequence points, a valid empty blob must be provided

## 8. Applying the Delta

Once the delta components are generated, they are applied to the running assembly:

```csharp
// Apply the delta
MetadataUpdater.ApplyUpdate(
    assembly,                  // Target assembly to update
    metadataDelta.AsSpan(),    // Metadata changes
    ilDelta.AsSpan(),          // IL code changes
    pdbDelta.AsSpan())         // Debug info changes
```

## 9. Complete Delta Generation Process

### 9.1 Reading Baseline Information

```fsharp
// Open baseline assembly
use fs = File.OpenRead(baselineDllPath)
use peReader = new PEReader(fs)
let reader = peReader.GetMetadataReader()

// Get module name and MVID
let moduleDef = reader.GetModuleDefinition()
let moduleNameString = reader.GetString(moduleDef.Name)
let moduleId = reader.GetGuid(moduleDef.Mvid)

// Get method attributes
let methodDefHandle = MetadataTokens.MethodDefinitionHandle(methodToken)
let methodDef = reader.GetMethodDefinition(methodDefHandle)
let methodAttributes = methodDef.Attributes
let methodImplAttributes = methodDef.ImplAttributes
```

### 9.2 Building the Delta Components

```fsharp
// 1. Generate metadata delta
let metadataDelta = generateMetadataDelta(
    baselineDllPath,
    methodToken,
    declaringTypeToken,
    moduleId)

// 2. Generate IL delta
let ilDelta = generateILDelta(newReturnValue)

// 3. Generate PDB delta
let pdbDelta = generatePDBDelta(methodToken)

// 4. Apply the update
MetadataUpdater.ApplyUpdate(
    assembly,
    metadataDelta.AsSpan(),
    ilDelta.AsSpan(),
    pdbDelta.AsSpan())
```

## 10. Complete Example Delta Hexdump

### 10.1 Metadata Delta Hexdump

For our example F# code update, the metadata delta would be (showing first 256 bytes):

```hex
42 53 4A 42 01 00 01 00 00 00 00 00 0C 00 00 00 76 34 2E 30 2E 33 30 33 31 39 00 00 00 00 06 00
7C 00 00 00 A4 00 00 00 23 2D 00 00 20 01 00 00 30 00 00 00 23 53 74 72 69 6E 67 73 00 00 00 00
50 01 00 00 04 00 00 00 23 55 53 00 54 01 00 00 30 00 00 00 23 47 55 49 44 00 00 00 84 01 00 00
10 00 00 00 23 42 6C 6F 62 00 00 00 94 01 00 00 00 00 00 00 23 4A 54 44 00 00 00 00 00 00 00 00
02 00 A7 01 43 00 00 C0 08 00 00 00 EA 01 33 00 16 00 00 01 00 00 00 01 00 00 00 01 00 00 00 03
00 00 00 03 00 00 00 01 00 00 00 01 00 00 00 70 02 00 00 02 00 00 00 03 00 00 00 00 00 00 00 06
00 00 00 7D 02 00 00 76 02 00 00 04 00 00 00 00 00 96 00 67 02 00 00 62 00 00 00 00 00 00 00 01
00 00 23 00 00 00 00 01 00 00 01 00 00 00 00 01 00 00 06 00 00 00 00 01 00 00 23 01 00 00 01 01
```

Breakdown of key structures:
- Bytes 0-3: "BSJB" signature
- Bytes 4-11: Version information
- Bytes 12-15: Size of metadata header
- Bytes 16-30: ".NET Runtime" version string
- Bytes 31-68: Stream headers (5 streams: `#~`, `#Strings`, `#US`, `#GUID`, `#Blob`)
- Bytes 69+: Metadata tables including `Module`, `AssemblyRef`, `TypeRef`, `MethodDef`, `EncLog`, and `EncMap`

### 10.2 IL Delta Hexdump

```hex
00 00 00 00 0E 1F 2B 2A
```

Breakdown:
- Bytes 0-3: Four null bytes (padding)
- Byte 4: Tiny method header (`0x0E = (3 << 2) | 0x2`)
- Bytes 5-7: IL instructions (`ldc.i4.s 43`; `ret`)

### 10.3 PDB Delta Hexdump

First 64 bytes of the PDB delta:

```hex
42 53 4A 42 01 00 01 00 00 00 00 00 0C 00 00 00 50 44 42 20 76 31 2E 30 00 00 00 00 00 00 06 00
7C 00 00 00 36 00 00 00 23 50 64 62 00 00 00 00 A0 00 00 00 30 00 00 00 23 7E 00 00 D0 00 00 00
```

This follows the standard Portable PDB format with specific adaptations for EnC scenarios.

## 11. Diagnostics and Verification

For diagnostic purposes, the delta files should be written to disk for inspection with the `mdv` tool:

```fsharp
// Write delta files to disk
File.WriteAllBytes(Path.Combine(deltaDir, "1.meta"), metadataDelta.ToArray())
File.WriteAllBytes(Path.Combine(deltaDir, "1.il"), ilDelta.ToArray())
File.WriteAllBytes(Path.Combine(deltaDir, "1.pdb"), pdbDelta.ToArray())
```

```bash
# Run mdv analysis
mdv <baselineDllPath> '/g:1.meta;1.il'
```

The `mdv` tool output should not show `<bad metadata>` and should correctly display the method's updated IL code.

## 12. F#-Specific Considerations

### 12.1 Type and Member Encoding

F# generates different metadata compared to C#, particularly for:

1. Static classes: F# uses `Abstract, Sealed` instead of C#'s `Abstract, Sealed, BeforeFieldInit`
2. Type layout: F# uses `AutoLayout` while C# often uses `SequentialLayout`
3. Custom attributes: F# adds its own attributes like `AbstractClassAttribute` and `CompilationMappingAttribute`

### 12.2 Method Implementation Patterns

F# has different method implementation patterns:

1. Regular methods: Direct IL implementation
2. InvokeStub methods: For methods with closures or captures
3. Compiler-generated methods: For discriminated unions, computation expressions, etc.

When updating F# methods, it's essential to identify the correct method to update:

```fsharp
// Look for both regular and compiler-generated methods
let methods =
    assembly.GetTypes()
    |> Array.collect (fun t ->
        t.GetMethods(BindingFlags.Public ||| BindingFlags.NonPublic ||| BindingFlags.Static)
        |> Array.filter (fun m ->
            m.Name.Contains(methodName) ||
            (m.Name.Contains("InvokeStub") && m.DeclaringType.Name.Contains(methodName))))
```

## 13. Complete F# Delta Generation Implementation

Below is a complete implementation for generating the *first* delta (Generation 1) to update an F# method. Extending this to handle subsequent generations would require tracking the `EncId` of the previous delta and passing it to `generateMetadataDelta`.

```fsharp
let generateDelta (assembly: Assembly) (methodToken: int) (typeToken: int) (newReturnValue: int) =
    // Step 1: Read baseline information
    let baselineDllPath = assembly.Location
    let moduleId = assembly.ManifestModule.ModuleVersionId

    // Assume we are generating the first delta (Gen 1)
    let generation = 1
    let previousEncId = None 

    // Step 2: Generate metadata delta (passing generation info)
    let metadataDelta = generateMetadataDelta 
        baselineDllPath
        methodToken
        declaringTypeToken
        moduleId
        generation 
        previousEncId

    // Step 3: Generate IL delta
    let ilDelta =
        let ilBuilder = new BlobBuilder()

        // Generate IL instructions
        let ilInstructions = [| 0x1Fuy; byte newReturnValue; 0x2Auy |] // ldc.i4.s <value>; ret
        let codeSize = ilInstructions.Length

        // 4-byte padding
        ilBuilder.WriteInt32(0)

        // Tiny format header
        let headerByte = byte ((codeSize <<< 2) ||| 0x2)
        ilBuilder.WriteByte(headerByte)

        // IL instructions
        ilBuilder.WriteBytes(ilInstructions)

        ilBuilder.ToImmutableArray()

    // Step 4: Generate PDB delta
    let pdbDelta =
        let debugBuilder = new MetadataBuilder()

        // Add document
        let docNameHandle = debugBuilder.GetOrAddDocumentName("SimpleTest.fs")
        let fsharpLangGuid = debugBuilder.GetOrAddGuid(Guid("3F5162F8-07C6-11D3-9053-00C04FA302A1"))
        let hashAlgoGuid = debugBuilder.GetOrAddGuid(Guid("8829d00f-11b8-4213-878b-770e8597ac16"))

        let docHandle = debugBuilder.AddDocument(
            docNameHandle,
            hashAlgoGuid,
            debugBuilder.GetOrAddBlob(Array.empty),
            fsharpLangGuid)

        // Add method debug info with no sequence points
        let methodHandle = MetadataTokens.MethodDefinitionHandle(methodToken % 0x01000000)
        let emptySeqPoints = debugBuilder.GetOrAddBlob(BlobBuilder())

        debugBuilder.AddMethodDebugInformation(docHandle, emptySeqPoints) |> ignore

        // Create row counts array with specific zeros
        let rowCounts = Array.zeroCreate<int>(64)
        rowCounts.[6] <- MetadataTokens.GetRowNumber(methodHandle) // MethodDef count
        rowCounts.[48] <- 0 // Document must be 0
        rowCounts.[49] <- 0 // MethodDebugInfo must be 0

        let typeSystemRowCounts = ImmutableArray.Create<int>(rowCounts)

        // Create PDB builder
        let pdbBlob = new BlobBuilder()
        let pdbBuilder = new PortablePdbBuilder(
            debugBuilder,
            typeSystemRowCounts,
            MethodDefinitionHandle(),
            null)

        pdbBuilder.Serialize(pdbBlob)
        pdbBlob.ToImmutableArray()

    // Return the complete delta
    {
        ModuleId = moduleId
        MetadataDelta = metadataDelta
        ILDelta = ilDelta
        PdbDelta = pdbDelta
        UpdatedMethods = ImmutableArray.Create<int>(methodToken)
        UpdatedTypes = ImmutableArray.Create<int>(typeToken)
    }
```

## 14. Conclusion

This specification provides a comprehensive guide for generating metadata, IL, and PDB deltas to support hot reload functionality for F# code in the .NET runtime. By following these guidelines and matching the exact format expected by the runtime, developers can implement reliable hot reload capabilities for F# applications.

The key requirements are:
1. Proper heap offsets in metadata
2. Correct `EncBaseId` handling in the `Module` table:
    * **Null `EncBaseId`** for the *first delta generation* (Gen 1).
    * **Previous generation's `EncId`** used as `EncBaseId` for *subsequent generations* (Gen 2+). (Verified via C# delta analysis).
3. Method RVA value of 4
4. Adding `HideBySig` to static methods
5. IL delta with 4-byte padding and correct IL method header
6. PDB delta with properly zeroed tables

Adherence to these specifications will ensure compatibility with the .NET runtime's hot reload functionality.

## 15. References
