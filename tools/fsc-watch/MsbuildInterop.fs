module FscWatch.MsbuildInterop

open System
open System.Diagnostics
open System.IO

type TemporaryDirectory() =
    let path = Path.Combine(Path.GetTempPath(), "fsc-watch", Guid.NewGuid().ToString("N"))
    do Directory.CreateDirectory(path) |> ignore
    member _.Path = path
    interface IDisposable with
        member _.Dispose() =
            try
                if Directory.Exists(path) then
                    Directory.Delete(path, true)
            with _ -> ()

let private writeCaptureTargets (directory: string) =
    let targetsPath = Path.Combine(directory, "FscWatchCapture.targets")
    let content =
        """
<Project xmlns="http://schemas.microsoft.com/developer/msbuild/2003">
  <Target Name="FscWatchCaptureArgs" AfterTargets="CoreCompile">
    <WriteLinesToFile File="$(FscWatchCommandLineLog)" Lines="@(FscCommandLineArgs)" Overwrite="true" />
  </Target>
</Project>
"""
    File.WriteAllText(targetsPath, content)
    targetsPath

let private runProcess (workingDirectory: string) (exe: string) (args: string list) =
    let psi = ProcessStartInfo()
    psi.FileName <- exe
    args |> List.iter psi.ArgumentList.Add
    psi.WorkingDirectory <- workingDirectory
    psi.RedirectStandardOutput <- true
    psi.RedirectStandardError <- true
    psi.UseShellExecute <- false

    use proc = new Process()
    proc.StartInfo <- psi

    if not (proc.Start()) then
        failwithf "Failed to start process '%s'." exe

    let stdout = proc.StandardOutput.ReadToEndAsync()
    let stderr = proc.StandardError.ReadToEndAsync()
    proc.WaitForExit()
    stdout.Wait()
    stderr.Wait()
    proc.ExitCode, stdout.Result, stderr.Result

let getFscCommandLine (projectPath: string) (configuration: string option) (targetFramework: string option) =
    let projectFullPath = Path.GetFullPath(projectPath)
    if not (File.Exists(projectFullPath)) then
        invalidArg "projectPath" $"Project file '{projectFullPath}' was not found."

    use tempDir = new TemporaryDirectory()
    let captureTargets = writeCaptureTargets tempDir.Path
    let argsFile = Path.Combine(tempDir.Path, "fsc-watch.args")

    let baseArgs =
        [   "msbuild"
            "/restore"
            projectFullPath
            "/t:Build"
            "/p:ProvideCommandLineArgs=true"
            $"/p:FscWatchCommandLineLog=\"{argsFile}\""
            $"/p:CustomAfterMicrosoftCommonTargets=\"{captureTargets}\""
            "/nologo"
            "/v:quiet" ]

    let withConfiguration =
        match configuration with
        | Some value -> baseArgs @ [ $"/p:Configuration={value}" ]
        | None -> baseArgs

    let fullArgs =
        match targetFramework with
        | Some value -> withConfiguration @ [ $"/p:TargetFramework={value}" ]
        | None -> withConfiguration

    let projectDirectory =
        match Path.GetDirectoryName(projectFullPath) with
        | null | "" -> Directory.GetCurrentDirectory()
        | value -> value

    let exitCode, stdout, stderr = runProcess projectDirectory "dotnet" fullArgs

    if exitCode <> 0 then
        failwithf "dotnet msbuild exited with code %d.\nSTDOUT:\n%s\nSTDERR:\n%s" exitCode stdout stderr

    if not (File.Exists(argsFile)) then
        failwith "Failed to capture F# compiler command-line arguments."

    File.ReadAllLines(argsFile)
    |> Array.collect (fun line ->
        line.Split([| ';' |], StringSplitOptions.RemoveEmptyEntries)
        |> Array.map (fun arg -> arg.Trim()))
    |> Array.filter (fun arg -> not (String.IsNullOrWhiteSpace(arg)))
