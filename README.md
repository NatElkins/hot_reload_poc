# F# Hot Reload Proof-of-Concept

> **WARNING**: This repository is a work-in-progress and primarily serves as a demonstration that patching a running F# assembly using F# code is possible. The code might be messy and is not guaranteed to be production-ready.

## Quick Start

To run the main demonstration project:

```bash
DOTNET_MODIFIABLE_ASSEMBLIES=debug dotnet run --project hot_reload_poc/src/TestApp/TestApp.fsproj
```

## Overview

This project demonstrates a minimal example of applying a hot reload patch to an F# assembly without restarting the application. Here's a high-level overview of the process implemented in `hot_reload_poc/src/TestApp/`:

1.  **Compile Baseline:** A simple F# library (`SimpleLib`) is compiled to a DLL on disk using `FSharp.Compiler.Services`.
2.  **Load Assembly:** The baseline DLL is loaded into a collectible `AssemblyLoadContext`.
3.  **Generate Delta Patch:** A patch (delta) is generated for the loaded assembly. This involves:
    *   Manually constructing metadata delta tables using `System.Reflection.Metadata.MetadataBuilder`.
    *   Generating the corresponding IL delta for the changed method body.
    *   (Optionally) Generating a PDB delta for debugging information.
4.  **Apply Update:** The runtime's `System.Reflection.Metadata.MetadataUpdater.ApplyUpdate` method is called with the generated metadata, IL, and PDB deltas to patch the assembly in memory.
5.  **Verify:** The updated method in the running assembly is invoked using reflection to confirm it now executes the patched code (e.g., returns a new value).

## Delta Components and Inspection

A hot reload delta typically consists of three byte arrays:

1.  **Metadata Delta (`.dmeta`):** Contains changes to the assembly's metadata tables.
2.  **IL Delta (`.dil`):** Contains the updated Intermediate Language code for modified method bodies.
3.  **PDB Delta (`.dpdb`):** Contains updated debugging information (optional but recommended).

You can inspect the baseline assembly and the generated deltas using the `mdv` (Metadata Visualizer) tool from the `dotnet/metadata-tools` repository.

**Example `mdv` Usage:**

```bash
# Navigate to the directory containing the baseline DLL and delta files
cd path/to/output

# Inspect baseline (Gen 0) and apply deltas for Gen 1 and Gen 2
mdv baseline.dll '/g:1.meta;1.il' '/g:2.meta;2.il' | cat
```

*(Note: Ensure arguments with semicolons are quoted correctly for your shell.)*

A successful multi-generation patch applied and viewed with `mdv` will show output similar to this, illustrating the `EncId` and `EncBaseId` linkage between generations:

```
MetadataVersion: v4.0.30319

>>> Generation 0: >>>
Module (0x00):
   Gen  Name          Mvid                                         EncId  EncBaseId
=======================================================================================
1: 0    'baseline.dll' {89c195e9-...}                              nil    nil

>>> Generation 1: >>>
Module (0x00):
   Gen  Name            Mvid                                         EncId                                        EncBaseId
===============================================================================================================================
1: 1    'baseline.dll' {89c195e9-...}                                {0f02ed24-...}                               nil <-- Gen 1 links to baseline implicitly

>>> Generation 2: >>>
Module (0x00):
   Gen  Name            Mvid                                         EncId                                        EncBaseId
===============================================================================================================================
1: 2    'baseline.dll' {89c195e9-...}                                {869f292c-...}                               {0f02ed24-...} <-- Gen 2 links to Gen 1 via EncBaseId=Gen1.EncId
... (rest of mdv output) ...
```

See [notes/fsharp\_hot\_reload\_overview.md](notes/fsharp_hot_reload_overview.md) for detailed specifications on the delta format required for F#.

## Generating Deltas with `hotreload-utils` (C# Comparison)

While this repository focuses on generating deltas using F# and `System.Reflection.Metadata`, comparing this with C# delta generation using official tools can be insightful. The [dotnet/hotreload-utils](https://github.com/dotnet/hotreload-utils/) repository provides tools for this.

The `hot_reload_poc/src/csharp_delta_test/` directory contains an example C# project configured to use `hotreload-utils`.

**Setup:**

1.  **Clone `hotreload-utils`:** This tool is not available on NuGet and must be cloned locally. Place it adjacent to this repository (e.g., `../hotreload-utils`).
    ```bash
    git clone https://github.com/dotnet/hotreload-utils ../hotreload-utils
    ```
2.  **Build `hotreload-utils` (Optional):** Building the tool first might be necessary. Follow instructions in its README (`build.sh` or similar).
3.  **Target Project (`csharp_delta_test`) Setup:**
    *   Contains a `diffscript.json` defining sequential code updates.
    *   Includes source files for each version (`SimpleLib_v1.cs`, `SimpleLib_v2.cs`).
    *   Excludes versioned files from the main build via `.csproj` (`<Compile Remove=... />`).

**Generating C# Deltas:**

1.  **Build Baseline:**
    ```bash
    cd hot_reload_poc/src/csharp_delta_test
    dotnet build csharp_delta_test.csproj -p:Configuration=Debug
    cd ../../../.. # Return to workspace root
    ```
2.  **Run Delta Generator Tool:** Execute `hotreload-delta-gen` via `dotnet run`:
    ```bash
    # Adjust path to hotreload-utils if needed
    dotnet run --project ../hotreload-utils/src/hotreload-delta-gen/src/hotreload-delta-gen.csproj -- \
      -msbuild:hot_reload_poc/src/csharp_delta_test/csharp_delta_test.csproj \
      -p:Configuration=Debug \
      -script:hot_reload_poc/src/csharp_delta_test/diffscript.json
    ```
    This generates delta files (`.1.dmeta`, `.1.dil`, `.2.dmeta`, etc.) in the C# project's output directory (`bin/Debug/net10.0/`).

## Required Tools & Environment

*   **[.NET SDK](https://dotnet.microsoft.com/download):** Tested with .NET 10 SDK. Compatibility with .NET 9 might be possible but requires further testing, especially when using `hotreload-utils`.
*   **[dotnet/metadata-tools](https://github.com/dotnet/metadata-tools):** Provides `mdv` (Metadata Visualizer).  See their repo for instructions on installation.
*   **(Optional) Cloned `dotnet/hotreload-utils` Repository:** Needed for the C# delta generation comparison described above.

## Questions?

Come find me on the F# Discord, or open an issue.
