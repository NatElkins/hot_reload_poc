type HotReloadMetadataHandler =
    static member ClearCache(_assemblies: System.Type[]) = ()
    static member UpdateApplication(_assemblies: System.Type[]) = ()

[<assembly: System.Reflection.Metadata.MetadataUpdateHandler(typeof<HotReloadMetadataHandler>)>]
do ()

open System
open System.Collections.Concurrent
open System.Globalization
open System.IO
open System.Threading
open System.Threading.Channels
open System.Threading.Tasks
open System.Reflection.Metadata
open FscWatch.HotReloadSession

let private log message =
    let timestamp = DateTime.Now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture)
    printfn "[fsc-watch %s] %s" timestamp message

let printUsage () =
    printfn "Usage: fsc-watch <path-to-project.fsproj> [--configuration <CONFIG>] [--framework <TFM>] [--no-runtime-apply]"
    printfn "       [--invoke Namespace.Type.Method] [--interval <seconds>] [--dump-deltas <DIR>] [--validate-with-mdv]"
    printfn "       [--mdv-command-only]   (emit deltas, print mdv command, skip runtime apply/invocation)"
    printfn "       [--quit-after-delta]   (stop after first successful delta emission)"

let parseArgs (argv: string[]) =
    if argv.Length = 0 then None
    else
        let projectPath = argv[0]
        let mutable configuration = None
        let mutable framework = None
        let mutable applyRuntime = true
        let mutable invokeTarget = None
        let mutable invokeInterval = TimeSpan.FromSeconds(2.0)
        let mutable deltaOutput = None
        let mutable validateWithMdv = false
        let mutable useDefaultLoad = false
        let mutable mdvCommandOnly = false
        let mutable quitAfterDelta = false
        let mutable index = 1

        let inline readValue () =
            if index >= argv.Length then
                failwith "Expected value after option."
            else
                let value = argv[index]
                index <- index + 1
                value

        try
            while index < argv.Length do
                match argv[index].ToLowerInvariant() with
                | "--configuration" | "-c" ->
                    index <- index + 1
                    configuration <- Some(readValue())
                | "--framework" | "-f" ->
                    index <- index + 1
                    framework <- Some(readValue())
                | "--no-runtime-apply" ->
                    index <- index + 1
                    applyRuntime <- false
                | "--invoke" ->
                    index <- index + 1
                    let value = readValue()
                    match value.Split([|'.'|], StringSplitOptions.RemoveEmptyEntries) with
                    | [| |] -> failwith "--invoke requires a value in the form Namespace.Type.Method"
                    | parts when parts.Length >= 2 ->
                        let methodName = parts.[parts.Length - 1]
                        let typeName = String.Join('.', parts, 0, parts.Length - 1)
                        invokeTarget <- Some(typeName, methodName)
                    | _ -> failwith "--invoke requires a value in the form Namespace.Type.Method"
                | "--interval" ->
                    index <- index + 1
                    let value = readValue()
                    match Double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture) with
                    | true, seconds when seconds > 0.0 -> invokeInterval <- TimeSpan.FromSeconds(seconds)
                    | _ -> failwith "--interval expects a positive numeric value representing seconds"
                | "--dump-deltas" ->
                    index <- index + 1
                    deltaOutput <- Some(readValue())
                | "--validate-with-mdv"
                | "--mdv" ->
                    index <- index + 1
                    validateWithMdv <- true
                | "--default-load" ->
                    index <- index + 1
                    useDefaultLoad <- true
                | "--mdv-command-only" ->
                    index <- index + 1
                    mdvCommandOnly <- true
                | "--quit-after-delta" ->
                    index <- index + 1
                    quitAfterDelta <- true
                | unknown ->
                    failwithf "Unknown option '%s'." unknown
            Some(projectPath, configuration, framework, applyRuntime, invokeTarget, invokeInterval, deltaOutput, validateWithMdv, useDefaultLoad, mdvCommandOnly, quitAfterDelta)
        with ex ->
            printfn "%s" ex.Message
            None

let ensureEnvironment () =
    let mutableAssemblies = Environment.GetEnvironmentVariable("DOTNET_MODIFIABLE_ASSEMBLIES")
    if not (String.Equals(mutableAssemblies, "debug", StringComparison.OrdinalIgnoreCase)) then
        log "DOTNET_MODIFIABLE_ASSEMBLIES is not 'debug'. Set it before launching to enable hot reload."

    if String.IsNullOrEmpty(Environment.GetEnvironmentVariable("FSHARP_HOTRELOAD_ENABLE_RUNTIME_APPLY")) then
        log "FSHARP_HOTRELOAD_ENABLE_RUNTIME_APPLY is not set. Set it to '1' before launching for runtime apply."

    if not MetadataUpdater.IsSupported then
        log "MetadataUpdater reports hot reload is not supported in this process. Please ensure the environment variables are set before launching."
        failwith "Hot reload not supported in current runtime session."

let printDiagnostics (diagnostics: FSharp.Compiler.Diagnostics.FSharpDiagnostic[]) =
    if diagnostics.Length > 0 then
        log (sprintf "Compilation failed with %d diagnostic(s):" diagnostics.Length)
        for diagnostic in diagnostics do
            let range = diagnostic.Range
            log (sprintf "  %s(%d,%d): %A %s" diagnostic.FileName range.StartLine range.StartColumn diagnostic.Severity diagnostic.Message)

let startWatchLoop (session: WatchSession) (applyRuntimeUpdate: bool) (mdvCommandOnly: bool) (quitAfterDelta: bool) (cts: CancellationTokenSource) =
    let cancellationToken = cts.Token
    let channelOptions = UnboundedChannelOptions(SingleReader = true, AllowSynchronousContinuations = false)
    let channel = Channel.CreateUnbounded<string>(channelOptions)
    let writer = channel.Writer
    let reader = channel.Reader
    let pending = ConcurrentDictionary<string, DateTime>()

    let enqueue (path: string) =
        if not cancellationToken.IsCancellationRequested then
            let fullPath = Path.GetFullPath(path)
            pending[fullPath] <- DateTime.UtcNow
            writer.TryWrite(fullPath) |> ignore

    let watchers =
        session.SourceFiles
        |> Array.choose (fun path ->
            match Path.GetDirectoryName(path) with
            | null | "" -> None
            | value -> Some value)
        |> Array.distinct
        |> Array.map (fun dir ->
            log (sprintf "Watching directory %s" dir)
            let watcher = new FileSystemWatcher(dir, "*.fs*")
            watcher.NotifyFilter <- NotifyFilters.LastWrite ||| NotifyFilters.FileName ||| NotifyFilters.Size
            watcher.IncludeSubdirectories <- false
            watcher.Changed.Add(fun args -> enqueue args.FullPath)
            watcher.Created.Add(fun args -> enqueue args.FullPath)
            watcher.Renamed.Add(fun args -> enqueue args.FullPath)
            watcher.EnableRaisingEvents <- true
            watcher)

    use cancellationRegistration =
        cancellationToken.Register(fun () ->
            log "Cancellation requested; stopping watcher loop."
            writer.TryComplete() |> ignore)

    use _cleanup =
        { new IDisposable with
            member _.Dispose() =
                writer.TryComplete() |> ignore
                for watcher in watchers do
                    watcher.Dispose() }

    let exitHint =
        if quitAfterDelta then
            "Watcher will exit after first successful delta."
        else
            "Press Ctrl+C to exit."
    log (sprintf "Watching %d source file(s). %s" session.SourceFiles.Length exitHint)

    let invokeLoop () =
        task {
            try
                let mutable keepRunning = true
                while keepRunning && not cancellationToken.IsCancellationRequested do
                    match session.InvokeTarget() with
                    | Some value -> log (sprintf "Invocation result: %O" value)
                    | None -> log "Invocation target returned no value."
                    try
                        do! Task.Delay(session.InvocationInterval, cancellationToken)
                    with
                    | :? OperationCanceledException ->
                        keepRunning <- false
                    | ex ->
                        log (sprintf "Invocation loop caught %s" ex.Message)
                        keepRunning <- false
            finally
                log "Invocation loop completed."
        }

    let invocationTask =
        if mdvCommandOnly then
            null
        else
            match session.Invocation with
            | None -> null
            | Some _ -> Task.Run(Func<Task>(fun () -> invokeLoop() :> Task))

    if mdvCommandOnly then
        log "mdv command-only mode active. Runtime apply and invocation loop are disabled."
    else
        match session.InvokeTarget() with
        | Some value -> log (sprintf "Initial invocation result: %O" value)
        | None -> log "Invocation target not configured."

    let rec loopAsync () =
        async {
            try
                let! hasItems = reader.WaitToReadAsync(cancellationToken).AsTask() |> Async.AwaitTask
                if not hasItems then
                    return ()
            with :? OperationCanceledException ->
                return ()

            if cancellationToken.IsCancellationRequested then
                return ()

            // Drain the channel to coalesce any burst of notifications.
            let mutable drainedItem = Unchecked.defaultof<string>
            while reader.TryRead(&drainedItem) do
                () // pending dictionary already tracks latest timestamp

            let files =
                pending.Keys
                |> Seq.choose (fun path ->
                    match pending.TryRemove(path) with
                    | true, _ -> Some path
                    | _ -> None)
                |> Seq.filter File.Exists
                |> Seq.toList

            if files.IsEmpty then
                return! loopAsync ()
            else
                log "Detected changes in the following files:"
                files |> List.iter (fun f -> log (sprintf "  %s" f))
                let! outcome = applyDelta session files applyRuntimeUpdate mdvCommandOnly
                let shouldQuit =
                    match outcome with
                    | ApplyDeltaOutcome.Applied delta ->
                        if mdvCommandOnly then
                            log (sprintf "Delta emitted for generation %d (runtime apply skipped)." session.Generation)
                        else
                            log (sprintf "Hot reload applied. Generation %d" session.Generation)
                            if delta.UpdatedMethods.Length > 0 then
                                log (sprintf "  Updated methods: %A" delta.UpdatedMethods)
                            if delta.UpdatedTypes.Length > 0 then
                                log (sprintf "  Updated types: %A" delta.UpdatedTypes)
                        if quitAfterDelta then
                            log "Quit-after-delta enabled; cancelling watcher loop."
                            cts.Cancel()
                            true
                        else
                            false
                    | ApplyDeltaOutcome.NoChanges ->
                        log "No changes detected after recompilation."
                        false
                    | ApplyDeltaOutcome.CompilationFailed diagnostics ->
                        printDiagnostics diagnostics
                        false
                    | ApplyDeltaOutcome.HotReloadError message ->
                        log (sprintf "Hot reload failed: %s" message)
                        false
                if shouldQuit then
                    return ()
                else
                    return! loopAsync ()
        }

    try
        loopAsync() |> Async.RunSynchronously
    finally
        writer.TryComplete() |> ignore
        log "Watcher loop exited."
        if invocationTask <> null then
            try
                invocationTask.Wait()
            with
            | :? AggregateException as ex when ex.InnerExceptions |> Seq.forall (fun e -> e :? OperationCanceledException) -> ()
            | :? OperationCanceledException -> ()

[<EntryPoint>]
let main argv =
    match parseArgs argv with
    | None ->
        printUsage()
        1
    | Some (projectPath, configuration, framework, applyRuntimeUpdate, invokeTarget, invokeInterval, deltaOutput, validateWithMdv, useDefaultLoad, mdvCommandOnly, quitAfterDelta) ->
        try
            if not mdvCommandOnly then
                ensureEnvironment()
            else
                log "Skipping runtime environment checks (mdv-command-only mode)."
            log "Initializing fsc-watch..."
            let settings =
                { WatchSettings.ProjectPath = projectPath
                  Configuration = configuration
                  TargetFramework = framework
                  ApplyRuntimeUpdate = (if mdvCommandOnly then false else applyRuntimeUpdate)
                  Invocation = (if mdvCommandOnly then None else invokeTarget)
                  InvocationInterval = invokeInterval
                  DeltaOutputPath = deltaOutput
                  ValidateWithMdv = (validateWithMdv || mdvCommandOnly)
                  UseDefaultLoadContext = useDefaultLoad
                  MdvCommandOnly = mdvCommandOnly
                  QuitAfterDelta = quitAfterDelta || mdvCommandOnly }

            log (sprintf "Project: %s" projectPath)
            configuration |> Option.iter (fun c -> log (sprintf "Configuration override: %s" c))
            framework |> Option.iter (fun f -> log (sprintf "Target framework override: %s" f))
            log (sprintf "Apply runtime update: %b" (if mdvCommandOnly then false else applyRuntimeUpdate))
            if not mdvCommandOnly then
                invokeTarget |> Option.iter (fun (t, m) -> log (sprintf "Invocation target: %s.%s" t m))
                log (sprintf "Invocation interval: %.2fs" invokeInterval.TotalSeconds)
            deltaOutput |> Option.iter (fun path -> log (sprintf "Delta output directory requested: %s" path))
            if validateWithMdv then log "mdv validation enabled."
            if mdvCommandOnly then log "mdv command-only mode enabled; metadata commands will be printed after each edit."
            if settings.QuitAfterDelta then log "Watcher will stop after the first successful delta emission."

            match initialize settings |> Async.RunSynchronously with
            | Error message ->
                log message
                1
            | Ok (session: WatchSession) ->
                use sessionDisp = session :> IDisposable
                log (sprintf "Baseline snapshot: %s" session.BaselineSnapshotPath)
                log (sprintf "Active output: %s" session.BaselineDllPath)
                log (sprintf "Runtime copy: %s" session.RuntimeDllPath)
                let cts = new CancellationTokenSource()
                let mutable cancellationInitiated = false
                Console.CancelKeyPress.Add(fun args ->
                    if cancellationInitiated then
                        log "Termination requested via Ctrl+C again; allowing immediate termination."
                        args.Cancel <- false
                    else
                        cancellationInitiated <- true
                        log "Termination requested via Ctrl+C."
                        args.Cancel <- true
                        cts.Cancel())
                startWatchLoop session (if mdvCommandOnly then false else applyRuntimeUpdate) mdvCommandOnly settings.QuitAfterDelta cts
                log "fsc-watch shutdown complete."
                0
        with ex ->
            log (sprintf "Unhandled exception: %s" (ex.ToString()))
            1
