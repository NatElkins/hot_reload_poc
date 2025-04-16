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
    // Can be invoked like DOTNET_MODIFIABLE_ASSEMBLIES=debug dotnet run -- test
    // Setting DOTNET_MODIFIABLE_ASSEMBLIES=debug is essential for hot reloading to work
    // Debug must be set to true and optimize must be set to false as well
    [<EntryPoint>]
    let main argv =
        match argv with
        | [| "verify" |] ->
            DeltaVerification.verifyDeltas()
            0
        | [| "test" |] ->
            HotReloadTest.runTest()
            |> Async.RunSynchronously
            0
        | [| "run" |] ->
            // Create a custom AssemblyLoadContext that allows updates
            let alc = new AssemblyLoadContext("HotReloadContext", true)
            
            // Load the assembly into our custom context
            let assembly = alc.LoadFromAssemblyPath(Assembly.GetExecutingAssembly().Location)
            
            // Create the hot reload agent
            let agent = HotReloadAgent.create assembly 42 "TestApp.SimpleTest" "getValue"
            
            printfn "Hot Reload Test Application"
            printfn "=========================="
            printfn "Current value: %d" (SimpleTest.getValue())
            printfn ""
            printfn "To test hot reload:"
            printfn "1. Edit SimpleTest.fs and change the return value from 42 to 43"
            printfn "2. Save the file"
            printfn "3. The new value should be automatically reflected"
            printfn ""
            printfn "Press Ctrl+C to exit"
            printfn "=========================="
            
            // Keep the application running and monitoring for changes
            while true do
                System.Threading.Thread.Sleep(1000)
                printfn "Current value: %d" (SimpleTest.getValue())
            
            // Clean up (this won't be reached due to the infinite loop, but it's good practice)
            HotReloadAgent.dispose agent
            0
        | _ ->
            printfn "Usage: TestApp.exe [verify | test | run]"
            1
