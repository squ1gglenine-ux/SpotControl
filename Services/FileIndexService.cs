using System.IO;
using SpotCont.Models;

namespace SpotCont.Services;

public sealed class FileIndexService : IDisposable
{
    private static readonly HashSet<string> ThumbnailExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg",
        ".jpeg",
        ".png",
        ".bmp",
        ".gif",
        ".webp",
        ".tif",
        ".tiff"
    };

    private static readonly HashSet<string> SkippedDirectoryNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git",
        ".svn",
        "node_modules",
        "bin",
        "obj"
    };

    private readonly object _syncRoot = new();
    private readonly object _publishSyncRoot = new();
    private readonly Dictionary<string, IndexedItem> _items = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<FileSystemWatcher> _watchers = new();
    private IndexedItem[] _snapshot = Array.Empty<IndexedItem>();
    private Timer? _publishTimer;
    private int _started;

    public event EventHandler? IndexUpdated;

    public IReadOnlyList<IndexedItem> GetSnapshot()
    {
        return Volatile.Read(ref _snapshot);
    }

    public void StartBackgroundIndexing()
    {
        if (Interlocked.Exchange(ref _started, 1) == 1)
        {
            return;
        }

        var roots = GetIndexedRoots();
        foreach (var root in roots)
        {
            var rootItem = TryCreateItem(root);
            if (rootItem is not null)
            {
                _items[rootItem.Id] = rootItem;
            }
        }

        PublishSnapshot();
        CreateWatchers(roots);
        _ = Task.Run(() => BuildInitialSnapshot(roots));
    }

    public void Dispose()
    {
        lock (_publishSyncRoot)
        {
            _publishTimer?.Dispose();
            _publishTimer = null;
        }

        foreach (var watcher in _watchers)
        {
            watcher.Dispose();
        }

        _watchers.Clear();
    }

    private void BuildInitialSnapshot(IReadOnlyList<string> roots)
    {
        var publishCounter = 0;

        foreach (var root in roots)
        {
            foreach (var path in EnumerateFileSystemEntriesSafe(root))
            {
                var item = TryCreateItem(path);
                if (item is null)
                {
                    continue;
                }

                lock (_syncRoot)
                {
                    _items[item.Id] = item;
                }

                publishCounter++;
                if (publishCounter % 500 == 0)
                {
                    SchedulePublish();
                }
            }
        }

        SchedulePublish();
    }

    private void CreateWatchers(IReadOnlyList<string> roots)
    {
        foreach (var root in roots)
        {
            try
            {
                var watcher = new FileSystemWatcher(root)
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite
                };

                watcher.Created += (_, args) => QueueRefresh(args.FullPath);
                watcher.Changed += (_, args) => QueueRefresh(args.FullPath);
                watcher.Deleted += (_, args) => QueueRemove(args.FullPath);
                watcher.Renamed += (_, args) =>
                {
                    QueueRemove(args.OldFullPath);
                    QueueRefresh(args.FullPath);
                };
                watcher.Error += (_, _) => _ = Task.Run(() => BuildInitialSnapshot(new[] { root }));
                watcher.EnableRaisingEvents = true;

                _watchers.Add(watcher);
            }
            catch
            {
            }
        }
    }

    private void QueueRefresh(string path)
    {
        var item = TryCreateItem(path);
        lock (_syncRoot)
        {
            if (item is null)
            {
                _items.Remove(GetItemId(path));
            }
            else
            {
                _items[item.Id] = item;
            }
        }

        SchedulePublish();
    }

    private void QueueRemove(string path)
    {
        lock (_syncRoot)
        {
            _items.Remove(GetItemId(path));
        }

        SchedulePublish();
    }

    private void PublishSnapshot()
    {
        lock (_syncRoot)
        {
            _snapshot = _items.Values
                .OrderBy(item => item.Kind == SearchResultKind.Folder ? 0 : 1)
                .ThenByDescending(item => item.LastModifiedUtc ?? DateTimeOffset.MinValue)
                .ThenBy(item => item.Title, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        IndexUpdated?.Invoke(this, EventArgs.Empty);
    }

    private void SchedulePublish()
    {
        lock (_publishSyncRoot)
        {
            if (_publishTimer is null)
            {
                _publishTimer = new Timer(
                    static state => ((FileIndexService)state!).PublishSnapshotFromTimer(),
                    this,
                    120,
                    Timeout.Infinite);
                return;
            }

            _publishTimer.Change(120, Timeout.Infinite);
        }
    }

    private void PublishSnapshotFromTimer()
    {
        try
        {
            PublishSnapshot();
        }
        catch
        {
        }
    }

    private static IReadOnlyList<string> GetIndexedRoots()
    {
        var userProfilePath = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        return new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            Path.Combine(userProfilePath, "Downloads")
        }
        .Where(path => !string.IsNullOrWhiteSpace(path) && Directory.Exists(path))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();
    }

    private static IEnumerable<string> EnumerateFileSystemEntriesSafe(string rootPath)
    {
        var pendingDirectories = new Stack<string>();
        pendingDirectories.Push(rootPath);

        while (pendingDirectories.Count > 0)
        {
            var currentDirectory = pendingDirectories.Pop();
            IEnumerable<string> directories = Array.Empty<string>();
            IEnumerable<string> files = Array.Empty<string>();

            try
            {
                directories = Directory.EnumerateDirectories(currentDirectory);
                files = Directory.EnumerateFiles(currentDirectory);
            }
            catch
            {
                continue;
            }

            foreach (var directory in directories)
            {
                if (ShouldSkipDirectory(directory))
                {
                    continue;
                }

                yield return directory;
                pendingDirectories.Push(directory);
            }

            foreach (var file in files)
            {
                if (ShouldSkipFile(file))
                {
                    continue;
                }

                yield return file;
            }
        }
    }

    private static IndexedItem? TryCreateItem(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            if (Directory.Exists(path))
            {
                if (ShouldSkipDirectory(path))
                {
                    return null;
                }

                var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(path));
                if (string.IsNullOrWhiteSpace(name))
                {
                    name = path;
                }

                return new IndexedItem(
                    GetItemId(path),
                    name,
                    path,
                    path,
                    SearchResultKind.Folder,
                    $"{name} {path}",
                    name,
                    Directory.GetLastWriteTimeUtc(path),
                    Bucket: SearchResultBucket.Folder,
                    ResolvedTarget: path,
                    ContainingDirectoryPath: Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(path)),
                    SearchInternetText: name,
                    CopyText: path);
            }

            if (!File.Exists(path) || ShouldSkipFile(path))
            {
                return null;
            }

            var title = Path.GetFileName(path);
            if (string.IsNullOrWhiteSpace(title))
            {
                return null;
            }

            return new IndexedItem(
                GetItemId(path),
                title,
                Path.GetDirectoryName(path) ?? path,
                path,
                SearchResultKind.File,
                $"{title} {path}",
                title,
                File.GetLastWriteTimeUtc(path),
                Bucket: SearchResultBucket.File,
                ResolvedTarget: path,
                ContainingDirectoryPath: Path.GetDirectoryName(path),
                UsesThumbnailPreview: ThumbnailExtensions.Contains(Path.GetExtension(path)),
                SearchInternetText: Path.GetFileNameWithoutExtension(title),
                CopyText: path);
        }
        catch
        {
            return null;
        }
    }

    private static string GetItemId(string path)
    {
        return $"file::{path}";
    }

    private static bool ShouldSkipDirectory(string directoryPath)
    {
        var directoryName = Path.GetFileName(Path.TrimEndingDirectorySeparator(directoryPath));
        if (SkippedDirectoryNames.Contains(directoryName))
        {
            return true;
        }

        try
        {
            var attributes = File.GetAttributes(directoryPath);
            return attributes.HasFlag(FileAttributes.Hidden) ||
                attributes.HasFlag(FileAttributes.System) ||
                attributes.HasFlag(FileAttributes.ReparsePoint);
        }
        catch
        {
            return true;
        }
    }

    private static bool ShouldSkipFile(string filePath)
    {
        try
        {
            var attributes = File.GetAttributes(filePath);
            return attributes.HasFlag(FileAttributes.Hidden) || attributes.HasFlag(FileAttributes.System);
        }
        catch
        {
            return true;
        }
    }
}
