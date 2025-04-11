namespace HotReloadAgent

open FSharp.Compiler.CodeAnalysis
open FSharp.Compiler.Text
open FSharp.Compiler.IO
open FSharp.Compiler.Diagnostics
open System
open System.IO
open System.Reflection
open System.Runtime.CompilerServices

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
                                   yield! projectOptions.OtherOptions
                                   yield! projectOptions.ReferencedProjects |> Array.map (fun v -> v.OutputFile)
                                   yield! projectOptions.SourceFiles |]
                        
                        match newError with
                        | None ->
                            let newBytes = filesystem.InMemoryStream.ToArray()
                            
                            // Calculate the deltas by comparing the bytes
                            let metadataDelta = 
                                Array.zip prevBytes newBytes
                                |> Array.filter (fun (a, b) -> a <> b)
                                |> Array.map snd
                            
                            // For now, we'll use the same delta for IL and PDB
                            // In a real implementation, we would parse the PE file format
                            // to extract the specific sections
                            return Some {
                                MetadataDelta = metadataDelta
                                ILDelta = metadataDelta
                                PdbDelta = metadataDelta
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