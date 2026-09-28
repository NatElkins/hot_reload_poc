module FSharpWatch.Program

open System
open System.Diagnostics
open System.IO
open System.Reflection
open System.Text.Json
open System.Text.Json.Nodes
open System.Text.RegularExpressions
open System.Threading
open System.Runtime.InteropServices

/// The selected SDK owns MSBuild and the compiler. The tool owns only the engine.
type private SelectedSdk = { Host: string; Directory: string }

/// A shared framework requirement or an installed framework version.
type private Framework = { Name: string; Version: string }

/// Read a bounded CLI query without a pipe deadlock.
let private query (host: string) (argument: string) =
    let start = ProcessStartInfo(host, argument, RedirectStandardOutput = true, RedirectStandardError = true)
    start.Environment["DOTNET_CLI_UI_LANGUAGE"] <- "en-US"
    use child = Process.Start start
    let output = child.StandardOutput.ReadToEndAsync()
    let errors = child.StandardError.ReadToEndAsync()
    if not (child.WaitForExit 30000) then
        child.Kill(entireProcessTree = true)
        child.WaitForExit()
        failwith $"The .NET host did not finish {argument} within 30 seconds."
    if child.ExitCode <> 0 then
        failwith $"The .NET host cannot run {argument}. {errors.Result.Trim()}"
    output.Result

/// Resolve the SDK through the CLI from the current directory, as dotnet watch does.
let private findSdk () =
    let host =
        match Environment.GetEnvironmentVariable "DOTNET_HOST_PATH" with
        | null | "" -> "dotnet"
        | value -> value
    let sdk =
        (query host "--info").Split '\n'
        |> Array.tryPick (fun line ->
            if line.TrimStart().StartsWith("Base Path:", StringComparison.Ordinal) then
                Some (line[(line.IndexOf ':') + 1..].Trim())
            else None)
        |> Option.defaultWith (fun () -> failwith "Install a .NET SDK that matches the current directory's global.json.")
    if not (File.Exists(Path.Combine(sdk, "MSBuild.dll"))) then
        failwith $"The selected SDK does not contain MSBuild: {sdk}"
    let executable = if OperatingSystem.IsWindows() then "dotnet.exe" else "dotnet"
    let selectedHost = Path.GetFullPath(Path.Combine(sdk, "..", "..", executable))
    if not (File.Exists selectedHost) then
        failwith $"The selected SDK does not have its .NET host at {selectedHost}."
    { Host = selectedHost; Directory = sdk }

/// Compare framework versions, including numeric prerelease identifiers.
let private compareFrameworkVersion (left: string) (right: string) =
    let split (value: string) =
        let parts = value.Split('-', 2)
        Version(parts[0]), if parts.Length = 1 then [] else parts[1].Split('.') |> Array.toList
    let rec comparePrerelease (leftParts: string list) (rightParts: string list) =
        match leftParts, rightParts with
        | [], [] -> 0
        | [], _ -> -1
        | _, [] -> 1
        | leftPart :: leftRest, rightPart :: rightRest ->
            let result =
                match UInt64.TryParse leftPart, UInt64.TryParse rightPart with
                | (true, leftNumber), (true, rightNumber) -> compare leftNumber rightNumber
                | (true, _), _ -> -1
                | _, (true, _) -> 1
                | _ -> StringComparer.Ordinal.Compare(leftPart, rightPart)
            if result = 0 then comparePrerelease leftRest rightRest else result
    let leftVersion, leftParts = split left
    let rightVersion, rightParts = split right
    match compare leftVersion rightVersion with
    | 0 ->
        match leftParts, rightParts with
        | [], [] -> 0
        | [], _ -> 1
        | _, [] -> -1
        | _ -> comparePrerelease leftParts rightParts
    | result -> result

/// Read framework requirements without importing SDK configuration into the application.
let private frameworksFromConfig path =
    if not (File.Exists path) then []
    else
        use document = JsonDocument.Parse(File.ReadAllText path)
        let options = document.RootElement.GetProperty "runtimeOptions"
        let frameworks =
            match options.TryGetProperty "frameworks" with
            | true, value -> value.EnumerateArray() |> Seq.toList
            | _ ->
                match options.TryGetProperty "framework" with
                | true, value -> [value]
                | _ -> []
        frameworks |> List.map (fun framework ->
            { Name = framework.GetProperty("name").GetString()
              Version = framework.GetProperty("version").GetString() })

/// Select one installed runtime family that satisfies the engine and SDK requirements.
let private writeRuntimeConfig sdk engineConfig destination =
    let requirements =
        [ engineConfig
          Path.Combine(sdk.Directory, "MSBuild.runtimeconfig.json")
          Path.Combine(sdk.Directory, "FSharp", "fsc.runtimeconfig.json") ]
        |> List.collect frameworksFromConfig
        |> List.groupBy _.Name
        |> List.map (fun (_, versions) ->
            versions |> List.sortWith (fun left right -> compareFrameworkVersion right.Version left.Version) |> List.head)
    let installed =
        (query sdk.Host "--list-runtimes").Split '\n'
        |> Array.choose (fun line ->
            let parsed = Regex.Match(line, @"^(\S+)\s+(\S+)\s+\[.+\]\s*$")
            if parsed.Success then Some { Name = parsed.Groups[1].Value; Version = parsed.Groups[2].Value } else None)
    let family (framework: Framework) =
        let version = Version(framework.Version.Split('-')[0])
        version.Major, version.Minor
    let selected =
        installed
        |> Array.groupBy family
        |> Array.sortBy fst
        |> Array.tryPick (fun (_, candidates) ->
            let matches =
                requirements |> List.choose (fun required ->
                    candidates
                    |> Array.filter (fun candidate -> candidate.Name = required.Name && compareFrameworkVersion candidate.Version required.Version >= 0)
                    |> Array.sortWith (fun left right -> compareFrameworkVersion right.Version left.Version)
                    |> Array.tryHead)
            if matches.Length = requirements.Length then Some matches else None)
        |> Option.defaultWith (fun () ->
            let required = requirements |> List.map (fun value -> $"{value.Name} {value.Version}") |> String.concat ", "
            failwith $"The selected SDK host has no compatible runtime for fsharp-watch. Required: {required}. Host: {sdk.Host}")
    let config = JsonNode.Parse(File.ReadAllText engineConfig)
    let options = config["runtimeOptions"].AsObject()
    options.Remove "framework" |> ignore
    options.Remove "rollForwardOnNoCandidateFx" |> ignore
    options["rollForward"] <- JsonValue.Create "Disable"
    options["frameworks"] <-
        selected |> List.map (fun value -> {| name = value.Name; version = value.Version |}) |> JsonSerializer.SerializeToNode
    File.WriteAllText(destination, config.ToJsonString())

/// Start a fresh engine after SDK or compiler changes, and bound cancellation cleanup.
let private watch root (args: string array) =
    use shutdown = new CancellationTokenSource()
    let cancel = ConsoleCancelEventHandler(fun _ event -> event.Cancel <- true; shutdown.Cancel())
    Console.CancelKeyPress.AddHandler cancel
    use termination =
        if OperatingSystem.IsWindows() then null
        else PosixSignalRegistration.Create(PosixSignal.SIGTERM, fun signal -> signal.Cancel <- true; shutdown.Cancel())
    let temporary = Directory.CreateTempSubdirectory "fsharp-watch-"
    try
        let mutable result = 75
        let mutable immediateRestarts = 0
        while result = 75 && not shutdown.IsCancellationRequested do
            let sdk = findSdk ()
            let engine = Path.Combine(root, "payload", "watch", "dotnet-watch.dll")
            let runtimeConfig = Path.Combine(temporary.FullName, "watch.runtimeconfig.json")
            writeRuntimeConfig sdk (Path.ChangeExtension(engine, "runtimeconfig.json")) runtimeConfig
            let start = ProcessStartInfo(sdk.Host, UseShellExecute = false)
            for argument in [| "exec"; "--runtimeconfig"; runtimeConfig; engine |] do start.ArgumentList.Add argument
            for argument in args do start.ArgumentList.Add argument
            start.Environment["FSHARP_WATCH_SDK_DIRECTORY"] <- sdk.Directory
            start.Environment["DOTNET_HOST_PATH"] <- sdk.Host
            start.Environment["FSHARP_WATCH_ENABLE_HOT_RELOAD"] <-
                if Array.contains "--no-hot-reload" args then "false" else "true"
            // Every engine must earn fresh per-project grants before the target can add compiler flags.
            start.Environment.Remove "FSHARP_WATCH_CAPABILITIES_FILE" |> ignore
            start.Environment["FSHARP_WATCH_ORIGINAL_COMMON_TARGETS"] <-
                Environment.GetEnvironmentVariable "CustomAfterMicrosoftCommonTargets" |> Option.ofObj |> Option.defaultValue ""
            start.Environment["CustomAfterMicrosoftCommonTargets"] <- Path.Combine(root, "FSharpWatch.targets")
            if not shutdown.IsCancellationRequested then
                let elapsed = Stopwatch.StartNew()
                use child = Process.Start start
                try
                    while not (child.WaitForExit 100) && not shutdown.IsCancellationRequested do ()
                    // Terminal signals reach the engine too. Parent-only cancellation still has a bounded cleanup path.
                    if shutdown.IsCancellationRequested && not (child.WaitForExit 5000) then
                        child.Kill(entireProcessTree = true)
                        child.WaitForExit()
                    result <- if shutdown.IsCancellationRequested then 130 else child.ExitCode
                    immediateRestarts <-
                        if result = 75 && elapsed.Elapsed < TimeSpan.FromSeconds 2.0 then immediateRestarts + 1 else 0
                finally
                    if not child.HasExited then
                        child.Kill(entireProcessTree = true)
                        child.WaitForExit()
            if immediateRestarts = 3 then
                failwith "The watch engine repeatedly requested a restart before startup. Check the selected SDK and compiler."
            if result = 75 && not shutdown.IsCancellationRequested then
                eprintfn "fsharp-watch: SDK or compiler changed. Starting a fresh watch session."
        if shutdown.IsCancellationRequested then 130 else result
    finally
        Console.CancelKeyPress.RemoveHandler cancel
        temporary.Delete(recursive = true)

/// Report tool provenance or start the engine with the selected SDK's compiler.
let private run (args: string array) =
    let root = AppContext.BaseDirectory
    if args = [| "--version" |] then
        let version = Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>().InformationalVersion
        printfn "fsharp-watch %s" (version.Split('+')[0])
        0
    elif args = [| "--tool-info" |] then
        let manifest = Path.Combine(root, "payload", "provenance.json")
        use document = JsonDocument.Parse(File.ReadAllText manifest)
        printfn "Watch engine source: %s" (document.RootElement.GetProperty("sdkCommit").GetString())
        printfn "Compiler ownership: %s" (document.RootElement.GetProperty("compilerOwnership").GetString())
        printfn "Payload hashes and build arguments: %s" manifest
        0
    else
        watch root args

[<EntryPoint>]
let main args =
    try run args
    with error ->
        eprintfn "fsharp-watch: %s" error.Message
        1
