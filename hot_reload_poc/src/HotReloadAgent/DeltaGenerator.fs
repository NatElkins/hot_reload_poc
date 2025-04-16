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
    PreviousCompilation: FSharpCheckFileResults option
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
    /// Generates metadata delta for a method update.
    /// </summary>
    let private generateMetadataDelta (methodToken: int) (declaringTypeToken: int) (moduleId: Guid) =
        printfn "[DeltaGenerator] Generating metadata delta for method token: 0x%08X" methodToken
        printfn "[DeltaGenerator] Declaring type token: 0x%08X" declaringTypeToken
        printfn "[DeltaGenerator] Using module ID: %A" moduleId
        
        let metadataBuilder = MetadataBuilder()
        
        // Set capacity for ENC tables
        metadataBuilder.SetCapacity(TableIndex.EncLog, 64)
        metadataBuilder.SetCapacity(TableIndex.EncMap, 64)

        // Step 1: Get proper tokens with correct masking
        // The token value is composed of table index (high byte) and row index (low 3 bytes)
        // We need to extract just the row part using a mask
        let methodDefHandle = MetadataTokens.MethodDefinitionHandle(methodToken % 0x01000000)
        let typeDefHandle = MetadataTokens.TypeDefinitionHandle(declaringTypeToken % 0x01000000)
        
        // Step 2: Generate a proper EncId for this delta
        // This is the GUID that mdv looks for to identify this as a valid EnC delta
        let encIdGuid = Guid.Parse("86d3b3ff-8f3c-44d6-bdea-dc76b2f7c2d1") // Use the same EncId as C# for testing
        let encIdHandle = metadataBuilder.GetOrAddGuid(encIdGuid)
        let mvidHandle = metadataBuilder.GetOrAddGuid(moduleId)
        let emptyGuidHandle = metadataBuilder.GetOrAddGuid(Guid.Empty)
        
        // Step 3: Add module definition with proper EncId
        let moduleNameHandle = metadataBuilder.GetOrAddString("SimpleLib")
        metadataBuilder.AddModule(
            1,                // generation - set to 1 for delta (this is crucial!)
            moduleNameHandle, // name
            mvidHandle,       // mvid
            encIdHandle,      // encId - critical for EnC
            emptyGuidHandle)  // encBaseId
        |> ignore
        
        // Step 4: Add required references similar to C# delta
        // Add System.Runtime reference as seen in the C# delta
        let systemRuntimeName = metadataBuilder.GetOrAddString("System.Runtime")
        let systemRuntimeVersion = System.Version(10, 0, 0, 0)
        // ECMA public key token for System.Runtime
        let ecmaPublicKeyTokenBlob = metadataBuilder.GetOrAddBlob([| 
            0xB0uy; 0x3Fuy; 0x5Fuy; 0x7Fuy; 0x11uy; 0xD5uy; 0x0Auy; 0x3Auy 
        |])
        
        let systemRuntimeRef = metadataBuilder.AddAssemblyReference(
            systemRuntimeName,
            systemRuntimeVersion,
            metadataBuilder.GetOrAddString(""), // Empty culture
            ecmaPublicKeyTokenBlob,
            AssemblyFlags.PublicKey,  // Use PublicKey flag instead of None
            BlobHandle())
        
        // Add System namespace and Object type reference
        let systemNamespace = metadataBuilder.GetOrAddString("System")
        let objectTypeName = metadataBuilder.GetOrAddString("Object")
        let objectTypeRef = metadataBuilder.AddTypeReference(
            systemRuntimeRef,
            systemNamespace,
            objectTypeName)
        
        // Add Int32 type reference
        let int32TypeName = metadataBuilder.GetOrAddString("Int32")
        let _int32TypeRef = metadataBuilder.AddTypeReference(
            systemRuntimeRef,
            systemNamespace,
            int32TypeName)
        
        // Step 5: Add proper EnC table entries with explicit entity handles
        // Use implicit conversion via type annotations to convert handles to EntityHandle
        let typeEntityHandle : EntityHandle = typeDefHandle
        let methodEntityHandle : EntityHandle = methodDefHandle
        
        // Add entries to the EnC tables - order matters here
        metadataBuilder.AddEncLogEntry(typeEntityHandle, EditAndContinueOperation.Default)
        metadataBuilder.AddEncLogEntry(methodEntityHandle, EditAndContinueOperation.Default)
        
        metadataBuilder.AddEncMapEntry(typeEntityHandle)
        metadataBuilder.AddEncMapEntry(methodEntityHandle)
        
        // Step 6: Create metadata root and serialize with correct parameters
        let metadataRoot = new MetadataRootBuilder(metadataBuilder)
        let metadataBlob = new BlobBuilder()
        
        // Critical: Use correct metadataStreamVersion (1) for EnC deltas
        metadataRoot.Serialize(metadataBlob, 1, 0)
        
        let deltaBytes = metadataBlob.ToImmutableArray()
        printfn "[DeltaGenerator] Generated metadata bytes: %d bytes" deltaBytes.Length
        printfn "[DeltaGenerator] Metadata bytes first 16 bytes: %A" 
            (if deltaBytes.Length >= 16 then deltaBytes.AsSpan().Slice(0, 16).ToArray() else deltaBytes.AsSpan().ToArray())
        deltaBytes

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
                let metadataDelta = generateMetadataDelta targetMethodToken targetTypeToken moduleId
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