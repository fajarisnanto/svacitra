using System.Text.Json;

namespace PicasaRevive.Core;

public sealed record BackupSchedule(bool Enabled, int Minutes, string Folder);
public sealed record BackupReceipt(DateTimeOffset Completed, string File, int Slot);

public sealed class AutomaticBackups
{
    private const string ScheduleKey = "backup_schedule_v1";
    private const string ReceiptKey = "backup_receipt_v1";
    private readonly Catalog _catalog;
    private readonly string _libraryId;
    private readonly string _defaultFolder;
    private int _running;

    public AutomaticBackups(Catalog catalog, AppPaths paths)
    {
        _catalog = catalog;
        _defaultFolder = Path.Combine(paths.DataDir, "Backups");
        _libraryId = catalog.GetSetting("backup_library_id") ?? Guid.NewGuid().ToString("N");
        if (!Guid.TryParseExact(_libraryId, "N", out _)) _libraryId = Guid.NewGuid().ToString("N");
        catalog.SetSetting("backup_library_id", _libraryId);
    }

    public BackupSchedule Schedule => Read<BackupSchedule>(ScheduleKey) ?? new(true, 1440, _defaultFolder);
    public BackupReceipt? LastBackup => Read<BackupReceipt>(ReceiptKey);
    public string? LastError => _catalog.GetSetting("backup_error");

    private T? Read<T>(string key) where T : class
    {
        var json = _catalog.GetSetting(key);
        if (json is null) return null;
        return JsonSerializer.Deserialize<T>(json, Json.Options);
    }

    public void Configure(BackupSchedule schedule)
    {
        if (schedule.Minutes < 1 || schedule.Minutes > 525600)
            throw new ArgumentException("Choose an interval between one minute and 365 days.");
        if (!Catalog.IsValidPath(schedule.Folder) || !Path.IsPathFullyQualified(schedule.Folder))
            throw new ArgumentException("Choose a full path for the backup folder.");
        _catalog.SetSetting(ScheduleKey, Json.Serialize(schedule with { Folder = Path.GetFullPath(schedule.Folder) }));
        _catalog.SetSetting("backup_error", "");
    }

    // A completed snapshot is published atomically by BackUpLibrary. Rotate only this library's seven slots.
    internal void CheckDue(DateTimeOffset now)
    {
        if (Interlocked.Exchange(ref _running, 1) != 0) return;
        try
        {
            var schedule = Schedule;
            if (!schedule.Enabled) return;
            var last = LastBackup;
            var folder = Path.Combine(schedule.Folder, "PicasaRevive-" + _libraryId);
            bool sameFolder = last is not null && string.Equals(Path.GetDirectoryName(last.File), folder, StringComparison.OrdinalIgnoreCase);
            if (sameFolder && now < last!.Completed.AddMinutes(schedule.Minutes)) return;
            if (_catalog.Count() == 0 && _catalog.GetWatchedFolders().Count == 0) return;

            Directory.CreateDirectory(folder);
            var slot = sameFolder ? last!.Slot % 7 + 1 : 1;
            var file = Path.Combine(folder, $"Library-{slot}.prlibrary");
            _catalog.BackUpLibrary(file);
            _catalog.SetSetting(ReceiptKey, Json.Serialize(new BackupReceipt(now, file, slot)));
            _catalog.SetSetting("backup_error", "");
        }
        finally { Volatile.Write(ref _running, 0); }
    }

    public async Task RunAsync(CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            var delay = TimeSpan.FromSeconds(15);
            try { CheckDue(DateTimeOffset.UtcNow); }
            catch (Exception ex)
            {
                _catalog.SetSetting("backup_error", ex.Message);
                delay = TimeSpan.FromMinutes(1);
            }
            try { await Task.Delay(delay, stop).ConfigureAwait(false); }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { return; }
        }
    }
}
