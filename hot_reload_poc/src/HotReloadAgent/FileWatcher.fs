namespace HotReloadAgent

open System.IO

type FileChangeEvent = {
    FilePath: string
    ChangeType: WatcherChangeTypes
}

type FileWatcher = {
    Watcher: FileSystemWatcher
    ChangeHandler: FileChangeEvent -> unit
}

module FileWatcher =
    let create (path: string) (filter: string) (handler: FileChangeEvent -> unit) =
        let watcher = new FileSystemWatcher(path, filter)
        watcher.EnableRaisingEvents <- true
        watcher.IncludeSubdirectories <- true

        let onChanged (e: FileSystemEventArgs) =
            handler {
                FilePath = e.FullPath
                ChangeType = e.ChangeType
            }

        watcher.Changed.Add onChanged
        watcher.Created.Add onChanged
        watcher.Deleted.Add onChanged
        watcher.Renamed.Add (fun e -> 
            handler {
                FilePath = e.FullPath
                ChangeType = e.ChangeType
            })

        {
            Watcher = watcher
            ChangeHandler = handler
        }

    let dispose (watcher: FileWatcher) =
        watcher.Watcher.Dispose() 