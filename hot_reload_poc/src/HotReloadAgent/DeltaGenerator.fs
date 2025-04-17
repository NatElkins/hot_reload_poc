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
open Prelude

#nowarn 3391

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
    /// Generates metadata delta for a method update using a patching approach.
    /// </summary>
    let private generateMetadataDelta (baselineDllPath: string) (methodToken: int) (declaringTypeToken: int) (moduleId: Guid) =
        printfn "[DeltaGenerator] --- Generating Metadata Delta ---"
        printfn "[DeltaGenerator] Baseline DLL Path: %s" baselineDllPath
        printfn "[DeltaGenerator] Method Token: 0x%08X" methodToken
        printfn "[DeltaGenerator] Declaring Type Token: 0x%08X" declaringTypeToken
        printfn "[DeltaGenerator] Module ID (MVID): %A" moduleId
        
        // --- Step 1: Read baseline module name --- 
        // We need the *name* itself, not just the handle value, to add to the delta's string heap.
        let baselineModuleName =
            try
                use fs = File.OpenRead(baselineDllPath)
                use peReader = new PEReader(fs)
                let reader = peReader.GetMetadataReader()
                let moduleDef = reader.GetModuleDefinition()
                let nameHandle = moduleDef.Name
                let moduleName = reader.GetString(nameHandle)
                printfn "[DeltaGenerator] Successfully read baseline module name: '%s'" moduleName
                moduleName
            with ex ->
                printfn "[DeltaGenerator] ERROR reading baseline module name: %s. Falling back." ex.Message
                // Fallback to a default name if reading fails. This might cause issues.
                Path.GetFileName(baselineDllPath) // Use filename as fallback
                
        printfn "[DeltaGenerator] Using module name: '%s' for delta." baselineModuleName

        // --- Step 2: Create Delta MetadataBuilder and Populate Essential Heaps --- 
        let deltaBuilder = MetadataBuilder()
        
        // Set capacity for ENC tables (recommended practice for deltas)
        deltaBuilder.SetCapacity(TableIndex.EncLog, 2) // Example capacity, adjust as needed
        deltaBuilder.SetCapacity(TableIndex.EncMap, 2) 
        printfn "[DeltaGenerator] Delta MetadataBuilder created with ENC table capacities."

        // Add necessary strings, guids, blobs to the DELTA heaps
        // We *must* add the module name string to the delta's string heap.
        let moduleNameHandle = deltaBuilder.GetOrAddString(baselineModuleName) 
        printfn "[DeltaGenerator] Added module name '%s' to delta string heap, handle: %A" baselineModuleName moduleNameHandle
        // Add baseline MVID and a new delta EncId to the delta GUID heap
        let mvidHandle = deltaBuilder.GetOrAddGuid(moduleId)
        let deltaEncId = Guid.NewGuid() // Generate a unique ID for this delta
        let encIdHandle = deltaBuilder.GetOrAddGuid(deltaEncId)
        // For the first generation delta (Gen 1), EncBaseId is the same as the baseline MVID.
        let encBaseIdHandle = mvidHandle 
        printfn "[DeltaGenerator] Added Baseline MVID to delta GUID heap, handle: %A" mvidHandle
        printfn "[DeltaGenerator] Generated unique delta EncId: %A" deltaEncId
        printfn "[DeltaGenerator] Added Delta EncId to delta GUID heap, handle: %A" encIdHandle
        printfn "[DeltaGenerator] Using baseline MVID as EncBaseId handle: %A" encBaseIdHandle

        // --- Step 3: Add Module Row ---
        // Add the single row to the Module table for this delta.
        // Use the handles obtained from the *deltaBuilder*.
        printfn "[DeltaGenerator] Adding Module table row..."
        deltaBuilder.AddModule(
            1,                 // Generation number (1 for the first delta)
            moduleNameHandle,  // Handle to the module name *in the delta's string heap*
            mvidHandle,        // Handle to the baseline MVID *in the delta's GUID heap*
            encIdHandle,       // Handle to the unique delta ID *in the delta's GUID heap*
            encBaseIdHandle)   // Handle to the baseline MVID *in the delta's GUID heap*
        |> ignore
        printfn "[DeltaGenerator] Module table row added."

        // --- Step 4: Add EnC table entries ---
        // Use the original metadata tokens from the baseline assembly.
        // Convert the full token to the specific handle type.
        let baselineTypeDefHandle = MetadataTokens.TypeDefinitionHandle(declaringTypeToken)
        let baselineMethodDefHandle = MetadataTokens.MethodDefinitionHandle(methodToken)
        printfn "[DeltaGenerator] Baseline TypeDef Handle for EnC: %A (Token: 0x%08X)" baselineTypeDefHandle declaringTypeToken
        printfn "[DeltaGenerator] Baseline MethodDef Handle for EnC: %A (Token: 0x%08X)" baselineMethodDefHandle methodToken

        // It's crucial that these handles correspond to actual entities in the baseline assembly.
        // We add entries to EncLog and EncMap to signify these baseline entities are part of the update.
        let typeEntityHandle : EntityHandle = baselineTypeDefHandle
        let methodEntityHandle : EntityHandle = baselineMethodDefHandle
        
        printfn "[DeltaGenerator] Adding EncLog entry for TypeDef: %A" typeEntityHandle
        deltaBuilder.AddEncLogEntry(typeEntityHandle, EditAndContinueOperation.Default) // Default = Modify
        printfn "[DeltaGenerator] Adding EncLog entry for MethodDef: %A" methodEntityHandle
        deltaBuilder.AddEncLogEntry(methodEntityHandle, EditAndContinueOperation.Default) // Default = Modify
        
        printfn "[DeltaGenerator] Adding EncMap entry for TypeDef: %A" typeEntityHandle
        deltaBuilder.AddEncMapEntry(typeEntityHandle) // Maps baseline token to itself in the delta context
        printfn "[DeltaGenerator] Adding EncMap entry for MethodDef: %A" methodEntityHandle
        deltaBuilder.AddEncMapEntry(methodEntityHandle) // Maps baseline token to itself in the delta context
        printfn "[DeltaGenerator] EnC Log and Map entries added."

        // --- Step 5: Serialize the delta metadata ---
        // Create the root builder. Suppress validation is important for deltas
        // as they are not complete, valid metadata tables on their own.
        printfn "[DeltaGenerator] Creating MetadataRootBuilder (suppressValidation=true)..."
        let rootBuilder = MetadataRootBuilder(deltaBuilder, suppressValidation=true) 
        
        let outputBuilder = BlobBuilder()
        printfn "[DeltaGenerator] Serializing delta metadata..."
        // methodBodyStreamRva and mappedFieldDataStreamRva should be 0 for deltas.
        rootBuilder.Serialize(outputBuilder, methodBodyStreamRva = 0, mappedFieldDataStreamRva = 0) 
        printfn "[DeltaGenerator] Serialization complete."
        
        let metadataBytes = outputBuilder.ToImmutableArray()
        printfn "[DeltaGenerator] Generated metadata delta: %d bytes" metadataBytes.Length
        printfn "[DeltaGenerator] Metadata delta first 32 bytes: %A" 
            (if metadataBytes.Length >= 32 then metadataBytes.AsSpan().Slice(0, 32).ToArray() else metadataBytes.AsSpan().ToArray())
        
        // --- Step 6: Return the generated bytes ---
        // No patching needed. The serialized 'metadataBytes' is the final delta.
        printfn "[DeltaGenerator] --- Metadata Delta Generation Complete ---"
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
    /// Generates method body for F# method or InvokeStub
    /// </summary>
    let private generateFSharpILDelta (returnValue: int) (isInvokeStub: bool) =
        printfn "[DeltaGenerator] Generating F# specific IL delta for returnValue=%d (isInvokeStub=%b)" returnValue isInvokeStub
        
        let ilBuilder = new BlobBuilder()
        
        // For F# methods, the IL is essentially the same whether it's a regular method
        // or an InvokeStub - we're just pushing an integer value and returning it
        
        // Choose the most optimal IL instruction based on return value
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
        
        // IL code size (without header)
        let codeSize = ilInstructions.Length
        
        // Use tiny method format (we know our method is small)
        let headerByte = byte ((codeSize <<< 2) ||| 0x2)
        ilBuilder.WriteByte(headerByte)
        ilBuilder.WriteBytes(ilInstructions)
        
        let ilBytes = ilBuilder.ToArray()
        printfn "[DeltaGenerator] Generated IL delta with header: %d bytes" ilBytes.Length
        printfn "[DeltaGenerator] IL delta bytes: %A" ilBytes
        printfn "[DeltaGenerator] IL header byte: 0x%02X (Tiny format, code size: %d)" headerByte codeSize
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
    /// </summary>
    let generateDelta (generator: DeltaGenerator) (assembly: Assembly) (returnValue: int) (isInvokeStub: bool) (typeName: string) (methodName: string) =
        async {
            printfn "[DeltaGenerator] ===== Starting delta generation ====="
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
                
                // Generate deltas
                printfn "[DeltaGenerator] Generating metadata delta..."
                let metadataDelta = generateMetadataDelta assembly.Location targetMethodToken targetTypeToken moduleId
                printfn "[DeltaGenerator] Metadata delta size: %d bytes" metadataDelta.Length
                
                printfn "[DeltaGenerator] Generating IL delta..."
                let ilDelta = generateFSharpILDelta returnValue isInvokeStub
                printfn "[DeltaGenerator] IL delta size: %d bytes" ilDelta.Length
                
                printfn "[DeltaGenerator] Generating PDB delta..."
                let pdbDelta = generatePDBDelta targetMethodToken
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
                printfn "[DeltaGenerator] ===== Delta generation complete ====="
                
                return Some {
                    ModuleId = moduleId
                    MetadataDelta = metadataDelta
                    ILDelta = ilDelta
                    PdbDelta = pdbDelta
                    UpdatedMethods = updatedMethods
                    UpdatedTypes = updatedTypes
                }
        } 