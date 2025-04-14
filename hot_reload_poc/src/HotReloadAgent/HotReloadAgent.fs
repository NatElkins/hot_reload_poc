namespace HotReloadAgent

open System
open System.Reflection
open System.Runtime.CompilerServices
open System.Reflection.Metadata
open System.Collections.Immutable

type HotReloadAgent = {
    DeltaGenerator: DeltaGenerator
    TargetAssembly: Assembly
    CurrentValue: int
}

module HotReloadAgent =
    let create (targetAssembly: Assembly) (initialValue: int) =
        printfn "[HotReloadAgent] Creating agent for assembly: %s" targetAssembly.FullName
        printfn "[HotReloadAgent] Initial value: %d" initialValue
        
        let deltaGenerator = DeltaGenerator.create()
        
        {
            DeltaGenerator = deltaGenerator
            TargetAssembly = targetAssembly
            CurrentValue = initialValue
        }

    let updateValue (agent: HotReloadAgent) (newValue: int) =
        async {
            printfn "[HotReloadAgent] Updating value from %d to %d" agent.CurrentValue newValue
            match! DeltaGenerator.generateDelta agent.DeltaGenerator agent.TargetAssembly newValue false with
            | Some delta ->
                try
                    printfn "[HotReloadAgent] Applying update to assembly..."
                    // Apply the delta to the running assembly
                    MetadataUpdater.ApplyUpdate(
                        agent.TargetAssembly,
                        delta.MetadataDelta.AsSpan(),
                        delta.ILDelta.AsSpan(),
                        delta.PdbDelta.AsSpan()
                    )
                    printfn "[HotReloadAgent] Successfully applied changes"
                    return Some { agent with CurrentValue = newValue }
                with ex ->
                    printfn "[HotReloadAgent] Failed to apply changes: %s" ex.Message
                    return None
            | None ->
                printfn "[HotReloadAgent] No delta generated for changes"
                return None
        }

    let dispose (agent: HotReloadAgent) =
        printfn "[HotReloadAgent] Disposing agent..."
        printfn "[HotReloadAgent] Agent disposed successfully" 