// For more information see https://aka.ms/fsharp-console-apps
open System
open HotReloadAgent

[<EntryPoint>]
let main argv =
    printfn "Starting Hot Reload Test Application"
    
    // Get the current assembly
    let assembly = System.Reflection.Assembly.GetExecutingAssembly()
    
    // Create the hot reload agent
    let agent = HotReloadAgent.create assembly "." "*.fs"
    
    // Main application loop
    let rec loop () =
        printfn "Current count: %d" (Counter.increment())
        System.Threading.Thread.Sleep(1000)
        loop()
    
    try
        loop()
    finally
        HotReloadAgent.dispose agent
    
    0
