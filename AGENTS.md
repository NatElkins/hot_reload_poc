# Repository Guidelines

## Project Structure & Module Organization
The primary solution sits at `hot_reload_poc/HotReloadPoc.sln`, grouping the F# demo in `hot_reload_poc/src/TestApp/` and the comparative C# harness in `hot_reload_poc/src/csharp_delta_test/`. Place new runtime experiments alongside these directories to keep the solution tidy. Use the `notes/` folder for deep dives on metadata formats or architectural context, and leave generated `bin/` and `obj/` outputs untracked. Helper assets such as `analyze_il.sh`, `setup.fsx`, and the Roslyn playground under `roslyn/` exist to inspect or prototype IL changes—keep scripts there so other agents can locate them quickly.
- Mirror Roslyn hot reload patterns wherever possible: reuse its terminology, metadata structures (EmitBaseline, DefinitionMap, SymbolMatcher, DeltaMetadataWriter), and component layout when adding new F# functionality so parity stays obvious.

## Build, Run, and Hot Reload Commands
- `dotnet build hot_reload_poc/HotReloadPoc.sln` compiles every project for .NET 9; pass `-c Release` when profiling perf-sensitive code paths.
- `DOTNET_MODIFIABLE_ASSEMBLIES=debug dotnet run --project hot_reload_poc/src/TestApp/TestApp.fsproj` launches the F# hot reload demo and streams delta-generation diagnostics.
- `dotnet run --project ../hotreload-utils/src/hotreload-delta-gen/src/hotreload-delta-gen.csproj -- -msbuild:hot_reload_poc/src/csharp_delta_test/csharp_delta_test.csproj -script:hot_reload_poc/src/csharp_delta_test/diffscript.json` reproduces the C# delta workflow once `hotreload-utils` is cloned adjacent to this repo.
- `./hot_reload_poc/analyze_il.sh <baseline.dll> <patched.dll>` exports IL with `ilspycmd` and highlights differences for manual review.
- `dotnet run --project tools/fsc-watch/fsc_watch.fsproj -- ...` starts a long-lived watch loop; never launch it on behalf of the user without confirming because it deliberately stays running until the user edits a file or presses Ctrl+C.

## Coding Style & Naming Conventions
Follow the existing F# style: four-space indentation, module-qualified functions, and `CamelCase` modules or types. Favor pipeline-friendly expressions over nested calls, and keep reflection helpers in separate modules (see `DeltaGenerator.fs`). For the C# samples, stick to `PascalCase` types, camelCase locals, and 120-character lines. Run `dotnet format` on touched projects before submitting; it keeps both F# and C# sources aligned with SDK defaults.
- When adding new public or internal types/functions, include XML documentation comments so downstream agents understand their responsibilities without re-reading the implementation.

## Testing Guidelines
There is no dedicated test project yet, so treat the console output from `HotReloadTest` as the regression gate: confirm the baseline compilation, delta application, and re-invocation sequences all succeed. When introducing new patch scenarios, add deterministic checks (e.g., validating `SimpleLib.GetValue()` responses) and capture expected console markers in comments. If you add a formal test suite, wire it up via `Microsoft.NET.Test.Sdk` and expose it through `dotnet test` so CI can exercise it uniformly.

## Commit & Pull Request Guidelines
Keep commits focused and descriptive in sentence case (see `git log --oneline` for examples). Reference related issues in the subject or body, and include before/after output snippets for hot reload flows when relevant. Pull requests should describe the scenario exercised, note any required environment variables (such as `DOTNET_MODIFIABLE_ASSEMBLIES`), and attach console excerpts or IL diffs that prove the change works. Request reviewers from agents who touched the same module lately to maintain shared context.
- Whenever a test suite passes (even locally), capture that state in a commit so future iterations can bisect or revert cleanly—don’t keep green builds uncommitted.

## Documentation Updates
- After completing any task or plan step, record the outcome in both `ARCHITECTURE_PROPOSAL.md` and `IMPLEMENTATION_PLAN.md`, including new learnings, status changes, and follow-up actions so downstream agents stay aligned.
- Before declaring tasks complete, run the minimal regression checks (`dotnet build FSharp.sln -c Debug` and `dotnet test tests/FSharp.Compiler.ComponentTests/FSharp.Compiler.ComponentTests.fsproj -c Debug --no-build --filter FullyQualifiedName~HotReload`) to catch obvious regressions quickly.

## Current Status (2025-11-02)
- Branch `main` remains 24 commits ahead of `origin/main`; keep the local-only artifacts in place unless coordinating (they backfill upstream work in progress).
- Untracked helpers include clones of dotnet/fsharp, runtime, sdk, roslyn, metadata-tools, hotreload-utils, plus `notes/ECMA-335_6th_edition_june_2012.pdf`, `NuGet.runtime.config`, and `tomas_transcript.md` - they feed design notes and tooling.
- Milestone 1 progress: Tasks 1.1-1.6 stay in the tree with passing coverage (`TypedTreeDiffTests.fs`, `RudeEditDiagnosticsTests.fs`, `BaselineTests.fs`, `NameMapTests.fs`).
- Milestone 2 status: Tasks 2.1-2.5 are implemented on the `fsharp/hot-reload` branch (`HotReloadPdb.fs`, `FSharpSymbolMatcher.fs`, `DeltaEmitterTests.fs`, etc.). The compiler emits metadata/IL/PDB deltas for method-body edits; locals/variable-slot mapping remains future work.
- Task 3.1 checker APIs are live (`StartHotReloadSession`, `EmitHotReloadDelta`, `EndHotReloadSession`, `HotReloadSessionActive`) and exercised by `tests/FSharp.Compiler.Service.Tests/HotReload/HotReloadCheckerTests.fs`.
- Task 3.5 progress: a new in-repo demo (`tests/projects/HotReloadDemo/HotReloadDemoApp`) now drives the `FSharpChecker` APIs end-to-end. The throwaway `hot_reload_poc` harness remains for history but is no longer required for demos; the console app prints a temp path to edit and applies deltas interactively. A scripted mode (`--scripted`) plus `tests/scripts/hot-reload-demo-smoke.sh` runs the sample non-interactively and verifies delta emission.
- Documentation: `docs/HotReload.md` has been refreshed to describe the new sample workflow and to capture the remaining limitations/follow-ups.
- Immediate focus: finish Task 3.5 to restore a working console sample, then resume Task 2.x follow-ups (locals, synthesized member mapping) and telemetry/capability work.
- Latest verification (2025-11-02): `./.dotnet/dotnet build FSharp.sln -c Debug`, `./.dotnet/dotnet test tests/FSharp.Compiler.Service.Tests/FSharp.Compiler.Service.Tests.fsproj -c Debug --no-build --filter FullyQualifiedName~HotReload`, and `./.dotnet/dotnet test tests/FSharp.Compiler.ComponentTests/FSharp.Compiler.ComponentTests.fsproj -c Debug --no-build --filter FullyQualifiedName~HotReload` all pass. The hot reload console sample must be fixed before using it as a regression gate again.
- When updating shared plan/docs/guidelines, add short inline comments in code/tests documenting non-obvious behaviors (e.g., why EncLog expects `AddPropertyMap`) so future agents can reason about deltas without re-reading Roslyn.

---

## F# Compiler Context for Hot Reload Implementation

- **Local SDK**: Run `./eng/common/dotnet.sh` once to install the repo-local .NET SDK into `.dotnet/`. Use `./.dotnet/dotnet …` for builds and tests.
- **Baseline build workaround**: Until the PreRelease props copy issue is fixed upstream, seed the file once per clean clone:
  ```bash
  mkdir -p artifacts/obj/FSharp.Build/Debug/netstandard2.0/PreRelease \
          artifacts/bin/FSharp.Build/Debug/netstandard2.0/PreRelease
  cp src/FSharp.Build/Microsoft.FSharp.Core.NetSdk.props artifacts/obj/FSharp.Build/Debug/netstandard2.0/PreRelease/
  ```
  After this, `./.dotnet/dotnet build FSharp.sln -c Debug` succeeds.
- **Key documentation**: README.md, DEVGUIDE.md, TESTGUIDE.md, and https://deepwiki.com/dotnet/fsharp (compiler overview, testing layout, build/config tips).
- **Primary focus files**:
  - `ARCHITECTURE_PROPOSAL.md`, `IMPLEMENTATION_PLAN.md` (overall design & milestones)
  - Compiler sources under `src/Compiler/**`, especially `CodeGen/IlxGen.fs`, `AbstractIL/ilwrite.fs`, `TypedTree/**`
  - Hot reload baseline helpers: `src/Compiler/CodeGen/HotReloadBaseline.fs[si]` (baseline capture) and component coverage in `tests/FSharp.Compiler.ComponentTests/HotReload/BaselineTests.fs`
- Test harness directories under `tests/`
- Documentation practice: when introducing new public types or functions add XML documentation comments and prefer explanatory inline comments for non-obvious logic.
- Full-suite testing is time-consuming on local machines; run targeted checks (`./.dotnet/dotnet test tests/FSharp.Compiler.ComponentTests/FSharp.Compiler.ComponentTests.fsproj -c Debug --no-build --filter FullyQualifiedName~HotReload`) during iteration and reserve `./build.sh --testcoreclr --testCompilerComponentTests --testScripting` for validation, noting that several legacy IL baselines currently fail until updated.
- The `metadata-tools (mdv)` CLI is optional for local runs; `DeltaEmitterTests` log and continue when the tool is missing or exits with a non-zero code (future deltas will still assert metadata via mdv when the tool is available).
- Dependency discipline: avoid introducing new framework or package dependencies (especially additional BCL assemblies) without prior discussion—hot reload work should continue to rely on the compiler’s existing abstractions so downstream consumers inherit no extra requirements.
- Fable compatibility: aim to keep the codebase cross-compilable by Fable’s maintained branch; flag any change that would break the JS toolchain or require browser-specific shims.
- **General build command**: `./.dotnet/dotnet build FSharp.sln -c Debug` (feature flag off by default).
