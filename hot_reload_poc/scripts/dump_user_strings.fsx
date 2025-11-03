open System
open System.IO
open System.Text

let alignTo4 value =
    let remainder = value &&& 3
    if remainder = 0 then value else value + (4 - remainder)

let readCompressedUInt (reader: BinaryReader) =
    let first = reader.ReadByte()
    if first &&& 0x80uy = 0uy then
        int first
    elif first &&& 0xC0uy = 0x80uy then
        let second = reader.ReadByte()
        let value = ((int first &&& 0x3F) <<< 8) ||| int second
        value
    elif first &&& 0xE0uy = 0xC0uy then
        let b2 = reader.ReadByte()
        let b3 = reader.ReadByte()
        let b4 = reader.ReadByte()
        let value =
            ((int first &&& 0x1F) <<< 24)
            ||| (int b2 <<< 16)
            ||| (int b3 <<< 8)
            ||| int b4
        value
    else
        failwithf "Invalid compressed integer prefix: 0x%02X" first

let readMetadataStreams (reader: BinaryReader) =
    let signature = reader.ReadUInt32()
    if signature <> 0x424A5342u then
        failwithf "Invalid metadata signature: 0x%08X" signature

    let _major = reader.ReadUInt16()
    let _minor = reader.ReadUInt16()
    let _reserved = reader.ReadUInt32()
    let versionLength = reader.ReadUInt32() |> int
    let _version = reader.ReadBytes(versionLength)
    let padding = alignTo4 versionLength - versionLength
    if padding > 0 then reader.ReadBytes(padding) |> ignore
    let flags = reader.ReadUInt16()
    let streamCount = reader.ReadUInt16()

    [| for _ in 0 .. int streamCount - 1 ->
        let offset = reader.ReadUInt32()
        let size = reader.ReadUInt32()
        let nameBytes = ResizeArray<byte>()
        let mutable b = reader.ReadByte()
        while b <> 0uy do
            nameBytes.Add(b)
            b <- reader.ReadByte()
        let name = Encoding.ASCII.GetString(nameBytes.ToArray())
        let headerSize = nameBytes.Count + 1
        let padded = alignTo4 headerSize
        let skip = padded - headerSize
        if skip > 0 then reader.ReadBytes(skip) |> ignore
        name, offset, size |]

let loadUserStrings (path: string) =
    use stream = File.OpenRead(path)
    use reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen = false)
    let metadataStart = reader.BaseStream.Position
    let streams = readMetadataStreams reader
    let userStringStream =
        streams
        |> Array.tryFind (fun (name, _, _) -> name = "#US")
        |> Option.defaultWith (fun () -> failwith "Unable to locate #US heap in metadata.")

    let _, offset, size = userStringStream
    reader.BaseStream.Position <- metadataStart + int64 offset

    let encoding = Encoding.Unicode
    let heapStart = reader.BaseStream.Position |> int
    let heapEnd = heapStart + int size

    let rec gather acc =
        let current = reader.BaseStream.Position |> int
        if current >= heapEnd then
            List.rev acc
        else
            let localOffset = current - heapStart
            let rawLength = readCompressedUInt reader
            let stringByteCount = rawLength &&& 0xFFFFFFFE
            let hasSpecialChar = (rawLength &&& 1) = 1
            let bytes = reader.ReadBytes(stringByteCount)
            let _kind =
                if hasSpecialChar && reader.BaseStream.Position < int64 heapEnd then
                    reader.ReadByte()
                else
                    0uy
            let validLength = if stringByteCount % 2 = 0 then stringByteCount else stringByteCount - 1
            let value =
                if validLength <= 0 then
                    ""
                else
                    let chars = Array.zeroCreate<char> (validLength / 2)
                    let mutable idx = 0
                    let mutable byteIndex = 0
                    while byteIndex < validLength do
                        let code = (int bytes[byteIndex] <<< 8) ||| int bytes[byteIndex + 1]
                        chars[idx] <- char code
                        idx <- idx + 1
                        byteIndex <- byteIndex + 2
                    new string(chars, 0, idx)
            let token = 0x70000000 ||| localOffset
            let next = alignTo4 (reader.BaseStream.Position |> int)
            reader.BaseStream.Position <- int64 next
            gather ((token, value) :: acc)

    gather [] |> List.toArray

let printUserStrings (label: string) (entries: (int * string)[]) =
    printfn "%s (#US count = %d)" label entries.Length
    for token, value in entries do
        printfn "  0x%08X : %s" token value

match fsi.CommandLineArgs |> Array.toList with
| [] ->
    eprintfn "Usage: dotnet fsi dump_user_strings.fsx <metadata-or-dll> [<metadata-or-dll> ...]"
    exit 1
| _ :: [] ->
    eprintfn "Usage: dotnet fsi dump_user_strings.fsx <metadata-or-dll> [<metadata-or-dll> ...]"
    exit 1
| _ :: paths ->
    for path in paths do
        if File.Exists(path) then
            try
                printUserStrings path (loadUserStrings path)
            with ex ->
                eprintfn "Failed to read user strings from %s: %s" path ex.Message
        else
            eprintfn "File not found: %s" path
