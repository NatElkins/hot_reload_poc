# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Build Commands
- Build solution: `dotnet build hot_reload_poc/HotReloadPoc.sln`
- Build HotReloadAgent: `dotnet build hot_reload_poc/src/HotReloadAgent/HotReloadAgent.fsproj`
- Build TestApp: `dotnet build hot_reload_poc/src/TestApp/TestApp.fsproj`
- Run TestApp: `dotnet run --project hot_reload_poc/src/TestApp/TestApp.fsproj`
- Run single test: `dotnet test hot_reload_poc/src/TestApp/TestApp.fsproj --filter "FullyQualifiedName~TestNameHere"`

## Code Style Guidelines
- **Naming**: Use PascalCase for types/modules, camelCase for functions/values/parameters
- **Indentation**: Use 4 spaces for indentation
- **Layout**: Keep line length under 120 characters
- **Documentation**: Use XML doc comments with `<summary>` for public APIs
- **Error Handling**: Use Result<'T, 'TError> or Option types for error cases
- **Imports**: Group imports by category (F# Core, System libraries, Project modules)
- **F# Features**: Prefer immutable types and pure functions when possible
- **Logging**: Use printfn with descriptive prefixes like "[DeltaGenerator]"

## Modules Organization
- Prelude.fs - Core utilities and common functions
- InMemoryCompiler.fs - F# compilation service integration
- FileWatcher.fs - File system change monitoring
- DeltaGenerator.fs - Hot reload delta generation
- HotReloadAgent.fs - Main hot reload implementation