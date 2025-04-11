namespace TestApp

open System
open FSharp.Compiler.CodeAnalysis
open FSharp.Compiler.Text
open System.IO
open System.Collections.Immutable

module DeltaVerification =
    let compileFile (source: string) (outputPath: string) =
        let checker = FSharpChecker.Create()
        let sourceText = SourceText.ofString source
        let scriptPath = Path.Combine(Path.GetTempPath(), "test.fsx")
        
        // Write source to temporary script file
        File.WriteAllText(scriptPath, source)
        
        // Create compilation options
        let options, _ = 
            checker.GetProjectOptionsFromScript(
                scriptPath, 
                sourceText,
                assumeDotNetFramework = false,
                useSdkRefs = true,
                useFsiAuxLib = false
            )
            |> Async.RunSynchronously

        // Add output path and target framework to options
        let options = { 
            options with 
                OtherOptions = Array.append options.OtherOptions [| 
                    $"--out:{outputPath}"
                    "--target:library"
                    "--langversion:preview"
                |] 
        }

        // Compile the file
        let errors, exitCode = 
            checker.Compile(
                [| "fsc.exe"
                   yield! options.OtherOptions
                   scriptPath |]
            )
            |> Async.RunSynchronously

        // Clean up temporary script file
        try File.Delete(scriptPath) with _ -> ()

        match exitCode with
        | None -> 
            printfn "Compilation successful: %s" outputPath
            if not (Array.isEmpty errors) then
                printfn "Warnings: %A" errors
        | Some exn -> 
            printfn "Compilation failed with error: %A" exn
            printfn "Errors: %A" errors
            failwithf "Compilation failed"

    let runMdv (dllPath: string) (outputPath: string) =
        let absoluteDllPath = Path.GetFullPath(dllPath)
        let absoluteOutputPath = Path.GetFullPath(outputPath)
        let dllFileName = Path.GetFileName(dllPath)
        
        if not (File.Exists(absoluteDllPath)) then
            failwithf "DLL not found: %s" absoluteDllPath

        let processStartInfo = System.Diagnostics.ProcessStartInfo()
        processStartInfo.FileName <- "/Users/nat/.dotnet/tools/mdv"
        processStartInfo.Arguments <- $"{dllFileName} /il+ /md+ /stats+ /assemblyRefs+"
        processStartInfo.UseShellExecute <- false
        processStartInfo.RedirectStandardOutput <- true
        processStartInfo.RedirectStandardError <- true
        processStartInfo.WorkingDirectory <- Path.GetDirectoryName(absoluteDllPath)

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
        File.WriteAllText(absoluteOutputPath, output)

    let verifyDeltas () =
        // Template for test code
        let codeTemplate = sprintf """
module SimpleTest

let getValue() = %d
"""
        
        // Original code
        let originalCode = codeTemplate 42
        
        // Modified code
        let modifiedCode = codeTemplate 43

        // Create temporary directory for our test files
        let tempDir = "temp_verification"
        if Directory.Exists(tempDir) then
            Directory.Delete(tempDir, true)
        Directory.CreateDirectory(tempDir) |> ignore

        // Define paths
        let originalDll = Path.Combine(tempDir, "original.dll")
        let modifiedDll = Path.Combine(tempDir, "modified.dll")
        let originalMdvOutput = Path.Combine(tempDir, "original_mdv.txt")
        let modifiedMdvOutput = Path.Combine(tempDir, "modified_mdv.txt")

        // Compile both versions
        printfn "Compiling original version..."
        compileFile originalCode originalDll
        printfn "Compiling modified version..."
        compileFile modifiedCode modifiedDll

        // Run mdv on both versions
        printfn "Running mdv on original version..."
        runMdv originalDll originalMdvOutput
        printfn "Running mdv on modified version..."
        runMdv modifiedDll modifiedMdvOutput

        // Read and display the outputs
        let originalOutput = File.ReadAllText(originalMdvOutput)
        let modifiedOutput = File.ReadAllText(modifiedMdvOutput)
        
        printfn "\nOriginal Assembly Analysis:\n%s" originalOutput
        printfn "\nModified Assembly Analysis:\n%s" modifiedOutput

        // Clean up
        try
            Directory.Delete(tempDir, true)
        with ex ->
            printfn "Warning: Failed to clean up temporary directory: %s" ex.Message 