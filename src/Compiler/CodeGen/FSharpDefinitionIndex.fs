module internal FSharp.Compiler.CodeGen.FSharpDefinitionIndex

open System.Collections.Generic

type private RowEntry<'T> =
    { RowId: int
      Item: 'T
      Added: bool }

/// Minimal analogue of Roslyn's DefinitionIndex<T>.
/// Tracks row ids for metadata definitions that are either reused from the baseline or
/// newly added in the current generation.
type FSharpDefinitionIndex<'T when 'T : equality>
    (tryGetExistingRowId: 'T -> int option, lastRowId: int) =

    let added = Dictionary<'T, int>()
    let rows = ResizeArray<RowEntry<'T>>()
    let mutable nextRowId = lastRowId
    let mutable frozen = false

    let ensureNotFrozen () =
        if frozen then invalidOp "Definition index has been frozen."

    let addRow rowId item addedFlag =
        rows.Add({ RowId = rowId; Item = item; Added = addedFlag })

    member _.Add(item: 'T) =
        ensureNotFrozen ()
        nextRowId <- nextRowId + 1
        added[item] <- nextRowId
        addRow nextRowId item true

    member _.AddExisting(item: 'T) =
        ensureNotFrozen ()
        match tryGetExistingRowId item with
        | Some rowId ->
            addRow rowId item false
        | None ->
            invalidOp "Existing row id not found for definition."

    member _.GetRowId(item: 'T) =
        match added.TryGetValue item with
        | true, rowId -> rowId
        | _ ->
            match tryGetExistingRowId item with
            | Some rowId -> rowId
            | None -> invalidOp "Row id not found for definition."

    member private _.Freeze() =
        if not frozen then
            frozen <- true
            rows.Sort(fun left right -> compare left.RowId right.RowId)

    member this.Rows =
        this.Freeze()
        rows
        |> Seq.map (fun entry -> struct (entry.RowId, entry.Item, entry.Added))
        |> Seq.toList

    member this.Added =
        this.Freeze()
        added
        |> Seq.map (fun kv -> kv.Key, kv.Value)
        |> Seq.toList
