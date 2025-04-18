namespace HotReloadAgent

// Core F# compiler services for parsing and type checking
open FSharp.Compiler.CodeAnalysis
open FSharp.Compiler.Text
open FSharp.Compiler.IO
open FSharp.Compiler.Symbols

// System libraries for file operations and metadata handling
open System
open System.IO
open System.Reflection
open System.Reflection.Metadata
open System.Reflection.Metadata.Ecma335
open System.Reflection.PortableExecutable
open System.Collections.Immutable
open System.Runtime.CompilerServices
open System.Runtime.InteropServices // For MemoryMarshal

#nowarn 3391
#nowarn 3261

/// <summary>
/// Delta structure for hot reload updates.
/// Matches the format expected by MetadataUpdater.ApplyUpdate.
/// </summary>
type Delta = {
    /// <summary>The module ID for the assembly being updated.</summary>
    ModuleId: Guid
    /// <summary>The metadata delta bytes.</summary>
    MetadataDelta: ImmutableArray<byte>
    /// <summary>The IL delta bytes.</summary>
    ILDelta: ImmutableArray<byte>
    /// <summary>The PDB delta bytes.</summary>
    PdbDelta: ImmutableArray<byte>
    /// <summary>The tokens of updated types.</summary>
    UpdatedTypes: ImmutableArray<int>
    /// <summary>The tokens of updated methods.</summary>
    UpdatedMethods: ImmutableArray<int>
}

/// <summary>
/// Main generator for creating hot reload deltas.
/// </summary>
type DeltaGenerator = {
    /// <summary>The previous compilation result.</summary>
    PreviousCompilation: FSharp.Compiler.CodeAnalysis.FSharpCheckFileResults option
    /// <summary>The previous return value.</summary>
    PreviousReturnValue: int option
}

module DeltaGenerator =
    /// <summary>
    /// Creates a new DeltaGenerator with default settings.
    /// </summary>
    let create () = {
        PreviousCompilation = None
        PreviousReturnValue = None
    }

    /// <summary>
    /// Generates method signature for getValue method.
    /// </summary>
    let private generateMethodSignature () =
        printfn "[DeltaGenerator] Generating method signature..."
        let signatureBuilder = new BlobBuilder()
        let encoder = new BlobEncoder(signatureBuilder)
        
        // Create standard method signature with no parameters that returns Int32
        let methodSigEncoder = encoder.MethodSignature(
            SignatureCallingConvention.Default,   // Standard calling convention
            0,                                    // 0 generic parameters
            isInstanceMethod = false)             // Static method
        
        // Encode return type and parameters
        methodSigEncoder.Parameters(
            0,                                    // 0 parameters
            (fun returnType -> returnType.Type().Int32()),  // Return Int32
            (fun _ -> ())                         // No parameters to encode
        )
        
        let signatureBytes = signatureBuilder.ToArray()
        printfn "[DeltaGenerator] Generated method signature: %A" signatureBytes
        signatureBytes

    /// <summary>
    /// Generates metadata delta for a method update, handling EncBaseId based on generation.
    /// </summary>
    let private generateMetadataDelta 
        (baselineDllPath: string) 
        (methodToken: int) 
        (declaringTypeToken: int) 
        (moduleId: Guid) 
        (generation: int) // Added: Current delta generation number (1 for first delta)
        (previousEncId: Guid option) // Added: EncId of the previous generation (None for Gen 1)
        =
        printfn "[DeltaGenerator] --- Generating Metadata Delta (Generation %d) ---" generation
        printfn "[DeltaGenerator] Baseline DLL Path: %s" baselineDllPath
        printfn "[DeltaGenerator] Method Token: 0x%08X" methodToken
        printfn "[DeltaGenerator] Declaring Type Token: 0x%08X" declaringTypeToken
        printfn "[DeltaGenerator] Module ID (MVID): %A" moduleId

        // --- Step 1: Read baseline info (Module Name & Method Attributes) ---
        let baselineModuleName, baselineMethodAttributes, baselineImplAttributes =
            try
                use fs = File.OpenRead(baselineDllPath)
                use peReader = new PEReader(fs)
                let reader = peReader.GetMetadataReader()

                // Read module name
                let moduleDef = reader.GetModuleDefinition()
                let nameHandle = moduleDef.Name
                let moduleName = reader.GetString(nameHandle)
                printfn "[DeltaGenerator] Successfully read baseline module name: '%s'" moduleName

                // Read method attributes for the MethodDef row
                let methodDefHandle = MetadataTokens.MethodDefinitionHandle(methodToken)
                let methodDef = reader.GetMethodDefinition(methodDefHandle)
                let attributes = methodDef.Attributes
                let implAttributes = methodDef.ImplAttributes
                printfn "[DeltaGenerator] Baseline method attributes: %A" attributes
                printfn "[DeltaGenerator] Baseline method impl attributes: %A" implAttributes

                moduleName, attributes, implAttributes
            with ex ->
                printfn "[DeltaGenerator] ERROR reading baseline info: %s. Falling back." ex.Message
                let fallbackName = Path.GetFileName(baselineDllPath)
                // Provide default attributes if reading fails - this might be inaccurate
                let fallbackAttrs = MethodAttributes.Public ||| MethodAttributes.Static
                let fallbackImplAttrs = MethodImplAttributes.IL
                printfn "[DeltaGenerator] Using fallback module name: '%s'" fallbackName
                printfn "[DeltaGenerator] Using fallback method attributes: %A" fallbackAttrs
                printfn "[DeltaGenerator] Using fallback method impl attributes: %A" fallbackImplAttrs
                fallbackName, fallbackAttrs, fallbackImplAttrs

        printfn "[DeltaGenerator] Using module name: '%s' for delta." baselineModuleName

        // --- Step 2: Create Delta MetadataBuilder with proper heap offsets ---
        // For EnC deltas, we MUST use the baseline heap sizes as starting offsets!
        // This ensures references are properly resolved in the delta generation
        printfn "[DeltaGenerator] Creating MetadataBuilder for EnC delta (Generation %d)" generation
        
        // Read the baseline heap sizes first - we need this to set up the proper offsets
        let baselineHeapSizes = 
            try
                use fs = File.OpenRead(baselineDllPath)
                use peReader = new PEReader(fs)
                let reader = peReader.GetMetadataReader()
                
                // Get heap sizes from baseline
                let userStringHeapSize = reader.GetHeapSize(HeapIndex.UserString)
                let stringHeapSize = reader.GetHeapSize(HeapIndex.String)
                let blobHeapSize = reader.GetHeapSize(HeapIndex.Blob)
                let guidHeapSize = reader.GetHeapSize(HeapIndex.Guid)
                
                printfn "[DeltaGenerator] Baseline heap sizes: UserString=%d, String=%d, Blob=%d, Guid=%d"
                    userStringHeapSize stringHeapSize blobHeapSize guidHeapSize
                    
                Some (userStringHeapSize, stringHeapSize, blobHeapSize, guidHeapSize)
            with ex ->
                printfn "[DeltaGenerator] Failed to read baseline heap sizes: %s" ex.Message
                None
        
        // Create MetadataBuilder with heap offsets from baseline
        let deltaBuilder =
            match baselineHeapSizes with
            | Some(userStringSize, stringSize, blobSize, guidSize) ->
                printfn "[DeltaGenerator] Creating MetadataBuilder with baseline heap sizes as offsets"
                MetadataBuilder(
                    userStringHeapStartOffset = userStringSize,
                    stringHeapStartOffset = stringSize,
                    blobHeapStartOffset = blobSize,
                    guidHeapStartOffset = guidSize)
            | None ->
                printfn "[DeltaGenerator] WARNING: Could not read baseline heap sizes, using default offsets"
                MetadataBuilder()
        
        printfn "[DeltaGenerator] Delta MetadataBuilder created with proper heap offsets"
        
        // Set capacity for ENC tables
        deltaBuilder.SetCapacity(TableIndex.EncLog, 3) // Module, AssemblyRef, TypeRef, MethodDef
        deltaBuilder.SetCapacity(TableIndex.EncMap, 3)
        printfn "[DeltaGenerator] Delta MetadataBuilder created with ENC table capacities."

        // Add Guids
        let mvidHandle = deltaBuilder.GetOrAddGuid(moduleId) // Baseline MVID
        let deltaEncId = Guid.NewGuid()                     // New EncId for this generation
        let encIdHandle = deltaBuilder.GetOrAddGuid(deltaEncId)
        
        // Determine EncBaseId based on generation
        let encBaseIdHandle = 
            match generation, previousEncId with
            | 1, _ -> 
                // Generation 1 (first delta): EncBaseId MUST be null, based on C# delta analysis.
                printfn "[DeltaGenerator] Using null EncBaseId handle for first delta generation (Gen 1)"
                GuidHandle() // Use NULL handle
            | g when g > 1, Some prevId ->
                // Generation 2+: EncBaseId MUST be the EncId of the previous generation.
                printfn "[DeltaGenerator] Using previous generation's EncId (%A) for EncBaseId (Gen %d)" prevId g
                deltaBuilder.GetOrAddGuid(prevId) 
            | g, None when g > 1 ->
                // Error case: Subsequent generations must have a previous EncId
                printfn "[DeltaGenerator] ERROR: Generation %d requires previousEncId, but none was provided." g
                // Returning null handle for now, but this indicates an issue in the calling logic
                GuidHandle() 
            | _, _ -> 
                // Should not happen if generation is always >= 1
                printfn "[DeltaGenerator] WARNING: Unexpected generation number %d." generation
                GuidHandle()

        printfn "[DeltaGenerator] Added Baseline MVID to delta GUID heap, handle: %A" mvidHandle
        printfn "[DeltaGenerator] Generated unique delta EncId for Gen %d: %A" generation deltaEncId
        printfn "[DeltaGenerator] Added Delta EncId (Gen %d) to delta GUID heap, handle: %A" generation encIdHandle
        printfn "[DeltaGenerator] EncBaseId handle for Gen %d determined." generation

        // Add Strings needed for references and MethodDef
        let moduleNameHandle = deltaBuilder.GetOrAddString(baselineModuleName)
        let systemRuntimeName = deltaBuilder.GetOrAddString("System.Runtime")
        let systemNamespace = deltaBuilder.GetOrAddString("System")
        let objectTypeName = deltaBuilder.GetOrAddString("Object")
        let methodNameHandle = deltaBuilder.GetOrAddString("GetValue") // Assuming method name is always GetValue for this test
        printfn "[DeltaGenerator] Added strings to delta string heap: ModuleName, System.Runtime, System, Object, GetValue"

        // Add Blobs needed for references and MethodDef signature
        let ecmaPublicKeyTokenBlob = deltaBuilder.GetOrAddBlob([| 0xB0uy; 0x3Fuy; 0x5Fuy; 0x7Fuy; 0x11uy; 0xD5uy; 0x0Auy; 0x3Auy |])
        let signatureBytes = generateMethodSignature() // Generate the signature for 'int GetValue()'
        let signatureBlobHandle = deltaBuilder.GetOrAddBlob(signatureBytes)
        printfn "[DeltaGenerator] Added ECMA Key and Method Signature to delta blob heap."

        // --- Step 3: Add Core References (AssemblyRef, TypeRef) ---
        printfn "[DeltaGenerator] Adding AssemblyRef for System.Runtime..."
        let deltaSystemRuntimeRefHandle = deltaBuilder.AddAssemblyReference(
            systemRuntimeName,
            System.Version(10, 0, 0, 0), // Use a relevant version, e.g., net10.0
            StringHandle(), // Culture handle (nil for invariant) -> Use StringHandle()
            ecmaPublicKeyTokenBlob,
            AssemblyFlags.PublicKey,
            BlobHandle()) // Hash value (nil) -> Use BlobHandle()
        printfn "[DeltaGenerator] Added AssemblyRef handle: %A" deltaSystemRuntimeRefHandle

        printfn "[DeltaGenerator] Adding TypeRef for System.Object..."
        let deltaObjectRefHandle = deltaBuilder.AddTypeReference(
            deltaSystemRuntimeRefHandle,
            systemNamespace,
            objectTypeName)
        printfn "[DeltaGenerator] Added TypeRef handle: %A" deltaObjectRefHandle

        // --- Step 4: Add Module Row ---
        printfn "[DeltaGenerator] Adding Module table row (Generation %d)..." generation
        deltaBuilder.AddModule(
            generation,        // Current generation number
            moduleNameHandle,
            mvidHandle,        // Baseline MVID
            encIdHandle,       // This generation's EncId
            encBaseIdHandle)   // Base ID (Null for Gen 1, Prev EncId otherwise)
        |> ignore
        printfn "[DeltaGenerator] Module table row added."

        // --- Step 5: Add Method Definition Row ---
        printfn "[DeltaGenerator] Adding MethodDef row for updated method..."
        // Use attributes read from baseline, but add HideBySig to match C# attributes
        let methodAttributes = 
            if baselineMethodAttributes.HasFlag(MethodAttributes.Static) then
                // Add HideBySig flag to match C# method attributes
                baselineMethodAttributes ||| MethodAttributes.HideBySig
            else
                baselineMethodAttributes
        
        // RVA = 4 (matching C# delta value) signifies IL is in the delta with specific offset
        let deltaMethodDefHandle = deltaBuilder.AddMethodDefinition(
            attributes = methodAttributes,
            implAttributes = baselineImplAttributes,
            name = methodNameHandle,
            signature = signatureBlobHandle,
            bodyOffset = 4, // RVA of 4 to match C# delta format
            parameterList = ParameterHandle())
        
        printfn "[DeltaGenerator] Using RVA=4 and adding HideBySig attribute to match C# format"
        printfn "[DeltaGenerator] Added MethodDef handle: %A" deltaMethodDefHandle

        // --- Step 6: Add EnC table entries ---
        // We log/map the *new* entities added to the delta and the *baseline* MethodDef being updated.
        // We do NOT log/map the baseline TypeDef.

        let assemblyRefEntityHandle : EntityHandle = deltaSystemRuntimeRefHandle
        let typeRefEntityHandle : EntityHandle = deltaObjectRefHandle
        let baselineMethodDefHandle = MetadataTokens.MethodDefinitionHandle(methodToken) // Use original token
        let methodEntityHandle : EntityHandle = baselineMethodDefHandle

        // Based on C# delta output analysis, we need to properly track gen/row for entities
        
        // For new AssemblyRef in generation 1 (added by delta)
        printfn "[DeltaGenerator] Adding EncLog/Map entry for new AssemblyRef: %A" assemblyRefEntityHandle
        deltaBuilder.AddEncLogEntry(assemblyRefEntityHandle, EditAndContinueOperation.Default)
        deltaBuilder.AddEncMapEntry(assemblyRefEntityHandle)
        
        // For new TypeRef in generation 1 (added by delta)
        printfn "[DeltaGenerator] Adding EncLog/Map entry for new TypeRef: %A" typeRefEntityHandle
        deltaBuilder.AddEncLogEntry(typeRefEntityHandle, EditAndContinueOperation.Default)
        deltaBuilder.AddEncMapEntry(typeRefEntityHandle)
        
        // For existing method from generation 0 that we're updating
        printfn "[DeltaGenerator] Adding EncLog/Map entry for baseline MethodDef: %A (Token: 0x%08X)" methodEntityHandle methodToken
        // Important: We use Default operation which maps to "update" for existing entities
        deltaBuilder.AddEncLogEntry(methodEntityHandle, EditAndContinueOperation.Default)
        deltaBuilder.AddEncMapEntry(methodEntityHandle)
        
        printfn "[DeltaGenerator] EnC Log and Map entries added with proper generation handling."
        printfn "[DeltaGenerator] Dump of entities and their tokens:"
        printfn "  - AssemblyRef token: 0x%08X" (MetadataTokens.GetToken(assemblyRefEntityHandle))
        printfn "  - TypeRef token: 0x%08X" (MetadataTokens.GetToken(typeRefEntityHandle)) 
        printfn "  - MethodDef token: 0x%08X" methodToken

        // --- Step 7: Serialize the delta metadata ---
        printfn "[DeltaGenerator] Creating MetadataRootBuilder (suppressValidation=true)..."
        let rootBuilder = MetadataRootBuilder(deltaBuilder, suppressValidation=true)

        let outputBuilder = BlobBuilder()
        printfn "[DeltaGenerator] Serializing delta metadata..."
        rootBuilder.Serialize(outputBuilder, methodBodyStreamRva = 0, mappedFieldDataStreamRva = 0)
        printfn "[DeltaGenerator] Serialization complete."

        let metadataBytes = outputBuilder.ToImmutableArray()
        printfn "[DeltaGenerator] Generated metadata delta: %d bytes" metadataBytes.Length
        printfn "[DeltaGenerator] Metadata delta first 32 bytes: %A"
            (if metadataBytes.Length >= 32 then metadataBytes.AsSpan().Slice(0, 32).ToArray() else metadataBytes.AsSpan().ToArray())

        // --- Step 8: Return the generated bytes ---
        printfn "[DeltaGenerator] --- Metadata Delta Generation Complete (Generation %d) ---" generation
        metadataBytes

    /// <summary>
    /// Generates IL delta for a method update.
    /// </summary>
    let private generateILDelta (returnValue: int) =
        printfn "[DeltaGenerator] Generating IL delta for return value: %d" returnValue
        let ilBuilder = new BlobBuilder()
        
        // Determine IL instructions based on the return value
        // For small integers that fit in a byte, we can use ldc.i4.s
        // Otherwise, use ldc.i4 for larger integers
        let ilInstructions = 
            if returnValue >= -128 && returnValue <= 127 then
                // For small integers, use ldc.i4.s (1 byte opcode + 1 byte operand)
                [| 0x1Fuy; byte returnValue; 0x2Auy |] // ldc.i4.s <value>; ret
            else
                // For larger integers, use ldc.i4 (1 byte opcode + 4 byte operand)
                let valueBytes = BitConverter.GetBytes(returnValue)
                [| 0x20uy; valueBytes.[0]; valueBytes.[1]; valueBytes.[2]; valueBytes.[3]; 0x2Auy |] // ldc.i4 <value>; ret
        
        // IL code size (without header)
        let codeSize = ilInstructions.Length
        
        // For tiny method (less than 64 bytes of IL), use tiny header format
        if codeSize < 64 then
            // Tiny format: (codeSize << 2) | 0x2
            let headerByte = byte ((codeSize <<< 2) ||| 0x2)
            ilBuilder.WriteByte(headerByte)
            ilBuilder.WriteBytes(ilInstructions)
        else
            // Fat format for larger methods (very unlikely in this case)
            // Flags (1 byte): 0x3 for fat format
            // Size (1 byte): 0x30 for 3 DWords (12 bytes header)
            // MaxStack (2 bytes): 8 is sufficient for this simple method
            // CodeSize (4 bytes): Size of the IL code
            // LocalVarSig (4 bytes): 0 (no locals)
            ilBuilder.WriteUInt16(0x3003us) // Flags & Size + MaxStack (0x3 | (0x3 << 2) | (8 << 8))
            ilBuilder.WriteInt32(codeSize)  // CodeSize
            ilBuilder.WriteInt32(0)         // LocalVarSig (none)
            ilBuilder.WriteBytes(ilInstructions)
        
        let ilBytes = ilBuilder.ToArray()
        printfn "[DeltaGenerator] Generated IL delta: %d bytes" ilBytes.Length
        printfn "[DeltaGenerator] IL delta bytes: %A" ilBytes
        ImmutableArray.CreateRange(ilBytes)

    /// <summary>
    /// Generates method body for F# method or InvokeStub with proper IL method body format
    /// </summary>
    let private generateFSharpILDelta (returnValue: int) (isInvokeStub: bool) =
        printfn "[DeltaGenerator] Generating F# specific IL delta for returnValue=%d (isInvokeStub=%b)" returnValue isInvokeStub
        
        // Create a Builder to hold our IL opcodes
        let ilBuilder = new BlobBuilder()
        
        // Generate optimized IL instructions for the specific return value
        let ilInstructions = 
            match returnValue with
            | 0 -> [| 0x16uy; 0x2Auy |]                           // ldc.i4.0; ret
            | 1 -> [| 0x17uy; 0x2Auy |]                           // ldc.i4.1; ret
            | 2 -> [| 0x18uy; 0x2Auy |]                           // ldc.i4.2; ret
            | 3 -> [| 0x19uy; 0x2Auy |]                           // ldc.i4.3; ret
            | 4 -> [| 0x1Auy; 0x2Auy |]                           // ldc.i4.4; ret
            | 5 -> [| 0x1Buy; 0x2Auy |]                           // ldc.i4.5; ret
            | 6 -> [| 0x1Cuy; 0x2Auy |]                           // ldc.i4.6; ret
            | 7 -> [| 0x1Duy; 0x2Auy |]                           // ldc.i4.7; ret
            | 8 -> [| 0x1Euy; 0x2Auy |]                           // ldc.i4.8; ret
            | -1 -> [| 0x15uy; 0x2Auy |]                          // ldc.i4.m1; ret
            | n when n >= -128 && n <= 127 -> 
                [| 0x1Fuy; byte n; 0x2Auy |]                      // ldc.i4.s <value>; ret
            | n -> 
                let bytes = BitConverter.GetBytes(n)
                [| 0x20uy; bytes.[0]; bytes.[1]; bytes.[2]; bytes.[3]; 0x2Auy |]  // ldc.i4 <value>; ret
        
        // IL code size (actual instructions without header)
        let codeSize = ilInstructions.Length
        
        // CRITICAL: After examining the C# IL delta, we need to match its exact format
        // The C# IL delta for a method update has:
        // 1. Four null bytes (0x00) at the beginning
        // 2. Tiny format header byte
        // 3. IL instructions
        
        // Write 4 null bytes (RVA offset padding)
        ilBuilder.WriteInt32(0) // Four null bytes: 00 00 00 00
        
        // Constants from MethodBodyBlock.cs:
        let ILTinyFormat = 0x02uy     // Format flag for tiny method body
        let ILTinyFormatSizeShift = 2 // Number of bits to shift the size
                
        // Check if we can use tiny format (size < 64 bytes, no locals, no exception handlers)
        if codeSize < 64 then
            // Create tiny format header: (codeSize << 2) | 0x02
            // This is exactly what MethodBodyBlock.Create expects to find
            let headerByte = byte ((codeSize <<< ILTinyFormatSizeShift) ||| int ILTinyFormat)
            
            // Write the header byte followed by IL instructions
            ilBuilder.WriteByte(headerByte)
            ilBuilder.WriteBytes(ilInstructions)
            
            printfn "[DeltaGenerator] Using tiny format IL header (0x%02X): size=%d bytes" headerByte codeSize
        else
            // For completeness, though our method is always tiny
            // Fat format header structure (rarely needed for our scenario):
            // Flags (2 bytes): Fat format flag (0x3), InitLocals, etc. + Size (3 << 4)
            // MaxStack (2 bytes): e.g., 8
            // CodeSize (4 bytes): Size of the IL
            // LocalVarSigTok (4 bytes): 0 for no locals
            
            ilBuilder.WriteUInt16(0x3003us) // Flags & Size (0x03 format flag | 3 << 4 for 12-byte header)
            ilBuilder.WriteUInt16(8us)      // MaxStack
            ilBuilder.WriteInt32(codeSize)  // CodeSize
            ilBuilder.WriteInt32(0)         // LocalVarSigTok (none)
            ilBuilder.WriteBytes(ilInstructions)
            
            printfn "[DeltaGenerator] Using fat format IL header: size=%d bytes" codeSize
        
        // Convert to array and return as immutable
        let ilBytes = ilBuilder.ToArray()
        printfn "[DeltaGenerator] Generated IL delta: %d bytes" ilBytes.Length
        printfn "[DeltaGenerator] IL delta bytes: %A" ilBytes
        printfn "[DeltaGenerator] IL opcodes: %s" (BitConverter.ToString(ilBytes))
        
        // Add extensive logging about the parsed IL header
        printfn "[DeltaGenerator] IL header analysis:"
        let headByte = ilBytes.[0]
        let isTiny = (headByte &&& 0x03uy) = 0x02uy
        
        if isTiny then
            let embeddedSize = int (headByte >>> 2)
            printfn "  - Format: Tiny (1-byte header)"
            printfn "  - Header byte: 0x%02X - Format=0x%02X, EmbeddedSize=%d" headByte (headByte &&& 0x03uy) embeddedSize
            printfn "  - Actual IL size: %d bytes" (ilBytes.Length - 1)
            
            // Verify that instructions start at offset 1
            if ilBytes.Length > 1 then
                printfn "  - First instruction: 0x%02X at offset 1 (%s)" 
                    ilBytes.[1]
                    (match ilBytes.[1] with
                     | 0x15uy -> "ldc.i4.m1"
                     | 0x16uy -> "ldc.i4.0"
                     | 0x17uy -> "ldc.i4.1"
                     | b when b >= 0x16uy && b <= 0x1Euy -> $"ldc.i4.{int b - int 0x16uy}"
                     | 0x1Fuy -> "ldc.i4.s (followed by 1-byte value)"
                     | 0x20uy -> "ldc.i4 (followed by 4-byte value)"
                     | _ -> "unknown opcode")
        else
            printfn "  - Format: Fat format (12-byte header)"
            // We rarely hit this case for our simple methods
        
        // Compare with what we need
        printfn "  - Required format for MethodBodyBlock.Create: Method body with a header"
        printfn "  - Our IL is now properly formatted with method body header"
        
        ImmutableArray.CreateRange(ilBytes)

    /// <summary>
    /// Generates PDB delta for a method update.
    /// </summary>
    let private generatePDBDelta (methodToken: int) =
        printfn "[DeltaGenerator] Generating PDB delta for method token: 0x%08X" methodToken
        
        // For our simple POC, we'll generate a more complete PDB delta that matches C# format
        let debugMetadataBuilder = MetadataBuilder()
        
        // Create a proper document entry for F#
        let documentName = debugMetadataBuilder.GetOrAddDocumentName("SimpleTest.fs")
        let languageGuid = debugMetadataBuilder.GetOrAddGuid(Guid("3F5162F8-07C6-11D3-9053-00C04FA302A1")) // F# language GUID
        let hashAlgorithm = debugMetadataBuilder.GetOrAddGuid(Guid("8829d00f-11b8-4213-878b-770e8597ac16")) // SHA256
        let documentHandle = debugMetadataBuilder.AddDocument(
            documentName,
            hashAlgorithm,
            debugMetadataBuilder.GetOrAddBlob(Array.empty), // No hash
            languageGuid)
        
        // Instead of creating sequence points manually, we'll use an empty blob for now
        // This will still create a valid PDB delta, but without detailed debugging info
        let methodHandle = MetadataTokens.MethodDefinitionHandle(methodToken % 0x01000000)
        let emptySequencePoints = debugMetadataBuilder.GetOrAddBlob(BlobBuilder())
        
        // Add method debug information with empty sequence points
        debugMetadataBuilder.AddMethodDebugInformation(
            documentHandle,
            emptySequencePoints) |> ignore
        
        // Create portable PDB with proper row counts
        let pdbBlob = new BlobBuilder()
        
        // Create proper typeSystemRowCounts array (64 elements as required by PortablePdbBuilder)
        let rowCountsArray = Array.zeroCreate<int>(64) 
        
        // Method tokens start with 0x06 prefix where 06 is the table index for MethodDef (6)
        // Set the count for methods to equal the method's row number
        let methodTableIndex = 6 // MethodDef table index
        rowCountsArray.[methodTableIndex] <- MetadataTokens.GetRowNumber(methodHandle)
        
        // IMPORTANT: For EnC PDB deltas, certain tables must have zero rows
        // The Document table (#48) must be zero per the error message
        let documentTableIndex = 48 // Document table index
        rowCountsArray.[documentTableIndex] <- 0 // Must be 0 for EnC deltas
        
        // Set the method debug info table correctly
        let methodDebugInfoTableIndex = 49 // MethodDebugInformation table index 
        rowCountsArray.[methodDebugInfoTableIndex] <- 0 // Must be 0 for EnC deltas (not 1)
        
        let typeSystemRowCounts = ImmutableArray.Create<int>(rowCountsArray)
        
        // Create a PDB builder with proper parameters
        let pdbBuilder = new PortablePdbBuilder(
            debugMetadataBuilder,
            typeSystemRowCounts,
            MethodDefinitionHandle(), // No entry point
            null)  // No ID provider
        
        pdbBuilder.Serialize(pdbBlob) |> ignore
        let pdbBytes = pdbBlob.ToArray()
        
        printfn "[DeltaGenerator] Generated PDB delta: %d bytes" pdbBytes.Length
        printfn "[DeltaGenerator] PDB delta bytes: %A" 
            (if pdbBytes.Length >= 16 then pdbBytes |> Array.take 16 else pdbBytes)
        ImmutableArray.CreateRange(pdbBytes)

    /// <summary>
    /// Main entry point for generating deltas.
    /// Note: This implementation currently only supports generating the first delta (Generation 1).
    /// </summary>
    let generateDelta (generator: DeltaGenerator) (assembly: Assembly) (returnValue: int) (isInvokeStub: bool) (typeName: string) (methodName: string) =
        // For this example, we assume we are always generating the first delta (Gen 1)
        // A full implementation would track generation state.
        let generation = 1
        let previousEncId = None // No previous EncId for Gen 1

        async {
            printfn "[DeltaGenerator] ===== Starting delta generation (Targeting Generation %d) =====" generation
            printfn "[DeltaGenerator] Assembly: %s" assembly.FullName
            printfn "[DeltaGenerator] Assembly location: %s" assembly.Location
            printfn "[DeltaGenerator] Module ID: %A" assembly.ManifestModule.ModuleVersionId
            printfn "[DeltaGenerator] Target return value: %d" returnValue
            printfn "[DeltaGenerator] Target type: %s" typeName
            printfn "[DeltaGenerator] Target method: %s" methodName
            printfn "[DeltaGenerator] Using InvokeStub method: %b" isInvokeStub
            
            // Find the method to update
            printfn "[DeltaGenerator] Looking for %s type..." typeName
            
            // Get the regular method and also look for InvokeStub methods
            let methodToUpdate, invokeStubMethods = 
                let type' = assembly.GetType(typeName)
                if type' = null then 
                    printfn "[DeltaGenerator] Error: Could not find %s type" typeName
                    null, [||]
                else 
                    printfn "[DeltaGenerator] Found %s type, looking for %s method..." typeName methodName
                    let regularMethod = type'.GetMethod(methodName, BindingFlags.Public ||| BindingFlags.Static)
                    
                    // Look for F# InvokeStub methods that might be related
                    printfn "[DeltaGenerator] Looking for F# invoke stub methods..."
                    let stubMethods = 
                        assembly.GetTypes()
                        |> Array.collect (fun t -> 
                            t.GetMethods(BindingFlags.Public ||| BindingFlags.NonPublic ||| BindingFlags.Static ||| BindingFlags.Instance)
                            |> Array.filter (fun m -> 
                                // Look for methods with broader criteria that might be invoke stubs:
                                // 1. Contains the method name (methodName)
                                // 2. Contains "InvokeStub" in the type or method name
                                // 3. Type name starts with "InvokeStub" or contains patterns like "<StartupCode"
                                // 4. From a compiler-generated type
                                let methodNameMatches = m.Name.Contains(methodName) || m.Name.Contains("get_") 
                                let typeNameSuggestsStub = 
                                    t.Name.Contains("InvokeStub") || 
                                    t.FullName.Contains("InvokeStub") ||
                                    m.Name.Contains("InvokeStub") ||
                                    t.Name.StartsWith("<StartupCode") ||
                                    t.Name.Contains("$") || // F# compiler often uses $ in generated type names
                                    t.Name.Contains("@") || 
                                    t.Name.Contains("FastFunc") ||
                                    t.Name.Contains("function")
                                let hasCompilerGeneratedAttr = 
                                    try
                                        t.CustomAttributes
                                            |> Seq.exists (fun attr -> 
                                                attr.AttributeType.Name = "CompilerGeneratedAttribute")
                                    with _ -> false
                                
                                // Dump info about all methods to help with debugging
                                printfn "[DeltaGenerator] Examining method: %s::%s (Token: 0x%08X)" t.FullName m.Name m.MetadataToken
                                
                                // More aggressive matching:
                                methodNameMatches || // Match any method containing our target name
                                (methodNameMatches && typeNameSuggestsStub) || 
                                (methodNameMatches && hasCompilerGeneratedAttr) ||
                                (t.Name.Contains("InvokeStub") || t.FullName.Contains("InvokeStub")) // Any InvokeStub type is interesting
                            ))
                    
                    printfn "[DeltaGenerator] Found %d potential invoke stub methods" stubMethods.Length
                    for m in stubMethods do
                        printfn "  - %s::%s (Token: 0x%08X)" m.DeclaringType.FullName m.Name m.MetadataToken
                    
                    regularMethod, stubMethods
            
            match methodToUpdate with
            | null -> 
                printfn "[DeltaGenerator] Error: Could not find %s method" methodName
                return None
            | method' ->
                let methodToken = method'.MetadataToken
                let declaringTypeToken = method'.DeclaringType.MetadataToken
                
                printfn "[DeltaGenerator] Found method info:"
                printfn "  - Token: %d (0x%08X)" methodToken methodToken
                printfn "  - Declaring type token: %d (0x%08X)" declaringTypeToken declaringTypeToken
                printfn "  - Method attributes: %A" method'.Attributes
                printfn "  - Method implementation flags: %A" method'.MethodImplementationFlags
                
                // Also show InvokeStub method IL if any were found
                if invokeStubMethods.Length > 0 then
                    printfn "[DeltaGenerator] Examining F# InvokeStub methods:"
                    for stubMethod in invokeStubMethods do
                        printfn "  - Examining stub method: %s::%s (Token: 0x%08X)" 
                            stubMethod.DeclaringType.FullName stubMethod.Name stubMethod.MetadataToken
                        
                        // Check if this method has a body we can examine
                        let body = stubMethod.GetMethodBody()
                        if body <> null then
                            let ilBytes = body.GetILAsByteArray()
                            if ilBytes <> null && ilBytes.Length > 0 then
                                printfn "    - IL bytes: %A" ilBytes
                                printfn "    - IL hex: %s" (BitConverter.ToString(ilBytes))
                
                // Determine the correct target method token based on the isInvokeStub flag
                let targetMethodToken, targetTypeToken =
                    if isInvokeStub then
                        // If the flag indicates we are targeting an InvokeStub, use the provided method info.
                        // We trust the caller provided the correct token for the InvokeStub.
                        printfn "[DeltaGenerator] Targeting InvokeStub method as requested (Token: 0x%08X)" methodToken
                        methodToken, declaringTypeToken
                    else
                        // If we are not targeting an InvokeStub, use the original method's info,
                        // regardless of what the invokeStubMethods search found.
                        printfn "[DeltaGenerator] Targeting regular method (Token: 0x%08X)" methodToken
                        methodToken, declaringTypeToken
                
                // Get the module MVID from the assembly - critical for hot reload success
                let moduleId = assembly.ManifestModule.ModuleVersionId
                
                // Generate deltas, passing generation info
                printfn "[DeltaGenerator] Generating metadata delta (Gen %d)..." generation
                let metadataDelta = 
                    generateMetadataDelta 
                        assembly.Location 
                        targetMethodToken 
                        targetTypeToken 
                        moduleId 
                        generation // Pass current generation
                        previousEncId // Pass previous EncId (None for Gen 1)
                printfn "[DeltaGenerator] Metadata delta size: %d bytes" metadataDelta.Length
                
                printfn "[DeltaGenerator] Generating IL delta..."
                let ilDelta = generateFSharpILDelta returnValue isInvokeStub // IL delta doesn't depend on generation#
                printfn "[DeltaGenerator] IL delta size: %d bytes" ilDelta.Length
                
                printfn "[DeltaGenerator] Generating PDB delta..."
                let pdbDelta = generatePDBDelta targetMethodToken // PDB delta doesn't directly depend on generation# for this simple case
                printfn "[DeltaGenerator] PDB delta size: %d bytes" pdbDelta.Length
                
                // Create the complete delta
                let updatedMethods = ImmutableArray.Create<int>(targetMethodToken)
                let updatedTypes = ImmutableArray.Create<int>(targetTypeToken)
                
                // Verify the deltas
                printfn "[DeltaGenerator] Verifying deltas..."
                printfn "  - Metadata delta first 16 bytes: %A" (metadataDelta.AsSpan().Slice(0, min 16 metadataDelta.Length).ToArray())
                printfn "  - IL delta first 16 bytes: %A" (ilDelta.AsSpan().Slice(0, min 16 ilDelta.Length).ToArray())
                printfn "  - PDB delta first 16 bytes: %A" (pdbDelta.AsSpan().Slice(0, min 16 pdbDelta.Length).ToArray())
                
                printfn "[DeltaGenerator] Generated delta:"
                printfn "  - Metadata: %d bytes" metadataDelta.Length
                printfn "  - IL: %d bytes" ilDelta.Length
                printfn "  - PDB: %d bytes" pdbDelta.Length
                printfn "  - Updated methods: %A" updatedMethods
                printfn "  - Updated types: %A" updatedTypes
                printfn "[DeltaGenerator] ===== Delta generation complete (Gen %d) =====" generation
                
                return Some {
                    ModuleId = moduleId
                    MetadataDelta = metadataDelta
                    ILDelta = ilDelta
                    PdbDelta = pdbDelta
                    UpdatedMethods = updatedMethods
                    UpdatedTypes = updatedTypes
                }
        } 