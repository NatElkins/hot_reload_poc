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
        Environment.CurrentDirectory

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
                
                // Also check if the assembly has the DebuggableAttribute with DisableOptimizations
                let debugAttribute = originalAssembly.GetCustomAttribute<DebuggableAttribute>()
                printfn "[HotReloadTest] DebuggableAttribute: %A" debugAttribute
                if debugAttribute <> null then
                    printfn "  - IsJITTrackingEnabled: %b" debugAttribute.IsJITTrackingEnabled
                    printfn "  - IsJITOptimizerDisabled: %b" debugAttribute.IsJITOptimizerDisabled
                
                // Exhaustively search for all methods that might be related to the getValue method
                let findRelatedMethods (assembly: Assembly) (methodName: string) =
                    printfn "[HotReloadTest] Searching for methods related to: %s" methodName
                    let relatedMethods =
                        assembly.GetTypes()
                        |> Array.collect (fun t ->
                            let methods = 
                                t.GetMethods(BindingFlags.Public ||| BindingFlags.NonPublic ||| BindingFlags.Static ||| BindingFlags.Instance)
                                |> Array.filter (fun m ->
                                    m.Name.Contains(methodName) || 
                                    (t.FullName.Contains("InvokeStub") && m.Name.Contains("get_")) ||
                                    t.Name.Contains("InvokeStub"))
                            
                            // Print all found methods for visibility
                            for m in methods do
                                printfn "  - Found related method: %s::%s (Token: 0x%08X)" t.FullName m.Name m.MetadataToken
                                
                                // Dump IL bytes if available
                                let body = m.GetMethodBody()
                                if body <> null then
                                    let ilBytes = body.GetILAsByteArray()
                                    if ilBytes <> null && ilBytes.Length > 0 then
                                        printfn "    - IL bytes: %A" ilBytes
                                        printfn "    - IL hex: %s" (BitConverter.ToString(ilBytes))
                            
                            methods)
                    relatedMethods
                    
                // Find all related methods
                let relatedMethods = findRelatedMethods originalAssembly "getValue"
                printfn "[HotReloadTest] Found %d related methods" relatedMethods.Length
                
                // Get the original getValue method
                let simpleTestType = originalAssembly.GetType(typeName)
                let getValueMethod = simpleTestType.GetMethod(methodName, BindingFlags.Public ||| BindingFlags.Static)
                
                // Try to find an InvokeStub method to use instead of the regular method
                let invokeStubMethod = 
                    relatedMethods 
                    |> Array.tryFind (fun m -> 
                        m.DeclaringType.FullName.Contains("InvokeStub") || 
                        m.DeclaringType.Name.Contains("InvokeStub"))
                
                // Get the final method to use
                let finalMethod, isInvokeStub = 
                    match invokeStubMethod with
                    | Some m -> 
                        printfn "[HotReloadTest] Found InvokeStub method to use: %s::%s (Token: 0x%08X)" 
                            m.DeclaringType.FullName m.Name m.MetadataToken
                        m, true
                    | None -> 
                        printfn "[HotReloadTest] No InvokeStub method found, using regular method: %s::%s (Token: 0x%08X)" 
                            getValueMethod.DeclaringType.FullName getValueMethod.Name getValueMethod.MetadataToken
                        getValueMethod, false
                
                // Detailed inspection of the method and type
                printfn "[HotReloadTest] Detailed method and type inspection before update:"
                printfn "  - Type full name: %s" simpleTestType.FullName
                printfn "  - Type namespace: %s" (if String.IsNullOrEmpty(simpleTestType.Namespace) then "<empty>" else simpleTestType.Namespace)
                printfn "  - Type attributes: %A" simpleTestType.Attributes
                printfn "  - Type token: 0x%08X" simpleTestType.MetadataToken
                
                // Method details
                printfn "  - Method name: %s" finalMethod.Name
                printfn "  - Method attributes: %A" finalMethod.Attributes
                printfn "  - Method impl flags: %A" finalMethod.MethodImplementationFlags
                printfn "  - Method token: 0x%08X" finalMethod.MetadataToken
                
                // Check if metadata updates are supported
                printfn "[HotReloadTest] MetadataUpdater.IsSupported: %b" MetadataUpdater.IsSupported
                let modifiableAssemblies = Environment.GetEnvironmentVariable("DOTNET_MODIFIABLE_ASSEMBLIES")
                printfn "[HotReloadTest] DOTNET_MODIFIABLE_ASSEMBLIES: %s" (if modifiableAssemblies = null then "not set" else modifiableAssemblies)
                
                // Record the module ID for verification later
                printfn "[HotReloadTest] Original module ID: %A" originalAssembly.ManifestModule.ModuleVersionId
                
                // Check if there's any F# metadata
                let fsharpAttrs = simpleTestType.GetCustomAttributes(true)
                                    |> Array.filter (fun attr -> attr.GetType().FullName.StartsWith("Microsoft.FSharp"))
                printfn "  - F# custom attributes on type: %d" fsharpAttrs.Length
                for attr in fsharpAttrs do
                    printfn "    - %s" (attr.GetType().FullName)
                
                // Check method body
                printfn "  - Method body inspection:"
                let methodBody = finalMethod.GetMethodBody()
                if methodBody <> null then
                    let ilBytes = methodBody.GetILAsByteArray()
                    printfn "    - IL bytes: %A" ilBytes
                    printfn "    - IL hex: %s" (BitConverter.ToString(ilBytes))
                    
                    // Try to parse the IL
                    if ilBytes.Length > 0 then
                        let firstByte = ilBytes.[0]
                        let tinyFormat = (firstByte &&& 0x03uy) = 0x02uy
                        if tinyFormat then
                            let codeSize = int (firstByte >>> 2)
                            printfn "    - Format: Tiny (1-byte header)"
                            printfn "    - Code size from header: %d bytes" codeSize
                            
                            // Display the actual IL instructions
                            printfn "    - Instructions:"
                            let mutable i = 1 // Skip header
                            while i < ilBytes.Length do
                                match ilBytes.[i] with
                                | 0x1Fuy -> // ldc.i4.s
                                    if i+1 < ilBytes.Length then
                                        printfn "      IL_%04X: ldc.i4.s %d" (i-1) (sbyte ilBytes.[i+1])
                                        i <- i + 1
                                    else
                                        printfn "      IL_%04X: ldc.i4.s <incomplete>" (i-1)
                                | 0x2Auy -> printfn "      IL_%04X: ret" (i-1)
                                | opcode -> printfn "      IL_%04X: Unknown opcode 0x%02X" (i-1) opcode
                                i <- i + 1
                        else
                            printfn "    - Format: Fat header or not a method body"
                    else
                        printfn "    - IL bytes are empty"
                else
                    printfn "    - Could not get method body"
                
                // Examine module structure
                let module' = originalAssembly.ManifestModule
                printfn "  - Module metadatatoken: 0x%08X" module'.MetadataToken
                printfn "  - Module mvid: %A" module'.ModuleVersionId
                
                // Examine all types in the assembly to look for related methods
                printfn "  - All types in assembly:"
                let allTypes = originalAssembly.GetTypes()
                for t in allTypes do
                    printfn "    - Type: %s (0x%08X)" t.FullName t.MetadataToken
                    let methods = t.GetMethods(BindingFlags.Public ||| BindingFlags.NonPublic ||| BindingFlags.Static ||| BindingFlags.Instance)
                    for m in methods do
                        printfn "      - Method: %s (0x%08X)" m.Name m.MetadataToken
                
                let originalValue = finalMethod.Invoke(null, [||]) :?> int
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
                    let! delta = DeltaGenerator.generateDelta generator originalAssembly 43 isInvokeStub
                    
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
                            
                            // Run mdv analysis on the delta files
                            printfn "[HotReloadTest] Running mdv analysis..."
                            
                            // Use mdv's auto-discovery feature in the current directory
                            let startInfo = ProcessStartInfo(
                                FileName = "mdv",
                                Arguments = "", // No arguments needed for auto-discovery
                                WorkingDirectory = deltaDir, // Set working directory to deltaDir
                                RedirectStandardOutput = true,
                                UseShellExecute = false,
                                CreateNoWindow = true
                            )
                            
                            try
                                use mdvProcess = Process.Start(startInfo)
                                let mdvOutput = mdvProcess.StandardOutput.ReadToEnd()
                                mdvProcess.WaitForExit()
                                printfn "[HotReloadTest] mdv output:\n%s" mdvOutput
                            with ex ->
                                printfn "[HotReloadTest] Failed to run mdv: %s" ex.Message
                            
                            // Also run ilspycmd on both versions to compare IL
                            printfn "[HotReloadTest] Running ilspycmd for IL analysis..."
                            
                            // Function to run ilspycmd and return output
                            let runILSpyCmdAndSave (dllPath: string) (outputPath: string) =
                                let startInfo = ProcessStartInfo(
                                    FileName = "ilspycmd",
                                    Arguments = dllPath,
                                    RedirectStandardOutput = true,
                                    UseShellExecute = false,
                                    CreateNoWindow = true
                                )
                                
                                try
                                    use process = Process.Start(startInfo)
                                    let output = process.StandardOutput.ReadToEnd()
                                    process.WaitForExit()
                                    
                                    // Save output to file
                                    File.WriteAllText(outputPath, output)
                                    printfn "[HotReloadTest] Saved IL analysis for %s to %s" 
                                        (Path.GetFileName(dllPath)) outputPath
                                    
                                    // Return true if successful
                                    true
                                with ex ->
                                    printfn "[HotReloadTest] Failed to run ilspycmd on %s: %s" 
                                        (Path.GetFileName(dllPath)) ex.Message
                                    false
                            
                            // Analyze original DLL
                            let originalILPath = Path.Combine(deltaDir, "original.il")
                            let originalSuccess = runILSpyCmdAndSave originalDll originalILPath
                            
                            // Analyze modified DLL
                            let modifiedILPath = Path.Combine(deltaDir, "modified.il")
                            let modifiedSuccess = runILSpyCmdAndSave modifiedDll modifiedILPath
                            
                            // Compare the IL files if both were analyzed successfully
                            if originalSuccess && modifiedSuccess then
                                printfn "[HotReloadTest] Comparing IL differences between original and modified assemblies..."
                                
                                // Run diff command through Process.Start
                                let diffStartInfo = ProcessStartInfo(
                                    FileName = "diff",
                                    Arguments = sprintf "-u \"%s\" \"%s\"" originalILPath modifiedILPath,
                                    RedirectStandardOutput = true,
                                    UseShellExecute = false,
                                    CreateNoWindow = true
                                )
                                
                                try
                                    use diffProcess = Process.Start(diffStartInfo)
                                    let diffOutput = diffProcess.StandardOutput.ReadToEnd()
                                    diffProcess.WaitForExit()
                                    
                                    // Save diff output
                                    let diffPath = Path.Combine(deltaDir, "il_diff.txt")
                                    File.WriteAllText(diffPath, diffOutput)
                                    
                                    if diffOutput.Trim().Length > 0 then
                                        printfn "[HotReloadTest] IL differences found. See %s for details." diffPath
                                    else
                                        printfn "[HotReloadTest] No IL differences found between original and modified assemblies."
                                with ex ->
                                    printfn "[HotReloadTest] Failed to compare IL files: %s" ex.Message
                            
                            // Dump more information about the method after the update
                            try
                                // Get the method descriptor info
                                printfn "[HotReloadTest] Method information after update:"
                                printfn "  - Method display name: %s" finalMethod.Name
                                printfn "  - Method declaring type: %s" finalMethod.DeclaringType.FullName
                                printfn "  - Method token: 0x%08X" finalMethod.MetadataToken
                                printfn "  - Method attributes: %A" finalMethod.Attributes
                                printfn "  - Method implementation flags: %A" finalMethod.MethodImplementationFlags 
                                printfn "  - Method return type: %s" finalMethod.ReturnType.FullName
                                
                                // Get module information
                                let module' = finalMethod.Module
                                printfn "  - Module: %s" module'.Name
                                printfn "  - Module version ID: %A" module'.ModuleVersionId
                                
                                // Get the entry point (code address)
                                printfn "  - Method handle: %A" finalMethod.MethodHandle
                                // Try to get function pointer
                                try
                                    let funcPtr = finalMethod.MethodHandle.GetFunctionPointer()
                                    let ptrValue = funcPtr.ToInt64()
                                    printfn "  - Function pointer: 0x%016X" ptrValue
                                with ex ->
                                    printfn "  - Failed to get function pointer: %s" ex.Message
                            with ex ->
                                printfn "[HotReloadTest] Failed to get additional method info: %s" ex.Message
                            
                            // Inspect the IL of the method after applying the update
                            printfn "[HotReloadTest] Inspecting method IL after update using MethodBody.GetILAsByteArray():"
                            let methodBody = finalMethod.GetMethodBody()
                            if methodBody <> null then
                                let ilBytes = methodBody.GetILAsByteArray()
                                printfn "  - IL bytes after update: %A" ilBytes
                                if ilBytes <> null && ilBytes.Length > 0 then
                                    printfn "  - IL hex after update: %s" (BitConverter.ToString(ilBytes))
                                    // Try to parse the IL
                                    let tinyFormat = (ilBytes[0] &&& 0x03uy) = 0x02uy
                                    if tinyFormat then
                                        let codeSize = int (ilBytes[0] >>> 2)
                                        printfn "  - IL format: Tiny (1-byte header)"
                                        printfn "  - Code size from header: %d bytes" codeSize
                                    else
                                        printfn "  - IL format: Fat header or not a method body"
                                else
                                    printfn "  - IL bytes are null or empty after update"
                            else
                                printfn "  - Could not get method body"
                            
                            // Verify the update
                            let newValue = finalMethod.Invoke(null, [||]) :?> int
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