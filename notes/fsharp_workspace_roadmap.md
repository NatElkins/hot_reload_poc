# FSharpWorkspace Roadmap (Long-Term)

## Vision
Deliver a Roslyn-style workspace for F# that underpins hot reload, IDE integrations, and tooling with immutable project/solution snapshots. This aligns with the direction sketched in [dotnet/fsharp#11976](https://github.com/dotnet/fsharp/issues/11976).

## Core Pillars
1. **Immutable Solution Model**
   - Introduce `FSharpSolution`, `FSharpProject`, and `FSharpDocument` abstractions that mirror Roslyn’s data flow.
   - Persist typed-tree, IL, and hot-reload baseline caches per generation.
2. **Incremental Build Integration**
   - Bridge existing incremental builder outputs into the workspace graph.
   - Surface fine-grained change notifications (syntax/semantic) for tooling.
3. **Analyzer & Tooling Extensibility**
   - Provide an analyzer host API analogous to Roslyn analyzers.
   - Expose hot reload deltas, rude edits, and diagnostics via workspace events.
4. **IDE/CLI Unification**
   - Share the workspace between `dotnet watch`, IDE hosts, and external tools.
   - Align telemetry and logging across environments.

## Milestones
1. **Scaffolding (Q1 2026)**
   - Spec data contracts for workspace objects.
   - Prototype conversion from project graph → workspace snapshot.
2. **Hot Reload Integration (Q2 2026)**
   - Rebase `FSharpChecker` hot reload APIs on the workspace model.
   - Validate deltas and diagnostics through workspace events.
3. **IDE Bridge (Q3 2026)**
   - Provide Visual Studio/Rider integration shims.
   - Ensure breakpoint, locals, and watch window scenarios operate against workspace state.
4. **General Availability (Q4 2026)**
   - Finalize public API, capability flags, and documentation.
   - Graduate feature flags and transition to default-on for supported TFMs.

## Dependencies & Risks
- Requires cooperation with Roslyn workspace infrastructure for shared services.
- Must preserve current `FSharpChecker` API stability during migration.
- Telemetry privacy and performance budgets need review.
