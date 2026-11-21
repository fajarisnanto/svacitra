using Microsoft.Win32;
using System.Security.Cryptography;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;

namespace PicasaRevive.Core;

/// <summary>
/// SQLite-backed photo catalog (picasa_data.db): photos, watched folders and settings.
/// All access goes through one connection guarded by a lock; operations are short.
/// </summary>
public sealed partial class Catalog : IDisposable
{
    private const string Columns =
        "id, path, filename, folder, size, width, height, year, month, date_taken, file_modified, make, model, " +
        "is_starred, rotation, edits, ai_processed, ai_caption, ai_tags, ai_fixes, thumb_path, added_at, offline";

    private readonly SqliteConnection _db;
    private readonly object _lock = new();
    private long _libraryVersion;

    /// <summary>Bumped when photos are added or removed (not on star/edit changes).</summary>
    public long LibraryVersion => Interlocked.Read(ref _libraryVersion);

    public Catalog(string databaseFile)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(databaseFile));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var cs = new SqliteConnectionStringBuilder { DataSource = databaseFile, Pooling = false }.ToString();
        _db = new SqliteConnection(cs);
        _db.Open();
        Exec("PRAGMA journal_mode=WAL;");
        Exec("PRAGMA synchronous=NORMAL;");
        Exec("""
            CREATE TABLE IF NOT EXISTS photos (
                id            INTEGER PRIMARY KEY AUTOINCREMENT,
                path          TEXT NOT NULL UNIQUE COLLATE NOCASE,
                filename      TEXT NOT NULL,
                folder        TEXT NOT NULL COLLATE NOCASE,
                size          INTEGER NOT NULL DEFAULT 0,
                width         INTEGER NOT NULL DEFAULT 0,
                height        INTEGER NOT NULL DEFAULT 0,
                year          INTEGER NOT NULL DEFAULT 0,
                month         INTEGER NOT NULL DEFAULT 0,
                date_taken    TEXT NOT NULL DEFAULT '',
                file_modified TEXT NOT NULL DEFAULT '',
                make          TEXT NOT NULL DEFAULT '',
                model         TEXT NOT NULL DEFAULT '',
                is_starred    INTEGER NOT NULL DEFAULT 0,
                rotation      INTEGER NOT NULL DEFAULT 0,
                edits         TEXT NOT NULL DEFAULT '{}',
                ai_processed  INTEGER NOT NULL DEFAULT 0,
                ai_caption    TEXT NOT NULL DEFAULT '',
                ai_tags       TEXT NOT NULL DEFAULT '[]',
                ai_fixes      TEXT NOT NULL DEFAULT '{}',
                thumb_path    TEXT NOT NULL DEFAULT '',
                added_at      TEXT NOT NULL DEFAULT ''
            );
            CREATE INDEX IF NOT EXISTS ix_photos_folder ON photos(folder);
            CREATE INDEX IF NOT EXISTS ix_photos_date ON photos(date_taken DESC, id DESC);
            CREATE TABLE IF NOT EXISTS folders (
                path     TEXT PRIMARY KEY COLLATE NOCASE,
                added_at TEXT NOT NULL DEFAULT ''
            );
            CREATE TABLE IF NOT EXISTS settings (
                key   TEXT PRIMARY KEY,
                value TEXT NOT NULL
            );
            """);
        using var schema = _db.CreateCommand();
        schema.CommandText = "SELECT COUNT(*) FROM pragma_table_info('photos') WHERE name='offline'";
        if (Convert.ToInt32(schema.ExecuteScalar()) == 0)
            Exec("ALTER TABLE photos ADD COLUMN offline INTEGER NOT NULL DEFAULT 0;");
        MigrateLegacyAiKey();
    }

    public void Dispose()
    {
        lock (_lock)
        {
            try { Exec("PRAGMA wal_checkpoint(TRUNCATE);"); } catch (SqliteException) { }
            _db.Dispose();
        }
    }

    // ---------------------------------------------------------------- settings

    private const string AiRegistryPath = @"Software\PicasaRevive\AiKeys";

    private (AiSettings Settings, string? CredentialId) ReadAiRecord()
    {
        var json = GetSetting("ai_settings");
        if (json is null) return (new AiSettings(), null);
        try
        {
            using var doc = JsonDocument.Parse(json);
            var settings = doc.RootElement.Deserialize<AiSettings>(Json.Options)?.Normalized() ?? new AiSettings();
            string? id = doc.RootElement.ValueKind == JsonValueKind.Object &&
                doc.RootElement.TryGetProperty("credential_id", out var prop) &&
                prop.ValueKind == JsonValueKind.String && Guid.TryParseExact(prop.GetString(), "N", out var guid)
                ? guid.ToString("N") : null;
            return (settings, id);
        }
        catch (JsonException) { return (new AiSettings(), null); }
    }

    // Bind each credential to its configured endpoint, including the provider's base path.
    private static string ValueName(string id, string endpoint) =>
        id + "_" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(endpoint.TrimEnd('/'))));

    public AiSettings GetAiSettings()
    {
        lock (_lock)
        {
            var (settings, id) = ReadAiRecord();
            settings.ApiKey = "";
            if (id is null) return settings;
            using var registry = Registry.CurrentUser.OpenSubKey(AiRegistryPath);
            if (registry?.GetValue(ValueName(id, settings.Endpoint)) is string value && value.StartsWith(DpapiPrefix, StringComparison.Ordinal))
                settings.ApiKey = UnprotectKey(value);
            return settings;
        }
    }

    internal AiSettings ResolveAiSettings(AiSettings input)
    {
        var settings = input.Normalized();
        if (settings.ApiKey != AiSettings.MaskedKey) return settings;
        var saved = GetAiSettings();
        if (!string.Equals(settings.Endpoint.TrimEnd('/'), saved.Endpoint.TrimEnd('/'), StringComparison.Ordinal))
            throw new ArgumentException("Enter a key for the new AI endpoint, or clear the key to use offline mode.");
        settings.ApiKey = saved.ApiKey;
        return settings;
    }

    private void MigrateLegacyAiKey()
    {
        var (legacy, _) = ReadAiRecord();
        if (string.IsNullOrEmpty(legacy.ApiKey)) return;
        Exec("PRAGMA secure_delete=ON;");
        legacy.ApiKey = UnprotectKey(legacy.ApiKey);
        if (string.IsNullOrEmpty(legacy.ApiKey)) return; // Only its owner can migrate a DPAPI-protected legacy key.
        SaveAiSettings(legacy);
        Exec("PRAGMA wal_checkpoint(TRUNCATE);");
    }

    public AiSettings SaveAiSettings(AiSettings input)
    {
        lock (_lock)
        {
            var settings = ResolveAiSettings(input);
            var (old, oldId) = ReadAiRecord();
            string? newId = null;
            using var registry = Registry.CurrentUser.CreateSubKey(AiRegistryPath);
            if (!string.IsNullOrEmpty(settings.ApiKey))
            {
                newId = Guid.NewGuid().ToString("N");
                registry.SetValue(ValueName(newId, settings.Endpoint), ProtectKey(settings.ApiKey), RegistryValueKind.String);
                registry.Flush();
            }
            try
            {
                SetSetting("ai_settings", Json.Serialize(new { settings.Endpoint, settings.Model, credential_id = newId }));
            }
            catch
            {
                if (newId is not null) registry.DeleteValue(ValueName(newId, settings.Endpoint), false);
                throw;
            }
            if (oldId is not null) registry.DeleteValue(ValueName(oldId, old.Endpoint), false);
            return settings;
        }
    }

    public string? GetSetting(string key)
    {
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT value FROM settings WHERE key=@k";
            cmd.Parameters.AddWithValue("@k", key);
            return cmd.ExecuteScalar() as string;
        }
    }

    public void SetSetting(string key, string value)
    {
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "INSERT INTO settings(key, value) VALUES(@k, @v) ON CONFLICT(key) DO UPDATE SET value=excluded.value";
            cmd.Parameters.AddWithValue("@k", key);
            cmd.Parameters.AddWithValue("@v", value);
            cmd.ExecuteNonQuery();
        }
    }

    // ---------------------------------------------------------------- folders

    public List<string> GetWatchedFolders()
    {
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT path FROM folders ORDER BY path";
            using var r = cmd.ExecuteReader();
            var list = new List<string>();
            while (r.Read()) list.Add(r.GetString(0));
            return list;
        }
    }

    public void AddWatchedFolder(string path)
    {
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "INSERT OR IGNORE INTO folders(path, added_at) VALUES(@p, @t)";
            cmd.Parameters.AddWithValue("@p", NormalizeDir(path));
            cmd.Parameters.AddWithValue("@t", DateTime.UtcNow.ToString("o"));
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>Stops watching a folder and forgets every photo inside it. Files on disk are never touched.</summary>
    public int RemoveFolder(string path)
    {
        var root = NormalizeDir(path);
        int removed;
        lock (_lock)
        {
            using var tx = _db.BeginTransaction();
            using (var cmd = _db.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = "DELETE FROM photos WHERE folder = @root OR folder LIKE @prefix ESCAPE '~'";
                cmd.Parameters.AddWithValue("@root", root);
                cmd.Parameters.AddWithValue("@prefix", EscapeLike(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar) + "%");
                removed = cmd.ExecuteNonQuery();
            }
            using (var cmd = _db.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = "DELETE FROM folders WHERE path = @root OR path LIKE @prefix ESCAPE '~'";
                cmd.Parameters.AddWithValue("@root", root);
                cmd.Parameters.AddWithValue("@prefix", EscapeLike(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar) + "%");
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
        }
        if (removed > 0) Interlocked.Increment(ref _libraryVersion);
        return removed;
    }

    // ---------------------------------------------------------------- photos: write

    /// <summary>Inserts new photos or refreshes file facts of known ones (stars, edits and AI data are kept).</summary>
    public void UpsertMany(IReadOnlyCollection<Photo> photos)
    {
        if (photos.Count == 0) return;
        lock (_lock)
        {
            using var tx = _db.BeginTransaction();
            using var cmd = _db.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO photos(path, filename, folder, size, width, height, year, month, date_taken, file_modified, make, model, thumb_path, added_at)
                VALUES(@path, @filename, @folder, @size, @width, @height, @year, @month, @date_taken, @file_modified, @make, @model, @thumb, @added)
                ON CONFLICT(path) DO UPDATE SET
                    offline=0, filename=excluded.filename, folder=excluded.folder, size=excluded.size,
                    width=excluded.width, height=excluded.height, year=excluded.year, month=excluded.month,
                    date_taken=excluded.date_taken, file_modified=excluded.file_modified,
                    make=excluded.make, model=excluded.model, thumb_path=excluded.thumb_path
                """;
            var pPath = cmd.Parameters.Add("@path", SqliteType.Text);
            var pFilename = cmd.Parameters.Add("@filename", SqliteType.Text);
            var pFolder = cmd.Parameters.Add("@folder", SqliteType.Text);
            var pSize = cmd.Parameters.Add("@size", SqliteType.Integer);
            var pWidth = cmd.Parameters.Add("@width", SqliteType.Integer);
            var pHeight = cmd.Parameters.Add("@height", SqliteType.Integer);
            var pYear = cmd.Parameters.Add("@year", SqliteType.Integer);
            var pMonth = cmd.Parameters.Add("@month", SqliteType.Integer);
            var pDate = cmd.Parameters.Add("@date_taken", SqliteType.Text);
            var pMod = cmd.Parameters.Add("@file_modified", SqliteType.Text);
            var pMake = cmd.Parameters.Add("@make", SqliteType.Text);
            var pModel = cmd.Parameters.Add("@model", SqliteType.Text);
            var pThumb = cmd.Parameters.Add("@thumb", SqliteType.Text);
            var pAdded = cmd.Parameters.Add("@added", SqliteType.Text);
            var now = DateTime.UtcNow.ToString("o");

            foreach (var p in photos)
            {
                pPath.Value = p.FilePath;
                pFilename.Value = p.Filename;
                pFolder.Value = p.FolderPath;
                pSize.Value = p.FileSize;
                pWidth.Value = p.Width;
                pHeight.Value = p.Height;
                pYear.Value = p.Year;
                pMonth.Value = p.Month;
                pDate.Value = p.DateTaken;
                pMod.Value = p.FileModified;
                pMake.Value = p.Make ?? "";
                pModel.Value = p.Model ?? "";
                pThumb.Value = p.ThumbPath ?? "";
                pAdded.Value = string.IsNullOrEmpty(p.AddedAt) ? now : p.AddedAt;
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
        }
        Interlocked.Increment(ref _libraryVersion);
    }

    public Photo Upsert(Photo photo)
    {
        UpsertMany(new[] { photo });
        return GetPhotoByPath(photo.FilePath)!;
    }

    public int SetOffline(IReadOnlyCollection<string> paths, bool offline)
    {
        if (paths.Count == 0) return 0;
        int removed = 0;
        lock (_lock)
        {
            using var tx = _db.BeginTransaction();
            using var cmd = _db.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "UPDATE photos SET offline=@offline WHERE path=@p AND offline<>@offline";
            cmd.Parameters.AddWithValue("@offline", offline);
            var p = cmd.Parameters.Add("@p", SqliteType.Text);
            foreach (var path in paths)
            {
                p.Value = path;
                removed += cmd.ExecuteNonQuery();
            }
            tx.Commit();
        }
        if (removed > 0) Interlocked.Increment(ref _libraryVersion);
        return removed;
    }

    /// <returns>New star state (0/1), or null when the photo does not exist.</returns>
    public int? ToggleStar(long id)
    {
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "UPDATE photos SET is_starred = 1 - is_starred WHERE id=@id RETURNING is_starred";
            cmd.Parameters.AddWithValue("@id", id);
            var v = cmd.ExecuteScalar();
            return v is null ? null : Convert.ToInt32(v);
        }
    }

    /// <returns>New rotation (0/90/180/270), or null when the photo does not exist.</returns>
    public int? Rotate(long id, int delta)
    {
        lock (_lock)
        {
            using var read = _db.CreateCommand();
            read.CommandText = "SELECT rotation FROM photos WHERE id=@id";
            read.Parameters.AddWithValue("@id", id);
            var cur = read.ExecuteScalar();
            if (cur is null) return null;
            int next = EditRecipe.NormalizeRotation(Convert.ToInt32(cur) + delta);
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "UPDATE photos SET rotation=@r WHERE id=@id";
            cmd.Parameters.AddWithValue("@r", next);
            cmd.Parameters.AddWithValue("@id", id);
            cmd.ExecuteNonQuery();
            return next;
        }
    }

    public bool SaveEdits(long id, JsonObject? edits)
    {
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "UPDATE photos SET edits=@e WHERE id=@id";
            cmd.Parameters.AddWithValue("@e", (edits ?? new JsonObject()).ToJsonString());
            cmd.Parameters.AddWithValue("@id", id);
            return cmd.ExecuteNonQuery() > 0;
        }
    }

    public bool UpdatePhotoAi(long id, string caption, IEnumerable<string> tags, IDictionary<string, int> fixes)
    {
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "UPDATE photos SET ai_processed=1, ai_caption=@c, ai_tags=@t, ai_fixes=@f WHERE id=@id";
            cmd.Parameters.AddWithValue("@c", caption ?? "");
            cmd.Parameters.AddWithValue("@t", JsonSerializer.Serialize(tags ?? Array.Empty<string>(), Json.Options));
            cmd.Parameters.AddWithValue("@f", JsonSerializer.Serialize(fixes ?? new Dictionary<string, int>(), Json.Options));
            cmd.Parameters.AddWithValue("@id", id);
            return cmd.ExecuteNonQuery() > 0;
        }
    }

    public void SetThumbPath(long id, string thumbPath)
    {
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "UPDATE photos SET thumb_path=@t WHERE id=@id";
            cmd.Parameters.AddWithValue("@t", thumbPath);
            cmd.Parameters.AddWithValue("@id", id);
            cmd.ExecuteNonQuery();
        }
    }

    // ---------------------------------------------------------------- photos: read

    public Photo? GetPhoto(long id)
    {
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = $"SELECT {Columns} FROM photos WHERE id=@id";
            cmd.Parameters.AddWithValue("@id", id);
            using var r = cmd.ExecuteReader();
            return r.Read() ? Map(r) : null;
        }
    }

    public Photo? GetPhotoByPath(string path)
    {
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = $"SELECT {Columns} FROM photos WHERE path=@p";
            cmd.Parameters.AddWithValue("@p", path);
            using var r = cmd.ExecuteReader();
            return r.Read() ? Map(r) : null;
        }
    }

    public int Count()
    {
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM photos";
            return Convert.ToInt32(cmd.ExecuteScalar());
        }
    }

    public List<long> GetAllIds(bool onlyUntagged = false)
    {
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT id FROM photos" + (onlyUntagged ? " WHERE ai_processed=0" : "") + " ORDER BY date_taken DESC, id DESC";
            using var r = cmd.ExecuteReader();
            var list = new List<long>();
            while (r.Read()) list.Add(r.GetInt64(0));
            return list;
        }
    }

    public readonly record struct IndexEntry(long Size, string FileModified, string ThumbPath, bool Offline);

    /// <summary>Lightweight map used by the scanner to skip unchanged files and to find deleted ones.</summary>
    public Dictionary<string, IndexEntry> GetIndexUnder(string root)
    {
        var dir = NormalizeDir(root);
        var prefix = dir.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var map = new Dictionary<string, IndexEntry>(StringComparer.OrdinalIgnoreCase);
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT path, size, file_modified, thumb_path, offline FROM photos WHERE folder = @root OR folder LIKE @prefix ESCAPE '~'";
            cmd.Parameters.AddWithValue("@root", dir);
            cmd.Parameters.AddWithValue("@prefix", EscapeLike(prefix) + "%");
            using var r = cmd.ExecuteReader();
            while (r.Read())
                map[r.GetString(0)] = new IndexEntry(r.GetInt64(1), r.GetString(2), r.GetString(3), r.GetInt32(4) != 0);
        }
        return map;
    }

    public (List<Photo> Photos, int Total) Query(PhotoQuery q)
    {
        var where = new StringBuilder(" WHERE 1=1");
        var args = new List<(string, object)>();

        if (!string.IsNullOrEmpty(q.Folder)) { where.Append(" AND folder = @folder"); args.Add(("@folder", NormalizeDir(q.Folder))); }
        if (q.StarredOnly) where.Append(" AND is_starred = 1");
        if (q.AiTaggedOnly) where.Append(" AND ai_processed = 1");
        if (q.Year > 0) { where.Append(" AND year = @year"); args.Add(("@year", q.Year)); }
        if (q.Month > 0) { where.Append(" AND month = @month"); args.Add(("@month", q.Month)); }
        if (!string.IsNullOrWhiteSpace(q.Search))
        {
            // Every whitespace-separated term must match filename, folder, caption, tags or the date.
            int i = 0;
            foreach (var term in q.Search.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var name = "@s" + i++;
                where.Append($" AND (filename LIKE {name} ESCAPE '~' OR folder LIKE {name} ESCAPE '~' OR ai_caption LIKE {name} ESCAPE '~' OR ai_tags LIKE {name} ESCAPE '~' OR date_taken LIKE {name} ESCAPE '~')");
                args.Add((name, "%" + EscapeLike(term) + "%"));
            }
        }

        int limit = q.Limit <= 0 ? PhotoQuery.DefaultLimit : q.Limit;
        int offset = Math.Max(0, q.Offset);
        string order = q.Recent ? " ORDER BY added_at DESC, id DESC" : " ORDER BY date_taken DESC, id DESC";

        lock (_lock)
        {
            int total;
            using (var count = _db.CreateCommand())
            {
                count.CommandText = "SELECT COUNT(*) FROM photos" + where;
                foreach (var (n, v) in args) count.Parameters.AddWithValue(n, v);
                total = Convert.ToInt32(count.ExecuteScalar());
            }

            var list = new List<Photo>(Math.Min(total, limit));
            using var cmd = _db.CreateCommand();
            cmd.CommandText = $"SELECT {Columns} FROM photos{where}{order} LIMIT @limit OFFSET @offset";
            foreach (var (n, v) in args) cmd.Parameters.AddWithValue(n, v);
            cmd.Parameters.AddWithValue("@limit", limit);
            cmd.Parameters.AddWithValue("@offset", offset);
            using var r = cmd.ExecuteReader();
            while (r.Read()) list.Add(Map(r));
            return (list, total);
        }
    }

    public LibrarySummary GetSummary()
    {
        var s = new LibrarySummary();
        lock (_lock)
        {
            using (var cmd = _db.CreateCommand())
            {
                cmd.CommandText = "SELECT COUNT(*), COALESCE(SUM(is_starred),0), COALESCE(SUM(ai_processed),0) FROM photos";
                using var r = cmd.ExecuteReader();
                if (r.Read())
                {
                    s.TotalPhotos = r.GetInt32(0);
                    s.TotalStarred = r.GetInt32(1);
                    s.AiTaggedCount = r.GetInt32(2);
                }
            }
            using (var cmd = _db.CreateCommand())
            {
                cmd.CommandText = "SELECT folder, COUNT(*), MAX(date_taken), MIN(offline) FROM photos GROUP BY folder ORDER BY folder";
                using var r = cmd.ExecuteReader();
                while (r.Read()) s.Folders.Add(new FolderSummary(r.GetString(0), r.GetInt32(1), r.IsDBNull(2) ? "" : r.GetString(2), r.GetInt32(3) != 0));
            }
            using (var cmd = _db.CreateCommand())
            {
                cmd.CommandText = "SELECT year, month, COUNT(*) FROM photos WHERE year > 1970 GROUP BY year, month ORDER BY year DESC, month DESC";
                using var r = cmd.ExecuteReader();
                while (r.Read()) s.Timeline.Add(new TimelineSummary(r.GetInt32(0), r.GetInt32(1), r.GetInt32(2)));
            }
        }
        s.WatchedFolders = GetWatchedFolders();
        return s;
    }

    // ---------------------------------------------------------------- helpers

    public static bool IsValidPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 4096) return false;
        if (path.IndexOf('\0') >= 0 || path.IndexOfAny(Path.GetInvalidPathChars()) >= 0) return false;
        if (path.IndexOfAny(new[] { '*', '?', '<', '>', '"' }) >= 0) return false;
        try
        {
            _ = Path.GetFullPath(path);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public static string NormalizeDir(string path)
    {
        if (!IsValidPath(path)) throw new ArgumentException("Invalid directory path.", nameof(path));
        try
        {
            var full = Path.GetFullPath(path);
            var root = Path.GetPathRoot(full);
            return full.Length > (root?.Length ?? 0) ? full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) : full;
        }
        catch (Exception ex)
        {
            throw new ArgumentException("Invalid directory path.", nameof(path), ex);
        }
    }

    private const string DpapiPrefix = "dpapi:";

    [StructLayout(LayoutKind.Sequential)]
    private struct DATA_BLOB
    {
        public int cbData;
        public IntPtr pbData;
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptProtectData(ref DATA_BLOB pDataIn, string? szDataDescr, IntPtr pOptionalEntropy, IntPtr pvReserved, IntPtr pPromptStruct, int dwFlags, out DATA_BLOB pDataOut);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptUnprotectData(ref DATA_BLOB pDataIn, IntPtr ppszDataDescr, IntPtr pOptionalEntropy, IntPtr pvReserved, IntPtr pPromptStruct, int dwFlags, out DATA_BLOB pDataOut);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr LocalFree(IntPtr hMem);

    private static string ProtectKey(string plain)
    {
        if (string.IsNullOrEmpty(plain)) return "";
        var bytes = Encoding.UTF8.GetBytes(plain);
        var inPtr = Marshal.AllocHGlobal(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, inPtr, bytes.Length);
            var inBlob = new DATA_BLOB { cbData = bytes.Length, pbData = inPtr };
            if (!CryptProtectData(ref inBlob, "PicasaRevive.AiKey", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0x01, out var outBlob))
                throw new InvalidOperationException("Failed to protect API key.");
            try
            {
                var cipher = new byte[outBlob.cbData];
                Marshal.Copy(outBlob.pbData, cipher, 0, cipher.Length);
                return DpapiPrefix + Convert.ToBase64String(cipher);
            }
            finally
            {
                if (outBlob.pbData != IntPtr.Zero) LocalFree(outBlob.pbData);
            }
        }
        finally
        {
            Array.Clear(bytes);
            Marshal.FreeHGlobal(inPtr);
        }
    }

    private static string UnprotectKey(string stored)
    {
        if (string.IsNullOrEmpty(stored)) return "";
        if (!stored.StartsWith(DpapiPrefix, StringComparison.Ordinal)) return stored;
        byte[] cipher;
        try { cipher = Convert.FromBase64String(stored[DpapiPrefix.Length..]); }
        catch (FormatException) { return ""; }
        if (cipher.Length == 0) return "";

        var inPtr = Marshal.AllocHGlobal(cipher.Length);
        try
        {
            Marshal.Copy(cipher, 0, inPtr, cipher.Length);
            var inBlob = new DATA_BLOB { cbData = cipher.Length, pbData = inPtr };
            if (!CryptUnprotectData(ref inBlob, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0x01, out var outBlob))
                return "";
            try
            {
                var plain = new byte[outBlob.cbData];
                Marshal.Copy(outBlob.pbData, plain, 0, plain.Length);
                var result = Encoding.UTF8.GetString(plain);
                Array.Clear(plain);
                return result;
            }
            finally
            {
                if (outBlob.pbData != IntPtr.Zero) LocalFree(outBlob.pbData);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(inPtr);
        }
    }

    private static string EscapeLike(string s) => s.Replace("~", "~~").Replace("%", "~%").Replace("_", "~_");

    private void Exec(string sql)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static Photo Map(SqliteDataReader r)
    {
        var p = new Photo
        {
            Id = r.GetInt64(0),
            FilePath = r.GetString(1),
            Filename = r.GetString(2),
            FolderPath = r.GetString(3),
            FileSize = r.GetInt64(4),
            Width = r.GetInt32(5),
            Height = r.GetInt32(6),
            Year = r.GetInt32(7),
            Month = r.GetInt32(8),
            DateTaken = r.GetString(9),
            FileModified = r.GetString(10),
            Make = r.GetString(11),
            Model = r.GetString(12),
            IsStarred = r.GetInt32(13),
            Rotation = r.GetInt32(14),
            Edits = Json.ParseObject(r.GetString(15)),
            AiProcessed = r.GetInt32(16) != 0,
            AiCaption = r.GetString(17),
            ThumbPath = r.GetString(20),
            AddedAt = r.GetString(21),
            Offline = r.GetInt32(22) != 0,
        };
        try { p.AiTags = JsonSerializer.Deserialize<List<string>>(r.GetString(18), Json.Options) ?? new(); }
        catch (JsonException) { p.AiTags = new(); }
        try { p.AiFixes = JsonSerializer.Deserialize<Dictionary<string, int>>(r.GetString(19), Json.Options) ?? new(); }
        catch (JsonException) { p.AiFixes = new(); }
        return p;
    }
}
