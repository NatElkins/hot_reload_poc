namespace HotReloadAgent

open System
open System.IO
open System.Reflection
open FSharp.Compiler.CodeAnalysis
open FSharp.Compiler.Text
open FSharp.Compiler.Symbols

module InMemoryCompiler =
    /// <summary>
    /// The source code template for our test module.
    /// </summary>
    let testModuleTemplate = """
module SimpleTest

/// <summary>
/// Returns a constant value that we'll change during hot reload.
/// </summary>
/// <returns>The value {0}.</returns>
let getValue() = {0}
"""

    /// <summary>
    /// Creates a new F# checker instance.
    /// </summary>
    let createChecker () = FSharpChecker.Create()

    /// <summary>
    /// Compiles the test module with the given return value.
    /// </summary>
    /// <param name="checker">The F# checker instance to use.</param>
    /// <param name="returnValue">The value to return from getValue().</param>
    /// <returns>The compiled assembly and the method token for getValue().</returns>
    let compileTestModule (checker: FSharpChecker) (returnValue: int) =
        async {
            // Create the source code with the given return value
            let sourceCode = String.Format(testModuleTemplate, returnValue)
            let sourceText = SourceText.ofString sourceCode

            // Create a temporary file name for the source
            let sourceFileName = "SimpleTest.fs"

            // Create project options for a single file
            let projectOptions = {
                ProjectFileName = sourceFileName
                ProjectId = None
                SourceFiles = [| sourceFileName |]
                OtherOptions = [||]
                ReferencedProjects = [||]
                IsIncompleteTypeCheckEnvironment = false
                UseScriptResolutionRules = false
                LoadTime = DateTime.Now
                UnresolvedReferences = None
                OriginalLoadReferences = []
                Stamp = None
            }

            // Parse and type check the file
            let! parseResults, checkResults = 
                checker.ParseAndCheckFileInProject(
                    sourceFileName,
                    0,
                    sourceText,
                    projectOptions
                )

            // Compile to disk
            let outputPath = Path.Combine(Path.GetTempPath(), "TestAssembly.dll")
            let! compileResult, optExn =
                checker.Compile(
                    [| "fsc.exe"
                       $"--out:{outputPath}"
                       yield! projectOptions.OtherOptions
                       yield! projectOptions.SourceFiles |]
                )

            if optExn = None then
                // Load the compiled assembly
                let assembly = Assembly.LoadFrom(outputPath)
                
                // Get the method token for getValue
                let methodToken = 
                    match checkResults with
                    | FSharpCheckFileAnswer.Succeeded results ->
                        results.GetSymbolUsesAtLocation(0, 0, "", [])
                        |> Seq.tryFind (fun (symbolUse: FSharpSymbolUse) -> 
                            symbolUse.Symbol.DisplayName = "getValue" &&
                            symbolUse.Symbol.DeclarationLocation.Value.FileName = sourceFileName
                        )
                        |> Option.map (fun symbolUse -> 
                            let method = symbolUse.Symbol :?> FSharpMemberOrFunctionOrValue
                            let typeName = method.DeclaringEntity.Value.FullName
                            let methodName = method.DisplayName
                            let typ = assembly.GetType(typeName)
                            let methodInfo = typ.GetMethod(methodName, BindingFlags.Public ||| BindingFlags.Static)
                            methodInfo.MetadataToken
                        )
                    | _ -> None

                match methodToken with
                | Some token ->
                    printfn "[InMemoryCompiler] Found method token: %d" token
                    return Some (checkResults, token, assembly)
                | None ->
                    printfn "[InMemoryCompiler] Could not find method token"
                    return None
            else
                printfn "[InMemoryCompiler] Compilation failed with errors: %A" compileResult
                return None
        } 