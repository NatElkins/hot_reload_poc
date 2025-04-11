namespace HotReloadAgent

open FSharp.Compiler.CodeAnalysis
open FSharp.Compiler.Text
open FSharp.Compiler.IO
open FSharp.Compiler.Diagnostics
open System
open System.IO
open System.Reflection
open System.Runtime.CompilerServices
open System.Reflection.Metadata
open System.Reflection.Metadata.Ecma335
open System.Reflection.PortableExecutable
open Prelude

type InMemoryFileSystem() =
    inherit DefaultFileSystem()
    member val InMemoryStream = new MemoryStream()
    override this.OpenFileForWriteShim(_, _, _, _) = this.InMemoryStream

type Delta = {
    MetadataDelta: byte[]
    ILDelta: byte[]
    PdbDelta: byte[]
}

type DeltaGenerator = {
    Compiler: FSharpChecker
    PreviousCompilation: FSharpCheckFileResults option
    PreviousAssemblyPath: string option
}

module DeltaGenerator =
    let create () =
        {
            Compiler = FSharpChecker.Create()
            PreviousCompilation = None
            PreviousAssemblyPath = None
        }

    let compileFile (generator: DeltaGenerator) (filePath: string) =
        async {
            let sourceText = SourceText.ofString (File.ReadAllText(filePath))
            let! projectOptions, _ = generator.Compiler.GetProjectOptionsFromScript(filePath, sourceText)
            let! _parseResults, checkResults = generator.Compiler.ParseAndCheckFileInProject(filePath, 0, sourceText, projectOptions)
            
            match checkResults with
            | FSharpCheckFileAnswer.Succeeded results ->
                return Some results
            | FSharpCheckFileAnswer.Aborted ->
                return None
        }

    let private parsePE (bytes: byte[]) =
        use stream = new MemoryStream(bytes)
        let reader = new PEReader(stream)
        let metadataReader = reader.GetMetadataReader()
        (reader, metadataReader)

    let private generateMetadataDelta (prevReader: PEReader) (newReader: PEReader) =
        let prevMetadata = prevReader.GetMetadataReader()
        let newMetadata = newReader.GetMetadataReader()
        
        // Compare metadata tables
        let mutable changes = ResizeArray<byte>()
        
        // Add version byte
        changes.Add(1uy) // Version 1
        
        // Add file path length and path
        let filePath = "changed.dll" // This should be the actual file path
        let pathBytes = System.Text.Encoding.UTF8.GetBytes(filePath)
        changes.AddRange(BitConverter.GetBytes(pathBytes.Length |> int32))
        changes.AddRange(pathBytes)
        
        // Add number of deltas
        changes.AddRange(BitConverter.GetBytes(3 |> int32)) // We always generate 3 deltas
        
        // Compare and generate metadata table changes
        for table in Enum.GetValues(typeof<TableIndex>) do
            let tableIndex = table :?> TableIndex
            let prevTable = prevMetadata.GetTableRowCount(tableIndex)
            let newTable = newMetadata.GetTableRowCount(tableIndex)
            
            if prevTable <> newTable then
                // Add table index and row count change
                changes.Add(byte tableIndex)
                changes.AddRange(BitConverter.GetBytes(newTable |> int32))
        
        changes.ToArray()

    let private generateILDelta (prevReader: PEReader) (newReader: PEReader) =
        let prevMetadata = prevReader.GetMetadataReader()
        let newMetadata = newReader.GetMetadataReader()
        
        let mutable changes = ResizeArray<byte>()
        
        // Compare method bodies
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
                    // Add method token and new RVA
                    changes.AddRange(BitConverter.GetBytes(MetadataTokens.GetToken(!> handle : EntityHandle) |> int32))
                    changes.AddRange(BitConverter.GetBytes(method.RelativeVirtualAddress |> int32))
            | None ->
                // New method
                changes.AddRange(BitConverter.GetBytes(MetadataTokens.GetToken(!> handle : EntityHandle) |> int32))
                changes.AddRange(BitConverter.GetBytes(method.RelativeVirtualAddress |> int32))
        
        changes.ToArray()

    let private generatePdbDelta (prevReader: PEReader) (newReader: PEReader) =
        let prevMetadata = prevReader.GetMetadataReader()
        let newMetadata = newReader.GetMetadataReader()
        
        let mutable changes = ResizeArray<byte>()
        
        // Compare sequence points
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
                    // Add method token and new sequence points
                    changes.AddRange(BitConverter.GetBytes(MetadataTokens.GetToken(!> handle : EntityHandle) |> int32))
                    if not debugInfo.SequencePointsBlob.IsNil then
                        let blob = newMetadata.GetBlobBytes(debugInfo.SequencePointsBlob)
                        changes.AddRange(blob)
            | None ->
                // New debug info
                changes.AddRange(BitConverter.GetBytes(MetadataTokens.GetToken(!> handle : EntityHandle) |> int32))
                if not debugInfo.SequencePointsBlob.IsNil then
                    let blob = newMetadata.GetBlobBytes(debugInfo.SequencePointsBlob)
                    changes.AddRange(blob)
        
        changes.ToArray()

    let generateDelta (generator: DeltaGenerator) (filePath: string) =
        async {
            let! newCompilation = compileFile generator filePath
            
            match newCompilation, generator.PreviousCompilation with
            | Some newComp, Some prevComp ->
                // Create in-memory filesystem
                let filesystem = InMemoryFileSystem()
                FileSystemAutoOpens.FileSystem <- filesystem
                
                try
                    // Get the project options for compilation
                    let sourceText = SourceText.ofString (File.ReadAllText(filePath))
                    let! projectOptions, _ = generator.Compiler.GetProjectOptionsFromScript(filePath, sourceText)
                    
                    // Compile both versions
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
                            
                            // Parse PE files
                            let prevReader, _ = parsePE prevBytes
                            let newReader, _ = parsePE newBytes
                            
                            // Generate proper deltas
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
                // First compilation, no delta to generate
                return None
            | None, _ ->
                // Compilation failed
                return None
        } 