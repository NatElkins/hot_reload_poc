module Counter

let mutable count = 0

let increment() =
    count <- count + 1
    count

let reset() =
    count <- 0
    count 