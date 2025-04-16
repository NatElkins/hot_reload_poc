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
    TargetTypeName: string
    TargetMethodName: string
}

module HotReloadAgent =
    let create (targetAssembly: Assembly) (initialValue: int) (targetTypeName: string) (targetMethodName: string) =
        printfn "[HotReloadAgent] Creating agent for assembly: %s" targetAssembly.FullName
        printfn "[HotReloadAgent] Initial value: %d" initialValue
        printfn "[HotReloadAgent] Target: %s::%s" targetTypeName targetMethodName
        
        let deltaGenerator = DeltaGenerator.create()
        
        {
            DeltaGenerator = deltaGenerator
            TargetAssembly = targetAssembly
            CurrentValue = initialValue
            TargetTypeName = targetTypeName
            TargetMethodName = targetMethodName
        }

    let updateValue (agent: HotReloadAgent) (newValue: int) =
        async {
            printfn "[HotReloadAgent] Updating value from %d to %d" agent.CurrentValue newValue
            match! DeltaGenerator.generateDelta agent.DeltaGenerator agent.TargetAssembly newValue false agent.TargetTypeName agent.TargetMethodName with
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