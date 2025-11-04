#I "../../fsharp/artifacts/bin/FSharp.Compiler.Service/Debug/net10.0"
#r "FSharp.Compiler.Service.dll"

open System
open System.Diagnostics
open System.IO
open System.Text

open FSharp.Compiler.CodeAnalysis
open FSharp.Compiler.Diagnostics
open FSharp.Compiler.Text

module Msbuild =
    let writeCaptureTargets (directory: string) =
        let path = Path.Combine(directory, "FscWatchCapture.targets")
        let content =
            """
<Project xmlns="http://schemas.microsoft.com/developer/msbuild/2003">
  <Target Name="FscWatchCaptureArgs" AfterTargets="CoreCompile">
    <WriteLinesToFile File="$(FscWatchCommandLineLog)" Lines="@(FscCommandLineArgs)" Overwrite="true" />
  </Target>
</Project>
"""
        File.WriteAllText(path, content)
        path

    let runProcess (workingDirectory: string) (exe: string) (args: string list) =
        let startInfo = ProcessStartInfo()
        startInfo.FileName <- exe
        args |> List.iter startInfo.ArgumentList.Add
        startInfo.WorkingDirectory <- workingDirectory
        startInfo.RedirectStandardOutput <- true
        startInfo.RedirectStandardError <- true
        use proc = new Process()
        proc.StartInfo <- startInfo
        proc.Start() |> ignore
        let stdout = proc.StandardOutput.ReadToEnd()
        let stderr = proc.StandardError.ReadToEnd()
        proc.WaitForExit()
        proc.ExitCode, stdout, stderr

    let getFscCommandLine (projectPath: string) (configuration: string option) (targetFramework: string option) =
        let fullProjectPath = Path.GetFullPath(projectPath)
        if not (File.Exists fullProjectPath) then
            failwithf "Project '%s' not found" fullProjectPath

        let tempDir = Path.Combine(Path.GetTempPath(), "fsc-mdv-args", Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory(tempDir) |> ignore
        let targets = writeCaptureTargets tempDir
        let argsFile = Path.Combine(tempDir, "fsc.args")
        let baseArgs =
            [ "msbuild"
              "/restore"
              fullProjectPath
              "/t:Build"
              "/p:ProvideCommandLineArgs=true"
              $"/p:FscWatchCommandLineLog=\"{argsFile}\""
              $"/p:CustomAfterMicrosoftCommonTargets=\"{targets}\""
              "/nologo"
              "/v:quiet" ]

        let argsWithConfig =
            match configuration with
            | Some config -> baseArgs @ [ $"/p:Configuration={config}" ]
            | None -> baseArgs

        let finalArgs =
            match targetFramework with
            | Some tfm -> argsWithConfig @ [ $"/p:TargetFramework={tfm}" ]
            | None -> argsWithConfig

        let projectDir =
            match Path.GetDirectoryName(fullProjectPath) with
            | null | "" -> Directory.GetCurrentDirectory()
            | value -> value

        let exitCode, stdout, stderr = runProcess projectDir "dotnet" finalArgs
        if exitCode <> 0 then
            failwithf "dotnet msbuild failed (%d)\nSTDOUT:%s\nSTDERR:%s" exitCode stdout stderr

        if not (File.Exists argsFile) then failwith "Failed to capture command line arguments"

        let result =
            File.ReadAllLines(argsFile)
            |> Array.collect (fun line ->
                line.Split(';', StringSplitOptions.RemoveEmptyEntries)
                |> Array.map (fun arg -> arg.Trim()))
            |> Array.filter (fun arg -> not (String.IsNullOrWhiteSpace arg))

        try Directory.Delete(tempDir, true) with _ -> ()
        result

module Compiler =
    let private sanitizeOptions (options: string[]) =
        options
        |> Array.filter (fun opt ->
            not (opt.Equals("--times", StringComparison.OrdinalIgnoreCase))
            && not (opt.StartsWith("--sourcelink:", StringComparison.OrdinalIgnoreCase)))
        |> Array.map (fun opt ->
            if opt.StartsWith("-o:", StringComparison.OrdinalIgnoreCase) then
                "--out:" + opt.Substring(3)
            else
                opt)

    let prepareCompileInputs (projectFilePath: string) (commandLine: string[]) =
        let projectDirectory =
            match Path.GetDirectoryName(projectFilePath) with
            | null | "" -> Directory.GetCurrentDirectory()
            | value -> value

        let normalize (path: string) =
            let trimmed = path.Trim().Trim('"')
            if Path.IsPathRooted(trimmed) then trimmed else Path.GetFullPath(trimmed, projectDirectory)

        let sanitized = sanitizeOptions commandLine
        let resolvedArgs = ResizeArray<string>(sanitized.Length)
        let sources = ResizeArray<string>()
        let mutable expectOutArgument = false

        for arg in sanitized do
            if expectOutArgument then
                resolvedArgs.Add("--out:" + normalize arg)
                expectOutArgument <- false
            elif arg.StartsWith("--out:", StringComparison.OrdinalIgnoreCase) then
                resolvedArgs.Add("--out:" + normalize (arg.Substring("--out:".Length)))
            elif String.Equals(arg, "-o", StringComparison.OrdinalIgnoreCase) then
                expectOutArgument <- true
            elif arg.EndsWith(".fs", StringComparison.OrdinalIgnoreCase) then
                let fullPath = normalize arg
                resolvedArgs.Add(fullPath)
                sources.Add(fullPath)
            else
                resolvedArgs.Add(arg)

        if expectOutArgument then failwith "Malformed command line: '-o' missing argument."
        resolvedArgs.ToArray(), sources.ToArray()

    let buildCompileArgs (commandLine: string[]) =
        Array.append [| "fsc.exe" |] commandLine

    let tryGetOutputPath (projectPath: string) (options: FSharpProjectOptions) =
        let projectDirectory =
            match Path.GetDirectoryName(projectPath) with
            | null | "" -> Directory.GetCurrentDirectory()
            | value -> value

        let toAbsolute (text: string) =
            let trimmed = text.Trim().Trim('"')
            if Path.IsPathRooted trimmed then trimmed else Path.GetFullPath(trimmed, projectDirectory)

        let otherOptions = options.OtherOptions
        otherOptions
        |> Array.tryPick (fun opt ->
            if opt.StartsWith("--out:", StringComparison.OrdinalIgnoreCase) then
                opt.Substring("--out:".Length) |> toAbsolute |> Some
            elif opt.StartsWith("-o:", StringComparison.OrdinalIgnoreCase) then
                opt.Substring("-o:".Length) |> toAbsolute |> Some
            else None)

    let compile (checker: FSharpChecker) (args: string[]) =
        let diagnostics, exOpt = checker.Compile(args) |> Async.RunSynchronously
        let errors = diagnostics |> Array.filter (fun d -> d.Severity = FSharpDiagnosticSeverity.Error)
        match errors, exOpt with
        | [||], None -> ()
        | errs, _ ->
            let msgs = errs |> Array.map (fun d -> d.Message)
            failwithf "Compilation failed: %s" (String.Join("; ", msgs))

let createProject (root: string) =
    Directory.CreateDirectory(root) |> ignore
    let projectPath = Path.Combine(root, "WatchLoop.fsproj")
    let sourcePath = Path.Combine(root, "Program.fs")
    let projectContents =
        """
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <LangVersion>preview</LangVersion>
    <Nullable>enable</Nullable>
  </PropertyGroup>
  <ItemGroup>
    <Compile Include="Program.fs" />
  </ItemGroup>
</Project>
"""
    File.WriteAllText(projectPath, projectContents)
    projectPath, sourcePath

let baselineSource =
    """
namespace WatchLoop

module Target =
    let GetMessage () = "Message version baseline"
"""

let updatedSource =
    """
namespace WatchLoop

module Target =
    let GetMessage () = "Message version updated"
"""

let run () =
    let tempRoot = Path.Combine(Path.GetTempPath(), "fsharp-mdv-check", Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory(tempRoot) |> ignore
    let projectPath, sourcePath = createProject tempRoot
    File.WriteAllText(sourcePath, baselineSource)

    let checker =
        FSharpChecker.Create(
            keepAssemblyContents = true,
            enableBackgroundItemKeyStoreAndSemanticClassification = false,
            captureIdentifiersWhenParsing = false)

    let commandLine =
        let args = Msbuild.getFscCommandLine projectPath (Some "Debug") (Some "net10.0")
        if args |> Array.exists (fun arg -> arg.StartsWith("--enable:hotreloaddeltas", StringComparison.OrdinalIgnoreCase)) then
            args
        else
            Array.append args [| "--enable:hotreloaddeltas" |]
    let compileArgs, compileSources = Compiler.prepareCompileInputs projectPath commandLine
    let optionsRaw = checker.GetProjectOptionsFromCommandLineArgs(projectPath, commandLine)
    let timestamp = DateTime.UtcNow
    let options =
        { optionsRaw with
            OtherOptions = compileArgs
            SourceFiles = if compileSources.Length > 0 then compileSources else optionsRaw.SourceFiles
            LoadTime = timestamp
            Stamp = Some timestamp.Ticks }

    Compiler.compile checker (Compiler.buildCompileArgs compileArgs)

    let outputPath =
        Compiler.tryGetOutputPath projectPath options
        |> Option.defaultWith (fun () -> failwith "Unable to find output path")

    match checker.StartHotReloadSession(options) |> Async.RunSynchronously with
    | Error err -> failwithf "StartHotReloadSession failed: %A" err
    | Ok () -> ()

    File.WriteAllText(sourcePath, updatedSource)
    let updatedCommandLine =
        let args = Msbuild.getFscCommandLine projectPath (Some "Debug") (Some "net10.0")
        if args |> Array.exists (fun arg -> arg.StartsWith("--enable:hotreloaddeltas", StringComparison.OrdinalIgnoreCase)) then
            args
        else
            Array.append args [| "--enable:hotreloaddeltas" |]
    let updatedArgs, updatedSources = Compiler.prepareCompileInputs projectPath updatedCommandLine
    let updatedOptionsRaw = checker.GetProjectOptionsFromCommandLineArgs(projectPath, updatedCommandLine)
    let updatedStamp = DateTime.UtcNow
    let updatedOptions =
        { updatedOptionsRaw with
            OtherOptions = updatedArgs
            SourceFiles = if updatedSources.Length > 0 then updatedSources else updatedOptionsRaw.SourceFiles
            LoadTime = updatedStamp
            Stamp = Some updatedStamp.Ticks }

    Compiler.compile checker (Compiler.buildCompileArgs updatedArgs)

    match checker.EmitHotReloadDelta(updatedOptions) |> Async.RunSynchronously with
    | Error err -> failwithf "EmitHotReloadDelta failed: %A" err
    | Ok delta ->
        let deltaDir = Path.Combine(tempRoot, "delta")
        Directory.CreateDirectory(deltaDir) |> ignore
        let metaPath = Path.Combine(deltaDir, "1.meta")
        let ilPath = Path.Combine(deltaDir, "1.il")
        File.WriteAllBytes(metaPath, delta.Metadata)
        File.WriteAllBytes(ilPath, delta.IL)
        printfn "Baseline: %s" outputPath
        printfn "Metadata delta: %s" metaPath
        printfn "IL delta: %s" ilPath
        checker.EndHotReloadSession()
        printfn "Generation metadata emitted successfully."

run ()
