// For more information see https://aka.ms/fsharp-console-apps
module TestApp.Program

open System
open System.IO
open System.Reflection
open System.Runtime.Loader
open HotReloadAgent

[<EntryPoint>]
let main _ =
    // Get the current assembly through the default load context
    let assembly = Assembly.GetExecutingAssembly()
    
    // Create the hot reload agent
    let agent = HotReloadAgent.create assembly __SOURCE_DIRECTORY__ "*.fs"
    
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
