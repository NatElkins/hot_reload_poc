#!/usr/bin/env dotnet fsi
#r "System.Reflection.Metadata"
open System
open System.IO
open System.Reflection.Metadata
open System.Reflection.Metadata.Ecma335
open System.Collections.Immutable

let usage () =
    printfn "Usage: dotnet fsi inspect_delta.fsx <metadata-path>"
    printfn "Example: dotnet fsi inspect_delta.fsx delta-dump/metadata.bin"

let args =
    Environment.GetCommandLineArgs()
    |> Array.skip 2

if args.Length <> 1 then
    usage ()
    Environment.Exit 1

let metadataPath = Path.GetFullPath args[0]
if not (File.Exists metadataPath) then
    eprintfn "error: metadata file not found: %s" metadataPath
    Environment.Exit 2

let bytes = File.ReadAllBytes metadataPath
let provider = MetadataReaderProvider.FromMetadataImage(ImmutableArray.CreateRange<byte>(bytes))
let reader = provider.GetMetadataReader()

printfn "Inspecting metadata delta: %s" metadataPath
printfn "  Metadata kind: %A" reader.MetadataKind

let heapSizes =
    [ "#Strings", HeapIndex.String
      "#Blob", HeapIndex.Blob
      "#GUID", HeapIndex.Guid
      "#US", HeapIndex.UserString ]

printfn "\nHeap sizes:"
for (name, heap) in heapSizes do
    let size = reader.GetHeapSize heap
    printfn "  %-8s %6d bytes" name size

printfn "\nTable row counts:"
for tableValue in 0 .. MetadataTokens.TableCount - 1 do
    let table = LanguagePrimitives.EnumOfValue<byte, TableIndex>(byte tableValue)
    let count = reader.GetTableRowCount table
    if count > 0 then
        printfn "  %-15A %5d" table count

printfn "\nEncLog entries: %d" (reader.GetTableRowCount TableIndex.EncLog)
let encLog = reader.GetEditAndContinueLogEntries() |> Seq.toArray
for entry in encLog do
    let token = MetadataTokens.GetToken(entry.Handle)
    printfn "  token=0x%08X operation=%A" token entry.Operation

printfn "\nEncMap entries: %d" (reader.GetTableRowCount TableIndex.EncMap)
let encMap = reader.GetEditAndContinueMapEntries() |> Seq.toArray
for handle in encMap do
    printfn "  token=0x%08X" (MetadataTokens.GetToken(handle))

printfn "\nDone."
provider.Dispose()
