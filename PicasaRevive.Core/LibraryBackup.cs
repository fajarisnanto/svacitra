using System.Text.Json;

namespace PicasaRevive.Core;

public sealed record LibraryBackup(string Format, List<string> Folders, List<Photo> Photos)
{
    public Dictionary<string, string[]>? ProofSheets { get; init; }

    public const string CurrentFormat = "PicasaRevive.Library/1";

    public static LibraryBackup Read(string file)
    {
        using var stream = File.OpenRead(file);
        var data = JsonSerializer.Deserialize<LibraryBackup>(stream, Json.Options);
        if (data?.Format != CurrentFormat || data.Folders is null || data.Photos is null)
            throw new InvalidDataException("Choose a svacitra library backup.");
        return data;
    }
}

public sealed partial class Catalog
{
    public void BackUpLibrary(string destination)
    {
        var temp = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            LibraryBackup snapshot;
            lock (_lock)
            {
                var photos = Query(new PhotoQuery { Limit = int.MaxValue }).Photos;
                foreach (var photo in photos) photo.ThumbPath = "";
                var sheets = new Dictionary<string, string[]>();
                using (var cmd = _db.CreateCommand())
                {
                    cmd.CommandText = "SELECT substr(key,7), value FROM settings WHERE key GLOB 'proof:*'";
                    using var rows = cmd.ExecuteReader();
                    while (rows.Read()) sheets.Add(rows.GetString(0), JsonSerializer.Deserialize<string[]>(rows.GetString(1))!);
                }
                snapshot = new(LibraryBackup.CurrentFormat, GetWatchedFolders(), photos) { ProofSheets = sheets };
            }
            using (var stream = File.Create(temp)) JsonSerializer.Serialize(stream, snapshot, Json.Options);
            File.Move(temp, destination, true);
        }
        finally { File.Delete(temp); }
    }

    public IReadOnlyCollection<string> RestoreLibrary(LibraryBackup backup, IReadOnlyDictionary<string, string> locations)
    {
        var roots = locations.OrderByDescending(pair => pair.Key.Length).ToList();
        string Reconnect(string path)
        {
            if (!IsValidPath(path) || !Path.IsPathFullyQualified(path))
                throw new InvalidDataException("The backup contains an invalid file path.");
            var full = Path.GetFullPath(path);
            foreach (var pair in roots)
            {
                var relative = Path.GetRelativePath(pair.Key, full);
                if (!Path.IsPathRooted(relative) && relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar))
                    return Path.GetFullPath(Path.Combine(pair.Value, relative));
            }
            return full;
        }

        lock (_lock)
        {
            using var tx = _db.BeginTransaction();
            using var folder = _db.CreateCommand();
            folder.Transaction = tx;
            folder.CommandText = "INSERT OR IGNORE INTO folders(path,added_at) VALUES(@p,@now)";
            folder.Parameters.AddWithValue("@now", DateTime.UtcNow.ToString("o"));
            var root = folder.Parameters.Add("@p", Microsoft.Data.Sqlite.SqliteType.Text);
            foreach (var entry in backup.Folders)
            {
                root.Value = NormalizeDir(Reconnect(entry));
                folder.ExecuteNonQuery();
            }
            using var cmd = _db.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO photos(path,folder,filename,size,width,height,year,month,date_taken,file_modified,make,model,
                                   is_starred,rotation,edits,ai_processed,ai_caption,ai_tags,ai_fixes,added_at,offline)
                VALUES(@path,@folder,@name,@file_size,@width,@height,@year,@month,@date_taken,@file_modified,@make,@model,@is_starred,@rotation,@edits,@ai_processed,@ai_caption,@ai_tags,@ai_fixes,@added_at,@offline)
                ON CONFLICT(path) DO UPDATE SET is_starred=excluded.is_starred,rotation=excluded.rotation,
                    edits=excluded.edits,ai_processed=excluded.ai_processed,ai_caption=excluded.ai_caption,
                    ai_tags=excluded.ai_tags,ai_fixes=excluded.ai_fixes,offline=excluded.offline
                """;
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var photo in backup.Photos)
            {
                var path = Reconnect(photo.FilePath);
                if (!ImageOps.SupportedExtensions.Contains(Path.GetExtension(path)) || !seen.Add(path))
                    throw new InvalidDataException("The backup contains a duplicate or unsupported photo path.");
                cmd.Parameters.Clear();
                cmd.Parameters.AddWithValue("@path", path);
                cmd.Parameters.AddWithValue("@folder", Path.GetDirectoryName(path)!);
                cmd.Parameters.AddWithValue("@name", Path.GetFileName(path));
                cmd.Parameters.AddWithValue("@offline", !File.Exists(path));
                cmd.Parameters.AddWithValue("@file_size", photo.FileSize);
                cmd.Parameters.AddWithValue("@width", photo.Width);
                cmd.Parameters.AddWithValue("@height", photo.Height);
                cmd.Parameters.AddWithValue("@year", photo.Year);
                cmd.Parameters.AddWithValue("@month", photo.Month);
                cmd.Parameters.AddWithValue("@date_taken", photo.DateTaken);
                cmd.Parameters.AddWithValue("@file_modified", photo.FileModified);
                cmd.Parameters.AddWithValue("@make", photo.Make);
                cmd.Parameters.AddWithValue("@model", photo.Model);
                cmd.Parameters.AddWithValue("@is_starred", photo.IsStarred);
                cmd.Parameters.AddWithValue("@rotation", photo.Rotation);
                cmd.Parameters.AddWithValue("@edits", Json.Serialize(photo.Edits));
                cmd.Parameters.AddWithValue("@ai_processed", photo.AiProcessed);
                cmd.Parameters.AddWithValue("@ai_caption", photo.AiCaption);
                cmd.Parameters.AddWithValue("@ai_tags", Json.Serialize(photo.AiTags));
                cmd.Parameters.AddWithValue("@ai_fixes", Json.Serialize(photo.AiFixes));
                cmd.Parameters.AddWithValue("@added_at", photo.AddedAt);
                cmd.ExecuteNonQuery();
            }
            if (backup.ProofSheets is { } sheets)
            {
                using var proof = _db.CreateCommand();
                proof.Transaction = tx;
                proof.CommandText = "INSERT OR REPLACE INTO settings(key,value) VALUES(@key,@value)";
                var key = proof.Parameters.Add("@key", Microsoft.Data.Sqlite.SqliteType.Text);
                var value = proof.Parameters.Add("@value", Microsoft.Data.Sqlite.SqliteType.Text);
                foreach (var (code, files) in sheets)
                {
                    if (code.Length != 12 || !code.All(Uri.IsHexDigit) || files is null)
                        throw new InvalidDataException("Invalid proof sheet in library backup.");
                    key.Value = "proof:" + code.ToUpperInvariant();
                    value.Value = Json.Serialize(files.Select(Reconnect));
                    proof.ExecuteNonQuery();
                }
            }
            tx.Commit();
            Interlocked.Increment(ref _libraryVersion);
            return seen;
        }
    }
}
