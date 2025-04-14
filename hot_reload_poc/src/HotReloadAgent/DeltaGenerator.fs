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
        printfn "[DeltaGenerator] Generating metadata delta for method getValue (token: %d)" methodToken
        printfn "[DeltaGenerator] Declaring type token: %d" declaringTypeToken
        printfn "[DeltaGenerator] Using module ID: %A" moduleId
        
        // Use the MetadataBuilder for correct metadata construction
        let metadataBuilder = MetadataBuilder()
        
        // The Module table must be present in all metadata blobs (including deltas)
        // Add a row to the Module table with the correct module ID
        let _ = metadataBuilder.AddModule(
            1, // Generation 1 for deltas
            metadataBuilder.GetOrAddString("original.dll"), // Use the actual module name
            metadataBuilder.GetOrAddGuid(moduleId), // Use the actual Mvid from the original assembly
            metadataBuilder.GetOrAddGuid(Guid.Empty), // EncId - can be empty for deltas
            metadataBuilder.GetOrAddGuid(Guid.Empty)  // EncBaseId - can be empty for deltas
        )
        
        // Add the type to TypeDef table
        let typeDefHandle = MetadataTokens.TypeDefinitionHandle(declaringTypeToken)
        metadataBuilder.AddTypeDefinition(
            TypeAttributes.Public ||| TypeAttributes.Class ||| TypeAttributes.BeforeFieldInit,
            metadataBuilder.GetOrAddString("SimpleTest"),
            metadataBuilder.GetOrAddString("TestApp"),
            MetadataTokens.TypeReferenceHandle(0x01000001), // System.Object
            MetadataTokens.FieldDefinitionHandle(0x04000001),
            MetadataTokens.MethodDefinitionHandle(0x06000001)
        ) |> ignore
        
        // Add the method to MethodDef table
        let methodDefHandle = MetadataTokens.MethodDefinitionHandle(methodToken)
        metadataBuilder.AddMethodDefinition(
            MethodAttributes.Public ||| MethodAttributes.Static ||| MethodAttributes.HideBySig,
            MethodImplAttributes.IL,
            metadataBuilder.GetOrAddString("getValue"),
            metadataBuilder.GetOrAddBlob(generateMethodSignature()),
            0, // RVA - will be filled in by the runtime
            MetadataTokens.ParameterHandle(0x08000001)  // ParameterList - will be filled in by the runtime
        ) |> ignore
        
        // Add both the type and method to ENCMap
        metadataBuilder.AddEncMapEntry(typeDefHandle)
        metadataBuilder.AddEncMapEntry(methodDefHandle)
        
        // Add EncLog entry for method update
        metadataBuilder.AddEncLogEntry(
            methodDefHandle,
            EditAndContinueOperation.Default)
        
        // Create and serialize the metadata using MetadataRootBuilder
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
        
        // For EnC updates, we only need the raw IL instructions
        // No header is needed as we're only updating the method body
        let ilCode = [| 0x1Fuy; byte returnValue; 0x2Auy |] // ldc.i4.s <value>; ret
        ilBuilder.WriteBytes(ilCode)
        
        let ilBytes = ilBuilder.ToArray()
        printfn "[DeltaGenerator] Generated IL delta: %d bytes" ilBytes.Length
        printfn "[DeltaGenerator] IL delta bytes: %A" ilBytes
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
    /// Main entry point for generating deltas.
    /// </summary>
    let generateDelta (generator: DeltaGenerator) (assembly: Assembly) (returnValue: int) =
        async {
            printfn "[DeltaGenerator] ===== Starting delta generation ====="
            printfn "[DeltaGenerator] Assembly: %s" assembly.FullName
            printfn "[DeltaGenerator] Assembly location: %s" assembly.Location
            printfn "[DeltaGenerator] Module ID: %A" assembly.ManifestModule.ModuleVersionId
            printfn "[DeltaGenerator] Target return value: %d" returnValue
            
            // Find the method to update
            printfn "[DeltaGenerator] Looking for SimpleTest type..."
            let methodToUpdate = 
                let type' = assembly.GetType("SimpleTest")
                if type' = null then 
                    printfn "[DeltaGenerator] Error: Could not find SimpleTest type"
                    null
                else 
                    printfn "[DeltaGenerator] Found SimpleTest type, looking for getValue method..."
                    type'.GetMethod("getValue", BindingFlags.Public ||| BindingFlags.Static)
            
            match methodToUpdate with
            | null -> 
                printfn "[DeltaGenerator] Error: Could not find getValue method"
                return None
            | method' ->
                let methodToken = method'.MetadataToken
                let declaringTypeToken = method'.DeclaringType.MetadataToken
                
                printfn "[DeltaGenerator] Found method info:"
                printfn "  - Token: %d" methodToken
                printfn "  - Declaring type token: %d" declaringTypeToken
                printfn "  - Method attributes: %A" method'.Attributes
                printfn "  - Method implementation attributes: %A" method'.MethodImplementationFlags
                
                // Generate minimal metadata and IL deltas
                printfn "[DeltaGenerator] Generating metadata delta..."
                // Ensure we're using the same ModuleId as the original assembly
                let moduleId = assembly.ManifestModule.ModuleVersionId
                let metadataDelta = generateMetadataDelta methodToken declaringTypeToken moduleId
                printfn "[DeltaGenerator] Metadata delta size: %d bytes" metadataDelta.Length
                
                printfn "[DeltaGenerator] Generating IL delta..."
                let ilDelta = generateILDelta returnValue
                printfn "[DeltaGenerator] IL delta size: %d bytes" ilDelta.Length
                
                printfn "[DeltaGenerator] Generating PDB delta..."
                let pdbDelta = generatePDBDelta methodToken
                printfn "[DeltaGenerator] PDB delta size: %d bytes" pdbDelta.Length
                
                // Create the complete delta
                let updatedMethods = ImmutableArray.Create<int>(methodToken)
                
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