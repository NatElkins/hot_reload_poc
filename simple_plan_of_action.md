# Simple Hot Reload POC Plan

## Goal
Implement a minimal proof of concept for F# hot reload that can handle changing a single integer value in a method return statement.

## Current State
- We have a simple test file `SimpleTest.fs` with a `getValue()` function returning 42
- Our current `DeltaGenerator.fs` implementation is too simplistic and doesn't properly handle deltas
- We need to focus on the most basic case first before tackling more complex scenarios

## Plan of Action

### 1. Simplify the Delta Format
- Focus only on IL delta generation for this POC
- Temporarily ignore metadata and PDB deltas
- Create a minimal delta structure that contains just the changed method's IL

### 2. Implement Basic IL Delta Generation
- Create a minimal IL delta containing just the changed method
- Generate proper IL for returning a constant integer
- Ensure the IL is in the format expected by `MetadataUpdater.ApplyUpdate`

### 3. Modify the DeltaGenerator
- Focus on improving the `generateILDelta` function
- Use `System.Reflection.Emit` to generate IL in memory
- Convert generated IL to the format expected by `MetadataUpdater`
- Handle proper method token preservation

### 4. Test the Implementation
- Create a test harness that:
  1. Loads the original assembly
  2. Calls `getValue()` to verify it returns 42
  3. Applies our IL delta
  4. Calls `getValue()` again to verify it returns the new value

## Success Criteria
- Successfully change the return value of `getValue()` from 42 to a different number
- Apply the change without restarting the application
- Verify the new value is returned when calling `getValue()`

## Next Steps After POC
- Add proper metadata delta generation
- Add proper PDB delta generation
- Handle more complex changes (multiple methods, type changes, etc.)
- Implement proper symbol matching and token preservation
- Add error handling and diagnostics 