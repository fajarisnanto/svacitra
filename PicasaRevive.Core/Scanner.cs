using System.Collections.Concurrent;
using System.Globalization;
using System.Threading.Channels;
using MetadataExtractor;
using MetadataExtractor.Formats.Exif;

namespace PicasaRevive.Core;

/// <summary>
/// Background library scanner. Folders are queued and scanned one at a time on worker threads,
/// unchanged files are skipped, and watched folders are kept in sync with a FileSystemWatcher.
/// </summary>
public sealed class Scanner : IDisposable
{
    private const int BatchSize = 48;

    private readonly Catalog _catalog;
    private readonly ImageOps _images;
    private readonly Channel<string> _queue = Channel.CreateUnbounded<string>();
    private readonly CancellationTokenSource _cts = new();
    private readonly object _statusLock = new();
    private readonly ScanStatus _status = new();
    private readonly Dictionary<string, FileSystemWatcher> _watchers = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> _pendingChanges = new(StringComparer.OrdinalIgnoreCase);
    private readonly Timer _debounce;
    private readonly Task _worker;
    private int _queued;

    public Scanner(Catalog catalog, ImageOps images)
    {
        _catalog = catalog;
        _images = images;
        _debounce = new Timer(_ => FlushPendingChanges(), null, Timeout.Infinite, Timeout.Infinite);
        _worker = Task.Run(WorkerLoopAsync);
    }

    public ScanStatus GetStatus()
    {
        lock (_statusLock)
        {
            return new ScanStatus
            {
                IsScanning = _status.IsScanning || Volatile.Read(ref _queued) > 0,
                CurrentFolder = _status.CurrentFolder,
                ProcessedCount = _status.ProcessedCount,
                TotalFound = _status.TotalFound,
                Errors = _status.Errors,
                Queued = Volatile.Read(ref _queued),
                LibraryVersion = _catalog.LibraryVersion,
            };
        }
    }

    /// <summary>Queues a folder for a background scan and starts watching it. Returns immediately.</summary>
    public bool ScanDirectoryAsync(string dirPath)
    {
        if (string.IsNullOrWhiteSpace(dirPath)) return false;
        Interlocked.Increment(ref _queued);
        _queue.Writer.TryWrite(Catalog.NormalizeDir(dirPath));
        return true;
    }

    /// <summary>Re-scans every watched folder (new, changed and deleted files).</summary>
    public int RescanAll()
    {
        int n = 0;
        foreach (var folder in _catalog.GetWatchedFolders())
            if (ScanDirectoryAsync(folder)) n++;
        return n;
    }

    /// <summary>Starts file watchers for every folder already in the catalog (call once at startup).</summary>
    public void StartWatching()
    {
        foreach (var folder in _catalog.GetWatchedFolders()) Watch(folder);
    }

    public void StopWatching(string folder)
    {
        var dir = Catalog.NormalizeDir(folder);
        lock (_watchers)
        {
            foreach (var key in _watchers.Keys.Where(k => k.Equals(dir, StringComparison.OrdinalIgnoreCase) ||
                                                          k.StartsWith(dir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)).ToList())
            {
                _watchers[key].Dispose();
                _watchers.Remove(key);
            }
        }
    }

    private async Task WorkerLoopAsync()
    {
        try
        {
            await foreach (var dir in _queue.Reader.ReadAllAsync(_cts.Token).ConfigureAwait(false))
            {
                try { ScanDirectory(dir); }
                catch (Exception) { lock (_statusLock) _status.Errors++; }
                finally { Interlocked.Decrement(ref _queued); }
            }
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>Synchronous scan of one folder tree. Public so tests (and the queue worker) can call it directly.</summary>
    public void ScanDirectory(string dirPath)
    {
        var root = Catalog.NormalizeDir(dirPath);
        if (!System.IO.Directory.Exists(root))
        {
            _catalog.SetOffline(_catalog.GetIndexUnder(root).Keys, true);
            StopWatching(root);
            return;
        }

        lock (_statusLock)
        {
            _status.IsScanning = true;
            _status.CurrentFolder = root;
            _status.ProcessedCount = 0;
            _status.TotalFound = 0;
            _status.Errors = 0;
        }

        try
        {
            _catalog.AddWatchedFolder(root);
            Watch(root);

            var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.System };
            var files = new List<string>();
            foreach (var f in System.IO.Directory.EnumerateFiles(root, "*", options))
            {
                if (_cts.IsCancellationRequested) return;
                if (ImageOps.SupportedExtensions.Contains(Path.GetExtension(f)) && !IsExcludedCachePath(f)) files.Add(f);
            }
            lock (_statusLock) _status.TotalFound = files.Count;

            var known = _catalog.GetIndexUnder(root);

            // Forget photos whose files disappeared since the last scan.
            var onDisk = new HashSet<string>(files, StringComparer.OrdinalIgnoreCase);
            var gone = known.Keys.Where(p => !onDisk.Contains(p)).ToList();
            _catalog.SetOffline(gone, true);
            _catalog.SetOffline(known.Where(kv => kv.Value.Offline && onDisk.Contains(kv.Key)).Select(kv => kv.Key).ToList(), false);

            var batch = new List<Photo>(BatchSize);
            var batchLock = new object();
            // Leave a core free so the UI and image serving stay responsive during big imports.
            var parallel = new ParallelOptions { MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount - 1, 1, 8), CancellationToken = _cts.Token };

            try
            {
                Parallel.ForEach(files, parallel, file =>
                {
                    try
                    {
                        var photo = IndexFile(file, known);
                        if (photo is not null)
                        {
                            List<Photo>? flush = null;
                            lock (batchLock)
                            {
                                batch.Add(photo);
                                if (batch.Count >= BatchSize) { flush = new List<Photo>(batch); batch.Clear(); }
                            }
                            if (flush is not null) _catalog.UpsertMany(flush);
                        }
                    }
                    catch (Exception)
                    {
                        lock (_statusLock) _status.Errors++;
                    }
                    finally
                    {
                        lock (_statusLock) _status.ProcessedCount++;
                    }
                });
            }
            catch (OperationCanceledException) { }

            if (batch.Count > 0) _catalog.UpsertMany(batch);
        }
        finally
        {
            lock (_statusLock)
            {
                _status.IsScanning = false;
                _status.CurrentFolder = "";
            }
        }
    }

    /// <summary>Builds the catalog record for one file, or null when it is already indexed and unchanged.</summary>
    /// <summary>Indexes one file right now (used for images the app itself just wrote) and returns its record.</summary>
    public Photo? IndexNow(string filePath)
    {
        var photo = IndexFile(filePath, null);
        return photo is null ? null : _catalog.Upsert(photo);
    }

    private Photo? IndexFile(string filePath, Dictionary<string, Catalog.IndexEntry>? known)
    {
        var full = Path.GetFullPath(filePath);
        var fi = new FileInfo(full);
        if (!fi.Exists || fi.Length == 0) return null;
        var modified = fi.LastWriteTime.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture);

        if (known is not null && known.TryGetValue(full, out var entry) &&
            entry.Size == fi.Length && entry.FileModified == modified &&
            !string.IsNullOrEmpty(entry.ThumbPath) && File.Exists(entry.ThumbPath) &&
            !ImageOps.LooksTruncated(entry.ThumbPath)) // cut-off thumbnails (partial cloud-drive reads) are redone
            return null;

        var (thumbPath, width, height) = _images.GenerateThumbnail(full);
        var (taken, make, model) = ReadExif(full);
        var date = taken ?? fi.LastWriteTime;

        return new Photo
        {
            FilePath = full,
            Filename = fi.Name,
            FolderPath = Catalog.NormalizeDir(fi.DirectoryName ?? ""),
            DateTaken = date.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture),
            Year = date.Year,
            Month = date.Month,
            FileModified = modified,
            FileSize = fi.Length,
            Width = width,
            Height = height,
            Make = make,
            Model = model,
            ThumbPath = thumbPath,
        };
    }

    public static (DateTime? Taken, string Make, string Model) ReadExif(string path)
    {
        try
        {
            var dirs = ImageMetadataReader.ReadMetadata(path);
            DateTime? taken = null;
            var sub = dirs.OfType<ExifSubIfdDirectory>().FirstOrDefault();
            if (sub is not null && sub.TryGetDateTime(ExifDirectoryBase.TagDateTimeOriginal, out var dto)) taken = dto;
            else if (sub is not null && sub.TryGetDateTime(ExifDirectoryBase.TagDateTimeDigitized, out var dtd)) taken = dtd;
            var ifd0 = dirs.OfType<ExifIfd0Directory>().FirstOrDefault();
            if (taken is null && ifd0 is not null && ifd0.TryGetDateTime(ExifDirectoryBase.TagDateTime, out var dt)) taken = dt;
            if (taken is { Year: < 1900 or > 2200 }) taken = null;
            return (taken, ifd0?.GetString(ExifDirectoryBase.TagMake)?.Trim() ?? "", ifd0?.GetString(ExifDirectoryBase.TagModel)?.Trim() ?? "");
        }
        catch (Exception)
        {
            return (null, "", "");
        }
    }

    // ------------------------------------------------------------------ file watching

    private void Watch(string folder)
    {
        lock (_watchers)
        {
            if (_watchers.ContainsKey(folder)) return;
            // A watcher on a parent folder already covers this one.
            if (_watchers.Keys.Any(k => folder.StartsWith(k + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))) return;
            try
            {
                var w = new FileSystemWatcher(folder)
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
                    InternalBufferSize = 64 * 1024,
                };
                w.Created += (_, e) => OnFileEvent(e.FullPath);
                w.Changed += (_, e) => OnFileEvent(e.FullPath);
                w.Deleted += (_, e) => OnFileEvent(e.FullPath);
                w.Renamed += (_, e) => { OnFileEvent(e.OldFullPath); OnFileEvent(e.FullPath); };
                w.Error += (_, _) => ScanDirectoryAsync(folder); // buffer overflow: fall back to a rescan
                w.EnableRaisingEvents = true;
                _watchers[folder] = w;
            }
            catch (Exception)
            {
                // Some locations (network shares, virtual drives) cannot be watched; manual rescan still works.
            }
        }
    }

    private bool IsExcludedCachePath(string path)
    {
        try
        {
            var full = Path.GetFullPath(path);
            var cacheDir = Catalog.NormalizeDir(_images.CacheDir);
            if (full.Equals(cacheDir, StringComparison.OrdinalIgnoreCase) ||
                full.StartsWith(cacheDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                return true;
            return full.Contains(Path.DirectorySeparatorChar + ".cache" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            return true;
        }
    }

    private void OnFileEvent(string path)
    {
        if (!ImageOps.SupportedExtensions.Contains(Path.GetExtension(path)) || IsExcludedCachePath(path)) return;
        _pendingChanges[path] = 0;
        try { _debounce.Change(1500, Timeout.Infinite); } catch (ObjectDisposedException) { }
    }

    private void FlushPendingChanges()
    {
        var paths = _pendingChanges.Keys.ToList();
        foreach (var p in paths) _pendingChanges.TryRemove(p, out _);
        if (paths.Count == 0 || _cts.IsCancellationRequested) return;

        var removed = new List<string>();
        var upserts = new List<Photo>();
        foreach (var path in paths)
        {
            try
            {
                if (!File.Exists(path)) { removed.Add(Path.GetFullPath(path)); continue; }
                var photo = IndexFile(path, null);
                if (photo is not null) upserts.Add(photo);
            }
            catch (IOException) { _pendingChanges[path] = 0; } // still being written: retry on the next tick
            catch (Exception) { }
        }
        if (removed.Count > 0) _catalog.SetOffline(removed, true);
        if (upserts.Count > 0) _catalog.UpsertMany(upserts);
        if (!_pendingChanges.IsEmpty)
        {
            try { _debounce.Change(3000, Timeout.Infinite); } catch (ObjectDisposedException) { }
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _queue.Writer.TryComplete();
        _debounce.Dispose();
        lock (_watchers)
        {
            foreach (var w in _watchers.Values) w.Dispose();
            _watchers.Clear();
        }
        try { _worker.Wait(TimeSpan.FromSeconds(3)); } catch (Exception) { }
        _cts.Dispose();
    }
}
