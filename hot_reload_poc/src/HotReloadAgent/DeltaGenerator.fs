namespace HotReloadAgent

// Core F# compiler services for parsing and type checking
open FSharp.Compiler.CodeAnalysis
open FSharp.Compiler.Text
open FSharp.Compiler.IO

// System libraries for file operations and metadata handling
open System
open System.IO
open System.Reflection.Metadata
open System.Reflection.Metadata.Ecma335
open System.Reflection.PortableExecutable
open Prelude

/// In-memory filesystem implementation to capture compilation output
/// This is used to intercept the compiler's output and store it in memory
/// rather than writing to disk, which is more efficient for hot reload
type InMemoryFileSystem() =
    inherit DefaultFileSystem()
    member val InMemoryStream = new MemoryStream()
    override this.OpenFileForWriteShim(_, _, _, _) = this.InMemoryStream

/// Represents the three types of deltas needed for hot reload:
/// 1. MetadataDelta: Changes to type definitions and metadata tables
/// 2. ILDelta: Changes to method bodies and IL instructions
/// 3. PdbDelta: Changes to debug information and sequence points
type Delta = {
    MetadataDelta: byte[]
    ILDelta: byte[]
    PdbDelta: byte[]
}

/// Main generator for creating hot reload deltas
/// Tracks the compiler state and previous compilation results
type DeltaGenerator = {
    Compiler: FSharpChecker
    PreviousCompilation: FSharpCheckFileResults option
    PreviousAssemblyPath: string option
}

module DeltaGenerator =
    /// Creates a new DeltaGenerator with a fresh F# compiler instance
    let create () =
        {
            Compiler = FSharpChecker.Create()
            PreviousCompilation = None
            PreviousAssemblyPath = None
        }

    /// Compiles a single file and returns the type checking results
    /// This is the first step in the hot reload process - getting the new compilation
    let compileFile (generator: DeltaGenerator) (filePath: string) =
        async {
            // Read and parse the source file
            let sourceText = SourceText.ofString (File.ReadAllText(filePath))
            
            // Get project options for the script/file
            let! projectOptions, _ = generator.Compiler.GetProjectOptionsFromScript(filePath, sourceText)
            
            // Parse and type check the file
            let! _parseResults, checkResults = generator.Compiler.ParseAndCheckFileInProject(filePath, 0, sourceText, projectOptions)
            
            match checkResults with
            | FSharpCheckFileAnswer.Succeeded results ->
                return Some results
            | FSharpCheckFileAnswer.Aborted ->
                return None
        }

    /// Parses a PE (Portable Executable) file into a PEReader and MetadataReader
    /// This is used to analyze both the previous and new versions of the assembly
    let private parsePE (bytes: byte[]) =
        use stream = new MemoryStream(bytes)
        let reader = new PEReader(stream)
        let metadataReader = reader.GetMetadataReader()
        (reader, metadataReader)

    /// Generates the metadata delta by comparing metadata tables between old and new versions
    /// This includes changes to type definitions, method signatures, and other metadata
    let private generateMetadataDelta (prevReader: PEReader) (newReader: PEReader) =
        let prevMetadata = prevReader.GetMetadataReader()
        let newMetadata = newReader.GetMetadataReader()
        
        // Compare metadata tables
        let mutable changes = ResizeArray<byte>()
        
        // Add version byte (currently version 1)
        changes.Add(1uy)
        
        // Add file path information for the changed assembly
        let filePath = "changed.dll"
        let pathBytes = System.Text.Encoding.UTF8.GetBytes(filePath)
        changes.AddRange(BitConverter.GetBytes(pathBytes.Length |> int32))
        changes.AddRange(pathBytes)
        
        // Add number of deltas (always 3: metadata, IL, and PDB)
        changes.AddRange(BitConverter.GetBytes(3 |> int32))
        
        // Compare each metadata table for changes
        for table in Enum.GetValues(typeof<TableIndex>) do
            let tableIndex = table :?> TableIndex
            let prevTable = prevMetadata.GetTableRowCount(tableIndex)
            let newTable = newMetadata.GetTableRowCount(tableIndex)
            
            if prevTable <> newTable then
                // Record changes to table row counts
                changes.Add(byte tableIndex)
                changes.AddRange(BitConverter.GetBytes(newTable |> int32))
        
        changes.ToArray()

    /// Generates the IL delta by comparing method bodies between old and new versions
    /// This includes changes to method implementations and IL instructions
    let private generateILDelta (prevReader: PEReader) (newReader: PEReader) =
        let prevMetadata = prevReader.GetMetadataReader()
        let newMetadata = newReader.GetMetadataReader()
        
        let mutable changes = ResizeArray<byte>()
        
        // Compare method bodies for changes
        for handle : MethodDefinitionHandle in newMetadata.MethodDefinitions do
            let method = newMetadata.GetMethodDefinition(handle)
            let prevMethod = prevMetadata.MethodDefinitions
                            |> Seq.tryFind (fun h -> 
                                let m = prevMetadata.GetMethodDefinition(h)
                                m.Name = method.Name && 
                                m.Signature = method.Signature)
            
            match prevMethod with
            | Some prevHandle ->
                let prevMethod = prevMetadata.GetMethodDefinition(prevHandle)
                if prevMethod.RelativeVirtualAddress <> method.RelativeVirtualAddress then
                    // Method body has changed - record the new RVA
                    changes.AddRange(BitConverter.GetBytes(MetadataTokens.GetToken(!> handle : EntityHandle) |> int32))
                    changes.AddRange(BitConverter.GetBytes(method.RelativeVirtualAddress |> int32))
            | None ->
                // New method added - record its token and RVA
                changes.AddRange(BitConverter.GetBytes(MetadataTokens.GetToken(!> handle : EntityHandle) |> int32))
                changes.AddRange(BitConverter.GetBytes(method.RelativeVirtualAddress |> int32))
        
        changes.ToArray()

    /// Generates the PDB delta by comparing debug information between old and new versions
    /// This includes changes to sequence points, local variables, and other debug info
    let private generatePdbDelta (prevReader: PEReader) (newReader: PEReader) =
        let prevMetadata = prevReader.GetMetadataReader()
        let newMetadata = newReader.GetMetadataReader()
        
        let mutable changes = ResizeArray<byte>()
        
        // Compare debug information for changes
        for handle: MethodDebugInformationHandle in newMetadata.MethodDebugInformation do
            let debugInfo = newMetadata.GetMethodDebugInformation(handle)
            let prevDebugInfo = prevMetadata.MethodDebugInformation
                              |> Seq.tryFind (fun h -> 
                                  let d = prevMetadata.GetMethodDebugInformation(h)
                                  d.SequencePointsBlob = debugInfo.SequencePointsBlob)
            
            match prevDebugInfo with
            | Some prevHandle ->
                let prevDebugInfo = prevMetadata.GetMethodDebugInformation(prevHandle)
                if prevDebugInfo.SequencePointsBlob <> debugInfo.SequencePointsBlob then
                    // Debug info has changed - record the new sequence points
                    changes.AddRange(BitConverter.GetBytes(MetadataTokens.GetToken(!> handle : EntityHandle) |> int32))
                    if not debugInfo.SequencePointsBlob.IsNil then
                        let blob = newMetadata.GetBlobBytes(debugInfo.SequencePointsBlob)
                        changes.AddRange(blob)
            | None ->
                // New debug info - record its token and sequence points
                changes.AddRange(BitConverter.GetBytes(MetadataTokens.GetToken(!> handle : EntityHandle) |> int32))
                if not debugInfo.SequencePointsBlob.IsNil then
                    let blob = newMetadata.GetBlobBytes(debugInfo.SequencePointsBlob)
                    changes.AddRange(blob)
        
        changes.ToArray()

    /// Main entry point for generating deltas
    /// This orchestrates the entire hot reload process:
    /// 1. Compiles the changed file
    /// 2. Compares with previous compilation
    /// 3. Generates all three types of deltas
    let generateDelta (generator: DeltaGenerator) (filePath: string) =
        async {
            // Step 1: Compile the changed file
            let! newCompilation = compileFile generator filePath
            
            match newCompilation, generator.PreviousCompilation with
            | Some newComp, Some prevComp ->
                // Step 2: Set up in-memory filesystem for capturing compilation output
                let filesystem = InMemoryFileSystem()
                FileSystemAutoOpens.FileSystem <- filesystem
                
                try
                    // Get project options for compilation
                    let sourceText = SourceText.ofString (File.ReadAllText(filePath))
                    let! projectOptions, _ = generator.Compiler.GetProjectOptionsFromScript(filePath, sourceText)
                    
                    // Step 3: Compile previous version
                    let! _prevDiagnostics, prevError = 
                        generator.Compiler.Compile
                            [| "fsc.exe"
                               "--out:prev.dll"
                               "--debug:full"
                               yield! projectOptions.OtherOptions
                               yield! projectOptions.ReferencedProjects |> Array.map (fun v -> v.OutputFile)
                               yield! projectOptions.SourceFiles |]
                    
                    match prevError with
                    | None ->
                        let prevBytes = filesystem.InMemoryStream.ToArray()
                        filesystem.InMemoryStream.SetLength(0L)
                        
                        // Step 4: Compile new version
                        let! _newDiagnostics, newError = 
                            generator.Compiler.Compile
                                [| "fsc.exe"
                                   "--out:new.dll"
                                   "--debug:full"
                                   yield! projectOptions.OtherOptions
                                   yield! projectOptions.ReferencedProjects |> Array.map (fun v -> v.OutputFile)
                                   yield! projectOptions.SourceFiles |]
                        
                        match newError with
                        | None ->
                            let newBytes = filesystem.InMemoryStream.ToArray()
                            
                            // Step 5: Parse both versions
                            let prevReader, _ = parsePE prevBytes
                            let newReader, _ = parsePE newBytes
                            
                            // Step 6: Generate all three types of deltas
                            let metadataDelta = generateMetadataDelta prevReader newReader
                            let ilDelta = generateILDelta prevReader newReader
                            let pdbDelta = generatePdbDelta prevReader newReader
                            
                            return Some {
                                MetadataDelta = metadataDelta
                                ILDelta = ilDelta
                                PdbDelta = pdbDelta
                            }
                        | Some _ ->
                            return None
                    | Some _ ->
                        return None
                finally
                    // Reset the filesystem
                    FileSystemAutoOpens.FileSystem <- DefaultFileSystem() :> IFileSystem
            | Some newComp, None ->
                // First compilation - no delta to generate
                return None
            | None, _ ->
                // Compilation failed
                return None
        } 