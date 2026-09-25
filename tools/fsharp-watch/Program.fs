module FSharpWatch.Program

open System
open System.Diagnostics
open System.IO
open System.Reflection
open System.Text.Json

/// Resolve the SDK through the CLI from the current directory, as dotnet watch does.
let private findSdk () =
    let host =
        match Environment.GetEnvironmentVariable "DOTNET_HOST_PATH" with
        | null | "" -> "dotnet"
        | value -> value
    let start = ProcessStartInfo(host, "--info", RedirectStandardOutput = true, RedirectStandardError = true)
    start.Environment["DOTNET_CLI_UI_LANGUAGE"] <- "en-US"
    use child = Process.Start start
    let output = child.StandardOutput.ReadToEndAsync()
    let errors = child.StandardError.ReadToEndAsync()
    child.WaitForExit()
    if child.ExitCode <> 0 then
        failwith $"The .NET SDK cannot start. {errors.Result.Trim()}"
    let sdk =
        output.Result.Split '\n'
        |> Array.tryPick (fun line ->
            if line.TrimStart().StartsWith("Base Path:", StringComparison.Ordinal) then
                Some (line[(line.IndexOf ':') + 1..].Trim())
            else None)
        |> Option.defaultWith (fun () -> failwith "Install a .NET 10 SDK that matches the project's global.json.")
    if not (File.Exists(Path.Combine(sdk, "MSBuild.dll"))) then
        failwith $"The selected SDK does not contain MSBuild: {sdk}"
    let version = Version(DirectoryInfo(sdk).Name.Split('-')[0])
    if version < Version(10, 0, 400) then
        failwith "fsharp-watch requires .NET SDK 10.0.400 or later. Update the project's global.json."
    let executable = if OperatingSystem.IsWindows() then "dotnet.exe" else "dotnet"
    Path.GetFullPath(Path.Combine(sdk, "..", "..", executable)), sdk

/// Start the bundled engine with a matched compiler and the installed SDK's MSBuild.
let private run (args: string array) =
    let root = AppContext.BaseDirectory
    if args = [| "--version" |] then
        let version = Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>().InformationalVersion
        printfn "fsharp-watch %s" (version.Split('+')[0])
        0
    elif args = [| "--tool-info" |] then
        let manifest = Path.Combine(root, "payload", "provenance.json")
        use document = JsonDocument.Parse(File.ReadAllText manifest)
        printfn "SDK source: %s" (document.RootElement.GetProperty("sdkCommit").GetString())
        printfn "F# source: %s" (document.RootElement.GetProperty("fsharpCommit").GetString())
        printfn "Payload hashes and build arguments: %s" manifest
        0
    else
        let host, sdk = findSdk ()
        let start = ProcessStartInfo(host, UseShellExecute = false)
        start.ArgumentList.Add(Path.Combine(root, "payload", "watch", "dotnet-watch.dll"))
        for arg in args do start.ArgumentList.Add arg
        start.Environment["FSHARP_WATCH_SDK_DIRECTORY"] <- sdk
        start.Environment["DOTNET_HOST_PATH"] <- host
        // These properties affect only the child process. Project files and the installed SDK stay intact.
        start.Environment["DotnetFscCompilerPath"] <- Path.Combine(root, "payload", "FSharp", "fsc.dll")
        start.Environment["DOTNET_WATCH_FSHARP_COMPILER_SERVICE_PATH"] <- Path.Combine(root, "payload", "FSharp", "FSharp.Compiler.Service.dll")
        start.Environment["FSHARP_WATCH_ENABLE_HOT_RELOAD"] <-
            if Array.contains "--no-hot-reload" args then "false" else "true"
        start.Environment["FSHARP_WATCH_ORIGINAL_COMMON_TARGETS"] <-
            Environment.GetEnvironmentVariable "CustomAfterMicrosoftCommonTargets" |> Option.ofObj |> Option.defaultValue ""
        start.Environment["CustomAfterMicrosoftCommonTargets"] <- Path.Combine(root, "FSharpWatch.targets")
        use child = Process.Start start
        // The terminal sends Ctrl+C to both processes. Let watch finish its child cleanup before this launcher exits.
        Console.CancelKeyPress.Add(fun event -> event.Cancel <- true)
        try
            child.WaitForExit()
            child.ExitCode
        finally
            if not child.HasExited then child.Kill(entireProcessTree = true)

[<EntryPoint>]
let main args =
    try run args
    with error ->
        eprintfn "fsharp-watch: %s" error.Message
        1
