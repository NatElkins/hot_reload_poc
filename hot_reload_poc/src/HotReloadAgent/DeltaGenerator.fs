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
    let private generateMetadataDelta (methodToken: int) (methodName: string) (declaringTypeToken: int) =
        printfn "[DeltaGenerator] Generating metadata delta for method %s (token: %d)" methodName methodToken
        printfn "[DeltaGenerator] Declaring type token: %d" declaringTypeToken
        let metadataBuilder = MetadataBuilder()
        
        // Add TypeDef table entry
        printfn "[DeltaGenerator] Adding TypeDef table entry..."
        let typeDefHandle = metadataBuilder.AddTypeDefinition(
            TypeAttributes.Public ||| TypeAttributes.Class,
            metadataBuilder.GetOrAddString(""),  // namespace
            metadataBuilder.GetOrAddString("DynamicType"),  // name
            metadataBuilder.AddTypeReference(
                metadataBuilder.AddAssemblyReference(
                    metadataBuilder.GetOrAddString("System.Runtime"),  // name
                    Version(9, 0, 0, 0),  // version
                    metadataBuilder.GetOrAddString(""),  // culture
                    metadataBuilder.GetOrAddBlob([|0xB0uy; 0x3Fuy; 0x5Fuy; 0x7Fuy; 0x11uy; 0xD5uy; 0x0Auy; 0x3Auy|]),  // publicKeyOrToken
                    enum<AssemblyFlags>(0),  // flags
                    metadataBuilder.GetOrAddBlob([||])  // hashValue
                ),
                metadataBuilder.GetOrAddString("System"),
                metadataBuilder.GetOrAddString("Object")
            ),  // baseType
            MetadataTokens.FieldDefinitionHandle(1),  // fieldList
            MetadataTokens.MethodDefinitionHandle(1)  // methodList
        )
        printfn "[DeltaGenerator] Added TypeDef with handle: %A" typeDefHandle
        
        // Add MethodDef table entry
        printfn "[DeltaGenerator] Adding MethodDef table entry..."
        let methodDefHandle = metadataBuilder.AddMethodDefinition(
            MethodAttributes.Public ||| MethodAttributes.Static,  // attributes
            MethodImplAttributes.IL,  // implAttributes
            metadataBuilder.GetOrAddString("DynamicMethod"),  // name
            metadataBuilder.GetOrAddBlob(generateMethodSignature()),  // signature
            0,  // bodyOffset
            MetadataTokens.ParameterHandle(1)  // parameterList
        )
        printfn "[DeltaGenerator] Added MethodDef with handle: %A" methodDefHandle
        
        // Add EncLog entry for method update
        printfn "[DeltaGenerator] Adding EncLog entry..."
        metadataBuilder.AddEncLogEntry(
            methodDefHandle,
            EditAndContinueOperation.Default)
            
        // Add EncMap entry
        printfn "[DeltaGenerator] Adding EncMap entry..."
        metadataBuilder.AddEncMapEntry(methodDefHandle)
        
        // Create metadata root builder with generation
        printfn "[DeltaGenerator] Creating metadata root builder..."
        let rootBuilder = new MetadataRootBuilder(metadataBuilder)
        
        // Serialize metadata
        printfn "[DeltaGenerator] Serializing metadata..."
        let metadataBlob = new BlobBuilder()
        rootBuilder.Serialize(metadataBlob, 1, 0)  // Generation 1 for delta
        let metadataBytes = metadataBlob.ToArray()
        printfn "[DeltaGenerator] Generated metadata bytes: %d bytes" metadataBytes.Length
        printfn "[DeltaGenerator] Metadata bytes: %A" metadataBytes
        ImmutableArray.CreateRange(metadataBytes)

    /// <summary>
    /// Generates IL delta for a method update.
    /// </summary>
    let private generateILDelta (returnValue: int) =
        printfn "[DeltaGenerator] Generating IL delta for return value: %d" returnValue
        let ilBuilder = new BlobBuilder()
        
        // Method header (tiny format)
        printfn "[DeltaGenerator] Writing method header..."
        ilBuilder.WriteByte(0x02uy)  // Tiny format, size follows
        ilBuilder.WriteByte(0x03uy)  // Method size = 3 bytes
        
        // Method body
        printfn "[DeltaGenerator] Writing IL instructions:"
        printfn "  ldc.i4.s %d" returnValue
        ilBuilder.WriteByte(0x16uy)  // ldc.i4.s
        ilBuilder.WriteByte(byte returnValue)
        
        printfn "  ret"
        ilBuilder.WriteByte(0x2Auy)  // ret
        
        let ilBytes = ilBuilder.ToArray()
        printfn "[DeltaGenerator] Generated IL bytes: %d bytes" ilBytes.Length
        printfn "[DeltaGenerator] IL bytes: %A" ilBytes
        ImmutableArray.CreateRange(ilBytes)

    /// <summary>
    /// Generates PDB delta for a method update.
    /// </summary>
    let private generatePDBDelta (methodToken: int) =
        printfn "[DeltaGenerator] Generating PDB delta for method token: %d" methodToken
        let pdbBuilder = new BlobBuilder()
        
        // Document table
        printfn "[DeltaGenerator] Writing document table..."
        pdbBuilder.WriteByte(0x01uy)  // 1 document
        pdbBuilder.WriteCompressedInteger(1)  // Document name index
        
        // Method debug info
        printfn "[DeltaGenerator] Writing method debug info..."
        pdbBuilder.WriteCompressedInteger(methodToken)  // Method token
        pdbBuilder.WriteByte(0x01uy)  // 1 sequence point
        
        // Sequence point
        printfn "[DeltaGenerator] Writing sequence point..."
        pdbBuilder.WriteCompressedInteger(0)  // IL offset
        pdbBuilder.WriteCompressedInteger(1)  // Source line
        pdbBuilder.WriteCompressedInteger(1)  // Source column
        
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
                let methodName = method'.Name
                let declaringTypeToken = method'.DeclaringType.MetadataToken
                
                printfn "[DeltaGenerator] Found method info:"
                printfn "  - Token: %d" methodToken
                printfn "  - Name: %s" methodName
                printfn "  - Declaring type token: %d" declaringTypeToken
                printfn "  - Method attributes: %A" method'.Attributes
                printfn "  - Method implementation attributes: %A" method'.MethodImplementationFlags
                
                // Generate minimal metadata and IL deltas
                printfn "[DeltaGenerator] Generating metadata delta..."
                let metadataDelta = generateMetadataDelta methodToken methodName declaringTypeToken
                printfn "[DeltaGenerator] Metadata delta size: %d bytes" metadataDelta.Length
                
                printfn "[DeltaGenerator] Generating IL delta..."
                let ilDelta = generateILDelta returnValue
                printfn "[DeltaGenerator] IL delta size: %d bytes" ilDelta.Length
                
                printfn "[DeltaGenerator] Generating PDB delta..."
                let pdbDelta = generatePDBDelta methodToken
                printfn "[DeltaGenerator] PDB delta size: %d bytes" pdbDelta.Length
                
                let updatedTypes = ImmutableArray<int>.Empty
                let updatedMethods = ImmutableArray.Create<int>(methodToken)
                
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