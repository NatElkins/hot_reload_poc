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
open Mono.Cecil
#nowarn FS3261

module HotReloadTest =
    /// Custom AssemblyLoadContext that forces assembly resolution to match the target framework
    type FrameworkAwareAssemblyLoadContext(name: string) =
        inherit AssemblyLoadContext(name, isCollectible = true)
        
        // Get the target framework from the running assembly
        let getTargetFramework() =
            let assembly = Assembly.GetExecutingAssembly()
            let frameworkName = assembly.GetCustomAttribute<System.Runtime.Versioning.TargetFrameworkAttribute>()
            if frameworkName <> null then
                let framework = frameworkName.FrameworkName
                // Extract version from framework name (e.g., ".NETCoreApp,Version=v9.0")
                let versionMatch = System.Text.RegularExpressions.Regex.Match(framework, @"Version=v(\d+\.\d+)")
                if versionMatch.Success then
                    versionMatch.Groups.[1].Value
                else
                    "9.0" // Fallback to 9.0 if we can't parse it
            else
                "9.0" // Fallback to 9.0 if we can't find the attribute

        // Find the highest matching runtime version for a given framework version
        let findRuntimeVersion frameworkVersion =
            let runtimeBasePath = Path.Combine(
                (match Environment.GetEnvironmentVariable("DOTNET_ROOT") with
                | null -> "/usr/local/share/dotnet"
                | path -> path),
                "shared",
                "Microsoft.NETCore.App"
            )
            
            if Directory.Exists(runtimeBasePath) then
                // Get all runtime versions that start with our framework version
                let matchingVersions = 
                    Directory.GetDirectories(runtimeBasePath)
                    |> Array.filter (fun dir -> 
                        let version = Path.GetFileName(dir)
                        version.StartsWith(frameworkVersion + ".")
                    )
                    |> Array.sortByDescending Path.GetFileName
                
                if matchingVersions.Length > 0 then
                    // Return the highest version number
                    Path.GetFileName(matchingVersions.[0])
                else
                    frameworkVersion
            else
                frameworkVersion
        
        override this.Load(assemblyName: AssemblyName) =
            let targetFramework = getTargetFramework()
            let runtimeVersion = findRuntimeVersion targetFramework
            printfn "[FrameworkAwareAssemblyLoadContext] Target framework: %s" targetFramework
            printfn "[FrameworkAwareAssemblyLoadContext] Using runtime version: %s" runtimeVersion
            printfn "[FrameworkAwareAssemblyLoadContext] Attempting to load assembly: %s (Version: %s)" 
                assemblyName.Name 
                (assemblyName.Version.ToString())
            
            // Try to load from the target framework runtime directory first
            let runtimePath = Path.Combine(
                (match Environment.GetEnvironmentVariable("DOTNET_ROOT") with
                | null -> "/usr/local/share/dotnet"
                | path -> path),
                "shared",
                "Microsoft.NETCore.App",
                runtimeVersion
            )
            
            printfn "[FrameworkAwareAssemblyLoadContext] Looking in runtime path: %s" runtimePath
            
            if Directory.Exists(runtimePath) then
                let assemblyPath = Path.Combine(runtimePath, $"{assemblyName.Name}.dll")
                if File.Exists(assemblyPath) then
                    printfn "[FrameworkAwareAssemblyLoadContext] Found assembly in runtime: %s" assemblyPath
                    this.LoadFromAssemblyPath(assemblyPath)
                else
                    printfn "[FrameworkAwareAssemblyLoadContext] Assembly not found in runtime: %s" assemblyPath
                    printfn "[FrameworkAwareAssemblyLoadContext] Falling back to default loading behavior for: %s" assemblyName.Name
                    base.Load(assemblyName)
            else
                printfn "[FrameworkAwareAssemblyLoadContext] Falling back to default loading behavior for: %s" assemblyName.Name
                base.Load(assemblyName)

    /// Helper to convert byte array to hex string
    let private bytesToHex (bytes: byte[]) =
        System.BitConverter.ToString(bytes).Replace("-", "")

    /// Template for our test CLASS (changed from module for Attempt #13)
    let testClassTemplate = """
namespace TestApp // Using the main namespace for simplicity

[<AbstractClass; Sealed>]
type SimpleLib =
    static member GetValue() = {0}
"""

    /// Creates a new F# checker instance
    let createChecker () = FSharpChecker.Create()

    /// Compiles the test module with the given return value
    let compileTestModule (checker: FSharpChecker) (returnValue: int) (outputPath: string) =
        async {
            printfn "[HotReloadTest] Starting compilation with return value: %d" returnValue
            printfn "[HotReloadTest] Output path: %s" outputPath
            
            // Create the source code with the given return value
            let sourceCode = String.Format(testClassTemplate, returnValue)
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
                // Get the type name and method name for GetValue in SimpleLib
                let typeAndMethodNameOpt =
                    match checkResults with
                    | FSharpCheckFileAnswer.Succeeded results ->
                        results.GetAllUsesOfAllSymbolsInFile()
                        // Find the definition of the static method GetValue within the SimpleLib type
                        |> Seq.tryFind (fun symbolUse -> 
                            match symbolUse.Symbol with
                            | :? FSharpMemberOrFunctionOrValue as mfv 
                                when symbolUse.IsFromDefinition && 
                                     mfv.DisplayName = "GetValue" && 
                                     mfv.IsModuleValueOrMember && // Check if it's a static member/module value
                                     not mfv.IsInstanceMember ->
                                        match mfv.DeclaringEntity with
                                        | Some entity when entity.FullName = "TestApp.SimpleLib" -> true // Found it!
                                        | _ -> false
                            | _ -> false
                            )
                        // Extract the names if found
                        |> Option.map (fun symbolUse -> 
                            let mfv = symbolUse.Symbol :?> FSharpMemberOrFunctionOrValue
                            let entity = mfv.DeclaringEntity.Value
                            printfn "[HotReloadTest] Found method: %s in type: %s (via Symbol)" mfv.DisplayName entity.FullName
                            (entity.FullName, mfv.DisplayName)
                            )
                    | _ -> None

                match typeAndMethodNameOpt with
                | Some (typeName, methodName) ->
                    printfn "[HotReloadTest] Successfully identified method: %s in type: %s" methodName typeName
                    return Some (checkResults, (typeName, methodName), outputPath)
                | None ->
                    printfn "[HotReloadTest] Could not find method token for TestApp.SimpleLib.GetValue"
                    return None
            else
                printfn "[HotReloadTest] Compilation failed with errors: %A" compileResult
                return None
        }

    /// Runs the hot reload test
    let runTest () =
        async {
            // Create a custom AssemblyLoadContext that forces assembly resolution to match target framework
            let alc = new FrameworkAwareAssemblyLoadContext("HotReloadContext")
            
            // Create temporary directory for our test files
            let tempDir = Path.Combine(Path.GetTempPath(), "HotReloadTest")
            if Directory.Exists(tempDir) then
                Directory.Delete(tempDir, true)
            Directory.CreateDirectory(tempDir) |> ignore

            // Define paths
            let baselineDll = Path.Combine(tempDir, "0.dll")

            // Create checker
            let checker = createChecker()

            // Compile baseline version (42)
            printfn "Compiling baseline version..."
            let! originalResult = compileTestModule checker 42 baselineDll
            match originalResult with
            | None -> return failwith "Failed to compile baseline version"
            | Some (_, (typeName, methodName), compiledBaselinePath) ->

                // Turn off F# metadata stripping for now
                // // ---- START: Strip F# Metadata ----
                // printfn "[HotReloadTest] Stripping F# metadata from baseline DLL..."
                // if not (stripFSharpMetadata compiledBaselinePath) then
                //     return failwith "Failed to strip F# metadata from baseline DLL"
                // // ---- END: Strip F# Metadata ----

                // Use the *compiler's output directory* as the delta directory
                let deltaDir = Path.GetDirectoryName(compiledBaselinePath)
                printfn "[HotReloadTest] Using Delta Directory: %s" deltaDir

                // Load using our custom context to enable hot reload
                let originalAssembly = alc.LoadFromAssemblyPath(baselineDll)
                printfn "[HotReloadTest] Baseline assembly loaded:"
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
                
                // --- Verification Step: Compare FCS names with Reflection names ---
                printfn "[HotReloadTest] Verifying names..."
                printfn "  - Name from FCS: %s::%s" typeName methodName
                printfn "  - Name from Reflection: %s::%s" simpleTestType.FullName getValueMethod.Name
                if typeName <> simpleTestType.FullName || methodName <> getValueMethod.Name then
                    printfn "[HotReloadTest] WARNING: Mismatch between names derived from FCS and Reflection!"
                // --- End Verification ---
                
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

                // Generate and apply deltas directly based on desired state (return 43)
                // No need for modifiedReader anymore
                // use originalReader = new PEReader(File.OpenRead(baselineDll))
                // use modifiedReader = new PEReader(File.OpenRead(modifiedDll))
                
                // Ensure we use the module ID from the original assembly
                // This is critical for hot reload to work properly
                let originalModuleId = originalAssembly.ManifestModule.ModuleVersionId
                
                // Create the delta generator
                let generator = DeltaGenerator.create()
                
                // Generate the delta - pass typeName, methodName and isInvokeStub flag
                printfn "[HotReloadTest] Generating delta to change return value to 43..."
                let! delta = DeltaGenerator.generateDelta generator originalAssembly 43 isInvokeStub typeName methodName
                
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
                    printfn "  - Metadata delta bytes: %A" metadataBytes
                    let ilBytes = delta.ILDelta.AsSpan().ToArray()
                    printfn "  - IL delta bytes: %A" ilBytes
                    let pdbBytes = delta.PdbDelta.AsSpan().ToArray()
                    printfn "  - PDB delta bytes: %A" pdbBytes
                    
                    // Write the delta files to disk for inspection with mdv
                    printfn "[HotReloadTest] Writing delta files to disk for mdv inspection..."
                    
                    // Baseline DLL (0.dll) is already in the correct location (baselineDll path)
                    // No need to copy: File.Copy(baselineDll, Path.Combine(deltaDir, "0.dll"), true)
                    
                    // Write the delta files - use generation 1 for the delta
                    // Using .meta extension as expected by mdv's auto-detection
                    File.WriteAllBytes(Path.Combine(deltaDir, "1.meta"), metadataBytes)
                    File.WriteAllBytes(Path.Combine(deltaDir, "1.il"), ilBytes)
                    // Also keep the .md extension for direct inspection if needed - REMOVED based on user feedback
                    // File.WriteAllBytes(Path.Combine(deltaDir, "1.md"), metadataBytes) 
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

                    // ---- START: Read and Print Delta Hex Dumps ----
                    try
                        // Print F# generated delta
                        let fsMetaPath = Path.Combine(deltaDir, "1.meta") 
                        let fsMetaBytes = File.ReadAllBytes(fsMetaPath)
                        printfn "\n[HotReloadTest] F# Delta Hex (%s - %d bytes):\n%s\n" fsMetaPath fsMetaBytes.Length (bytesToHex fsMetaBytes)

                        // Print C# generated delta
                        let csMetaPath = "/Users/nat/Projects/ExpressionEvaluator/csharp_delta_test/bin/Debug/net10.0/1.meta" // Use absolute path
                        if File.Exists(csMetaPath) then
                            let csMetaBytes = File.ReadAllBytes(csMetaPath)
                            printfn "[HotReloadTest] C# Delta Hex (%s - %d bytes):\n%s\n" csMetaPath csMetaBytes.Length (bytesToHex csMetaBytes)
                        else
                            printfn "[HotReloadTest] C# Delta File NOT FOUND at: %s" csMetaPath
                    with ex ->
                        printfn "[HotReloadTest] Error reading/printing delta hex: %s" ex.Message
                    // ---- END: Read and Print Delta Hex Dumps ----

                    // Run mdv analysis on the delta files BEFORE attempting update
                    printfn "[HotReloadTest] Running mdv analysis BEFORE update attempt..."
                    
                    // Create a bash script to run mdv with the correct arguments
                    let scriptPath = Path.Combine(Path.GetTempPath(), "run_mdv.sh")
                    let scriptContent = 
                        "#!/bin/bash\n\n" +
                        "# Change to the specified directory\n" +
                        $"cd \"{deltaDir}\" || {{ echo \"Failed to change directory\"; exit 1; }}\n\n" +
                        "# Run mdv with the specified arguments\n" +
                        "mdv 0.dll '/g:1.meta;1.il' /stats+ /assemblyRefs+ /il+ /md+\n\n" +
                        "# Store and display exit code\n" +
                        "EXIT_CODE=$?\n" +
                        "echo \"mdv exited with code: $EXIT_CODE\"\n\n" +
                        "exit $EXIT_CODE"
                    
                    // Write script to file
                    File.WriteAllText(scriptPath, scriptContent)
                    
                    // Make script executable
                    let chmodStartInfo = ProcessStartInfo(
                        FileName = "chmod",
                        Arguments = $"+x \"{scriptPath}\"",
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    )
                    
                    try
                        use chmodProcess = Process.Start(chmodStartInfo)
                        chmodProcess.WaitForExit()
                        
                        // Now run the script
                        let scriptStartInfo = ProcessStartInfo(
                            FileName = scriptPath,
                            RedirectStandardOutput = true,
                            RedirectStandardError = true,
                            UseShellExecute = false, 
                            CreateNoWindow = true
                        )
                        
                        use scriptProcess = Process.Start(scriptStartInfo)
                        let scriptOutput = scriptProcess.StandardOutput.ReadToEnd()
                        let scriptError = scriptProcess.StandardError.ReadToEnd()
                        scriptProcess.WaitForExit()
                        
                        printfn "[HotReloadTest] Script output:\n%s" scriptOutput
                        if not (String.IsNullOrWhiteSpace(scriptError)) then
                            printfn "[HotReloadTest] Script errors:\n%s" scriptError
                            
                        // Clean up the script file
                        try
                            File.Delete(scriptPath)
                        with ex ->
                            printfn "[HotReloadTest] Failed to delete script file: %s" ex.Message
                    with ex ->
                        printfn "[HotReloadTest] Failed to run mdv script: %s" ex.Message

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
            } 