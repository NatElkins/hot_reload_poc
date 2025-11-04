#nowarn "57"
#nowarn "3261"

module FscWatch.HotReloadSession

open System
open System.IO
open System.Reflection
open System.Runtime.Loader
open System.Reflection.Metadata
open System.Globalization
open System.Threading.Tasks
open System.Diagnostics
open FSharp.Compiler.CodeAnalysis
open FSharp.Compiler.Diagnostics
open FSharp.Compiler.Text
open FscWatch.MsbuildInterop

let private log message =
    let timestamp = DateTime.Now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture)
    printfn "[fsc-watch %s] %s" timestamp message

let private tryDeleteDirectory path description =
    if Directory.Exists(path) then
        try
            Directory.Delete(path, true)
            log (sprintf "Removed %s directory: %s" description path)
        with ex ->
            log (sprintf "Failed to delete %s directory %s: %s" description path ex.Message)

let private cleanProjectOutputs (projectDirectory: string) =
    let binDir = Path.Combine(projectDirectory, "bin")
    let objDir = Path.Combine(projectDirectory, "obj")
    tryDeleteDirectory binDir "bin"
    tryDeleteDirectory objDir "obj"

let private persistMdvCommand (logOutput: bool) (generation: int) (baselinePath: string) (metadataPath: string) (ilPath: string) =
    let command = sprintf "mdv \"%s\" \"/g:%s;%s\"" baselinePath metadataPath ilPath
    if logOutput then
        log (sprintf "  %s" command)
        log "  (append /il+ or /md- switches as desired)"
    try
        let directory = Path.GetDirectoryName(metadataPath)
        if not (String.IsNullOrWhiteSpace(directory)) then
            Directory.CreateDirectory(directory) |> ignore
            let commandPath = Path.Combine(directory, sprintf "mdv-gen%d.cmd" generation)
            File.WriteAllText(commandPath, command + Environment.NewLine)
            if logOutput then
                log (sprintf "  mdv command recorded at %s" commandPath)
            else
                log (sprintf "mdv command recorded at %s" commandPath)
    with ex ->
        log (sprintf "  failed to persist mdv command: %s" ex.Message)

let private logTruncated (prefix: string) (values: string[]) =
    let limit = 32
    let count = values.Length
    values |> Array.truncate limit |> Array.iter (fun value -> log (sprintf "  %s: %s" prefix value))
    if count > limit then
        log (sprintf "  ... (%d additional %s entries suppressed)" (count - limit) prefix)

[<RequireQualifiedAccess>]
type ApplyDeltaOutcome =
    | Applied of FSharpHotReloadDelta
    | NoChanges
    | CompilationFailed of FSharpDiagnostic[]
    | HotReloadError of string

[<CLIMutable>]
type WatchSettings =
    { ProjectPath: string
      Configuration: string option
      TargetFramework: string option
      ApplyRuntimeUpdate: bool
      Invocation: (string * string) option
      InvocationInterval: TimeSpan
      DeltaOutputPath: string option
      ValidateWithMdv: bool
      UseDefaultLoadContext: bool
      MdvCommandOnly: bool
      QuitAfterDelta: bool
      CleanBuild: bool }

type WatchSession
    ( checker: FSharpChecker,
      projectOptions: FSharpProjectOptions,
      compileArgsWithCapture: string[],
      compileArgsWithoutCapture: string[],
      baselineSnapshotPath: string,
      baselineDllPath: string,
      runtimeDllPath: string,
      runtimeAssembly: Assembly,
      loadContext: AssemblyLoadContext,
      projectDirectory: string,
      workingDirectory: string,
      sourceFiles: string[],
      invocationSignature: (string * string) option,
      deltaOutputPath: string option,
      validateWithMdv: bool,
      useDefaultLoadContext: bool,
      invocation: MethodInfo option,
      invocationInterval: TimeSpan,
      mdvCommandOnly: bool,
      quitAfterDelta: bool ) =

    let mutable generation = 0
    let mutable runtimeAssemblyValue = runtimeAssembly
    let mutable loadContextValue = loadContext
    let mutable invocationMethod = invocation
    let invocationSignatureValue = invocationSignature

    member _.Checker = checker
    member _.ProjectOptions = projectOptions
    member _.CompileArgsWithCapture = compileArgsWithCapture
    member _.CompileArgsWithoutCapture = compileArgsWithoutCapture
    member _.BaselineSnapshotPath = baselineSnapshotPath
    member _.BaselineDllPath = baselineDllPath
    member _.RuntimeDllPath = runtimeDllPath
    member _.RuntimeAssembly = runtimeAssemblyValue
    member _.LoadContext = loadContextValue
    member _.ProjectDirectory = projectDirectory
    member _.WorkingDirectory = workingDirectory
    member _.SourceFiles = sourceFiles
    member _.DeltaOutputPath = deltaOutputPath
    member _.ValidateWithMdv = validateWithMdv
    member _.UseDefaultLoadContext = useDefaultLoadContext
    member _.InvocationSignature = invocationSignatureValue
    member _.Invocation = invocationMethod
    member _.InvocationInterval = invocationInterval
    member _.MdvCommandOnly = mdvCommandOnly
    member _.QuitAfterDelta = quitAfterDelta

    member _.InvokeTarget () =
        match invocationMethod with
        | None -> None
        | Some methodInfo ->
            try
                let result = methodInfo.Invoke(null, [||])
                Some result
            with ex ->
                printfn "Invocation of %s.%s failed: %s" methodInfo.DeclaringType.FullName methodInfo.Name ex.Message
                None

    member private this.ResolveInvocation (assembly: Assembly) =
        match invocationSignatureValue with
        | None -> None
        | Some (typeName, methodName) ->
            let flags = BindingFlags.Public ||| BindingFlags.NonPublic ||| BindingFlags.Static
            match assembly.GetType(typeName, throwOnError = false, ignoreCase = false) with
            | null -> None
            | typ ->
                match typ.GetMethod(methodName, flags) with
                | null -> None
                | mi -> Some mi

    member this.RebindInvocationTarget () =
        match invocationSignatureValue, runtimeAssemblyValue with
        | None, _ -> ()
        | _, null -> ()
        | Some (typeName, methodName), assembly ->
            invocationMethod <- this.ResolveInvocation assembly
            match invocationMethod with
            | Some mi -> log (sprintf "Invocation target rebound to %s.%s" mi.DeclaringType.FullName mi.Name)
            | None -> log (sprintf "Invocation target %s.%s could not be rebound on current assembly." typeName methodName)

    member this.ReloadRuntimeAssembly () =
        if useDefaultLoadContext then
            log "Default load context in use; skipping runtime assembly reload."
            ()
        else
            try
                match loadContextValue with
                | null -> ()
                | ctx when ctx.IsCollectible -> ctx.Unload()
                | _ -> ()
            with _ -> ()

            let baselineTimestamp = File.GetLastWriteTimeUtc(baselineDllPath)
            let baselineSize = try FileInfo(baselineDllPath).Length with _ -> -1L
            log (sprintf "Reloading runtime assembly from baseline '%s' (timestamp=%O size=%d)." baselineDllPath baselineTimestamp baselineSize)

            File.Copy(baselineDllPath, runtimeDllPath, true)
            let runtimeTimestamp = File.GetLastWriteTimeUtc(runtimeDllPath)
            let runtimeSize = try FileInfo(runtimeDllPath).Length with _ -> -1L
            log (sprintf "Runtime assembly copy written to '%s' (timestamp=%O size=%d)." runtimeDllPath runtimeTimestamp runtimeSize)

            let newContext = new AssemblyLoadContext("fsc-watch-runtime", isCollectible = true)
            let assembly = newContext.LoadFromAssemblyPath(runtimeDllPath)
            log (sprintf "Loaded runtime assembly version %s." (assembly.GetName().Version.ToString()))
            loadContextValue <- newContext
            runtimeAssemblyValue <- assembly
            match invocationSignatureValue with
            | None -> ()
            | Some (typeName, methodName) ->
                invocationMethod <- this.ResolveInvocation assembly
                match invocationMethod with
                | Some mi -> log (sprintf "Updated invocation target resolved: %s.%s" mi.DeclaringType.FullName mi.Name)
                | None -> log (sprintf "Invocation target %s.%s could not be resolved after reload." typeName methodName)
    member _.IncrementGeneration() = generation <- generation + 1
    member _.Generation = generation

    interface IDisposable with
        member this.Dispose() =
            try checker.EndHotReloadSession() with _ -> ()
            match loadContextValue with
            | ctx when ctx.IsCollectible ->
                try ctx.Unload() with _ -> ()
            | _ -> ()
            if mdvCommandOnly then
                log (sprintf "Retaining working directory for inspection: %s" workingDirectory)
            else
                try
                    if Directory.Exists(workingDirectory) then
                        Directory.Delete(workingDirectory, true)
                with _ -> ()

let private createChecker () =
    FSharpChecker.Create(
        keepAssemblyContents = true,
        keepAllBackgroundResolutions = false,
        keepAllBackgroundSymbolUses = false,
        enableBackgroundItemKeyStoreAndSemanticClassification = false,
        enablePartialTypeChecking = false,
        captureIdentifiersWhenParsing = false)

let private ensureHotReloadOption (commandLine: string[]) =
    if commandLine |> Array.exists (fun arg -> arg.StartsWith("--enable:hotreloaddeltas", StringComparison.OrdinalIgnoreCase)) then
        commandLine
    else
        Array.append commandLine [| "--enable:hotreloaddeltas" |]

let private sanitizeOptions (options: string[]) =
    options
    |> Array.filter (fun opt ->
        not (opt.Equals("--times", StringComparison.OrdinalIgnoreCase))
        && not (opt.StartsWith("--sourcelink:", StringComparison.OrdinalIgnoreCase)))
    |> Array.map (fun opt ->
        if opt.StartsWith("-o:", StringComparison.Ordinal) then
            "--out:" + opt.Substring(3)
        elif opt.StartsWith("-o:", StringComparison.OrdinalIgnoreCase) then
            "--out:" + opt.Substring(3)
        else
            opt)

let private buildCompileArgs (otherOptions: string[]) (sourceFiles: string[]) =
    Array.concat [ [| "fsc.exe" |]; sanitizeOptions otherOptions; sourceFiles ]

let private extractOutputPath (args: string[]) =
    args
    |> Array.tryPick (fun arg ->
        if arg.StartsWith("-o:", StringComparison.OrdinalIgnoreCase) || arg.StartsWith("--out:", StringComparison.OrdinalIgnoreCase) then
            Some (arg.Substring(arg.IndexOf(':') + 1))
        else
            None)

let private copyBaselineToRuntime (baselineDllPath: string) (workingDirectory: string) =
    let runtimeDir = Path.Combine(workingDirectory, "runtime")
    Directory.CreateDirectory(runtimeDir) |> ignore
    let runtimeFileName =
        match Path.GetFileName(baselineDllPath) with
        | null | "" -> "baseline.runtime.dll"
        | value -> value
    let runtimeDllPath = Path.Combine(runtimeDir, runtimeFileName)
    File.Copy(baselineDllPath, runtimeDllPath, true)
    match Path.ChangeExtension(baselineDllPath, ".pdb") with
    | null -> ()
    | pdbPath when File.Exists(pdbPath) ->
        match Path.ChangeExtension(runtimeDllPath, ".pdb") with
        | null -> ()
        | runtimePdb -> File.Copy(pdbPath, runtimePdb, true)
    | _ -> ()
    runtimeDllPath

let private loadRuntimeAssembly (useDefault: bool) (runtimeDllPath: string) =
    if useDefault then
        let assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(runtimeDllPath)
        AssemblyLoadContext.Default, assembly
    else
        let contextName = $"fsc-watch-runtime-{Guid.NewGuid():N}"
        let context = new AssemblyLoadContext(contextName, isCollectible = false)
        let assembly = context.LoadFromAssemblyPath(runtimeDllPath)
        context, assembly

let private ensureDirectory (path: string) =
    Directory.CreateDirectory(path) |> ignore

let private resetDirectory (path: string) =
    if Directory.Exists(path) then
        for file in Directory.EnumerateFiles(path) do
            try File.SetAttributes(file, FileAttributes.Normal) with _ -> ()
            File.Delete(file)
        for dir in Directory.EnumerateDirectories(path) do
            try Directory.Delete(dir, true) with _ -> ()
    Directory.CreateDirectory(path) |> ignore

let private writeDeltaArtifacts (rootDirectory: string) (generation: int) (delta: FSharpHotReloadDelta) =
    let generationDir = Path.Combine(rootDirectory, sprintf "gen%d" generation)
    resetDirectory generationDir
    let metadataPath = Path.Combine(generationDir, sprintf "delta_gen%d.meta" generation)
    let ilPath = Path.Combine(generationDir, sprintf "delta_gen%d.il" generation)
    File.WriteAllBytes(metadataPath, delta.Metadata)
    File.WriteAllBytes(ilPath, delta.IL)
    let pdbPathOpt =
        delta.Pdb
        |> Option.map (fun pdb ->
            let pdbPath = Path.Combine(generationDir, sprintf "delta_gen%d.pdb" generation)
            File.WriteAllBytes(pdbPath, pdb)
            pdbPath)
    let metadataInfo = FileInfo(metadataPath)
    let ilInfo = FileInfo(ilPath)
    log (
        sprintf
            "Delta artifacts for generation %d written to %s (meta=%d bytes @ %O, il=%d bytes @ %O)"
            generation
            generationDir
            metadataInfo.Length
            metadataInfo.LastWriteTimeUtc
            ilInfo.Length
            ilInfo.LastWriteTimeUtc
    )
    metadataPath, ilPath, pdbPathOpt

let private runMdvAsync (baselinePath: string) (metadataPath: string) (ilPath: string) (generation: int) =
    async {
        let psi = ProcessStartInfo()
        psi.FileName <- "mdv"
        psi.ArgumentList.Add(baselinePath)
        psi.ArgumentList.Add($"/g:{metadataPath};{ilPath}")
        psi.RedirectStandardOutput <- true
        psi.RedirectStandardError <- true
        psi.UseShellExecute <- false
        psi.CreateNoWindow <- true

        try
            use proc = new Process()
            proc.StartInfo <- psi
            if proc.Start() then
                let! output = proc.StandardOutput.ReadToEndAsync() |> Async.AwaitTask
                let! errors = proc.StandardError.ReadToEndAsync() |> Async.AwaitTask
                let! _ = proc.WaitForExitAsync() |> Async.AwaitTask
                let exitCode = proc.ExitCode
                log (sprintf "mdv exited with code %d for generation %d." exitCode generation)
                if not (String.IsNullOrWhiteSpace output) then
                    output.Split([|'\r';'\n'|], StringSplitOptions.RemoveEmptyEntries)
                    |> Array.truncate 40
                    |> Array.iter (fun line -> log (sprintf "  mdv: %s" line))
                if not (String.IsNullOrWhiteSpace errors) then
                    errors.Split([|'\r';'\n'|], StringSplitOptions.RemoveEmptyEntries)
                    |> Array.iter (fun line -> log (sprintf "  mdv (stderr): %s" line))
            else
                log "Failed to start mdv process."
        with
        | :? System.ComponentModel.Win32Exception as ex ->
            log (sprintf "Unable to start mdv: %s" ex.Message)
        | ex ->
            log (sprintf "mdv execution failed: %s" ex.Message)
    }

let private compileProject
    (checker: FSharpChecker)
    (projectDirectory: string)
    (options: FSharpProjectOptions)
    (includeHotReloadCapture: bool)
    (sourceFiles: string[])
    =
    async {
        log (sprintf "Invoking compiler (includeHotReloadCapture=%b) with %d source file(s)." includeHotReloadCapture sourceFiles.Length)
        let otherOptions =
            if includeHotReloadCapture then
                options.OtherOptions
            else
                options.OtherOptions
                |> Array.filter (fun opt -> not (opt.StartsWith("--enable:hotreloaddeltas", StringComparison.OrdinalIgnoreCase)))

        let argv = buildCompileArgs otherOptions sourceFiles
        let originalDirectory = Directory.GetCurrentDirectory()

        try
            if not (String.IsNullOrWhiteSpace projectDirectory) then
                Directory.SetCurrentDirectory(projectDirectory)

            let! diagnostics, exitCodeOpt = checker.Compile(argv)
            let errors = diagnostics |> Array.filter (fun d -> d.Severity = FSharpDiagnosticSeverity.Error)

            match errors, exitCodeOpt with
            | [||], None ->
                log "Compiler returned success with no errors."
                return Ok ()
            | _ ->
                log "Compiler returned diagnostics."
                diagnostics |> Array.iter (fun d -> log (sprintf "  %s(%d,%d): %s" d.FileName d.StartLine d.StartColumn d.Message))
                return Error diagnostics
        finally
            try
                Directory.SetCurrentDirectory(originalDirectory)
            with _ -> ()
    }

let initialize (settings: WatchSettings) =
    async {
        let checker = createChecker ()
        let workingDirectory = Path.Combine(Path.GetTempPath(), "fsc-watch", Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory(workingDirectory) |> ignore

        let projectFullPath = Path.GetFullPath(settings.ProjectPath)
        let projectDirectory =
            match Path.GetDirectoryName(projectFullPath) with
            | null | "" -> Directory.GetCurrentDirectory()
            | value -> value

        if settings.CleanBuild then
            log "Clean build requested; deleting bin/ and obj/ directories before capturing command line arguments."
            cleanProjectOutputs projectDirectory

        let commandLine = getFscCommandLine projectFullPath settings.Configuration settings.TargetFramework |> ensureHotReloadOption
        log (sprintf "Captured %d compiler argument(s)." commandLine.Length)
        logTruncated "arg" commandLine
        if commandLine.Length = 0 then
            return Error "FscCommandLineArgs was empty."
        else
            let optionArgs, sourceArgs =
                commandLine
                |> Array.partition (fun arg -> arg.StartsWith("-", StringComparison.Ordinal) || arg.StartsWith("--", StringComparison.Ordinal))

            let normalizedSources =
                sourceArgs
                |> Array.choose (fun file ->
                    if String.IsNullOrWhiteSpace(file) then None
                    else
                        let fullPath = if Path.IsPathRooted(file) then file else Path.Combine(projectDirectory, file)
                        Some(Path.GetFullPath(fullPath)))

            let projectOptionsRaw = checker.GetProjectOptionsFromCommandLineArgs(projectFullPath, commandLine)
            let projectOptions =
                { projectOptionsRaw with
                    SourceFiles =
                        if normalizedSources.Length > 0 then normalizedSources else projectOptionsRaw.SourceFiles
                    OtherOptions =
                        let baseOptions = if optionArgs.Length > 0 then optionArgs else projectOptionsRaw.OtherOptions
                        sanitizeOptions baseOptions }

            log (sprintf "Resolved %d source file(s)." projectOptions.SourceFiles.Length)
            logTruncated "source" projectOptions.SourceFiles
            log (sprintf "Resolved %d compiler option(s)." projectOptions.OtherOptions.Length)
            logTruncated "option" projectOptions.OtherOptions

            let sourceFiles =
                projectOptions.SourceFiles
                |> Array.choose (fun path ->
                    if String.IsNullOrWhiteSpace(path) then None else Some(Path.GetFullPath(path)))
                |> Array.filter (fun path ->
                    let ext = Path.GetExtension(path)
                    let isFs = ext.Equals(".fs", StringComparison.OrdinalIgnoreCase) || ext.Equals(".fsi", StringComparison.OrdinalIgnoreCase)
                    let containsObj =
                        path.Split([| Path.DirectorySeparatorChar; Path.AltDirectorySeparatorChar |], StringSplitOptions.RemoveEmptyEntries)
                        |> Array.exists (fun segment -> segment.Equals("obj", StringComparison.OrdinalIgnoreCase))
                    isFs && not containsObj)

            let deltaOutputPath =
                match settings.DeltaOutputPath with
                | Some path when not (String.IsNullOrWhiteSpace(path)) ->
                    let resolved =
                        if Path.IsPathRooted(path) then path else Path.Combine(projectDirectory, path)
                    let full = Path.GetFullPath(resolved)
                    resetDirectory full
                    log (sprintf "Delta artifacts will be written to %s" full)
                    Some full
                | _ ->
                    if settings.ValidateWithMdv || settings.MdvCommandOnly then
                        let inferred = Path.Combine(workingDirectory, "deltas")
                        resetDirectory inferred
                        log (sprintf "Delta artifacts will be written to %s" inferred)
                        Some inferred
                    else
                        None

            log "Starting baseline compilation."
            let! baselineResult = compileProject checker projectDirectory projectOptions true projectOptions.SourceFiles

            match baselineResult with
            | Ok () ->
                log "Baseline compilation succeeded. Starting hot reload session."
                let! sessionResult = checker.StartHotReloadSession(projectOptions)
                match sessionResult with
                | Error error -> return Error (sprintf "Failed to start hot reload session: %A" error)
                | Ok () ->
                    match extractOutputPath commandLine with
                    | None -> return Error "Unable to determine output assembly path from command line."
                    | Some baselineDllRelPath ->
                        let baselineDllPath =
                            if Path.IsPathRooted(baselineDllRelPath) then
                                baselineDllRelPath
                            else
                                Path.Combine(projectDirectory, baselineDllRelPath)
                            |> Path.GetFullPath
                        if not (File.Exists(baselineDllPath)) then
                            return Error (sprintf "Baseline assembly '%s' was not produced." baselineDllPath)
                        else
                            log (sprintf "Baseline assembly located at %s" baselineDllPath)
                            let baselineSnapshotDir = Path.Combine(workingDirectory, "baseline")
                            ensureDirectory baselineSnapshotDir
                            let baselineSnapshotPath =
                                let fileName =
                                    match Path.GetFileName(baselineDllPath) with
                                    | null | "" -> "baseline.dll"
                                    | value -> value
                                Path.Combine(baselineSnapshotDir, fileName)
                            do File.Copy(baselineDllPath, baselineSnapshotPath, true)
                            match Path.ChangeExtension(baselineDllPath, ".pdb"), Path.ChangeExtension(baselineSnapshotPath, ".pdb") with
                            | src, dest when not (String.IsNullOrWhiteSpace(src)) && not (String.IsNullOrWhiteSpace(dest)) && File.Exists(src) ->
                                File.Copy(src, dest, true)
                            | _ -> ()
                            let runtimeDllPath =
                                if settings.UseDefaultLoadContext then baselineDllPath
                                else
                                    let path = copyBaselineToRuntime baselineDllPath workingDirectory
                                    log (sprintf "Runtime assembly copy created at %s" path)
                                    path
                            let loadContext, runtimeAssembly = loadRuntimeAssembly settings.UseDefaultLoadContext runtimeDllPath
                            let invocationMethod =
                                match settings.Invocation with
                                | None -> Ok None
                                | Some (typeName, methodName) ->
                                    try
                                        let typ = runtimeAssembly.GetType(typeName, throwOnError = true, ignoreCase = false)
                                        let flags = BindingFlags.Public ||| BindingFlags.NonPublic ||| BindingFlags.Static
                                        match typ.GetMethod(methodName, flags) with
                                        | null -> Error (sprintf "Invocation method %s.%s not found." typeName methodName)
                                        | methodInfo when methodInfo.GetParameters().Length = 0 -> Ok (Some methodInfo)
                                        | _ -> Error (sprintf "Invocation method %s.%s must be static and parameterless." typeName methodName)
                                    with ex -> Error (sprintf "Failed to resolve invocation target %s.%s: %s" typeName methodName ex.Message)

                            match invocationMethod with
                            | Error message -> return Error message
                            | Ok invocationInfo ->
                                match invocationInfo with
                                | Some m -> log (sprintf "Invocation target resolved: %s.%s" m.DeclaringType.FullName m.Name)
                                | None -> ()
                                let compileArgsWithCapture = buildCompileArgs projectOptions.OtherOptions projectOptions.SourceFiles
                                let compileArgsWithoutCapture =
                                    buildCompileArgs
                                        (projectOptions.OtherOptions |> Array.filter (fun opt -> not (opt.StartsWith("--enable:hotreloaddeltas", StringComparison.OrdinalIgnoreCase))))
                                        projectOptions.SourceFiles
                                let session =
                                    new WatchSession(
                                        checker,
                                        projectOptions,
                                        compileArgsWithCapture,
                                        compileArgsWithoutCapture,
                                        baselineSnapshotPath,
                                        baselineDllPath,
                                        runtimeDllPath,
                                        runtimeAssembly,
                                        loadContext,
                                        projectDirectory,
                                        workingDirectory,
                                        sourceFiles,
                                        settings.Invocation,
                                        deltaOutputPath,
                                        settings.ValidateWithMdv,
                                        settings.UseDefaultLoadContext,
                                        invocationInfo,
                                        settings.InvocationInterval,
                                        settings.MdvCommandOnly,
                                        settings.QuitAfterDelta)
                                log "Session initialization completed."
                                return Ok session
            | Error diagnostics ->
                let errorText =
                    diagnostics
                    |> Array.map (fun d -> $"  {d.Message} ({d.StartLine},{d.StartColumn})")
                    |> String.concat "\n"
                return Error ($"Baseline compilation failed:\n{errorText}")
    }

let applyDelta (session: WatchSession) (changedFiles: string list) (applyRuntimeUpdate: bool) (mdvCommandOnly: bool) =
    async {
        let filesToInvalidate =
            changedFiles
            |> List.map Path.GetFullPath
            |> List.filter (fun path -> session.SourceFiles |> Array.contains path)

        if filesToInvalidate.IsEmpty then
            log "No tracked F# source files detected in change set."
        else
            log (sprintf "Notifying checker about %d changed file(s)." filesToInvalidate.Length)
            do!
                filesToInvalidate
                |> List.map (fun file -> session.Checker.NotifyFileChanged(file, session.ProjectOptions))
                |> Async.Parallel
                |> Async.Ignore

        log "Recompiling project to produce delta."
        let! compileResult =
            compileProject
                session.Checker
                session.ProjectDirectory
                session.ProjectOptions
                false
                session.ProjectOptions.SourceFiles

        match compileResult with
        | Ok () ->
            log "Compilation succeeded. Requesting hot reload delta."
            let! deltaResult = session.Checker.EmitHotReloadDelta(session.ProjectOptions)
            match deltaResult with
            | Error FSharpHotReloadError.NoChanges -> return ApplyDeltaOutcome.NoChanges
            | Error (FSharpHotReloadError.CompilationFailed diags) -> return ApplyDeltaOutcome.CompilationFailed diags
            | Error (FSharpHotReloadError.UnsupportedEdit message) -> return ApplyDeltaOutcome.HotReloadError ($"Unsupported edit: {message}")
            | Error (FSharpHotReloadError.DeltaEmissionFailed message) -> return ApplyDeltaOutcome.HotReloadError ($"Delta emission failed: {message}")
            | Error FSharpHotReloadError.NoActiveSession -> return ApplyDeltaOutcome.HotReloadError "Hot reload session is no longer active."
            | Error FSharpHotReloadError.MissingOutputPath -> return ApplyDeltaOutcome.HotReloadError "Project options are missing an output path."
            | Ok delta ->
                log (sprintf "Delta emitted. Metadata %d bytes, IL %d bytes, PDB %s." delta.Metadata.Length delta.IL.Length (delta.Pdb |> Option.map (fun b -> string b.Length) |> Option.defaultValue "none"))
                let mutable runtimePatched = false
                let isSupported =
                    try MetadataUpdater.IsSupported
                    with _ -> false
                log (sprintf "MetadataUpdater support=%b" isSupported)
                if applyRuntimeUpdate then
                    try
                        if not isSupported then
                            log "MetadataUpdater reports assembly is not supported; attempting update anyway."
                        let pdbBytes = delta.Pdb |> Option.defaultValue Array.empty
                        MetadataUpdater.ApplyUpdate(session.RuntimeAssembly, delta.Metadata, delta.IL, pdbBytes)
                        log "MetadataUpdater.ApplyUpdate succeeded."
                        runtimePatched <- true
                    with ex ->
                        let message =
                            match ex.InnerException with
                            | null -> ex.Message
                            | inner -> $"{ex.Message} (inner: {inner.GetType().FullName}: {inner.Message})"
                        log (sprintf "MetadataUpdater.ApplyUpdate failed: %s" message)
                        log (sprintf "MetadataUpdater exception detail:%s%s" Environment.NewLine (ex.ToString()))
                else
                    log "MetadataUpdater.ApplyUpdate skipped (applyRuntimeUpdate=false)."

                let nextGeneration = session.Generation + 1
                let deltaRoot =
                    match session.DeltaOutputPath with
                    | Some path when not (String.IsNullOrWhiteSpace(path)) -> path
                    | _ ->
                        match Path.GetDirectoryName(session.BaselineSnapshotPath) with
                        | null | "" -> Directory.GetCurrentDirectory()
                        | value -> value
                let deltaArtifactsOpt = Some (writeDeltaArtifacts deltaRoot nextGeneration delta)

                let metadataPairOpt =
                    match deltaArtifactsOpt with
                    | Some (metadataPath, ilPath, _) -> Some (metadataPath, ilPath)
                    | None -> None

                if runtimePatched then
                    session.RebindInvocationTarget()
                elif not mdvCommandOnly then
                    session.ReloadRuntimeAssembly()
                session.IncrementGeneration()
                log (sprintf "Session advanced to generation %d." session.Generation)

                if session.ValidateWithMdv && not mdvCommandOnly then
                    match deltaArtifactsOpt with
                    | Some (metadataPath, ilPath, _) ->
                        do! runMdvAsync session.BaselineSnapshotPath metadataPath ilPath session.Generation
                    | None ->
                        log "mdv validation requested but no delta output directory configured; skipping."

                if mdvCommandOnly then
                    match metadataPairOpt with
                    | Some (metadataPath, ilPath) ->
                        log "Delta ready for manual inspection."
                        persistMdvCommand true session.Generation session.BaselineSnapshotPath metadataPath ilPath
                    | None -> log "Delta ready for manual inspection, but no metadata/IL artifacts were persisted."
                else
                    match session.InvokeTarget() with
                    | Some value -> log (sprintf "Invocation result after delta: %O" value)
                    | None -> ()

                    metadataPairOpt
                    |> Option.iter (fun (metadataPath, ilPath) -> persistMdvCommand false session.Generation session.BaselineSnapshotPath metadataPath ilPath)
                return ApplyDeltaOutcome.Applied delta
        | Error diagnostics ->
            log "Compilation produced diagnostics; aborting delta."
            diagnostics |> Array.iter (fun d -> log (sprintf "  %s(%d,%d): %s" d.FileName d.StartLine d.StartColumn d.Message))
            return ApplyDeltaOutcome.CompilationFailed diagnostics
    }
