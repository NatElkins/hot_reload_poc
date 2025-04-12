namespace TestApp

open System
open System.IO
open System.Reflection
open System.Reflection.Metadata
open System.Reflection.PortableExecutable
open System.Collections.Immutable
open System.Runtime.Loader
open FSharp.Compiler.CodeAnalysis
open FSharp.Compiler.Text
open FSharp.Compiler.Symbols

module HotReloadTest =
    /// Template for our test module
    let testModuleTemplate = """
module SimpleTest

let getValue() = {0}
"""

    /// Creates a new F# checker instance
    let createChecker () = FSharpChecker.Create()

    /// Compiles the test module with the given return value
    let compileTestModule (checker: FSharpChecker) (returnValue: int) (outputPath: string) =
        async {
            // Create the source code with the given return value
            let sourceCode = String.Format(testModuleTemplate, returnValue)
            let sourceText = SourceText.ofString sourceCode
            let sourceFileName = Path.Combine(Path.GetTempPath(), "SimpleTest.fsx")
            
            // Write source to file
            File.WriteAllText(sourceFileName, sourceCode)

            // Get project options from script
            let! projectOptions, _ = 
                checker.GetProjectOptionsFromScript(
                    sourceFileName,
                    sourceText,
                    assumeDotNetFramework = false,
                    useSdkRefs = true,
                    useFsiAuxLib = false
                )

            // Update output path in options
            let projectOptions = { 
                projectOptions with 
                    OtherOptions = Array.append projectOptions.OtherOptions [| 
                        $"--out:{outputPath}"
                        "--target:library"
                        "--langversion:preview"
                        "--debug:full"
                        "--optimize-"
                    |] 
            }

            // Parse and type check
            let! parseResults, checkResults = 
                checker.ParseAndCheckFileInProject(
                    sourceFileName,
                    0,
                    sourceText,
                    projectOptions
                )

            // Compile
            let! compileResult, exitCode =
                checker.Compile(
                    [| "fsc.exe"
                       $"--out:{outputPath}"
                       yield! projectOptions.OtherOptions
                       sourceFileName |]
                )

            match exitCode with
            | None ->
                // Get the method token for getValue
                let methodToken = 
                    match checkResults with
                    | FSharpCheckFileAnswer.Succeeded results ->
                        results.GetAllUsesOfAllSymbolsInFile()
                        |> Seq.tryFind (fun (symbolUse: FSharpSymbolUse) -> 
                            symbolUse.Symbol.DisplayName = "getValue" &&
                            symbolUse.IsFromDefinition
                        )
                        |> Option.map (fun symbolUse -> 
                            let method = symbolUse.Symbol :?> FSharpMemberOrFunctionOrValue
                            let typeName = method.DeclaringEntity.Value.FullName
                            let methodName = method.DisplayName
                            let assembly = Assembly.LoadFrom(outputPath)
                            let typ = assembly.GetType(typeName)
                            let methodInfo = typ.GetMethod(methodName, BindingFlags.Public ||| BindingFlags.Static)
                            methodInfo.MetadataToken
                        )
                    | _ -> None

                match methodToken with
                | Some token ->
                    printfn "[HotReloadTest] Found method token: %d" token
                    return Some (checkResults, token, outputPath)
                | None ->
                    printfn "[HotReloadTest] Could not find method token"
                    return None
            | Some _ ->
                printfn "[HotReloadTest] Compilation failed with errors: %A" compileResult
                return None
        }

    /// Runs the hot reload test
    let runTest () =
        async {
            // Create a custom AssemblyLoadContext
            let alc = new AssemblyLoadContext("HotReloadTestContext", true)
            
            // Create temporary directory for our test files
            let tempDir = Path.Combine(Path.GetTempPath(), "HotReloadTest")
            if Directory.Exists(tempDir) then
                Directory.Delete(tempDir, true)
            Directory.CreateDirectory(tempDir) |> ignore

            // Define paths
            let originalDll = Path.Combine(tempDir, "original.dll")
            let modifiedDll = Path.Combine(tempDir, "modified.dll")

            // Create checker
            let checker = createChecker()

            // Compile original version (42)
            printfn "Compiling original version..."
            let! originalResult = compileTestModule checker 42 originalDll
            match originalResult with
            | None -> return failwith "Failed to compile original version"
            | Some (_, originalToken, _) ->
                // Load and verify original version
                let originalAssembly = alc.LoadFromAssemblyPath(originalDll)
                let simpleTestType = originalAssembly.GetType("SimpleTest")
                let getValueMethod = simpleTestType.GetMethod("getValue", BindingFlags.Public ||| BindingFlags.Static)
                let originalValue = getValueMethod.Invoke(null, [||]) :?> int
                printfn "Original value: %d" originalValue

                // Compile modified version (43)
                printfn "Compiling modified version..."
                let! modifiedResult = compileTestModule checker 43 modifiedDll
                match modifiedResult with
                | None -> return failwith "Failed to compile modified version"
                | Some (_, modifiedToken, _) ->
                    // Generate and apply deltas
                    use originalReader = new PEReader(File.OpenRead(originalDll))
                    use modifiedReader = new PEReader(File.OpenRead(modifiedDll))
                    
                    // Get the entire PE image
                    let originalImage = originalReader.GetEntireImage()
                    let modifiedImage = modifiedReader.GetEntireImage()
                    
                    // Convert ImmutableArray to byte array
                    let toByteArray (immutableArray: ImmutableArray<byte>) =
                        let array = Array.zeroCreate<byte> immutableArray.Length
                        immutableArray.CopyTo(array)
                        array
                    
                    // Calculate delta between two byte arrays
                    let calculateDelta (newBytes: byte[]) (oldBytes: byte[]) =
                        let delta = Array.zeroCreate<byte> newBytes.Length
                        for i in 0 .. newBytes.Length - 1 do
                            delta[i] <- newBytes[i] - oldBytes[i]
                        delta
                    
                    // Get the metadata and IL deltas
                    let originalContent = toByteArray (originalImage.GetContent())
                    let modifiedContent = toByteArray (modifiedImage.GetContent())
                    let metadataDelta = calculateDelta modifiedContent originalContent
                    let ilDelta = calculateDelta modifiedContent originalContent
                    
                    // Apply the update without PDB data
                    MetadataUpdater.ApplyUpdate(
                        originalAssembly,
                        metadataDelta.AsSpan(),
                        ilDelta.AsSpan(),
                        ReadOnlySpan<byte>.Empty
                    )

                    // Verify the update
                    let newValue = getValueMethod.Invoke(null, [||]) :?> int
                    printfn "New value after update: %d" newValue

                    // Clean up
                    try Directory.Delete(tempDir, true) with _ -> ()
                    return ()
        } 