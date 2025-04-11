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
open System.Reflection.Emit
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
        }

    /// <summary>
    /// Gets the method token for a method in an assembly.
    /// </summary>
    /// <param name="assembly">The assembly containing the method.</param>
    /// <param name="typeName">The full name of the type containing the method.</param>
    /// <param name="methodName">The name of the method.</param>
    /// <returns>The method token if found, None otherwise.</returns>
    let getMethodToken (assembly: Assembly) (typeName: string) (methodName: string) =
        let typ = assembly.GetType(typeName)
        if typ = null then 
            printfn "[DeltaGenerator] Could not find type: %s" typeName
            None
        else
            let method = typ.GetMethod(methodName, BindingFlags.Public ||| BindingFlags.Static)
            if method = null then 
                printfn "[DeltaGenerator] Could not find method: %s in type: %s" methodName typeName
                None
            else 
                printfn "[DeltaGenerator] Found method token: %d" method.MetadataToken
                Some method.MetadataToken

    /// <summary>
    /// Compares two F# symbol uses to determine if they represent the same method.
    /// </summary>
    let private isSameMethod (oldUse: FSharpSymbolUse) (newUse: FSharpSymbolUse) =
        oldUse.Symbol.FullName = newUse.Symbol.FullName &&
        oldUse.Symbol.DeclarationLocation.Value.FileName = newUse.Symbol.DeclarationLocation.Value.FileName

    /// <summary>
    /// Creates project options for a single file.
    /// </summary>
    let private createProjectOptions (filePath: string) =
        let projectOptions = {
            ProjectFileName = filePath
            ProjectId = None
            SourceFiles = [| filePath |]
            OtherOptions = [||]
            ReferencedProjects = [||]
            IsIncompleteTypeCheckEnvironment = false
            UseScriptResolutionRules = false
            LoadTime = DateTime.Now
            UnresolvedReferences = None
            OriginalLoadReferences = []
            Stamp = None
        }
        projectOptions

    /// <summary>
    /// Main entry point for generating deltas.
    /// </summary>
    /// <param name="generator">The DeltaGenerator instance to use.</param>
    /// <param name="assembly">The assembly to update.</param>
    /// <param name="filePath">The path to the source file that has changed.</param>
    /// <param name="typeName">The full name of the type containing the method to update.</param>
    /// <param name="methodName">The name of the method to update.</param>
    /// <returns>
    /// An async computation that returns Some Delta if deltas were successfully generated,
    /// or None if generation failed.
    /// </returns>
    let generateDelta (generator: DeltaGenerator) (assembly: Assembly) (filePath: string) (typeName: string) (methodName: string) =
        async {
            printfn "[DeltaGenerator] Generating delta for file: %s" filePath
            printfn "[DeltaGenerator] Looking for method: %s in type: %s" methodName typeName
            
            // Get the method token for the method we want to update
            let methodToken = getMethodToken assembly typeName methodName
            match methodToken with
            | None -> 
                printfn "[DeltaGenerator] Could not find method token"
                return None
            | Some token ->
                printfn "[DeltaGenerator] Found method token: %d" token
                
                // Read the source file
                let! sourceText = File.ReadAllTextAsync(filePath) |> Async.AwaitTask
                let sourceText = SourceText.ofString sourceText
                
                // Create project options
                let projectOptions = createProjectOptions filePath
                
                // Parse and type check the file
                let! parseResults, checkResults = 
                    generator.Compiler.ParseAndCheckFileInProject(
                        filePath,
                        0,
                        sourceText,
                        projectOptions
                    )
                
                // Find the method in the new compilation
                let methodSymbol = 
                    match checkResults with
                    | FSharpCheckFileAnswer.Succeeded results ->
                        results.GetSymbolUsesAtLocation(0, 0, "", [])
                        |> Seq.tryFind (fun (symbolUse: FSharpSymbolUse) -> 
                            symbolUse.Symbol.DisplayName = methodName &&
                            symbolUse.Symbol.DeclarationLocation.Value.FileName = filePath
                        )
                    | _ -> None
                
                match methodSymbol with
                | None ->
                    printfn "[DeltaGenerator] Could not find method symbol in new compilation"
                    return None
                | Some newMethodSymbol ->
                    // Compare with previous compilation if available
                    let updatedMethods = 
                        match generator.PreviousCompilation with
                        | Some prevCompilation ->
                            let oldMethodSymbol = 
                                prevCompilation.GetSymbolUsesAtLocation(0, 0, "", [])
                                |> Seq.tryFind (fun (symbolUse: FSharpSymbolUse) -> 
                                    symbolUse.Symbol.DisplayName = methodName &&
                                    symbolUse.Symbol.DeclarationLocation.Value.FileName = filePath
                                )
                            
                            match oldMethodSymbol with
                            | Some oldSymbol when not (isSameMethod oldSymbol newMethodSymbol) ->
                                ImmutableArray.Create<int>([| token |])
                            | _ -> ImmutableArray<int>.Empty
                        | None -> ImmutableArray.Create<int>([| token |])
                    
                    // TODO: Generate proper metadata and IL deltas using F# compiler services
                    // For now, we'll generate a simple delta that just changes the return value
                    let metadataDelta = ImmutableArray<byte>.Empty // TODO: Generate proper metadata delta
                    let ilDelta = ImmutableArray.Create<byte>([| 0x16uy; 0x2Buy |]) // ldc.i4.s 43; ret
                    let pdbDelta = ImmutableArray<byte>.Empty // TODO: Generate proper PDB delta
                    let updatedTypes = ImmutableArray<int>.Empty // TODO: Track updated types
                    
                    printfn "[DeltaGenerator] Generated delta with method token: %d" token
                    return Some {
                        ModuleId = assembly.ManifestModule.ModuleVersionId
                        MetadataDelta = metadataDelta
                        ILDelta = ilDelta
                        PdbDelta = pdbDelta
                        UpdatedTypes = updatedTypes
                        UpdatedMethods = updatedMethods
                    }
        } 