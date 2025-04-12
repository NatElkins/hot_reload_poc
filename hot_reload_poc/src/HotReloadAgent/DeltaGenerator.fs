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
/// Tracks the compiler state and previous compilation results.
/// </summary>
type DeltaGenerator = {
    /// <summary>The F# compiler instance used for compilation.</summary>
    Compiler: FSharpChecker
    /// <summary>The previous compilation result.</summary>
    PreviousCompilation: FSharpCheckFileResults option
    /// <summary>The previous return value.</summary>
    PreviousReturnValue: int option
}

module DeltaGenerator =
    /// <summary>
    /// Creates a new DeltaGenerator with a fresh F# compiler instance.
    /// </summary>
    /// <returns>A new DeltaGenerator instance with default settings.</returns>
    let create () =
        {
            Compiler = FSharpChecker.Create()
            PreviousCompilation = None
            PreviousReturnValue = None
        }

    /// <summary>
    /// Gets the method token and name for a method in an assembly.
    /// </summary>
    let getMethodInfo (assembly: Assembly) (typeName: string) (methodName: string) =
        printfn "[DeltaGenerator] Looking for method %s in type %s" methodName typeName
        let typ = assembly.GetType(typeName)
        if typ = null then 
            printfn "[DeltaGenerator] Could not find type: %s" typeName
            printfn "[DeltaGenerator] Available types: %A" (assembly.GetTypes() |> Array.map (fun t -> t.FullName))
            None
        else
            printfn "[DeltaGenerator] Found type: %s" typ.FullName
            let method = typ.GetMethod(methodName, BindingFlags.Public ||| BindingFlags.Static)
            if method = null then 
                printfn "[DeltaGenerator] Could not find method: %s in type: %s" methodName typeName
                printfn "[DeltaGenerator] Available methods: %A" (typ.GetMethods() |> Array.map (fun m -> m.Name))
                None
            else 
                printfn "[DeltaGenerator] Found method: %s" method.Name
                printfn "[DeltaGenerator] Method token: %d" method.MetadataToken
                printfn "[DeltaGenerator] Method attributes: %A" method.Attributes
                printfn "[DeltaGenerator] Method declaring type: %s" method.DeclaringType.FullName
                printfn "[DeltaGenerator] Method declaring type token: %d" method.DeclaringType.MetadataToken
                Some (method.MetadataToken, method.Name, method.DeclaringType.MetadataToken)

    /// <summary>
    /// Generates metadata delta for a method update.
    /// </summary>
    let private generateMetadataDelta (methodToken: int) (methodName: string) (declaringTypeToken: int) =
        printfn "[DeltaGenerator] Generating metadata delta for method %s (token: %d)" methodName methodToken
        let metadataBuilder = new MetadataBuilder()
        
        // Create method definition handle
        let methodDef = MetadataTokens.MethodDefinitionHandle(methodToken)
        printfn "[DeltaGenerator] Created method definition handle: %d" (methodDef.GetHashCode())
        
        // Generate method signature
        printfn "[DeltaGenerator] Generating method signature"
        let signatureBuilder = new BlobBuilder()
        let encoder = new BlobEncoder(signatureBuilder)
        let methodSigEncoder = encoder.MethodSignature(SignatureCallingConvention.Default, 0, isInstanceMethod = false)
        
        // Encode return type and parameters
        methodSigEncoder.Parameters(
            0, 
            (fun (returnType: ReturnTypeEncoder) -> returnType.Type().Int32()),
            (fun (parameters: ParametersEncoder) -> ())
        )
        
        // Add method definition update
        printfn "[DeltaGenerator] Adding method definition update"
        let methodHandle = metadataBuilder.AddMethodDefinition(
            MethodAttributes.Public ||| MethodAttributes.Static,
            MethodImplAttributes.IL ||| MethodImplAttributes.Managed,
            metadataBuilder.GetOrAddString(methodName),
            metadataBuilder.GetOrAddBlob(signatureBuilder.ToArray()), // Use the generated signature
            declaringTypeToken, // Use the raw token value
            MetadataTokens.ParameterHandle(0)
        )
        
        // Add method debug information
        printfn "[DeltaGenerator] Adding method debug information"
        let documentHandle = metadataBuilder.AddDocument(
            metadataBuilder.GetOrAddDocumentName("SimpleTest.fs"),
            metadataBuilder.GetOrAddGuid(Guid.Empty),
            metadataBuilder.GetOrAddBlob([||]),
            metadataBuilder.GetOrAddGuid(Guid.Empty)
        )
        
        let methodDebugBuilder = new BlobBuilder()
        methodDebugBuilder.WriteByte(0x01uy) // Document count
        methodDebugBuilder.WriteByte(0x00uy) // Document index
        methodDebugBuilder.WriteByte(0x00uy) // Line count
        let methodDebugHandle = metadataBuilder.AddMethodDebugInformation(
            documentHandle,
            metadataBuilder.GetOrAddBlob(methodDebugBuilder.ToArray())
        )
        
        // Add ENC log entry for the method update
        printfn "[DeltaGenerator] Adding ENC log entry"
        metadataBuilder.AddEncLogEntry(
            methodDef,
            EditAndContinueOperation.AddMethod
        )
        
        // Add ENC map entry for token remapping
        printfn "[DeltaGenerator] Adding ENC map entry"
        metadataBuilder.AddEncMapEntry(methodDef)
        
        // Create metadata root builder with generation
        printfn "[DeltaGenerator] Creating metadata root builder"
        let rootBuilder = new MetadataRootBuilder(metadataBuilder)
        
        // Serialize metadata
        printfn "[DeltaGenerator] Serializing metadata"
        let metadataBlob = new BlobBuilder()
        rootBuilder.Serialize(metadataBlob, 1, 0) // Use generation 1 for the update
        let metadataBytes = metadataBlob.ToArray()
        printfn "[DeltaGenerator] Generated metadata bytes: %d bytes" metadataBytes.Length
        printfn "[DeltaGenerator] First 16 bytes of metadata: %A" (metadataBytes |> Array.take 16)
        ImmutableArray.CreateRange(metadataBytes)

    /// <summary>
    /// Generates IL delta for a method update.
    /// </summary>
    let private generateILDelta (returnValue: int) =
        printfn "[DeltaGenerator] Generating IL delta for return value: %d" returnValue
        let ilBuilder = new BlobBuilder()
        
        // Method header
        // Flags: 0x03 (Tiny format, no locals, no exceptions)
        ilBuilder.WriteByte(0x03uy)
        
        // Code size: 3 bytes (ldc.i4.s + ret)
        ilBuilder.WriteByte(0x03uy)
        
        // Method body
        printfn "[DeltaGenerator] Writing IL instructions:"
        printfn "  ldc.i4.s %d" returnValue
        ilBuilder.WriteByte(0x16uy) // ldc.i4.s
        ilBuilder.WriteByte(byte returnValue) // The new return value
        
        printfn "  ret"
        ilBuilder.WriteByte(0x2Auy) // ret
        
        let ilBytes = ilBuilder.ToArray()
        printfn "[DeltaGenerator] Generated IL bytes: %d bytes" ilBytes.Length
        printfn "[DeltaGenerator] IL bytes: %A" ilBytes
        ImmutableArray.CreateRange(ilBytes)

    let private generatePDBDelta (methodToken: int) =
        printfn "[DeltaGenerator] Generating PDB delta for method token: %d" methodToken
        let pdbBuilder = new BlobBuilder()
        
        // Add minimal PDB information
        // Document table entry
        pdbBuilder.WriteByte(0x01uy) // Document table entry count
        pdbBuilder.WriteByte(0x00uy) // Document name index
        pdbBuilder.WriteByte(0x00uy) // Document hash algorithm
        pdbBuilder.WriteByte(0x00uy) // Document hash
        
        // Method debug information
        pdbBuilder.WriteByte(0x01uy) // Method debug info count
        pdbBuilder.WriteInt32(methodToken) // Method token
        pdbBuilder.WriteByte(0x00uy) // Sequence point count
        
        let pdbBytes = pdbBuilder.ToArray()
        printfn "[DeltaGenerator] Generated PDB bytes: %d bytes" pdbBytes.Length
        printfn "[DeltaGenerator] PDB bytes: %A" pdbBytes
        ImmutableArray.CreateRange(pdbBytes)

    /// <summary>
    /// Main entry point for generating deltas.
    /// </summary>
    let generateDelta (generator: DeltaGenerator) (assembly: Assembly) (returnValue: int) =
        async {
            printfn "[DeltaGenerator] ===== Starting delta generation ====="
            printfn "[DeltaGenerator] Assembly: %s" assembly.FullName
            printfn "[DeltaGenerator] Module ID: %A" assembly.ManifestModule.ModuleVersionId
            printfn "[DeltaGenerator] Target return value: %d" returnValue
            
            // Get the method token and name for getValue
            let methodInfo = getMethodInfo assembly "SimpleTest" "getValue"
            
            match methodInfo with
            | None ->
                printfn "[DeltaGenerator] Failed to find method info"
                return None
            | Some (token, methodName, declaringTypeToken) ->
                printfn "[DeltaGenerator] Found method info: token=%d, name=%s, declaringType=%d" 
                    token methodName declaringTypeToken
                
                // Generate minimal metadata and IL deltas
                printfn "[DeltaGenerator] Generating metadata delta..."
                let metadataDelta = generateMetadataDelta token methodName declaringTypeToken
                printfn "[DeltaGenerator] Metadata delta size: %d bytes" metadataDelta.Length
                
                printfn "[DeltaGenerator] Generating IL delta..."
                let ilDelta = generateILDelta returnValue
                printfn "[DeltaGenerator] IL delta size: %d bytes" ilDelta.Length
                
                printfn "[DeltaGenerator] Generating PDB delta..."
                let pdbDelta = generatePDBDelta token
                printfn "[DeltaGenerator] PDB delta size: %d bytes" pdbDelta.Length
                
                let updatedTypes = ImmutableArray<int>.Empty
                let updatedMethods = ImmutableArray.Create<int>(token)
                
                // Ensure we're using the same ModuleId as the original assembly
                let moduleId = assembly.ManifestModule.ModuleVersionId
                printfn "[DeltaGenerator] Using module ID: %A" moduleId
                
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
                    UpdatedTypes = updatedTypes
                    UpdatedMethods = updatedMethods
                }
        } 