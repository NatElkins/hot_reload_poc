# SDK-Side `dotnet watch` Integration (Short-Term)

## Goal
Wire the existing `FSharpChecker` hot reload services into `dotnet watch --hot-reload` so contributors can demo the feature without waiting on the long-term workspace overhaul.

## Proposed Workflow
1. **SDK Live Reload Host**
   - Update `dotnet/sdk` hot reload host (`src/BuiltInTools/dotnet-watch/HotReload/HotReloadAgent.cs`) to recognize F# by feature flag.
   - Register an F# language service shim that forwards edit notifications to the compiler via `FSharpChecker.EmitHotReloadDelta`.
2. **Protocol Surface**
   - Extend the `IManagedHotReloadLanguageService` external access layer (mirroring `Microsoft.CodeAnalysis.ExternalAccess.FSharp`) with bindings for the new checker methods.
   - Translate Roslyn `ManagedHotReloadUpdate` payloads into the F# `IlxDelta` shape.
3. **Project Discovery**
   - Reuse SDK project graph to locate F# compilations; enable the feature when `TargetFramework` is net9.0+ and `LangVersion` is `preview` or `latest`.
4. **Runtime Apply**
   - Ensure the agent sets `DOTNET_MODIFIABLE_ASSEMBLIES=debug` and invokes `MetadataUpdater.ApplyUpdate` using the generated metadata/IL/PDB streams.
5. **Diagnostics/Logging**
   - Bubble rude edits and failure diagnostics back to the watch console; mirror the formatting used for C# so messages stay consistent.

## Deliverables
- SDK PR with the new language service shim, feature flag wiring, and smoke-test coverage.
- Documentation snippet in `docs/HotReload.md` mapping the short-term watch command to the new SDK bits.
- Automation hook updating `tests/scripts/run-hot-reload-demo.sh` (or new SDK test) to prove the integration works.

## Open Questions
- How to ship the shim without introducing new runtime dependencies (target netstandard2.0?).
- Whether to backport to servicing branches or keep feature behind preview flag until workspace work lands.
- Coordination with Roslyn watchers to ensure no regressions for C#.
