namespace TestApp

open System
open System.IO
open System.Reflection
open System.Reflection.Metadata
open System.Reflection.PortableExecutable
open System.Collections.Immutable
open System.Runtime.Loader
open System.Diagnostics
open FSharp.Compiler.CodeAnalysis
open FSharp.Compiler.Text
open FSharp.Compiler.Symbols
open HotReloadAgent
open System.Runtime.CompilerServices
#nowarn FS3261

module HotReloadTest =
    /// Create a stable location for delta files
    let deltaDir = 
        let dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "HotReloadTest")
        Directory.CreateDirectory(dir) |> ignore
        dir

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
            printfn "[HotReloadTest] Starting compilation with return value: %d" returnValue
            printfn "[HotReloadTest] Output path: %s" outputPath
            
            // Create the source code with the given return value
            let sourceCode = String.Format(testModuleTemplate, returnValue)
            let sourceText = SourceText.ofString sourceCode
            let sourceFileName = Path.Combine(Path.GetTempPath(), "SimpleTest.fsx")
            
            printfn "[HotReloadTest] Writing source to: %s" sourceFileName
            File.WriteAllText(sourceFileName, sourceCode)

            // Get project options from script
            printfn "[HotReloadTest] Getting project options..."
            let! projectOptions, _ = 
                checker.GetProjectOptionsFromScript(
                    sourceFileName,
                    sourceText,
                    assumeDotNetFramework = false,
                    useSdkRefs = true,
                    useFsiAuxLib = false
                )

            // Update output path in options with consistent settings
            printfn "[HotReloadTest] Configuring compilation options..."
            let projectOptions = { 
                projectOptions with 
                    OtherOptions = Array.append projectOptions.OtherOptions [| 
                        $"--out:{outputPath}"
                        "--target:library"
                        "--langversion:preview"
                        "--debug:full"
                        "--optimize-"
                        "--deterministic"  // Add deterministic compilation
                        "--publicsign-"    // Disable strong naming
                    |] 
            }
            // printfn "[HotReloadTest] Compilation options: %A" projectOptions.OtherOptions

            // Parse and type check
            printfn "[HotReloadTest] Parsing and type checking..."
            let! parseResults, checkResults = 
                checker.ParseAndCheckFileInProject(
                    sourceFileName,
                    0,
                    sourceText,
                    projectOptions
                )

            // Verify parse results
            if parseResults.Diagnostics.Length > 0 then
                printfn "[HotReloadTest] Parse diagnostics:"
                for diag in parseResults.Diagnostics do
                    printfn "  - %s" diag.Message

            // Verify check results
            match checkResults with
            | FSharpCheckFileAnswer.Succeeded results ->
                if results.Diagnostics.Length > 0 then
                    printfn "[HotReloadTest] Type check diagnostics:"
                    for diag in results.Diagnostics do
                        printfn "  - %s" diag.Message
            | FSharpCheckFileAnswer.Aborted ->
                printfn "[HotReloadTest] Type checking aborted"
                ()

            // Compile
            printfn "[HotReloadTest] Compiling..."
            let! compileResult, optExn =
                checker.Compile(
                    [| "fsc.exe"
                       $"--out:{outputPath}"
                       yield! projectOptions.OtherOptions
                       sourceFileName |]
                )

            if optExn = None then
                printfn "[HotReloadTest] Compilation successful"
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
                            printfn "[HotReloadTest] Found method: %s in type: %s" methodName typeName
                            (typeName, methodName)
                        )
                    | _ -> None

                match methodToken with
                | Some (typeName, methodName) ->
                    printfn "[HotReloadTest] Found method: %s in type: %s" methodName typeName
                    return Some (checkResults, (typeName, methodName), outputPath)
                | None ->
                    printfn "[HotReloadTest] Could not find method token"
                    return None
            else
                printfn "[HotReloadTest] Compilation failed with errors: %A" compileResult
                return None
        }

    /// Runs the hot reload test
    let runTest () =
        async {
            // Create a custom AssemblyLoadContext that allows updates
            let alc = new AssemblyLoadContext("HotReloadContext", isCollectible = true)
            
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
            | Some (_, (typeName, methodName), _) ->
                // Load using our custom context to enable hot reload
                let originalAssembly = alc.LoadFromAssemblyPath(originalDll)
                printfn "[HotReloadTest] Original assembly loaded:"
                printfn "  - Name: %s" originalAssembly.FullName
                printfn "  - Location: %s" originalAssembly.Location
                printfn "  - IsCollectible: %b" originalAssembly.IsCollectible
                printfn "  - IsDynamic: %b" originalAssembly.IsDynamic
                printfn "  - IsFullyTrusted: %b" originalAssembly.IsFullyTrusted
                printfn "  - ReflectionOnly: %b" originalAssembly.ReflectionOnly
                printfn "  - SecurityRuleSet: %A" originalAssembly.SecurityRuleSet
                
                // Check if the assembly has the DebuggableAttribute with DisableOptimizations
                let debugAttribute = originalAssembly.GetCustomAttribute<DebuggableAttribute>()
                printfn "[HotReloadTest] DebuggableAttribute: %A" debugAttribute
                if debugAttribute <> null then
                    printfn "  - IsJITTrackingEnabled: %b" debugAttribute.IsJITTrackingEnabled
                    printfn "  - IsJITOptimizerDisabled: %b" debugAttribute.IsJITOptimizerDisabled
                
                // Check if metadata updates are supported
                printfn "[HotReloadTest] MetadataUpdater.IsSupported: %b" MetadataUpdater.IsSupported
                let modifiableAssemblies = Environment.GetEnvironmentVariable("DOTNET_MODIFIABLE_ASSEMBLIES")
                printfn "[HotReloadTest] DOTNET_MODIFIABLE_ASSEMBLIES: %s" (if modifiableAssemblies = null then "not set" else modifiableAssemblies)
                
                // Record the module ID for verification later
                printfn "[HotReloadTest] Original module ID: %A" originalAssembly.ManifestModule.ModuleVersionId
                
                let simpleTestType = originalAssembly.GetType(typeName)
                let getValueMethod = simpleTestType.GetMethod(methodName, BindingFlags.Public ||| BindingFlags.Static)
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
                    
                    // Ensure we use the module ID from the original assembly
                    // This is critical for hot reload to work properly
                    let originalModuleId = originalAssembly.ManifestModule.ModuleVersionId
                    
                    // Create the delta generator
                    let generator = DeltaGenerator.create()
                    
                    // Generate the delta - ensure we use originalModuleId
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
                        
                        // Write the delta files to disk for inspection with mdv
                        printfn "[HotReloadTest] Writing delta files to disk for mdv inspection..."
                        
                        // Write the original DLL
                        File.Copy(originalDll, Path.Combine(deltaDir, "0.dll"), true)
                        
                        // Write the delta files - use generation 1 for the delta
                        // Using .meta extension as expected by mdv's auto-detection
                        File.WriteAllBytes(Path.Combine(deltaDir, "1.meta"), metadataBytes)
                        File.WriteAllBytes(Path.Combine(deltaDir, "1.il"), ilBytes)
                        // Also keep the .md extension for direct inspection if needed
                        File.WriteAllBytes(Path.Combine(deltaDir, "1.md"), metadataBytes)
                        File.WriteAllBytes(Path.Combine(deltaDir, "1.pdb"), pdbBytes)

                        // Run mdv and ilspycmd on the delta files
                        let runAnalysisTools () =
                            // Run mdv with detailed output
                            let mdvProcess = new Process()
                            mdvProcess.StartInfo.FileName <- "/Users/nat/.dotnet/tools/mdv"
                            mdvProcess.StartInfo.Arguments <- $"/g:1.meta;1.il 0.dll /il+ /md+ /stats+ /assemblyRefs+"
                            mdvProcess.StartInfo.UseShellExecute <- false
                            mdvProcess.StartInfo.RedirectStandardOutput <- true
                            mdvProcess.StartInfo.RedirectStandardError <- true
                            mdvProcess.StartInfo.WorkingDirectory <- deltaDir
                            
                            printfn "[HotReloadTest] Running mdv analysis..."
                            mdvProcess.Start() |> ignore
                            let mdvOutput = mdvProcess.StandardOutput.ReadToEnd()
                            let mdvError = mdvProcess.StandardError.ReadToEnd()
                            mdvProcess.WaitForExit()
                            
                            if mdvProcess.ExitCode <> 0 then
                                printfn "[HotReloadTest] mdv failed with exit code %d" mdvProcess.ExitCode
                                printfn "Standard Error: %s" mdvError
                            else
                                printfn "[HotReloadTest] mdv output:\n%s" mdvOutput
                            
                            // Run ilspycmd on both versions
                            let ilspyProcess = new Process()
                            ilspyProcess.StartInfo.FileName <- "/Users/nat/.dotnet/tools/ilspycmd"
                            ilspyProcess.StartInfo.Arguments <- "0.dll"
                            ilspyProcess.StartInfo.UseShellExecute <- false
                            ilspyProcess.StartInfo.RedirectStandardOutput <- true
                            ilspyProcess.StartInfo.RedirectStandardError <- true
                            ilspyProcess.StartInfo.WorkingDirectory <- deltaDir
                            
                            printfn "[HotReloadTest] Running ilspycmd on original version..."
                            ilspyProcess.Start() |> ignore
                            let ilspyOutput = ilspyProcess.StandardOutput.ReadToEnd()
                            let ilspyError = ilspyProcess.StandardError.ReadToEnd()
                            ilspyProcess.WaitForExit()
                            
                            if ilspyProcess.ExitCode <> 0 then
                                printfn "[HotReloadTest] ilspycmd failed with exit code %d" ilspyProcess.ExitCode
                                printfn "Standard Error: %s" ilspyError
                            else
                                printfn "[HotReloadTest] ilspycmd output:\n%s" ilspyOutput
                        
                        // Run the analysis tools
                        runAnalysisTools()
                        
                        // Analyze the IL delta in more detail
                        printfn "[HotReloadTest] Detailed IL delta analysis:"
                        printfn "  Hex dump of IL delta bytes: %s" (BitConverter.ToString(ilBytes))
                        
                        // Parse and display the IL delta in a human-readable form
                        if ilBytes.Length > 0 then
                            let tinyFormat = (ilBytes[0] &&& 0x03uy) = 0x02uy
                            if tinyFormat then
                                let codeSize = int (ilBytes[0] >>> 2)
                                printfn "  IL format: Tiny (1-byte header)"
                                printfn "  Code size from header: %d bytes" codeSize
                                printfn "  Header byte: 0x%02X" ilBytes[0]
                                
                                // Display the actual IL instructions
                                printfn "  IL Instructions:"
                                let mutable i = 1 // Skip header
                                while i < ilBytes.Length do
                                    match ilBytes[i] with
                                    | 0x16uy -> printfn "    IL_%04X: ldc.i4.0" (i-1)
                                    | 0x17uy -> printfn "    IL_%04X: ldc.i4.1" (i-1)
                                    | 0x18uy -> printfn "    IL_%04X: ldc.i4.2" (i-1)
                                    | 0x19uy -> printfn "    IL_%04X: ldc.i4.3" (i-1)
                                    | 0x1Auy -> printfn "    IL_%04X: ldc.i4.4" (i-1)
                                    | 0x1Buy -> printfn "    IL_%04X: ldc.i4.5" (i-1)
                                    | 0x1Cuy -> printfn "    IL_%04X: ldc.i4.6" (i-1)
                                    | 0x1Duy -> printfn "    IL_%04X: ldc.i4.7" (i-1)
                                    | 0x1Euy -> printfn "    IL_%04X: ldc.i4.8" (i-1)
                                    | 0x1Fuy -> 
                                        if i+1 < ilBytes.Length then
                                            printfn "    IL_%04X: ldc.i4.s %d" (i-1) (sbyte ilBytes[i+1])
                                            i <- i + 1
                                        else
                                            printfn "    IL_%04X: ldc.i4.s <incomplete>" (i-1)
                                    | 0x20uy -> 
                                        if i+4 < ilBytes.Length then
                                            let value = 
                                                ilBytes[i+1] ||| 
                                                (ilBytes[i+2] <<< 8) ||| 
                                                (ilBytes[i+3] <<< 16) ||| 
                                                (ilBytes[i+4] <<< 24)
                                            printfn "    IL_%04X: ldc.i4 %d" (i-1) value
                                            i <- i + 4
                                        else
                                            printfn "    IL_%04X: ldc.i4 <incomplete>" (i-1)
                                    | 0x2Auy -> printfn "    IL_%04X: ret" (i-1)
                                    | opcode -> printfn "    IL_%04X: Unknown opcode 0x%02X" (i-1) opcode
                                    
                                    i <- i + 1
                            else
                                printfn "  IL format: Not tiny format (first byte: 0x%02X)" ilBytes[0]
                        else
                            printfn "  IL delta is empty"
                        
                        printfn "[HotReloadTest] Delta files written to: %s" deltaDir
                        printfn "[HotReloadTest] To analyze with mdv, run: cd \"%s\" && mdv 0.dll" deltaDir
                        printfn "[HotReloadTest] Or with explicit parameters: mdv /g:1.meta;1.il 0.dll"
                        
                        try
                            printfn "[HotReloadTest] Attempting to apply update..."
                            printfn "  - Assembly: %s" originalAssembly.FullName
                            printfn "  - Assembly location: %s" originalAssembly.Location
                            printfn "  - IsCollectible: %b" originalAssembly.IsCollectible
                            printfn "  - IsDynamic: %b" originalAssembly.IsDynamic
                            printfn "  - IsFullyTrusted: %b" originalAssembly.IsFullyTrusted
                            printfn "  - ReflectionOnly: %b" originalAssembly.ReflectionOnly
                            printfn "  - SecurityRuleSet: %A" originalAssembly.SecurityRuleSet
                            printfn "  - Module version ID: %A" originalAssembly.ManifestModule.ModuleVersionId
                            
                            // Validate the deltas before applying
                            printfn "[HotReloadTest] Validating deltas before applying..."
                            printfn "  - Metadata delta size: %d" delta.MetadataDelta.Length
                            printfn "  - IL delta size: %d" delta.ILDelta.Length
                            printfn "  - PDB delta size: %d" delta.PdbDelta.Length
                            
                            // Check if metadata updates are supported
                            printfn "[HotReloadTest] Checking metadata update support..."
                            printfn "  - MetadataUpdater.IsSupported: %b" MetadataUpdater.IsSupported
                            let modifiableAssemblies = Environment.GetEnvironmentVariable("DOTNET_MODIFIABLE_ASSEMBLIES")
                            printfn "  - DOTNET_MODIFIABLE_ASSEMBLIES: %s" (if modifiableAssemblies = null then "not set" else modifiableAssemblies)
                            
                            // Ensure the environment is configured for EnC
                            if not MetadataUpdater.IsSupported then
                                printfn "[HotReloadTest] Error: Metadata updates are not supported in this environment"
                                return ()
                            
                            if modifiableAssemblies = null then
                                printfn "[HotReloadTest] Warning: DOTNET_MODIFIABLE_ASSEMBLIES is not set. This may prevent updates from working."
                            
                            // Apply the update
                            MetadataUpdater.ApplyUpdate(
                                originalAssembly,
                                delta.MetadataDelta.AsSpan(),
                                delta.ILDelta.AsSpan(),
                                delta.PdbDelta.AsSpan()
                            )
                            printfn "[HotReloadTest] Update applied successfully"
                            
                            // Inspect the IL of the method after applying the update
                            printfn "[HotReloadTest] Inspecting method IL after update using MethodBody.GetILAsByteArray():"
                            let methodBody = getValueMethod.GetMethodBody()
                            if methodBody <> null then
                                let ilBytes = methodBody.GetILAsByteArray()
                                printfn "  - IL bytes after update: %A" ilBytes
                            else
                                printfn "  - Could not get method body"
                            
                            // Verify the update
                            let newValue = getValueMethod.Invoke(null, [||]) :?> int
                            printfn "New value after update: %d" newValue
                            
                            if newValue = 43 then
                                printfn "[HotReloadTest] Hot reload success! 🎉"
                            else
                                printfn "[HotReloadTest] Value didn't change as expected! Got %d, expected 43" newValue
                        with ex ->
                            printfn "[HotReloadTest] Failed to apply update: %A" ex
                            printfn "[HotReloadTest] Exception details:"
                            printfn "  - Message: %s" ex.Message
                            printfn "  - Stack trace: %s" ex.StackTrace
                            if ex.InnerException <> null then
                                printfn "  - Inner exception: %A" ex.InnerException
                        
                        // Don't clean up so we can inspect the files
                        // try Directory.Delete(tempDir, true) with _ -> ()
                        return ()
        } 