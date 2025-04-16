# .NET Hot Reload Delta Format Details

This document outlines the technical format of the deltas used by the .NET Hot Reload mechanism, specifically focusing on the Metadata, IL, and PDB deltas applied via `System.Reflection.Metadata.MetadataUpdater.ApplyUpdate`. Information is synthesized from Roslyn source code (primarily `EmitHelpers.cs`, `CSharpCompilation.cs`, `DeltaMetadataWriter.cs`, and related files) and existing F# Hot Reload investigation notes.

## Overall Structure

Hot Reload updates are packaged into a structure containing separate binary blobs for metadata changes, IL changes, and PDB changes. The core runtime API `MetadataUpdater.ApplyUpdate` takes these as `ReadOnlySpan<byte>` arguments:

```csharp
public static void ApplyUpdate(
    Assembly assembly,
    ReadOnlySpan<byte> metadataDelta,
    ReadOnlySpan<byte> ilDelta,
    ReadOnlySpan<byte> pdbDelta)
```

The `EmitDifferenceResult` in Roslyn encapsulates the output of the delta generation process, which includes these byte arrays along with metadata tokens for updated methods and types. The F# `Delta` type mirrors this:

```fsharp
type Delta = {
    ModuleId: Guid // Corresponds to the target assembly's ManifestModule.ModuleVersionId
    MetadataDelta: ImmutableArray<byte>
    ILDelta: ImmutableArray<byte>
    PdbDelta: ImmutableArray<byte>
    UpdatedTypes: ImmutableArray<int> // Metadata tokens of updated types
    UpdatedMethods: ImmutableArray<int> // Metadata tokens of updated methods
}
```

The `ModuleId` must match the `ModuleVersionId` of the assembly being updated. The `UpdatedTypes` and `UpdatedMethods` arrays contain the metadata tokens (handles) of the types and methods that were modified in this delta.

## Hot Reload Delta Generation Workflow

Before diving into format details, here's the complete workflow for generating deltas based on the Roslyn implementation in `EmitHelpers.EmitDifference`:

1. **Setup**:
   - Create diagnostics bag
   - Configure emit options (matching baseline PDB format)
   - Get module serialization properties using the baseline ModuleVersionId
   - Verify HotReloadException type availability

2. **Symbol Mapping Setup**:
   - Hydrate symbols from initial metadata (critical for consistency)
   - Create symbol matchers between:
     - Source symbols → metadata symbols 
     - Previous source symbols → metadata symbols
     - Current source symbols → previous source symbols

3. **Create Definition Map and Changes**:
   - Create definition map tracking all changes
   - Create symbol changes from edits and added symbols

4. **Create and Populate Delta Assembly**:
   - Instantiate delta assembly builder
   - Compile only changed symbols
   - Serialize delta to streams (metadata, IL, PDB)

5. **Return Updated Baseline**:
   - Return EmitDifferenceResult with updated baseline and updated method handles

## Critical Implementation Considerations for F#

Before diving into format details, here are critical considerations when implementing hot reload for F#:

1. **ModuleId Matching**: The most common source of hot reload failures is when the ModuleId doesn't exactly match the target assembly's ModuleVersionId. Even a slight mismatch will cause the runtime to silently reject the update.

2. **Token Stability**: For hot reload to work correctly, the metadata tokens of updated methods and types must remain stable across recompilations. F# compiler should preserve token values for existing methods between compilations.

3. **Delta Minimization**: Effective hot reload generates the smallest possible delta containing only changed methods. Larger deltas have higher likelihood of rejection by the runtime.

4. **Error Detection**: MetadataUpdater.ApplyUpdate doesn't throw exceptions on failure. Implement verification mechanisms to confirm updates were applied.

5. **Debugging Experience**: Users expect hot reload to maintain debugging state - breakpoints, locals, watch windows. This requires careful PDB generation.

## 1. Metadata Delta Format

The metadata delta is essentially a minimal valid PE metadata section containing only the changes required for the update. It follows the ECMA-335 metadata structure.

### 1.1. Metadata Builder Configuration

Based on Roslyn's implementation, a `MetadataBuilder` must be properly configured for delta generation:

```csharp
private static MetadataBuilder MakeTablesBuilder(EmitBaseline previousGeneration)
{
    // Create a new metadata builder
    var builder = new MetadataBuilder();
    
    // Configure for delta generation - allocate space for ENC tables
    builder.SetCapacity(TableIndex.EncLog, 64);
    builder.SetCapacity(TableIndex.EncMap, 64);
    
    return builder;
}
```

### 1.2. Metadata Header ("BSJB" Header)

The metadata delta stream begins with a standard metadata header:

| Offset | Size (bytes) | Field                | Description / Example Value                                 |
| :----- | :----------- | :------------------- | :---------------------------------------------------------- |
| 0      | 4            | Signature            | `0x42534A42` ('BSJB')                                       |
| 4      | 2            | Major Version        | `1`                                                         |
| 6      | 2            | Minor Version        | `1` (or `0` depending on tooling?)                           |
| 8      | 4            | Reserved             | `0`                                                         |
| 12     | 4            | Version String Length| Length of the version string + null terminator, padded to 4 |
| 16     | Variable     | Version String       | UTF8 encoded, null-terminated (e.g., "v4.0.30319 ")         |
| Variable| Variable     | Padding              | Zero bytes to align to 4-byte boundary                      |
| Variable| 2            | Flags                | Reserved, `0`                                               |
| Variable| 2            | Number of Streams    | Count of metadata streams present in the delta              |

### 1.3. Stream Headers

Following the main header are the stream headers for each metadata stream included in the delta. The key streams involved in Hot Reload deltas are typically `#~` (or `#-` for compressed metadata) and potentially `#Strings`, `#Blob`, `#GUID`, `#US` if new entries are added to them. The `#Schema` stream (`#~` or `#-`) contains the actual metadata table changes.

Each stream header has the format:

| Offset | Size (bytes) | Field     | Description                                       |
| :----- | :----------- | :-------- | :------------------------------------------------ |
| 0      | 4            | Offset    | Offset of the stream data from the start of the metadata header |
| 4      | 4            | Size      | Size of the stream data in bytes                  |
| 8      | Variable     | Name      | Null-terminated stream name (e.g., "#~ ", "#Strings ") |
| Variable| Variable     | Padding   | Zero bytes to align to 4-byte boundary            |

**Important:** The delta **must** contain headers for *all* standard streams (`#~`/`#-`, `#Strings`, `#US`, `#GUID`, `#Blob`), even if those streams are empty (Size = 0) in the delta itself. This is handled automatically by the `MetadataBuilder`.

### 1.4. Required Metadata Tables

Based on the Roslyn implementation, the following tables are essential for delta generation:

1. **`ENCLog` Table**: Records operations performed on specific tokens. Each entry contains:
   - Token: The metadata token being updated (e.g., method, type)
   - FuncCode: The operation (typically `EditAndContinueOperation.Default`)

2. **`ENCMap` Table**: Maps tokens from previous generation to current. Each entry contains:
   - Token: The metadata token being mapped

3. **Other Modified Tables**: Depending on the changes, may include:
   - `TypeDef`: For added/updated types
   - `MethodDef`: For added/updated methods
   - `Field`: For new fields
   - `Property`: For new properties
   - `Event`: For new events
   - `Param`: For parameters of new methods
   - `CustomAttribute`: For any attributes on new entities

### 1.5. ENCLog and ENCMap Population

From `DeltaMetadataWriter.cs`, here's how the ENC tables are populated:

```csharp
// PopulateEncLogTableRows adds entries to the ENCLog table
private void PopulateEncLogTableRows(ImmutableArray<int> rowCounts, ArrayBuilder<int> paramEncMapRows)
{
    // Add TypeDef entries
    foreach (var typeDef in _typeDefs.GetRows())
    {
        metadata.AddEncLogEntry(
            MetadataTokens.TypeDefinitionHandle(_typeDefs.GetRowId(typeDef)),
            (EditAndContinueOperation)0);  // Default operation
    }
    
    // Add Method entries
    foreach (var methodDef in _methodDefs.GetRows())
    {
        metadata.AddEncLogEntry(
            MetadataTokens.MethodDefinitionHandle(_methodDefs.GetRowId(methodDef)),
            (EditAndContinueOperation)0);
    }
    
    // Similar entries for fields, events, properties, etc.
}

// PopulateEncMapTableRows adds entries to the ENCMap table
private void PopulateEncMapTableRows(ImmutableArray<int> rowCounts, ArrayBuilder<int> paramEncMapRows)
{
    // Map TypeDef tokens
    foreach (var typeDef in _typeDefs.GetRows())
    {
        metadata.AddEncMapEntry(
            MetadataTokens.TypeDefinitionHandle(_typeDefs.GetRowId(typeDef)));
    }
    
    // Map Method tokens
    foreach (var methodDef in _methodDefs.GetRows())
    {
        metadata.AddEncMapEntry(
            MetadataTokens.MethodDefinitionHandle(_methodDefs.GetRowId(methodDef)));
    }
    
    // Similar mappings for fields, events, properties, etc.
}
```

### 1.6. MetadataBuilder API for Delta Generation

Here's how MetadataBuilder is used specifically for generating deltas:

```csharp
// Create a MetadataBuilder
MetadataBuilder metadataBuilder = new MetadataBuilder();

// Set capacity for ENC tables
metadataBuilder.SetCapacity(TableIndex.EncLog, 64);
metadataBuilder.SetCapacity(TableIndex.EncMap, 64);

// For each updated method (required for all changes):
metadataBuilder.AddEncLogEntry(
    MetadataTokens.MethodDefinitionHandle(methodTokenRowId),
    EditAndContinueOperation.Default);

metadataBuilder.AddEncMapEntry(
    MetadataTokens.MethodDefinitionHandle(methodTokenRowId));

// When adding new types (if applicable):
metadataBuilder.AddEncLogEntry(
    MetadataTokens.TypeDefinitionHandle(typeTokenRowId),
    EditAndContinueOperation.Default);

metadataBuilder.AddEncMapEntry(
    MetadataTokens.TypeDefinitionHandle(typeTokenRowId));

// Add any relevant CustomAttributes
EntityHandle parentHandle = MetadataTokens.MethodDefinitionHandle(methodTokenRowId);
BlobHandle signature = metadataBuilder.GetOrAddBlob(customAttributeSignatureBytes);
metadataBuilder.AddCustomAttribute(parentHandle, attributeCtor, signature);

// Serialize using MetadataRootBuilder
MetadataRootBuilder rootBuilder = new MetadataRootBuilder(metadataBuilder);
BlobBuilder blobBuilder = new BlobBuilder();
rootBuilder.Serialize(blobBuilder, metadataStreamVersion: 1, 0);

// The result is in blobBuilder
```

### 1.7. F# Metadata Delta Implementation Example

```fsharp
// Example approach for building metadata delta in F#
let buildMetadataDelta (methodToken: int) =
    let metadataBuilder = MetadataBuilder()
    
    // Set capacity for ENC tables
    metadataBuilder.SetCapacity(TableIndex.EncLog, 64)
    metadataBuilder.SetCapacity(TableIndex.EncMap, 64)
    
    // Add the method update to ENCLog
    metadataBuilder.AddEncLogEntry(
        MetadataTokens.MethodDefinitionHandle(methodToken),
        EditAndContinueOperation.Default)
    
    // Add method to ENCMap
    metadataBuilder.AddEncMapEntry(
        MetadataTokens.MethodDefinitionHandle(methodToken))
    
    // Create metadata root and serialize
    let rootBuilder = MetadataRootBuilder(metadataBuilder)
    let metadataBlob = BlobBuilder()
    
    // Important: metadataStreamVersion must be 1 for EnC deltas
    rootBuilder.Serialize(metadataBlob, metadataStreamVersion = 1, 0) 
    
    metadataBlob.ToImmutableArray()
```

**Common Pitfalls**:
- Incorrect stream offsets in manual header construction
- Missing stream headers for the 5 standard streams
- Incorrect alignment/padding of headers
- Wrong metadata version numbers
- Not setting metadataStreamVersion to 1 during serialization
- Not adding both ENCLog and ENCMap entries for the same tokens

## 2. IL Delta Format

The IL delta contains the raw CIL bytecode for the updated method bodies.

### 2.1. Structure

The IL delta is simply a concatenation of the updated method bodies. Each method body consists of:

1.  **Method Header:** Describes the method body's size, max stack depth, local variable signature token, and flags. Common formats include:
    *   **Tiny Format (Flags = `0x02`):** Used for small methods (CodeSize < 64, no locals, maxstack <= 8). The header is 1 byte: `(CodeSize << 2) | 0x02`.
    *   **Fat Format (Flags = `0x03`):** Used for larger methods. Header is 12 bytes.
2.  **Method Body:** The CIL instructions.
3.  **Exception Handling Clauses (Optional):** If the method has try/catch blocks.

### 2.2. Method Body Generation in Roslyn

Method bodies are serialized in `MetadataWriter.SerializeMethodBody`:

```csharp
private int SerializeMethodBody(
    MethodBodyStreamEncoder encoder, 
    IMethodBody methodBody,
    StandaloneSignatureHandle localSignatureHandleOpt, 
    ref UserStringHandle mvidStringHandle, 
    ref Blob mvidStringFixup)
{
    // Create method header based on method characteristics
    var bodyEncoder = methodBody.LocalsCount > 0 || methodBody.MaxStack > 8 || methodBody.ExceptionRegions.Length > 0
        ? encoder.AddMethodBody(
            codeSize: methodBody.IL.Length,
            maxStack: methodBody.MaxStack, 
            exceptionRegionCount: methodBody.ExceptionRegions.Length,
            hasSmallExceptionRegions: MayUseSmallExceptionHeaders(methodBody.ExceptionRegions),
            localVariablesSignature: localSignatureHandleOpt)
        : encoder.AddMethodBody(methodBody.IL.Length);

    // Add IL bytes
    var body = bodyEncoder.CreateBlobBuilder();
    WriteInstructions(body, methodBody.IL, ref mvidStringHandle, ref mvidStringFixup);
    
    // Add exception handlers if present
    if (methodBody.ExceptionRegions.Length > 0)
    {
        var exceptionEncoder = bodyEncoder.ExceptionRegions;
        SerializeMethodBodyExceptionHandlerTable(exceptionEncoder, methodBody.ExceptionRegions);
    }
    
    return bodyEncoder.Offset;
}
```

### 2.3. Tiny vs. Fat Method Format Selection

Roslyn chooses between tiny and fat formats based on method characteristics:

```csharp
// For tiny methods (MaxStack <= 8, no locals, CodeSize < 64, no exceptions):
encoder.AddMethodBody(methodBody.IL.Length);

// For all other methods (fat format):
encoder.AddMethodBody(
    codeSize: methodBody.IL.Length,
    maxStack: methodBody.MaxStack, 
    exceptionRegionCount: methodBody.ExceptionRegions.Length,
    hasSmallExceptionRegions: MayUseSmallExceptionHeaders(methodBody.ExceptionRegions),
    localVariablesSignature: localSignatureHandle);
```

### 2.4. Local Variable Signature Handling

From `DeltaMetadataWriter.SerializeLocalVariablesSignature`:

```csharp
protected override StandaloneSignatureHandle SerializeLocalVariablesSignature(IMethodBody body)
{
    // For methods with locals:
    if (body.LocalsCount > 0)
    {
        var localBuilder = new BlobBuilder();
        
        // Write LOCAL_SIG header (0x07)
        localBuilder.WriteByte(0x07);
        
        // Write local count as compressed integer
        localBuilder.WriteCompressedInteger(body.LocalsCount);
        
        // Write each local's type signature
        foreach (var local in body.Locals)
        {
            SerializeLocalVariableType(new SignatureTypeEncoder(localBuilder), local);
        }
        
        // Create standalone signature
        var localSigBlob = metadata.GetOrAddBlob(localBuilder);
        return metadata.AddStandaloneSignature(localSigBlob);
    }
    
    return default;
}
```

### 2.5. F# IL Delta Implementation Example

```fsharp
// Create IL delta for a method
let createILDelta (ilBytes: byte[]) (hasLocals: bool) (maxStack: int) (localVarSigToken: int option) =
    let ilBlob = BlobBuilder()
    
    // Choose method format
    if not hasLocals && maxStack <= 8 && ilBytes.Length < 64 then
        // Tiny format
        let headerByte = byte ((ilBytes.Length <<< 2) ||| 0x2)
        ilBlob.WriteByte(headerByte)
    else
        // Fat format (requires 12-byte header)
        // See ECMA-335 II.25.4.2 for fat header format
        let flags = 0x3 // Fat format
        let headerSize = 0x3 // 3 DWords = 12 bytes
        let maxStackEncoded = maxStack
        let codeSize = ilBytes.Length
        
        // Create header
        let header = BitConverter.GetBytes((flags ||| (headerSize <<< 2) ||| (maxStackEncoded <<< 8)) ||| 0)
        ilBlob.WriteBytes(header)
        ilBlob.WriteInt32(codeSize)
        
        // Write LocalVarSig token if hasLocals
        if hasLocals && localVarSigToken.IsSome then
            ilBlob.WriteInt32(localVarSigToken.Value)
        else
            ilBlob.WriteInt32(0)
    
    // Write IL bytes
    ilBlob.WriteBytes(ilBytes)
    
    ilBlob.ToImmutableArray()
```

**Tiny Method Format Correction**: According to ECMA-335 (II.25.4.2), a tiny format method header is a single byte where:
- The lowest two bits are set to 0x2
- The size is encoded in the upper 6 bits (size << 2)

So for a method with size 3:
```
Header byte = (3 << 2) | 0x2 = 0xE
```

**Common Issues**:
- If you have local variables, you must use a fat header format
- Method size includes the IL instructions but not the header itself
- For try/catch blocks, you must use a fat header

## 3. PDB Delta Format

The PDB delta contains updates to the debugging information, enabling features like setting breakpoints and stepping through the updated code. It uses the Portable PDB format (#Pdb stream).

### 3.1. PDB Stream Creation

From Roslyn, we can see that it uses `PortablePdbBuilder` for portable PDBs:

```csharp
// From MetadataWriter.GetPortablePdbBuilder
public PortablePdbBuilder GetPortablePdbBuilder(
    ImmutableArray<int> typeSystemRowCounts, 
    MethodDefinitionHandle debugEntryPoint,
    Func<IEnumerable<Blob>, BlobContentId> deterministicIdProviderOpt)
{
    return new PortablePdbBuilder(
        metadata: _debugMetadataOpt, 
        typeSystemRowCounts: typeSystemRowCounts,
        entryPoint: debugEntryPoint, 
        idProvider: deterministicIdProviderOpt);
}
```

### 3.2. Structure

The PDB delta typically contains updates to specific tables within the Portable PDB format, primarily:

1.  **`Document` Table:** References source document names. Deltas might add entries if new documents are involved (unlikely for simple method updates).
2.  **`MethodDebugInformation` Table:** Contains sequence points mapping IL offsets to source locations, and potentially local variable scope information. This is the most frequently updated table for Hot Reload.
3.  **`LocalScope` Table:** Defines lexical scopes for local variables.
4.  **`LocalVariable` Table:** Defines local variables.
5.  **`LocalConstant` Table:** Defines local constants.
6.  **`CustomDebugInformation` Table:** Can contain various types of debug info, such as state machine stepping info or dynamic local variable info.

### 3.3. Sequence Points and Document Creation

PDB generation requires careful tracking of sequence points to map IL offsets to source locations:

```csharp
// Create document info
var documentName = metadataBuilder.GetOrAddDocumentName(sourceFilePath);
var documentLanguage = metadataBuilder.GetOrAddGuid(LanguageGuid);
var documentHashAlgorithm = metadataBuilder.GetOrAddGuid(HashAlgorithmGuid);
var documentHash = metadataBuilder.GetOrAddBlob(hashBytes);

var documentHandle = metadataBuilder.AddDocument(
    documentName,
    hashAlgorithm,
    documentHash,
    documentLanguage);

// Create method debug info with sequence points
var methodHandle = MetadataTokens.MethodDefinitionHandle(methodDef.Handle.RowId);
var sequencePoints = new BlobBuilder();
var encoder = new SequencePointEncoder(sequencePoints);

// Add sequence points
encoder.AddSequencePoint(
    ilOffset: 0,
    startLine: 10,
    startColumn: 1,
    endLine: 10,
    endColumn: 20,
    isHidden: false);

encoder.AddSequencePoint(/*...*/);

// Add method debug information
var methodDebugInfoHandle = metadataBuilder.AddMethodDebugInformation(
    documentHandle,
    sequencePoints.ToImmutableArray());
```

### 3.4. Local Variable Scope Handling

PDB deltas must properly track local variable scopes:

```csharp
// Add local scope
var localScopeHandle = metadataBuilder.AddLocalScope(
    methodDef: methodHandle,
    importScopeHandle: importScope,
    variableList: firstVariableHandle,
    constantList: firstConstantHandle,
    startOffset: 0,
    length: methodBodyLength);

// Add local variables
foreach (var local in locals)
{
    var localVariableHandle = metadataBuilder.AddLocalVariable(
        attributes: LocalVariableAttributes.None,
        index: local.Slot,
        name: metadataBuilder.GetOrAddString(local.Name));
}

// Add local constants if needed
foreach (var constant in constants)
{
    metadataBuilder.AddLocalConstant(
        name: metadataBuilder.GetOrAddString(constant.Name),
        signature: GetLocalConstantSignature(constant));
}
```

### 3.5. F# PDB Delta Implementation Example

```fsharp
// Create PDB delta for a method
let createPdbDelta (methodToken: int) (sourceFile: string) (sequencePoints: SequencePoint[]) =
    let debugMetadataBuilder = MetadataBuilder()
    
    // Add document
    let documentName = debugMetadataBuilder.GetOrAddDocumentName(sourceFile)
    let languageGuid = debugMetadataBuilder.GetOrAddGuid(new Guid("3F5162F8-07C6-11D3-9053-00C04FA302A1")) // GUID_Language_FSharp
    let hashAlgorithm = debugMetadataBuilder.GetOrAddGuid(new Guid("8829d00f-11b8-4213-878b-770e8597ac16")) // SHA256
    let documentHandle = debugMetadataBuilder.AddDocument(
        documentName,
        hashAlgorithm,
        default, // No hash for now
        languageGuid)
    
    // Add sequence points
    let blobBuilder = BlobBuilder()
    let encoder = SequencePointEncoder(blobBuilder)
    
    for sp in sequencePoints do
        encoder.AddSequencePoint(
            sp.ILOffset,
            sp.StartLine,
            sp.StartColumn,
            sp.EndLine,
            sp.EndColumn,
            sp.IsHidden)
    
    // Add method debug information
    let methodHandle = MetadataTokens.MethodDefinitionHandle(methodToken)
    debugMetadataBuilder.AddMethodDebugInformation(
        documentHandle,
        debugMetadataBuilder.GetOrAddBlob(blobBuilder))
    
    // Create portable PDB
    let pdbBlob = BlobBuilder()
    let pdbBuilder = PortablePdbBuilder(
        debugMetadataBuilder,
        ImmutableArray.CreateRange([| methodToken |]), // Just tracking this method
        default, // No entry point
        null) // No deterministic ID provider
    
    pdbBuilder.Serialize(pdbBlob)
    pdbBlob.ToImmutableArray()
```

**WARNING**: PDB delta generation is the most complex part of hot reload implementation. A manual byte construction approach is insufficient for production use.

**Recommended Approaches for F# PDB Generation**:

1. **Use FCS Debug Information Emission**: If the F# Compiler Services can emit debug information for a method, adapt this to produce portable PDB deltas.

2. **Leverage System.Reflection.Metadata.Ecma335 APIs**: Use the MetadataBuilder and debugging metadata APIs to construct proper PDB deltas.

3. **Minimal Approach for Prototyping**: For early prototyping, a minimal PDB delta may allow basic debugging functionality.

## 4. Common Errors and Troubleshooting

When implementing hot reload for F#, you'll likely encounter these issues:

1. **Silent Update Failures**: The runtime may silently reject deltas without errors. Implement verification by comparing the actual function behavior after updates.

2. **ModuleVersionId Mismatch**: Ensure the delta's ModuleId exactly matches the target assembly. Even a single bit difference will cause silent rejection.

3. **Method Token Mismatch**: If tokens in UpdatedMethods don't match actual method tokens in the assembly, updates will fail.

4. **Invalid IL Generation**: Malformed IL or headers will cause silent failures or crashes.

5. **Breaking Changes**: Hot reload cannot handle certain changes like:
   - Adding/removing fields
   - Changing method signatures
   - Changing type hierarchies
   - Adding/removing generic parameters

6. **PDB Delta Debugging Issues**: Without proper sequence points, breakpoints may not bind correctly or stepping may act erratically.

## 5. Testing and Verification

A robust testing approach for F# hot reload implementation:

1. **Verify ModuleId Matching**: Test that your code correctly extracts and matches ModuleVersionIds.

2. **Build Progression Tests**: Test increasingly complex changes:
   - Change a constant/return value
   - Add local variables  
   - Add control flow (if/loops)
   - Add lambda expressions
   - Add exception handling

3. **Debugging Tests**: Verify that after updates:
   - Breakpoints still work
   - Local variables are accessible
   - Step debugging works correctly
   - Watch windows update properly

4. **Boundary Testing**: Test edge cases like:
   - Very large method bodies
   - Methods with many locals
   - Generics and closures
   - Async/Task methods
   - Interface implementations

## Summary and Recommendations for F#

1. **Metadata Delta:** Use System.Reflection.Metadata APIs to generate the entire metadata delta structure, including all required stream headers and correctly formatted/serialized `ENCLog`/`ENCMap` entries. Key points:
   - Use `MetadataBuilder` and `MetadataRootBuilder`
   - Set proper capacities for ENC tables
   - Ensure metadataStreamVersion = 1 during serialization
   - Add entries to both ENCLog and ENCMap tables
   - Ensure ModuleId matches target assembly exactly

2. **IL Delta:** Use the proper method body format based on method characteristics:
   - Tiny format for small methods (no locals, size < 64, stack ≤ 8)
   - Fat format for everything else
   - Correctly serialize local variable signatures if needed
   - Exception handling requires special consideration

3. **PDB Delta:** Use System.Reflection.Metadata.Ecma335 APIs for PDB generation:
   - Create document entries with proper language GUIDs
   - Add sequence points mapping IL offsets to source locations
   - Add local variable scopes and variable information
   - Use PortablePdbBuilder for serialization

**Implementation Strategy**:
1. Start with a prototype that can update simple method bodies
2. Add proper metadata generation using System.Reflection.Metadata
3. Focus on IL generation for common F# patterns
4. Gradually improve PDB generation for better debugging
5. Build validation and error reporting to detect failures

With careful implementation, F# can provide a hot reload experience comparable to C#, enhancing developer productivity with rapid feedback cycles. 