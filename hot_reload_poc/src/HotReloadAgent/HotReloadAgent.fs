namespace HotReloadAgent

open System
open System.Reflection
open System.Runtime.CompilerServices
open System.Reflection.Metadata

type HotReloadAgent = {
    FileWatcher: FileWatcher
    DeltaGenerator: DeltaGenerator
    TargetAssembly: Assembly
}

module HotReloadAgent =
    let create (targetAssembly: Assembly) (watchPath: string) (fileFilter: string) =
        printfn "[HotReloadAgent] Creating agent for assembly: %s" targetAssembly.FullName
        printfn "[HotReloadAgent] Watching path: %s with filter: %s" watchPath fileFilter
        
        let deltaGenerator = DeltaGenerator.create()
        
        let handleFileChange (event: FileChangeEvent) =
            async {
                printfn "[HotReloadAgent] File change detected: %s" event.FilePath
                match! DeltaGenerator.generateDelta deltaGenerator event.FilePath with
                | Some delta ->
                    try
                        printfn "[HotReloadAgent] Converting delta to format expected by MetadataUpdater"
                        // Convert our simplified delta to the format expected by MetadataUpdater
                        let ilDelta = ReadOnlySpan(delta.ILBytes)
                        let emptySpan = ReadOnlySpan(Array.empty<byte>)
                        
                        printfn "[HotReloadAgent] Applying update to assembly..."
                        // Apply the delta to the running assembly
                        MetadataUpdater.ApplyUpdate(
                            targetAssembly,
                            emptySpan, // No metadata changes
                            ilDelta,
                            emptySpan  // No PDB changes
                        )
                        printfn "[HotReloadAgent] Successfully applied changes to %s" event.FilePath
                    with ex ->
                        printfn "[HotReloadAgent] Failed to apply changes: %s" ex.Message
                | None ->
                    printfn "[HotReloadAgent] No delta generated for changes"
            }
            |> Async.Start

        let fileWatcher = FileWatcher.create watchPath fileFilter handleFileChange
        printfn "[HotReloadAgent] File watcher created successfully"

        {
            FileWatcher = fileWatcher
            DeltaGenerator = deltaGenerator
            TargetAssembly = targetAssembly
        }

    let dispose (agent: HotReloadAgent) =
        printfn "[HotReloadAgent] Disposing agent..."
        FileWatcher.dispose agent.FileWatcher
        printfn "[HotReloadAgent] Agent disposed successfully" 