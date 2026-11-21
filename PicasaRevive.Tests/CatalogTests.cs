using System.Text.Json.Nodes;
using PicasaRevive.Core;

namespace PicasaRevive.Tests;

public class CatalogTests : IDisposable
{
    private readonly TempDir _tmp = new();
    private readonly string _dbFile;
    private Catalog _catalog;

    public CatalogTests()
    {
        _dbFile = Path.Combine(_tmp.Path, "picasa_data.db");
        _catalog = new Catalog(_dbFile);
    }

    public void Dispose()
    {
        _catalog.SaveAiSettings(new AiSettings());
        _catalog.Dispose();
        _tmp.Dispose();
    }

    private static Photo MakePhoto(string folder, string name, int year, int month, int day = 1) => new()
    {
        FilePath = Path.Combine(folder, name),
        Filename = name,
        FolderPath = folder,
        DateTaken = $"{year:0000}-{month:00}-{day:00}T12:00:00",
        Year = year,
        Month = month,
        FileModified = $"{year:0000}-{month:00}-{day:00}T12:00:00",
        FileSize = 1234,
        Width = 800,
        Height = 600,
    };

    private void Seed()
    {
        _catalog.UpsertMany(new[]
        {
            MakePhoto(@"C:\Photos\Summer", "beach.jpg", 2024, 8, 15),
            MakePhoto(@"C:\Photos\Summer", "lake.jpg", 2024, 8, 14),
            MakePhoto(@"C:\Photos\Winter", "snow.jpg", 2023, 12, 25),
            MakePhoto(@"C:\Photos\Winter", "cabin.png", 2023, 12, 24),
            MakePhoto(@"C:\Photos\Winter\Sub", "pine.jpg", 2023, 11, 2),
        });
    }

    [Fact]
    public void Query_DefaultLimitIs50000_AndSortsNewestFirst()
    {
        Seed();
        Assert.Equal(50_000, PhotoQuery.DefaultLimit);

        var (photos, total) = _catalog.Query(new PhotoQuery());

        Assert.Equal(5, total);
        Assert.Equal(new[] { "beach.jpg", "lake.jpg", "snow.jpg", "cabin.png", "pine.jpg" }, photos.Select(p => p.Filename));
    }

    [Fact]
    public void Query_ZeroOrNegativeLimit_FallsBackToDefault()
    {
        Seed();
        var (photos, _) = _catalog.Query(new PhotoQuery { Limit = 0 });
        Assert.Equal(5, photos.Count);
    }

    [Fact]
    public void Query_FolderIsExactAndCaseInsensitive()
    {
        Seed();
        var (winter, total) = _catalog.Query(new PhotoQuery { Folder = @"c:\photos\WINTER" });

        Assert.Equal(2, total); // the sub-folder is its own folder
        Assert.All(winter, p => Assert.Equal(@"C:\Photos\Winter", p.FolderPath));
    }

    [Fact]
    public void Query_LimitAndOffset_PageThroughResults_TotalStaysFull()
    {
        Seed();
        var (page, total) = _catalog.Query(new PhotoQuery { Limit = 2, Offset = 2 });

        Assert.Equal(5, total);
        Assert.Equal(new[] { "snow.jpg", "cabin.png" }, page.Select(p => p.Filename));
    }

    [Fact]
    public void Query_YearAndMonthFilters()
    {
        Seed();
        Assert.Equal(3, _catalog.Query(new PhotoQuery { Year = 2023 }).Total);
        Assert.Equal(2, _catalog.Query(new PhotoQuery { Year = 2023, Month = 12 }).Total);
        Assert.Equal(0, _catalog.Query(new PhotoQuery { Year = 1999 }).Total);
    }

    [Fact]
    public void Query_StarredOnly_AndToggleStar()
    {
        Seed();
        var id = _catalog.Query(new PhotoQuery { Search = "lake" }).Photos.Single().Id;

        Assert.Equal(1, _catalog.ToggleStar(id));
        Assert.Equal("lake.jpg", _catalog.Query(new PhotoQuery { StarredOnly = true }).Photos.Single().Filename);
        Assert.Equal(1, _catalog.GetSummary().TotalStarred);

        Assert.Equal(0, _catalog.ToggleStar(id));
        Assert.Equal(0, _catalog.Query(new PhotoQuery { StarredOnly = true }).Total);
        Assert.Null(_catalog.ToggleStar(987654));
    }

    [Fact]
    public void Query_SearchMatchesFilenameFolderCaptionAndTags()
    {
        Seed();
        var snow = _catalog.Query(new PhotoQuery { Search = "snow" }).Photos.Single();
        _catalog.UpdatePhotoAi(snow.Id, "A frosty morning by the river", new[] { "frost", "evergreen" }, new Dictionary<string, int> { ["contrast"] = 10 });

        Assert.Equal(2, _catalog.Query(new PhotoQuery { Search = "summer" }).Total);          // folder
        Assert.Equal(1, _catalog.Query(new PhotoQuery { Search = "CABIN" }).Total);            // filename, case-insensitive
        Assert.Equal(1, _catalog.Query(new PhotoQuery { Search = "river" }).Total);            // AI caption
        Assert.Equal(1, _catalog.Query(new PhotoQuery { Search = "evergreen" }).Total);        // AI tag
        Assert.Equal(1, _catalog.Query(new PhotoQuery { Search = "winter snow" }).Total);      // all terms must match
        Assert.Equal(0, _catalog.Query(new PhotoQuery { Search = "100%_nothing" }).Total);     // LIKE wildcards are escaped
    }

    [Fact]
    public void UpdatePhotoAi_MarksProcessed_AndFeedsAiTaggedFilter()
    {
        Seed();
        var p = _catalog.Query(new PhotoQuery { Search = "beach" }).Photos.Single();
        Assert.True(_catalog.UpdatePhotoAi(p.Id, "Sunset", new[] { "sunset", "beach" }, new Dictionary<string, int> { ["warmth"] = 14 }));

        var reloaded = _catalog.GetPhoto(p.Id)!;
        Assert.True(reloaded.AiProcessed);
        Assert.Equal("Sunset", reloaded.AiCaption);
        Assert.Equal(new[] { "sunset", "beach" }, reloaded.AiTags);
        Assert.Equal(14, reloaded.AiFixes["warmth"]);
        Assert.Equal(1, _catalog.GetSummary().AiTaggedCount);
        Assert.Equal(1, _catalog.Query(new PhotoQuery { AiTaggedOnly = true }).Total);
        Assert.Equal(4, _catalog.GetAllIds(onlyUntagged: true).Count);
    }

    [Fact]
    public void Rotate_WrapsAround360()
    {
        Seed();
        var id = _catalog.Query(new PhotoQuery()).Photos[0].Id;
        Assert.Equal(90, _catalog.Rotate(id, 90));
        Assert.Equal(0, _catalog.Rotate(id, -90));
        Assert.Equal(270, _catalog.Rotate(id, -90));
        Assert.Equal(0, _catalog.Rotate(id, 90));
        Assert.Null(_catalog.Rotate(424242, 90));
    }

    [Fact]
    public void SaveEdits_PersistsAcrossReopen_AndUpsertKeepsUserData()
    {
        Seed();
        var p = _catalog.Query(new PhotoQuery { Search = "beach" }).Photos.Single();
        var edits = new JsonObject { ["brightness"] = 12, ["effect"] = "sepia", ["crop"] = new JsonArray(0.1, 0.1, 0.8, 0.8), ["feeling_lucky"] = true };
        Assert.True(_catalog.SaveEdits(p.Id, edits));
        _catalog.ToggleStar(p.Id);

        // A rescan upserts the same path again: stars and edits must survive.
        _catalog.UpsertMany(new[] { MakePhoto(@"C:\Photos\Summer", "beach.jpg", 2024, 8, 15) });

        _catalog.Dispose();
        _catalog = new Catalog(_dbFile);

        var reloaded = _catalog.GetPhoto(p.Id)!;
        Assert.Equal(1, reloaded.IsStarred);
        Assert.True(reloaded.HasEdits);
        var recipe = EditRecipe.From(reloaded.Edits);
        Assert.Equal(12, recipe.Brightness);
        Assert.Equal("sepia", recipe.Effect);
        Assert.True(recipe.FeelingLucky);
        Assert.Equal(new[] { 0.1, 0.1, 0.8, 0.8 }, recipe.Crop);
        Assert.Equal(5, _catalog.Count());
    }

    [Fact]
    public void Summary_GroupsFoldersAndTimeline()
    {
        Seed();
        var s = _catalog.GetSummary();

        Assert.Equal(5, s.TotalPhotos);
        Assert.Equal(3, s.Folders.Count);
        Assert.Equal(2, s.Folders.Single(f => f.FolderPath == @"C:\Photos\Summer").Count);
        Assert.Equal(new TimelineSummary(2024, 8, 2), s.Timeline[0]);
        Assert.Equal(3, s.Timeline.Count);
    }

    [Fact]
    public void RemoveFolder_ForgetsPhotosInTheTree_ButNotSiblings()
    {
        Seed();
        _catalog.AddWatchedFolder(@"C:\Photos\Winter");
        _catalog.AddWatchedFolder(@"C:\Photos\Summer");
        long before = _catalog.LibraryVersion;

        Assert.Equal(3, _catalog.RemoveFolder(@"C:\Photos\Winter"));

        Assert.Equal(2, _catalog.Count());
        Assert.Equal(new[] { @"C:\Photos\Summer" }, _catalog.GetWatchedFolders());
        Assert.True(_catalog.LibraryVersion > before);
    }

    [Fact]
    public void AiSettings_DefaultsAndPersistence()
    {
        var defaults = _catalog.GetAiSettings();
        Assert.Equal("gpt-image-2.5-flare", defaults.Model);
        Assert.Equal("https://api.openai.com/v1", defaults.Endpoint);
        Assert.Equal("", defaults.ApiKey);

        _catalog.SaveAiSettings(new AiSettings { ApiKey = " sk-test ", Endpoint = "", Model = "" });
        _catalog.Dispose();
        _catalog = new Catalog(_dbFile);

        var saved = _catalog.GetAiSettings();
        Assert.Equal("sk-test", saved.ApiKey);
        Assert.Equal("gpt-image-2.5-flare", saved.Model);            // empty falls back to the default
        Assert.Equal("https://api.openai.com/v1", saved.Endpoint);
    }

    [Fact]
    public void LegacyAiKey_MigratesOnOpen_AndClearingRemovesRegistryValue()
    {
        const string dummy = "migration-test-value";
        _catalog.SetSetting("ai_settings", Json.Serialize(new AiSettings { ApiKey = dummy }));
        _catalog.Dispose();
        _catalog = new Catalog(_dbFile);
        Assert.Equal(dummy, _catalog.GetAiSettings().ApiKey);
        var record = Json.ParseObject(_catalog.GetSetting("ai_settings"));
        Assert.False(record.ContainsKey("api_key"));
        var id = (string)record["credential_id"]!;
        using var registry = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\PicasaRevive\AiKeys")!;
        var name = Assert.Single(registry.GetValueNames(), value => value.StartsWith(id + "_"));
        Assert.StartsWith("dpapi:", Assert.IsType<string>(registry.GetValue(name)));
        Assert.Throws<ArgumentException>(() => _catalog.ResolveAiSettings(new AiSettings
        {
            ApiKey = AiSettings.MaskedKey, Endpoint = "https://new.example"
        }));
        _catalog.SaveAiSettings(new AiSettings());
        Assert.Null(registry.GetValue(name));
        Assert.Empty(_catalog.GetAiSettings().ApiKey);
    }

    [Fact]
    public void Recent_OrdersByAddedTime()
    {
        var older = MakePhoto(@"C:\P", "old_but_added_last.jpg", 2001, 1);
        older.AddedAt = "2026-02-01T00:00:00Z";
        var newer = MakePhoto(@"C:\P", "new_but_added_first.jpg", 2025, 1);
        newer.AddedAt = "2026-01-01T00:00:00Z";
        _catalog.UpsertMany(new[] { older, newer });

        Assert.Equal("new_but_added_first.jpg", _catalog.Query(new PhotoQuery()).Photos[0].Filename);
        Assert.Equal("old_but_added_last.jpg", _catalog.Query(new PhotoQuery { Recent = true }).Photos[0].Filename);
    }
    [Fact]
    public void BackupRestore_ReconnectsFolders_AndPreservesOtherRecords()
    {
        var old = _tmp.Sub("old");
        var next = _tmp.Sub("new");
        _catalog.AddWatchedFolder(old);
        var input = _catalog.Upsert(MakePhoto(old, "one.jpg", 2020, 1));
        _catalog.ToggleStar(input.Id);
        _catalog.SaveEdits(input.Id, new JsonObject { ["brightness"] = 12 });
        _catalog.SetSetting("excluded-test", "never-write-this-marker");
        var file = Path.Combine(_tmp.Path, "backup.prlibrary");
        _catalog.BackUpLibrary(file);
        Assert.DoesNotContain("never-write-this-marker", File.ReadAllText(file));
        using var copy = new Catalog(Path.Combine(_tmp.Path, "copy.db"));
        copy.Upsert(MakePhoto(next, "other.jpg", 2020, 1));
        var snapshot = LibraryBackup.Read(file);
        var remap = new Dictionary<string,string> { [old] = next };
        copy.RestoreLibrary(snapshot, remap);
        copy.RestoreLibrary(snapshot, remap);
        Assert.Equal(2, copy.Count());
        var photo = copy.GetPhotoByPath(Path.Combine(next, "one.jpg"))!;
        Assert.Equal(1, photo.IsStarred);
        Assert.Equal(12, (int)photo.Edits["brightness"]!);
        Assert.Contains(next, copy.GetWatchedFolders());
    }

    [Fact]
    public void AutomaticBackups_RespectIntervalRestartDisableAndRetention()
    {
        var paths = new AppPaths(_tmp.Path);
        var scheduler = new AutomaticBackups(_catalog, paths);
        var start = DateTimeOffset.UtcNow;
        Assert.True(scheduler.Schedule.Enabled);
        Assert.Equal(1440, scheduler.Schedule.Minutes);
        scheduler.Configure(new(true, 10, _tmp.Sub("backups")));
        var photo = _catalog.Upsert(MakePhoto(_tmp.Path, "test.jpg", 2024, 1));
        scheduler.CheckDue(start);
        var first = scheduler.LastBackup!;
        Assert.Single(LibraryBackup.Read(first.File).Photos);
        _catalog.ToggleStar(photo.Id);
        scheduler = new AutomaticBackups(_catalog, paths);
        scheduler.CheckDue(start.AddMinutes(9));
        Assert.Equal(first, scheduler.LastBackup);
        scheduler.CheckDue(start.AddDays(2));
        var second = scheduler.LastBackup!;
        Assert.Equal(2, second.Slot);
        Assert.Equal(1, LibraryBackup.Read(second.File).Photos.Single().IsStarred);
        for (int i = 1; i <= 8; i++) scheduler.CheckDue(start.AddDays(2).AddMinutes(i * 10));
        Assert.Equal(7, Directory.GetFiles(Path.GetDirectoryName(first.File)!, "*.prlibrary").Length);
        var last = scheduler.LastBackup;
        scheduler.Configure(scheduler.Schedule with { Enabled = false });
        scheduler.CheckDue(start.AddDays(5));
        Assert.Equal(last, scheduler.LastBackup);
    }

    [Fact]
    public void AutomaticBackups_RetryFailedDestinationWithoutAdvancingSchedule()
    {
        var scheduler = new AutomaticBackups(_catalog, new AppPaths(_tmp.Path));
        var folder = Path.Combine(_tmp.Path, "blocked");
        File.WriteAllText(folder, "test obstruction");
        scheduler.Configure(new(true, 1, folder));
        _catalog.AddWatchedFolder(_tmp.Path);
        var now = DateTimeOffset.UtcNow;
        Assert.ThrowsAny<IOException>(() => scheduler.CheckDue(now));
        Assert.Null(scheduler.LastBackup);
        File.Delete(folder);
        scheduler.CheckDue(now.AddMinutes(1));
        Assert.True(File.Exists(scheduler.LastBackup!.File));
    }

}
