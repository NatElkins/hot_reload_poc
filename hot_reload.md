# F# Hot Reload Implementation Guide

## Introduction

Hot Reload is a powerful feature that allows developers to make changes to their code while the application is running, without having to pause or stop the application. This document outlines how Hot Reload works under the hood and what's required to implement it for F#.

### Current State

As of 2024, F# does not yet have native support for Hot Reload, while C# has had this capability since .NET 6. This has created a gap in developer experience, particularly for web UI development where frameworks like Falco, Giraffe, and Fun.Blazor would benefit significantly from this feature.

The implementation of Hot Reload for F# will need to:
- Integrate with the existing .NET Hot Reload infrastructure
- Support F#-specific language features and constructs
- Maintain compatibility with the current F# compiler architecture
- Provide a seamless experience for web UI development

## Core Concepts

### What Hot Reload Does

The fundamental idea behind Hot Reload is simple:
- Make changes to (managed) code while your application is running
- Apply changes without pausing or stopping the application
- Maintain the application's state when possible
- No recompilation needed for supported changes

### Supported Changes

Hot Reload supports most types of code changes within method bodies. However, there are limitations:
- Changes outside method bodies may not be supported
- Some changes within method bodies cannot be applied while running
- The specific limitations for F# will need to be determined during implementation

## Technical Implementation

### Unified Infrastructure

The .NET hot reload infrastructure is unified across different tools and scenarios:

1. **Core Components**:
   - `WatchHotReloadService`: Base implementation used by `dotnet watch`
   - `IManagedHotReloadLanguageService`: Full IDE integration
   - `IEditAndContinueService`: Core service for applying changes
   - `MetadataUpdater.ApplyUpdate`: Runtime API for applying changes

2. **Service Architecture**:
   - All implementations share the same core infrastructure
   - Different tools use different levels of integration
   - The implementation can start simple and add features incrementally

### Two Modes of Operation

Hot Reload can operate in two different modes depending on how the application is launched:

1. **Debug Mode (Edit and Continue - EnC)**
   - Uses the Just-in-Time (JIT) compiler
   - Works through the debugger's `ICorDebugModule2::ApplyChanges` method
   - Currently only supported on Windows
   - Based on the Edit and Continue (EnC) feature
   - Only used when actively debugging
   - Separate from the main hot reload path
   - Uses ExpressionEvaluator for debugging scenarios to evaluate expressions in the debugger context

2. **Non-Debug Mode (Hot Reload Agent)**
   - Uses a Hot Reload agent assembly
   - Loaded via the `DOTNET_STARTUP_HOOKS` environment variable
   - Communicates over named pipes
   - Uses the `MetadataUpdater.ApplyUpdate` method (new in .NET 6)
   - Fully cross-platform
   - Primary path for most hot reload usage
   - Does not require ExpressionEvaluator since it's not evaluating expressions in a debugger context

### The Compiler Workspace

The implementation relies on the compiler's workspace API which:
- Parses source code
- Resolves metadata references
- Manages compilation state
- Handles document updates

For F#, we'll need to:
- Create a workspace implementation specific to F#
- Hook it up to the compiler through the appropriate interfaces
- Handle F#-specific compilation and metadata updates

### Applying Changes

When changes are applied:
1. The runtime tracks application state (executing methods, call stacks, memory pointers)
2. For method body changes:
   - The virtual machine recompiles the method
   - Replaces the implementation in the running process
   - Only replaces when the method is not currently executing

## F# Specific Considerations

### Required Components

1. **F# Compiler Integration**
   - Integration with F# compiler services
   - Support for F#-specific syntax and features
   - Handling of F# metadata and type system
   - Integration with Roslyn's Hot Reload infrastructure

2. **State Management**
   - Tracking F#-specific state (modules, namespaces)
   - Handling F# type system changes
   - Managing F# compilation context
   - Preserving F#-specific runtime state

3. **Change Application**
   - Support for F#-specific language features
   - Handling of F# module and type changes
   - Support for F# computation expressions
   - Integration with F# interactive evaluation

### Implementation Challenges

1. **F# Language Features**
   - Pattern matching
   - Discriminated unions
   - Computation expressions
   - Type providers
   - Units of measure
   - Module-level bindings
   - Type abbreviations
   - Active patterns

2. **State Preservation**
   - Maintaining F#-specific state during updates
   - Handling F# module and namespace changes
   - Preserving F# type system information
   - Managing F# interactive state

3. **Performance Considerations**
   - Efficient F# compilation updates
   - Minimal state disruption
   - Fast change application
   - Integration with F# compiler services

## Implementation Strategy

### Phase 1: Basic Support
1. Implement basic `WatchHotReloadService` integration
2. Support simple F# method body changes
3. Basic state preservation
4. Focus on non-debug scenarios

### Phase 2: Advanced Features
1. Support for F#-specific language features
2. Enhanced state management
3. Performance optimizations
4. Add IDE integration

### Phase 3: Debug Support
1. Implement Edit and Continue support
2. Debugger integration
3. Full IDE support
4. Cross-platform debugging

## Next Steps

1. Create proof of concept using `WatchHotReloadService`
2. Implement F#-specific change detection and application
3. Develop state preservation mechanisms
4. Create integration with F# compiler services
5. Build testing infrastructure
6. Implement IDE integration

## Resources

- [How Rider Hot Reload Works Under the Hood](https://blog.jetbrains.com/dotnet/2021/12/02/how-rider-hot-reload-works-under-the-hood/)
- F# Compiler Services documentation
- .NET Hot Reload documentation
- Concord Extensibility framework documentation

### Key Reference Files

1. Core Services and Interfaces:
- `roslyn/src/Features/Core/Portable/Contracts/EditAndContinue/IManagedHotReloadService.cs`
- `roslyn/src/Features/Core/Portable/ExternalAccess/Watch/Api/WatchHotReloadService.cs`

2. Language Service Integration:
- `roslyn/src/Workspaces/Remote/Core/EditAndContinue/ManagedHotReloadLanguageService.cs`
- `roslyn/src/Workspaces/Remote/Core/EditAndContinue/ManagedHotReloadLanguageServiceDescriptor.cs`

3. Diagnostic Handling:
- `roslyn/src/LanguageServer/ExternalAccess/VisualDiagnostics/Internal/HotReloadDiagnosticSource.cs`
- `roslyn/src/LanguageServer/ExternalAccess/VisualDiagnostics/Contracts/IHotReloadDiagnosticManager.cs`

4. Remote Service Implementation:
- `roslyn/src/Workspaces/Remote/Core/EditAndContinue/ManagedHotReloadServiceProxy.cs`
- `roslyn/src/Workspaces/Remote/ServiceHub/Services/EditAndContinue/RemoteEditAndContinueService.cs`

5. Update Application:
- `roslyn/src/Features/Core/Portable/EditAndContinue/Utilities/Extensions.cs`

6. Project Structure Reference:
- `hot_reload_poc/src/HotReloadAgent/HotReloadAgent.fsproj`

## Technical Implementation Details

### Edit and Continue (EnC) Infrastructure

1. **Core Components**
   - `IManagedHotReloadLanguageService` - Main interface for language-specific hot reload support
   - `AbstractEditAndContinueAnalyzer` - Base class for language-specific change analysis
   - `Compilation.EmitDifference` - API for emitting IL+metadata+PDB deltas
   - `EditSession.EmitSolutionUpdateAsync` - Handles solution-wide updates

2. **Change Analysis Process**
   - Document change detection
   - Textual diff analysis
   - Symbol impact analysis
   - Rude edit detection
   - LSP integration for real-time edit validation

3. **Delta Generation**
   - IL generation for changed methods
   - Metadata table updates
   - PDB information updates
   - Token remapping
   - Symbol matching and slot preservation

### Implementation Requirements

1. **Language Service Integration**
   ```fsharp
   type IManagedHotReloadLanguageService =
       abstract member GetCapabilities: unit -> HotReloadCapabilities
       abstract member GetRequiredCapabilities: unit -> HotReloadCapabilities
       abstract member GetDeltaUpdates: 
           solution: Solution * 
           baseline: EmitBaseline * 
           edits: ImmutableArray<SemanticEdit> * 
           cancellationToken: CancellationToken -> Task<EmitDifferenceResult>
   ```

2. **Change Analysis**
   - Implement `AbstractEditAndContinueAnalyzer`
   - Handle F#-specific syntax and semantics
   - Support for F#-specific language constructs
   - Integration with F# compiler services

3. **Delta Compilation**
   - Symbol matching and remapping
   - IL generation for F# constructs
   - Metadata table updates
   - PDB information generation

### Technical Challenges

1. **Symbol Matching**
   - Matching F# symbols across generations
   - Handling F#-specific metadata
   - Preserving symbol identity
   - Managing symbol references

2. **IL Generation**
   - F#-specific IL patterns
   - Computation expression handling
   - Pattern matching compilation
   - Type provider integration

3. **State Management**
   - Module-level state preservation
   - Type provider state handling
   - Interactive evaluation state
   - Runtime type information

### Implementation Strategy

1. **Phase 1: Core Infrastructure**
   - Implement `IManagedHotReloadLanguageService`
   - Basic change detection and analysis
   - Simple method body updates
   - Integration with F# compiler services

2. **Phase 2: Language Features**
   - Support for F#-specific constructs
   - Type provider integration
   - Module-level updates
   - State preservation

3. **Phase 3: Advanced Features**
   - Interactive evaluation support
   - Web framework integration
   - Performance optimizations
   - Tooling support

### Integration Points

1. **Compiler Services**
   - FSharp.Compiler.Service integration
   - Symbol table management
   - Type checking and inference
   - Error reporting

2. **Runtime Integration**
   - Metadata update handling
   - State preservation
   - Type system updates
   - Performance monitoring

3. **Tooling Support**
   - IDE integration
   - Debugging support
   - Error reporting
   - Performance profiling

## Implementation Details from Investigation

### Key Technical Findings

1. **Compiler Service Integration**
   - F# Compiler Service (FCS) has built-in incremental build capabilities
   - `IncrementalBuilder` handles minimal rebuilds based on changes
   - `TcConfig` uses flags to control whether artifacts are kept in memory
   - `BuildGraph` is used for tracking dependencies and minimal rebuilds

2. **IL and PDB Generation**
   - FCS can emit PDBs with `--debug:full` flag
   - IL generation handled through `ilwrite.fs`
   - `ilreflect.fs` provides incremental output capabilities
   - `emitModuleFragment` in `ilreflect.fs` is key for delta generation

3. **Delta Generation Design**
   ```fsharp
   // Proposed design for delta generator
   type DeltaGenerator = {
       ChangedFiles: string[] -> ModuleUpdates[]
       // or
       Update: string[] -> MimicOfWatchHotReloadService.Update
   }
   ```

4. **Metadata and PDB Handling**
   - `System.Reflection.Metadata.MetadataUpdater` is used for applying updates
   - Module IDs are derived from ModuleVersionIds
   - Deltas are adds and updates only (no deletions)
   - PDB information is crucial for debugging support

5. **Runtime Integration**
   - `IDeltaApplier` communicates with `HotReloadAgent` via pipes
   - `HotReloadAgent` uses `System.Reflection.Metadata.MetadataUpdater`
   - CoreCLR/runtime handles the actual metadata updates
   - Tests available in `ApplyUpdateTest.cs` for reference

## Hot Reload Agent Implementation Details

### Named Pipe Communication
1. **Pipe Implementation**:
   - Uses `NamedPipeServerStream` and `NamedPipeClientStream` from `System.IO.Pipes`
   - Pipe name format: `Global\` prefix on Windows, `/tmp/` on Unix
   - Buffer size: 64K (0x10000)
   - Pipe options: Asynchronous | WriteThrough

2. **Key Classes**:
   - `NamedPipeUtil`: Handles cross-platform pipe creation and security
   - `NamedPipeClientConnection`: Manages client connections
   - `NamedPipeClientConnectionHost`: Hosts multiple client connections

3. **Communication Protocol**:
   - Uses JSON-RPC for message passing
   - Messages include:
     - Change detection notifications
     - Update requests
     - Error reporting
     - State synchronization

### Runtime Integration

1. **MetadataUpdater.ApplyUpdate**:
   ```csharp
   public static void ApplyUpdate(
       Assembly assembly,
       ReadOnlySpan<byte> metadataDelta,
       ReadOnlySpan<byte> ilDelta,
       ReadOnlySpan<byte> pdbDelta)
   ```
   - Parameters:
     - `assembly`: Target assembly to update
     - `metadataDelta`: Binary changes to assembly metadata
     - `ilDelta`: Binary changes to method bodies
     - `pdbDelta`: Debug information updates

2. **State Tracking**:
   - Runtime maintains:
     - Method execution state through `ManagedActiveStatementDebugInfo`
     - Call stacks via `ICorDebugModule2::ApplyChanges`
     - Memory pointers in `ModuleUpdates`
     - Type system information in `EmitDifferenceResult`

3. **Update Process**:
   ```csharp
   // From WatchHotReloadService.cs
   public readonly struct Update
   {
       public Guid ModuleId { get; }
       public ImmutableArray<byte> ILDelta { get; }
       public ImmutableArray<byte> MetadataDelta { get; }
       public ImmutableArray<byte> PdbDelta { get; }
       public ImmutableArray<int> UpdatedMethods { get; }
       public ImmutableArray<int> UpdatedTypes { get; }
   }
   ```

### Error Handling and Diagnostics

1. **HotReloadResult Enum**:
   ```csharp
   internal enum HotReloadResult
   {
       Applied = 0,
       NoChanges = 1,
       RestartRequired = 2,
       ErrorEdits = 3,
       ApplyUpdateFailure = 4
   }
   ```

2. **Diagnostic Integration**:
   - `IHotReloadDiagnosticManager`: Manages hot reload diagnostics
   - `HotReloadDiagnosticSource`: Provides diagnostic information
   - `HotReloadDiagnosticSourceProvider`: Creates diagnostic sources

### Implementation Requirements

1. **F# Specific Components**:
   - Need to implement:
     - `IHotReloadLanguageService` interface
     - F#-specific `HotReloadDiagnosticSource`
     - Integration with F# compiler services
     - Custom `NamedPipeUtil` for F#-specific communication

2. **State Management**:
   - Track F#-specific state:
     - Module-level bindings
     - Type provider state
     - Computation expression context
     - Pattern matching state

3. **Change Detection**:
   - Use F# compiler services for:
     - Source file change detection
     - Dependency analysis
     - Type checking
     - Symbol resolution

## Resources

- [How Rider Hot Reload Works Under the Hood](https://blog.jetbrains.com/dotnet/2021/12/02/how-rider-hot-reload-works-under-the-hood/)
- F# Compiler Services documentation
- .NET Hot Reload documentation
- Concord Extensibility framework documentation

### Technical Implementation Details

#### Accessing MetadataUpdater from F#

To use `MetadataUpdater.ApplyUpdate` from F#, you need to:

1. Add a reference to the `System.Runtime.CompilerServices.Unsafe` package which contains the `MetadataUpdater` class.

2. The method can be accessed through the `System.Runtime.CompilerServices` namespace:
   ```fsharp
   open System.Runtime.CompilerServices
   
   // The ApplyUpdate method signature:
   // public static void ApplyUpdate(Assembly assembly, ReadOnlySpan<byte> metadataDelta, ReadOnlySpan<byte> ilDelta, ReadOnlySpan<byte> pdbDelta)
   ```

3. The process for using it involves:
   - Using F# Compiler Service to recompile changed files
   - Generating three types of deltas:
     - Metadata delta (changes to type definitions)
     - IL delta (changes to method bodies)
     - PDB delta (changes to debug information)
   - Calling `ApplyUpdate` with these deltas to apply the changes to the running assembly

This is the core mechanism that allows hot reload to work in non-debug mode, as it enables updating the running assembly's metadata and IL without requiring a restart.

### Delta Format and Generation Process

#### Binary Payload Format

The hot reload system uses a specific binary payload format for communicating deltas between the watcher and the application. The format is as follows:

```
[Version: byte | Currently 0]
[Absolute path of file changed: string]
[Number of deltas produced: int32]
[Delta item 1]
[Delta item 2]
...
[Delta item n]
```

Where each delta item has the following structure:
```
[ModuleId: string]
[MetadataDelta byte count: int32]
[MetadataDelta bytes]
[ILDelta byte count: int32]
[ILDelta bytes]
```

#### Metadata Delta Format

The metadata delta must follow the PE (Portable Executable) file format structure and specifically target the metadata section. The metadata section contains:

1. **Metadata Tables**:
   - TypeDef table (0x02)
   - MethodDef table (0x06)
   - FieldDef table (0x04)
   - ParamDef table (0x08)
   - InterfaceImpl table (0x09)
   - MemberRef table (0x0A)
   - Constant table (0x0B)
   - CustomAttribute table (0x0C)
   - FieldMarshal table (0x0D)
   - DeclSecurity table (0x0E)
   - ClassLayout table (0x0F)
   - FieldLayout table (0x10)
   - StandAloneSig table (0x11)
   - EventMap table (0x12)
   - Event table (0x14)
   - PropertyMap table (0x15)
   - Property table (0x17)
   - MethodSemantics table (0x18)
   - MethodImpl table (0x19)
   - ModuleRef table (0x1A)
   - TypeSpec table (0x1B)
   - ImplMap table (0x1C)
   - FieldRVA table (0x1D)
   - Assembly table (0x20)
   - AssemblyRef table (0x23)
   - File table (0x26)
   - ExportedType table (0x27)
   - ManifestResource table (0x28)
   - NestedClass table (0x29)
   - GenericParam table (0x2A)
   - MethodSpec table (0x2B)
   - GenericParamConstraint table (0x2C)

2. **Metadata Streams**:
   - #Strings stream
   - #Blob stream
   - #GUID stream
   - #US stream

The metadata delta should only include changes to these tables and streams, not the entire metadata section.

#### IL Delta Format

The IL delta contains changes to method bodies and must follow the Common Intermediate Language (CIL) format. Each method body change includes:

1. **Method Header**:
   - Flags (2 bytes)
   - MaxStack (2 bytes)
   - CodeSize (4 bytes)
   - LocalVarSigTok (4 bytes)

2. **Method Body**:
   - CIL instructions
   - Exception handling clauses
   - Local variable signatures

The IL delta should only include the changed method bodies, not the entire IL section.

#### PDB Delta Format

The PDB delta contains changes to debug information and must follow the Portable PDB format. It includes:

1. **Document Table**:
   - Source file information
   - Language information
   - Hash information

2. **MethodDebugInformation Table**:
   - Sequence points
   - Local scopes
   - State machine information

3. **LocalScope Table**:
   - Variable information
   - Constant information
   - Import scope information

#### Generation Process

1. **Change Detection**:
   - Use F# Compiler Service to detect changes in source files
   - Track changes at the method and type level
   - Identify which metadata tables and streams are affected

2. **Delta Generation**:
   - For metadata deltas:
     - Compare metadata tables between old and new compilation
     - Generate minimal changes to affected tables
     - Update string, blob, and GUID streams as needed
   
   - For IL deltas:
     - Compare method bodies between old and new compilation
     - Generate new IL for changed methods
     - Preserve method tokens and signatures
   
   - For PDB deltas:
     - Compare debug information between old and new compilation
     - Update sequence points and local scopes
     - Preserve document and method debug information

3. **Delta Application**:
   - Use `MetadataUpdater.ApplyUpdate` to apply the deltas
   - Handle state preservation through `MetadataUpdateHandlerAttribute`
   - Clear reflection caches as needed
   - Update application state through `UpdateApplication` method

#### Implementation Notes

1. **Module Identification**:
   - Use `Module.ModuleVersionId` to identify modules
   - Each assembly typically has one module
   - Module IDs must be preserved across updates

2. **State Preservation**:
   - Use `MetadataUpdateHandlerAttribute` to mark types that handle updates
   - Implement `ClearCache` and `UpdateApplication` methods
   - Handle reflection-based caches appropriately

3. **Error Handling**:
   - Track `HotReloadResult` for each update
   - Handle rude edits appropriately
   - Provide detailed diagnostics for failures

4. **Performance Considerations**:
   - Generate minimal deltas
   - Preserve token mappings
   - Handle metadata table updates efficiently
   - Optimize IL generation for changed methods

   