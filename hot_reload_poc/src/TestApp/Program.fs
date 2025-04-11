// For more information see https://aka.ms/fsharp-console-apps
open System
open HotReloadAgent

[<EntryPoint>]
let main argv =
    // Get the current assembly
    let assembly = System.Reflection.Assembly.GetExecutingAssembly()
    
    // Create the hot reload agent
    let agent = HotReloadAgent.create assembly "." "*.fs"
    
    // Main loop
    while true do
        printfn "Current value: %d" (SimpleTest.getValue())
        System.Threading.Thread.Sleep(1000)
    
    // Cleanup
    HotReloadAgent.dispose agent
    0
