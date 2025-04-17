// For more information see https://aka.ms/fsharp-console-apps
namespace TestApp

open System
open System.IO
open System.Reflection
open System.Reflection.Metadata
open System.Reflection.PortableExecutable
open System.Collections.Immutable
open System.Runtime.Loader
open HotReloadAgent

module Program =
    // Setting DOTNET_MODIFIABLE_ASSEMBLIES=debug is essential for hot reloading to work
    [<EntryPoint>]
    let main argv =
        // Check if DOTNET_MODIFIABLE_ASSEMBLIES is set correctly
        let modifiableAssemblies = Environment.GetEnvironmentVariable("DOTNET_MODIFIABLE_ASSEMBLIES")
        if modifiableAssemblies <> "debug" then
            printfn "⚠️ WARNING: DOTNET_MODIFIABLE_ASSEMBLIES is not set to 'debug'."
            printfn "Hot reload will not work correctly."
            printfn ""
            printfn "To run with hot reload support on macOS/Linux:"
            printfn "  DOTNET_MODIFIABLE_ASSEMBLIES=debug dotnet run --project src/TestApp/TestApp.fsproj"
            printfn ""
            printfn "To run with hot reload support on Windows (PowerShell):"
            printfn "  $env:DOTNET_MODIFIABLE_ASSEMBLIES=\"debug\"; dotnet run --project src/TestApp/TestApp.fsproj"
            printfn ""
            printfn "Continuing without hot reload capability..."
            printfn ""
            
        // Run the hot reload test
        HotReloadTest.runTest()
        |> Async.RunSynchronously
        0
