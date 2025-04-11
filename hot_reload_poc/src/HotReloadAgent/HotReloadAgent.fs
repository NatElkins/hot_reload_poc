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
        let deltaGenerator = DeltaGenerator.create()
        
        let handleFileChange (event: FileChangeEvent) =
            async {
                match! DeltaGenerator.generateDelta deltaGenerator event.FilePath with
                | Some delta ->
                    try
                        // Apply the delta to the running assembly
                        MetadataUpdater.ApplyUpdate(
                            targetAssembly,
                            ReadOnlySpan(delta.MetadataDelta),
                            ReadOnlySpan(delta.ILDelta),
                            ReadOnlySpan(delta.PdbDelta)
                        )
                        printfn $"Successfully applied changes to {event.FilePath}"
                    with ex ->
                        printfn $"Failed to apply changes: {ex.Message}"
                | None ->
                    printfn "No delta generated for changes"
            }
            |> Async.Start

        let fileWatcher = FileWatcher.create watchPath fileFilter handleFileChange

        {
            FileWatcher = fileWatcher
            DeltaGenerator = deltaGenerator
            TargetAssembly = targetAssembly
        }

    let dispose (agent: HotReloadAgent) =
        FileWatcher.dispose agent.FileWatcher 