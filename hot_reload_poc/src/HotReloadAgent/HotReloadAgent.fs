namespace HotReloadAgent

open System
open System.Reflection
open System.Runtime.CompilerServices
open System.Reflection.Metadata
open System.Collections.Immutable

type HotReloadAgent = {
    FileWatcher: FileWatcher
    DeltaGenerator: DeltaGenerator
    TargetAssembly: Assembly
    TypeName: string
    MethodName: string
}

module HotReloadAgent =
    let create (targetAssembly: Assembly) (watchPath: string) (fileFilter: string) (typeName: string) (methodName: string) =
        printfn "[HotReloadAgent] Creating agent for assembly: %s" targetAssembly.FullName
        printfn "[HotReloadAgent] Watching path: %s with filter: %s" watchPath fileFilter
        printfn "[HotReloadAgent] Will update method: %s in type: %s" methodName typeName
        
        let deltaGenerator = DeltaGenerator.create()
        
        let handleFileChange (event: FileChangeEvent) =
            async {
                printfn "[HotReloadAgent] File change detected: %s" event.FilePath
                match! DeltaGenerator.generateDelta deltaGenerator targetAssembly event.FilePath typeName methodName with
                | Some delta ->
                    try
                        printfn "[HotReloadAgent] Applying update to assembly..."
                        // Apply the delta to the running assembly
                        MetadataUpdater.ApplyUpdate(
                            targetAssembly,
                            delta.MetadataDelta.AsSpan(),
                            delta.ILDelta.AsSpan(),
                            delta.PdbDelta.AsSpan()
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
            TypeName = typeName
            MethodName = methodName
        }

    let dispose (agent: HotReloadAgent) =
        printfn "[HotReloadAgent] Disposing agent..."
        FileWatcher.dispose agent.FileWatcher
        printfn "[HotReloadAgent] Agent disposed successfully" 