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
open HotReloadAgent

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
                    
                    // Run mdv on both versions for verification
                    printfn "[HotReloadTest] Running mdv verification..."
                    let originalMdvOutput = Path.Combine(tempDir, "original_mdv.txt")
                    let modifiedMdvOutput = Path.Combine(tempDir, "modified_mdv.txt")
                    
                    let runMdv (dllPath: string) (outputPath: string) =
                        let processStartInfo = System.Diagnostics.ProcessStartInfo()
                        processStartInfo.FileName <- "/Users/nat/.dotnet/tools/mdv"
                        processStartInfo.Arguments <- $"{Path.GetFileName(dllPath)} /il+ /md+ /stats+ /assemblyRefs+"
                        processStartInfo.UseShellExecute <- false
                        processStartInfo.RedirectStandardOutput <- true
                        processStartInfo.RedirectStandardError <- true
                        processStartInfo.WorkingDirectory <- Path.GetDirectoryName(dllPath)

                        use proc = System.Diagnostics.Process.Start(processStartInfo)
                        if proc = null then
                            failwith "Failed to start mdv process. Make sure mdv is installed and in your PATH."
                        
                        let output = proc.StandardOutput.ReadToEnd()
                        let error = proc.StandardError.ReadToEnd()
                        proc.WaitForExit()

                        if proc.ExitCode <> 0 then
                            printfn "mdv process details:"
                            printfn "  Exit Code: %d" proc.ExitCode
                            printfn "  Standard Output: %s" output
                            printfn "  Standard Error: %s" error
                            failwithf "mdv failed with exit code %d" proc.ExitCode

                        if String.IsNullOrWhiteSpace(output) then
                            printfn "Warning: mdv produced no output"
                            printfn "Standard Error: %s" error

                        // Write output to file
                        File.WriteAllText(outputPath, output)
                        output

                    printfn "[HotReloadTest] Running mdv on original version..."
                    let originalMdv = runMdv originalDll originalMdvOutput
                    printfn "[HotReloadTest] Running mdv on modified version..."
                    let modifiedMdv = runMdv modifiedDll modifiedMdvOutput
                    
                    printfn "\n[HotReloadTest] Original Assembly Analysis:\n%s" originalMdv
                    printfn "\n[HotReloadTest] Modified Assembly Analysis:\n%s" modifiedMdv
                    
                    // Print detailed information about the original and modified assemblies
                    printfn "[HotReloadTest] Original assembly metadata:"
                    printfn "  - Module version ID: %A" originalAssembly.ManifestModule.ModuleVersionId
                    printfn "  - Module name: %s" originalAssembly.ManifestModule.Name
                    printfn "  - Module scope: %s" originalAssembly.ManifestModule.ScopeName
                    printfn "  - Module fully qualified name: %s" originalAssembly.ManifestModule.FullyQualifiedName
                    
                    printfn "[HotReloadTest] Modified assembly metadata:"
                    let modifiedAssembly = Assembly.LoadFrom(modifiedDll)
                    printfn "  - Module version ID: %A" modifiedAssembly.ManifestModule.ModuleVersionId
                    printfn "  - Module name: %s" modifiedAssembly.ManifestModule.Name
                    printfn "  - Module scope: %s" modifiedAssembly.ManifestModule.ScopeName
                    printfn "  - Module fully qualified name: %s" modifiedAssembly.ManifestModule.FullyQualifiedName
                    
                    // Check if the assembly is a runtime assembly
                    if not (originalAssembly.GetType().FullName.StartsWith("System.Runtime")) then
                        printfn "[HotReloadTest] Warning: Assembly is not a runtime assembly"
                    
                    // Check if metadata updates are supported
                    if not (MetadataUpdater.IsSupported) then
                        printfn "[HotReloadTest] Error: Metadata updates are not supported"
                        return ()
                    
                    // Create the delta generator
                    let generator = DeltaGenerator.create()
                    
                    // Generate the delta
                    let! delta = DeltaGenerator.generateDelta generator originalAssembly 43
                    
                    match delta with
                    | None ->
                        printfn "[HotReloadTest] Failed to generate delta"
                        return ()
                    | Some delta ->
                        printfn "[HotReloadTest] Generated delta:"
                        printfn "  - Metadata: %d bytes" delta.MetadataDelta.Length
                        printfn "  - IL: %d bytes" delta.ILDelta.Length
                        printfn "  - PDB: %d bytes" delta.PdbDelta.Length
                        printfn "  - Updated methods: %A" delta.UpdatedMethods
                        printfn "  - Updated types: %A" delta.UpdatedTypes
                        
                        // Print detailed information about the deltas
                        printfn "[HotReloadTest] Delta details:"
                        printfn "  - Module ID: %A" delta.ModuleId
                        let metadataBytes = delta.MetadataDelta.AsSpan().ToArray()
                        printfn "  - Metadata delta first 16 bytes: %A" (if metadataBytes.Length >= 16 then metadataBytes |> Array.take 16 else metadataBytes)
                        let ilBytes = delta.ILDelta.AsSpan().ToArray()
                        printfn "  - IL delta first 16 bytes: %A" (if ilBytes.Length >= 16 then ilBytes |> Array.take 16 else ilBytes)
                        let pdbBytes = delta.PdbDelta.AsSpan().ToArray()
                        printfn "  - PDB delta first 16 bytes: %A" (if pdbBytes.Length >= 16 then pdbBytes |> Array.take 16 else pdbBytes)
                        
                        // Apply the update
                        try
                            printfn "[HotReloadTest] Attempting to apply update..."
                            
                            // Try loading the assembly in the default context first
                            let defaultAssembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(originalDll)
                            try
                                MetadataUpdater.ApplyUpdate(
                                    defaultAssembly,
                                    delta.MetadataDelta.AsSpan(),
                                    delta.ILDelta.AsSpan(),
                                    delta.PdbDelta.AsSpan()
                                )
                                printfn "[HotReloadTest] Update applied successfully to default context"
                                
                                // Verify the update
                                let newValue = getValueMethod.Invoke(null, [||]) :?> int
                                printfn "New value after update: %d" newValue
                            with ex ->
                                printfn "[HotReloadTest] Failed to apply update to default context: %A" ex
                                printfn "[HotReloadTest] Trying custom context..."
                                
                                // Try the custom context as fallback
                                MetadataUpdater.ApplyUpdate(
                                    originalAssembly,
                                    delta.MetadataDelta.AsSpan(),
                                    delta.ILDelta.AsSpan(),
                                    delta.PdbDelta.AsSpan()
                                )
                                printfn "[HotReloadTest] Update applied successfully to custom context"
                                
                                // Verify the update
                                let newValue = getValueMethod.Invoke(null, [||]) :?> int
                                printfn "New value after update: %d" newValue
                            
                            return ()
                        with ex ->
                            printfn "[HotReloadTest] Failed to apply update: %A" ex
                            printfn "[HotReloadTest] Exception details:"
                            printfn "  - Message: %s" ex.Message
                            printfn "  - Stack trace: %s" ex.StackTrace
                            if ex.InnerException <> null then
                                printfn "  - Inner exception: %A" ex.InnerException
                            return ()

                    // Clean up
                    try Directory.Delete(tempDir, true) with _ -> ()
                    return ()
        } 