# F# Expression Evaluator Architecture

## Introduction

This document outlines the high-level architecture and implementation plan for creating an F# Expression Evaluator. An Expression Evaluator (EE) is a component that allows debuggers in both Visual Studio and Visual Studio Code to evaluate expressions in a specific language at runtime. This is a prerequisite for implementing Hot Reload functionality for F#.

## Background

Modern debuggers use Expression Evaluators to:
- Evaluate watch expressions in the debugger windows
- Format values and types in the language's syntax
- Display stack frames in the call stack window
- Format method names in the breakpoints window
- Support breakpoint conditions and more

Currently, F# debugging uses the C# Expression Evaluator, which provides basic functionality but lacks F#-specific features and syntax.

## Architecture Overview

The F# Expression Evaluator will follow the CLR Expression Evaluator model as documented in the Concord Extensibility framework. The MVP architecture consists of these key components:

1. **Expression Compiler (MVP)**
   - Implements `IDkmClrExpressionCompiler`
   - Responsible for taking F# expressions as text and compiling them into IL code
   - Handles basic error reporting
   - Manages simple compilation context

2. **Result Formatter (MVP)**
   - Implements `IDkmClrFormatter`
   - Formats CLR values into F# syntax (e.g., proper type names, string literals)
   - Provides basic visualization for F# types

3. **Configuration and Registration (MVP)**
   - vsdconfig files to register the components with Concord
   - Basic Visual Studio integration

### Post-MVP Components

1. **Frame Decoder**
   - Implements `IDkmLanguageFrameDecoder`
   - Formats stack frame information in F# style
   - Provides return type information for methods

2. **Metadata Context**
   - Manages F# compilation context
   - Handles module and type resolution
   - Provides access to assembly references

3. **Cross-Platform Support**
   - VS Code integration
   - Cross-platform packaging
   - Platform-specific optimizations

## Detailed Component Design

### Expression Compiler Implementation (MVP)

Based on the Roslyn implementation, the F# Expression Compiler needs to:

1. **Interface Implementation**
   - Implement `IDkmClrExpressionCompiler` as the primary interface
   - Basic error handling and reporting

2. **Core Functionality**
   - `CompileExpression`: Main method for compiling F# expressions
   - `GetClrLocalVariableQuery`: Retrieve local variables in the current scope
   - Basic error handling and diagnostic reporting

### Evaluation Context (MVP)

The F# Expression Evaluator needs an evaluation context that:

1. **Manages Compilation Environment**
   - Creates and manages F# compilation instances
   - Handles basic symbol resolution
   - Supports essential compilation flags

2. **Provides Methods For**
   - Expression compilation
   - Local variable discovery
   - Basic type resolution

### Parser and Syntax Support (MVP)

Based on the Roslyn implementation, we need to:

1. **Create Basic Parser**
   - Parser for simple F# expressions
   - Basic error reporting

2. **Handle Basic F# Syntax**
   - Support for simple expressions
   - Basic type resolution
   - Essential operators

### Result Handling and Compilation (MVP)

1. **Compilation Pipeline**
   - Parse F# expressions into syntax trees
   - Compile syntax into valid IL
   - Generate method bodies for evaluation
   - Basic error handling

2. **Result Properties**
   - Track basic result categories
   - Handle access types
   - Manage storage types

## Post-MVP Features

### Advanced Compilation Support
- Robust retry mechanism for compilation failures
- Support for dynamic assembly references
- Metadata caching for performance
- Advanced error handling and diagnostic reporting

### Advanced F# Features
- Pattern matching expressions
- Discriminated unions
- Computation expressions
- Custom operators
- Type providers
- Units of measure

### Performance Optimizations
- Metadata caching
- Assembly reference management
- Incremental compilation
- Memory optimization

### Enhanced Formatting
- Specialized F# type formatting
- Custom visualization
- Advanced type name resolution
- Format specifiers

## Testing Strategy

### MVP Testing
1. **Unit Tests**
   - Basic expression compilation
   - Simple value formatting
   - Error handling

2. **Integration Tests**
   - Basic VS debugging scenarios
   - Simple watch window evaluation
   - Local variable inspection

### Post-MVP Testing
1. **Advanced Unit Tests**
   - Complex F# constructs
   - Performance benchmarks
   - Cross-platform compatibility

2. **Integration Tests**
   - VS Code integration
   - Advanced debugging scenarios
   - Cross-platform testing

## Project Structure

### MVP Structure
```
src/Compiler/FSharp.Compiler.ExpressionEvaluator/
├── ExpressionCompiler/
│   ├── FSharpExpressionCompiler.fs       # Main entry point implementing IDkmClrExpressionCompiler
│   ├── EvaluationContext.fs              # Basic context for expression compilation
│   └── CompilationContext.fs             # Simple compilation settings and state
├── Formatter/
│   └── FSharpFormatter.fs                # Basic implementation of IDkmClrFormatter
├── Common/
│   ├── SharedTypes.fs                    # Common type definitions
│   └── DiagnosticFormatter.fs            # Basic error formatting
├── Configuration/
│   └── FSharpExpressionCompiler.vsdconfigxml # Visual Studio integration
└── FSharp.Compiler.ExpressionEvaluator.fsproj
```

### Current Implementation Status
- ✅ Project structure created
- ✅ Basic project files and dependencies set up
- ✅ Core interfaces defined
- ✅ Basic type definitions implemented
- ✅ Initial diagnostic formatting support
- ⏳ Expression compilation implementation in progress
- ⏳ Visual Studio integration configuration in progress

### Post-MVP Structure
Additional directories and files will be added as needed for advanced features.

## Resources

- Concord Extensibility Samples wiki documentation
- F# compiler codebase (for understanding F# compiler services)
- Roslyn Expression Evaluator implementation (as reference)
- Iris sample expression evaluator from ConcordExtensibilitySamples

## Current Challenges

### Dependency Resolution
- Need to resolve version conflicts with Microsoft.VisualStudio.Debugger.Engine
- Current build errors related to missing or mismatched package versions
- Need to ensure consistent package versions across all dependencies

### Build Configuration
- Visual Studio integration configuration (vsdconfig) needs to be properly set up
- Project references need to be correctly configured in both solutions
- Need to ensure proper build order and dependencies

### Implementation Notes
- Current focus is on resolving build errors and dependency issues
- Need to ensure proper namespace usage for Visual Studio debugger interfaces
- Need to verify F# compiler service integration is working correctly

## Next Steps

1. ~~Study the Iris sample in detail to understand the implementation patterns~~
2. ~~Create core interfaces and project structure~~
3. Implement basic expression evaluation functionality
   - Complete `FSharpExpressionCompiler.fs` implementation
   - Add support for basic F# expressions
   - Implement error handling and diagnostics
4. Add basic error handling and diagnostics
   - Enhance diagnostic formatter
   - Add comprehensive error reporting
5. Develop and run test suite
   - Create unit tests for expression evaluation
   - Add integration tests for VS debugging
6. Integrate with Visual Studio
   - Complete vsdconfig setup
   - Test in Visual Studio environment 