# F# Hot Reload Developer Walkthrough

This document tracks the current end-to-end workflow for exercising the F#
compiler's hot reload pipeline. As of 2025-11-02 the demo lives directly inside
the `fsharp/` repository so contributors can build, run, and iterate without the
throwaway `hot_reload_poc` harness.

## Prerequisites

- Restore and build the repo-local SDK at least once:

  ```bash
  cd fsharp
  ./.dotnet/dotnet build FSharp.sln -c Debug
  ```

- The sample references the in-repo `FSharp.Compiler.Service` and `FSharp.Core`
  projects, so no extra NuGet work is required beyond the standard build above.

## Running the new in-repo demo

1. **Build the solution (optional if you already ran the prerequisite build):**

   ```bash
   cd fsharp
   ./.dotnet/dotnet build FSharp.sln -c Debug
   ```

2. **Run the demo console app (auto mode):**

   ```bash
   cd tests/projects/HotReloadDemo/HotReloadDemoApp
   ../../../.dotnet/dotnet run
   ```

   The executable copies `DemoTarget.fs` into a temporary working directory,
   compiles it with `FSharpChecker.StartHotReloadSession`, edits the file on
   your behalf, and emits a delta while logging each step. The process exits on
   its own—no manual input required.

   Use `--interactive` to opt into the original, prompt-driven experience:

   ```bash
   ../../../.dotnet/dotnet run -- --interactive
   ```

3. **(Interactive mode only)** Edit the generated source file: change the body of
   `HotReloadDemo.Target.Demo.GetMessage` (for example, modify the returned
   string) and save your changes. Stick to method-body edits - signature changes
   will surface rude-edit diagnostics.

4. **(Interactive mode only)** Return to the console and press Enter. The app
   recompiles the file, calls `FSharpChecker.EmitHotReloadDelta`, and attempts to
   apply the update via `MetadataUpdater.ApplyUpdate`. The console shows
   generation IDs and the updated metadata/IL token lists (see the limitations
   below for the current runtime status).

5. **Repeat** steps 3-4 for additional generations or type `q` followed by
   Enter to exit. The demo automatically tears down the hot reload session and
   deletes the temporary working directory.

### Why the old `hot_reload_poc` harness was removed

- The new demo exercises the real compiler pipeline (typed-tree diff,
  symbol matcher, metadata/IL/PDB delta emitters) instead of the bespoke
  `DeltaGenerator` metadata writer.
- Everything now lives inside the primary repo, so `./.dotnet/dotnet build
  FSharp.sln -c Debug` compiles the demo alongside the rest of the compiler.
- The temporary working directory keeps repository files clean - contributors no
  longer need to edit tracked sources just to try the workflow.

### Scripted smoke test

- Run the non-interactive check from the repo root (CI drives the same command via `eng/Build.ps1` during CoreCLR/desktop test passes):

  ```bash
  cd fsharp
  tests/scripts/hot-reload-demo-smoke.sh
  ```

  The script sets `DOTNET_MODIFIABLE_ASSEMBLIES=debug`, runs the demo in
  `--scripted --multi-delta` mode, and verifies that two sequential
  `EmitHotReloadDelta` calls emit non-empty metadata/IL payloads. Runtime
  application is skipped for now, but the script catches compile/delta
  regressions automatically—and the Build pipeline fails if the scripted run
  does not report success.

### Watch-loop + mdv regression harness

- To reproduce the watch-loop issue we are currently chasing (`Message version 3 …` sneaking back into the delta metadata), run the integration script from the repository root:

  ```bash
  hot_reload_poc/scripts/watchloop_mdv_integration.sh
  ```

  The script cleans the watch-loop sample (`tools/fsc-watch/sample/WatchLoop`), launches `fsc-watch` in `--mdv-command-only` mode, rewrites `Message.fs` once, and then executes the emitted `mdv` command. If everything were healthy the script would report “mdv validation succeeded”; at the moment it fails with a truncated literal, giving us a concrete reproduction while we diagnose Task 3.7.

### Querying capabilities

- Use `FSharpChecker.HotReloadCapabilities` to inspect the current feature set. The preview build
  advertises IL, metadata, portable PDB, and multi-generation support; the runtime-apply flag stays
  off by default (set `FSHARP_HOTRELOAD_ENABLE_RUNTIME_APPLY=1` to experiment once runtime support
  stabilises). Hosts such as `dotnet watch` should consult these flags before assuming a specific
  workflow is supported.

## Current limitations

- Only method-body edits are guaranteed to succeed. Rude edits (signature
  changes, added members, etc.) surface friendly diagnostics but the delta will
  not be applied.
- Applying deltas through `MetadataUpdater.ApplyUpdate` still fails on the
  current runtime preview (the scripted mode records the emitted payloads but
  does not attempt to swap them in). Completing Tasks 2.x (locals/synthesised
  members) and runtime integration work remains a follow-up.
- `dotnet watch --hot-reload` is not yet wired into the new sample. Once the
  compiler/runtime path stabilises we will either extend the script or add a
  dedicated watcher harness.

## Next steps

- Bring the same pipeline to IDE hosts (`dotnet watch`, VS, Rider) once the API
  surface stabilises.
- Continue filling the coverage gaps called out in `IMPLEMENTATION_PLAN.md`
  (locals/variable-slot mapping, synthesized member remapping, richer PDB deltas).
- Capture richer diagnostics/telemetry during automation (generation IDs, rude
  edit counts) to aid CI analysis, and enable runtime apply validation once the
  runtime path stabilises.

Keeping these steps recorded ensures downstream contributors understand how to
exercise the feature and what remains before we reach Roslyn parity.
