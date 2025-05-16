#r "nuget: FSharp.Data"

open System
open System.IO
open System.IO.Compression
open System.Diagnostics
open FSharp.Data
open FSharp.Data.JsonExtensions

let buildDefId = 174
let apiVersion = "7.1"

printfn "[INFO] Starting setup script for mdv tool installation."
printfn "[INFO] buildDefId: %d, apiVersion: %s" buildDefId apiVersion

// 1. Get the latest successful build
let buildsUrl = sprintf "https://dev.azure.com/dnceng-public/public/_apis/build/builds?definitions=%d&statusFilter=completed&resultFilter=succeeded&$top=1&api-version=%s" buildDefId apiVersion
printfn "[INFO] Fetching latest build from: %s" buildsUrl
let buildsJson = Http.RequestString(buildsUrl)
printfn "[DEBUG] Raw buildsJson: %s" buildsJson
let buildId =
    let json = JsonValue.Parse(buildsJson)
    let asdsf = json?aaaj
    printfn "[DEBUG] Parsed buildsJson: %A" json
    match json with
    | JsonValue.Record props ->
        match props |> Array.tryFind (fun (k, _) -> k = "value") with
        | Some (_, JsonValue.Array arr) when arr.Length > 0 ->
            match arr.[0] with
            | JsonValue.Record buildProps ->
                match buildProps |> Array.tryFind (fun (k, _) -> k = "id") with
                | Some (_, JsonValue.Number id) ->
                    printfn "[DEBUG] Found build id (number): %A" id
                    id.ToString()
                | Some (_, v) ->
                    printfn "[ERROR] Unexpected build id value: %A" v
                    failwith "[ERROR] Could not find build id in JSON."
                | _ -> failwith "[ERROR] Could not find build id in JSON."
            | _ -> failwith "[ERROR] Unexpected JSON structure for build."
        | _ -> failwith "[ERROR] No builds found in JSON."
    | _ -> failwith "[ERROR] Unexpected JSON structure."
printfn "[INFO] Latest buildId: %s" buildId

// 2. Download the PackageArtifacts zip
let artifactUrl = sprintf "https://dev.azure.com/dnceng-public/public/_apis/build/builds/%s/artifacts?artifactName=PackageArtifacts&api-version=%s&%%24format=zip" buildId apiVersion
let zipPath = "PackageArtifacts.zip"
printfn "[INFO] Downloading PackageArtifacts from: %s" artifactUrl
let resp = Http.Request(artifactUrl, httpMethod="GET", silentHttpErrors=true)
printfn "[DEBUG] resp.StatusCode: %d" resp.StatusCode
printfn "[DEBUG] resp.Body type: %A" (resp.Body.GetType())
let bytes : byte array =
    match resp.Body with
    | HttpResponseBody.Binary data ->
        printfn "[DEBUG] Downloaded %d bytes" data.Length
        data
    | _ ->
        printfn "[ERROR] Expected binary response body, got: %A" resp.Body
        failwith "[ERROR] Expected binary response body."
File.WriteAllBytes(zipPath, bytes)
printfn "[INFO] Downloaded artifacts to %s (%d bytes)" zipPath bytes.Length

// 3. Extract the nupkg and install mdv
try
    let extractDir = "PackageArtifacts"
    if Directory.Exists(extractDir) then (
        printfn "[INFO] Deleting existing directory: %s" extractDir
        Directory.Delete(extractDir, true)
    )
    printfn "[INFO] Extracting %s to %s" zipPath extractDir
    ZipFile.ExtractToDirectory(zipPath, extractDir)

    let nupkgs = Directory.GetFiles(extractDir, "mdv.*.nupkg", SearchOption.AllDirectories)
    printfn "[INFO] Found nupkg files: %A" nupkgs
    let nupkg = nupkgs |> Array.tryHead

    match nupkg with
    | Some pkg ->
        printfn "[INFO] Using nupkg: %s" pkg
        let version =
            let name = Path.GetFileNameWithoutExtension(pkg)
            printfn "[DEBUG] nupkg file name: %s" name
            if name.StartsWith("mdv.") then
                let v = name.Substring(4)
                printfn "[DEBUG] Parsed version: %s" v
                v
            else (
                printfn "[WARN] nupkg file name did not start with 'mdv.': %s" name
                "2.0.0-ci" // fallback
            )
        printfn "[INFO] Parsed version: %s" version
        let installCmd = sprintf "dotnet tool install --global --add-source %s mdv --version %s" (Path.GetDirectoryName(pkg)) version
        printfn "[INFO] Running: %s" installCmd
        let p = Process.Start(ProcessStartInfo(FileName = "bash", Arguments = $"-c \"{installCmd}\"", UseShellExecute = false))
        p.WaitForExit() |> ignore
        printfn "[INFO] dotnet tool install exited with code %d" p.ExitCode
    | None ->
        printfn "[ERROR] No mdv nupkg found in artifacts."
with ex ->
    printfn "[ERROR] Exception during extraction or install: %s" ex.Message
    printfn "[ERROR] StackTrace: %s" ex.StackTrace 