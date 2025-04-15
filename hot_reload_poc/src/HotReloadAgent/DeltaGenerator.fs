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
        let methodSigEncoder = encoder.MethodSignature(SignatureCallingConvention.Default, 0, isInstanceMethod = false)
        
        // Encode return type and parameters
        methodSigEncoder.Parameters(
            0, 
            (fun returnType -> returnType.Type().Int32()),
            (fun _ -> ())
        )
        
        let signatureBytes = signatureBuilder.ToArray()
        printfn "[DeltaGenerator] Generated method signature: %A" signatureBytes
        signatureBytes

    /// <summary>
    /// Generates metadata delta for a method update.
    /// </summary>
    let private generateMetadataDelta (methodToken: int) (declaringTypeToken: int) (moduleId: Guid) =
        printfn "[DeltaGenerator] Generating metadata delta for method token: 0x%08X (Restoring Module+TypeDef+MethodDef + Refs + MethodSig EnC)" methodToken
        printfn "[DeltaGenerator] Declaring type token: 0x%08X" declaringTypeToken
        printfn "[DeltaGenerator] Using module ID: %A" moduleId
        
        let metadataBuilder = MetadataBuilder()

        // 1. Add Module definition 
        let moduleNameHandle = metadataBuilder.GetOrAddString("0.dll")
        let mvidHandle = metadataBuilder.GetOrAddGuid(moduleId)
        let zeroGuidHandle = metadataBuilder.GetOrAddGuid(Guid.Empty)
        let _ = metadataBuilder.AddModule(1, moduleNameHandle, mvidHandle, zeroGuidHandle, zeroGuidHandle)

        // 2. Add essential references (BEFORE TypeDef)
        let systemRuntimeAssemblyName = metadataBuilder.GetOrAddString("System.Runtime")
        let systemRuntimeVersion = System.Version(10, 0, 0, 0) 
        let ecmaPublicKeyTokenBlob = metadataBuilder.GetOrAddBlob([| 0xB0uy; 0x3Fuy; 0x5Fuy; 0x7Fuy; 0x11uy; 0xD5uy; 0x0Auy; 0x3Auy |])
        let systemRuntimeAssemblyRef = metadataBuilder.AddAssemblyReference(
            name = systemRuntimeAssemblyName, 
            version = systemRuntimeVersion, 
            culture = metadataBuilder.GetOrAddString(""), 
            publicKeyOrToken = ecmaPublicKeyTokenBlob,
            flags = AssemblyFlags.PublicKey, 
            hashValue = BlobHandle() 
        )
        let systemNamespace = metadataBuilder.GetOrAddString("System")
        let objectTypeName = metadataBuilder.GetOrAddString("Object")
        let objectTypeRef = metadataBuilder.AddTypeReference(systemRuntimeAssemblyRef, systemNamespace, objectTypeName) 
        let int32TypeName = metadataBuilder.GetOrAddString("Int32")
        let _int32TypeRef = metadataBuilder.AddTypeReference(systemRuntimeAssemblyRef, systemNamespace, int32TypeName) 

        // 3. Add TypeDef definition (AFTER References)
        let typeDefHandle = MetadataTokens.TypeDefinitionHandle(declaringTypeToken)
        let emptyFieldHandle = FieldDefinitionHandle()
        let emptyMethodHandle = MethodDefinitionHandle()
        metadataBuilder.AddTypeDefinition(
            // Attributes from baseline mdv: Public, Abstract, Sealed (0x181)
            TypeAttributes.Public ||| TypeAttributes.Abstract ||| TypeAttributes.Sealed, 
            metadataBuilder.GetOrAddString("SimpleTest"),
            metadataBuilder.GetOrAddString(""), // Empty namespace
            objectTypeRef, // Use the handle created above for System.Object
            emptyFieldHandle, 
            emptyMethodHandle 
        ) |> ignore
        
        // 4. Add MethodDef definition (Restoring this)
        let methodDefHandle = MetadataTokens.MethodDefinitionHandle(methodToken)
        let emptyParamHandle = ParameterHandle()
        // Get the handle for the method signature blob we generated earlier
        let methodSignatureBlobHandle = metadataBuilder.GetOrAddBlob(generateMethodSignature())
        metadataBuilder.AddMethodDefinition(
            MethodAttributes.Public ||| MethodAttributes.Static, 
            MethodImplAttributes.IL,
            metadataBuilder.GetOrAddString("getValue"),
            methodSignatureBlobHandle, // Use the handle for the signature blob
            0, // RVA - filled by runtime
            emptyParamHandle // No parameters
        ) |> ignore

        // 5. Get MethodDef handle for EnC tables
        let methodEntityHandle : EntityHandle = !> methodDefHandle // Use the handle from AddMethodDefinition
        
        // 6. Create AND Add Standalone Signature for empty locals (Reverting to explicit add)
        let emptyLocalsBlob = metadataBuilder.GetOrAddBlob([| 0x07uy; 0x00uy |]) 
        let sigHandle = metadataBuilder.AddStandaloneSignature(emptyLocalsBlob) // Use the added handle
        let sigEntityHandle : EntityHandle = !> sigHandle

        // 7. Add EncMap entries (MethodDef and StandAloneSig only) in known correct order
        metadataBuilder.AddEncMapEntry(methodEntityHandle)
        metadataBuilder.AddEncMapEntry(sigEntityHandle)

        // 8. Add EncLog entries (MethodDef and StandAloneSig only) using Default operation
        metadataBuilder.AddEncLogEntry(methodEntityHandle, EditAndContinueOperation.Default)
        metadataBuilder.AddEncLogEntry(sigEntityHandle, EditAndContinueOperation.Default)

        // 9. Create and serialize the metadata using MetadataRootBuilder
        let metadataBytes = BlobBuilder()
        let rootBuilder = MetadataRootBuilder(metadataBuilder)
        
        // Serialize with the correct generation (1) for deltas
        rootBuilder.Serialize(metadataBytes, 1, 0)
        
        // Convert to immutable array and return
        let deltaBytes = metadataBytes.ToArray()
        printfn "[DeltaGenerator] Generated metadata bytes: %d bytes" deltaBytes.Length
        printfn "[DeltaGenerator] Metadata bytes first 16 bytes: %A" 
            (if deltaBytes.Length >= 16 then deltaBytes |> Array.take 16 else deltaBytes)
        ImmutableArray.CreateRange(deltaBytes)

    /// <summary>
    /// Generates IL delta for a method update.
    /// </summary>
    let private generateILDelta (returnValue: int) =
        printfn "[DeltaGenerator] Generating IL delta..."
        let ilBuilder = new BlobBuilder()
        
        // For EnC updates, we need to include a valid method body header
        // For a tiny method format (less than 64 bytes of IL), the header is a single byte:
        // The format is: (codeSize << 2) | 2
        // Where the lowest two bits are 10 (0x2) and the upper 6 bits are the code size
        
        // Our IL code is just 2 bytes (ldc.i4.s + ret + operand)
        let ilInstructions = [| 0x1Fuy; byte returnValue; 0x2Auy |] // ldc.i4.s <value>; ret
        let codeSize = ilInstructions.Length
        
        // Create the tiny format method header: (codeSize << 2) | 2
        // This tells the runtime that this is a tiny-format method body with 'codeSize' bytes of IL
        let headerByte = byte ((codeSize <<< 2) ||| 2) // Should be 0x0E for 3 bytes of IL
        
        // Write the method header followed by the IL code
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
        printfn "[DeltaGenerator] Generating PDB delta for method token: %d" methodToken
        
        // For simple EnC scenarios with method body updates,
        // an empty PDB delta with the correct header is often sufficient
        // because no actual debugging information changes
        let pdbBuilder = new BlobBuilder()
        
        // Standard Portable PDB header with "BSJB" signature
        pdbBuilder.WriteBytes([|
            // Magic bytes for Portable PDB ("BSJB")
            0x42uy; 0x53uy; 0x4Auy; 0x42uy;
            
            // Major/Minor version (1.1) - same as in metadata
            0x01uy; 0x00uy; 
            0x01uy; 0x00uy;
            
            // Reserved (4 bytes of zeros)
            0x00uy; 0x00uy; 0x00uy; 0x00uy;
        |])
        
        // Version string length and string - using a minimal "v4.0.30319" version string
        let versionString = "v4.0.30319"
        let versionStringBytes = System.Text.Encoding.UTF8.GetBytes(versionString + "\0")
        let paddedLength = (versionStringBytes.Length + 3) / 4 * 4 // round up to nearest multiple of 4
        
        pdbBuilder.WriteInt32(versionStringBytes.Length)
        pdbBuilder.WriteBytes(versionStringBytes)
        
        // Add padding to align to 4 bytes if needed
        let padding = paddedLength - versionStringBytes.Length
        if padding > 0 then
            pdbBuilder.WriteBytes(Array.zeroCreate padding)
        
        // Flags (2 bytes) and stream count (2 bytes) - using 0 for flags and 0 streams
        // This creates a minimal valid PDB without any actual debug info
        pdbBuilder.WriteInt16(0s)   // Flags
        pdbBuilder.WriteInt16(0s)   // StreamCount
        
        let pdbBytes = pdbBuilder.ToArray()
        printfn "[DeltaGenerator] Generated PDB delta: %d bytes" pdbBytes.Length
        printfn "[DeltaGenerator] PDB delta bytes: %A" 
            (if pdbBytes.Length >= 16 then pdbBytes |> Array.take 16 else pdbBytes)
        ImmutableArray.CreateRange(pdbBytes)

    /// <summary>
    /// Generates IL delta for F# method
    /// </summary>
    let generateFSharpILDelta (returnValue: int) (isInvokeStub: bool) =
        printfn "[DeltaGenerator] Generating F# specific IL delta for returnValue=%d (isInvokeStub=%b)" returnValue isInvokeStub
        
        // The IL will be different depending on if we're targeting an InvokeStub method or a regular F# method
        if isInvokeStub then
            // For InvokeStub methods, we need to handle the F# compiler's invocation pattern
            let ilBuilder = new BlobBuilder()
            
            // IL instructions for the method body
            let ilInstructions = [| 0x1Fuy; byte returnValue; 0x2Auy |] // ldc.i4.s <value>; ret
            let codeSize = ilInstructions.Length
            
            // Create the tiny format method header: (codeSize << 2) | 2
            let headerByte = byte ((codeSize <<< 2) ||| 2) // 0x0E for 3 bytes
            
            // Write the method header followed by the IL code
            ilBuilder.WriteByte(headerByte)
            ilBuilder.WriteBytes(ilInstructions)
            
            let ilBytes = ilBuilder.ToArray()
            printfn "[DeltaGenerator] Generated InvokeStub IL delta with header: %d bytes" ilBytes.Length
            printfn "[DeltaGenerator] InvokeStub IL delta bytes: %A" ilBytes
            printfn "[DeltaGenerator] IL header byte: 0x%02X (Tiny format, code size: %d)" headerByte codeSize
            ImmutableArray.CreateRange(ilBytes)
        else
            // For regular F# methods, use our improved approach with proper headers
            generateILDelta returnValue

    /// <summary>
    /// Main entry point for generating deltas.
    /// </summary>
    let generateDelta (generator: DeltaGenerator) (assembly: Assembly) (returnValue: int) (isInvokeStub: bool) =
        async {
            printfn "[DeltaGenerator] ===== Starting delta generation ====="
            printfn "[DeltaGenerator] Assembly: %s" assembly.FullName
            printfn "[DeltaGenerator] Assembly location: %s" assembly.Location
            printfn "[DeltaGenerator] Module ID: %A" assembly.ManifestModule.ModuleVersionId
            printfn "[DeltaGenerator] Target return value: %d" returnValue
            printfn "[DeltaGenerator] Using InvokeStub method: %b" isInvokeStub
            
            // Find the method to update
            printfn "[DeltaGenerator] Looking for SimpleTest type..."
            
            // Get the regular method and also look for InvokeStub methods
            let methodToUpdate, invokeStubMethods = 
                let type' = assembly.GetType("SimpleTest")
                if type' = null then 
                    printfn "[DeltaGenerator] Error: Could not find SimpleTest type"
                    null, [||]
                else 
                    printfn "[DeltaGenerator] Found SimpleTest type, looking for getValue method..."
                    let regularMethod = type'.GetMethod("getValue", BindingFlags.Public ||| BindingFlags.Static)
                    
                    // Look for F# InvokeStub methods that might be related
                    printfn "[DeltaGenerator] Looking for F# invoke stub methods..."
                    let stubMethods = 
                        assembly.GetTypes()
                        |> Array.collect (fun t -> 
                            t.GetMethods(BindingFlags.Public ||| BindingFlags.NonPublic ||| BindingFlags.Static ||| BindingFlags.Instance)
                            |> Array.filter (fun m -> 
                                // Look for methods with broader criteria that might be invoke stubs:
                                // 1. Contains the method name (getValue)
                                // 2. Contains "InvokeStub" in the type or method name
                                // 3. Type name starts with "InvokeStub" or contains patterns like "<StartupCode"
                                // 4. From a compiler-generated type
                                let methodNameMatches = m.Name.Contains("getValue") || m.Name.Contains("get_") 
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
                printfn "[DeltaGenerator] Error: Could not find getValue method"
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
                
                // Generate minimal metadata and IL deltas
                printfn "[DeltaGenerator] Generating metadata delta..."
                // Ensure we're using the same ModuleId as the original assembly
                let moduleId = assembly.ManifestModule.ModuleVersionId
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
                
                // Verify the deltas
                printfn "[DeltaGenerator] Verifying deltas..."
                printfn "  - Metadata delta first 16 bytes: %A" (metadataDelta.AsSpan().Slice(0, min 16 metadataDelta.Length).ToArray())
                printfn "  - IL delta first 16 bytes: %A" (ilDelta.AsSpan().Slice(0, min 16 ilDelta.Length).ToArray())
                printfn "  - PDB delta first 16 bytes: %A" (pdbDelta.AsSpan().Slice(0, min 16 pdbDelta.Length).ToArray())
                let updatedTypes = ImmutableArray<int>.Empty
                
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