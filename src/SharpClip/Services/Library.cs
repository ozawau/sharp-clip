using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;

namespace SharpClip;

internal sealed class ClipItem
{
    public Guid Id { get; init; }
    public string ContentType { get; init; } = "plainText";
    public byte[] Raw { get; init; } = [];
    public string? Text { get; init; }
    public string? Title { get; set; }
    public string? SourceApp { get; init; }
    public string? SourceBundle { get; init; }
    public byte[]? Thumbnail { get; set; }
    public string Hash { get; init; } = "";
    public DateTimeOffset CopiedAt { get; set; }
    public bool IsPinned { get; set; }

    public string Display => string.IsNullOrWhiteSpace(Title)
        ? (string.IsNullOrWhiteSpace(Text) ? ContentType : FirstLine(Text))
        : Title!;

    public ClipItem Detach() => new()
    {
        Id = Id,
        ContentType = ContentType,
        Raw = Raw,
        Text = Text,
        Title = Title,
        SourceApp = SourceApp,
        SourceBundle = SourceBundle,
        Thumbnail = Thumbnail,
        Hash = Hash,
        CopiedAt = CopiedAt,
        IsPinned = IsPinned,
    };

    private static string FirstLine(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var line = text.Replace("\r\n", "\n").Split('\n', 2)[0].Trim();
        return line.Length > 80 ? line[..80] : line;
    }
}

internal sealed class Pinboard
{
    public Guid Id { get; init; }
    public string Name { get; set; } = "";
    public int Order { get; set; }
    public string? Color { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

internal readonly record struct ImportSummary(int Items, int SkippedItems, int Boards, int Exclusions);

internal sealed class Library
{
    public static Library Shared { get; } = new();

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true,
    };

    private readonly string _path;
    private UndoBatch? _undo;

    private Library()
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SharpClip");
        Directory.CreateDirectory(dir);
        _path = Path.Combine(dir, "sharpclip.db");
    }

    public void Open()
    {
        try
        {
            using var db = Connect();
            Migrate(db);
        }
        catch
        {
            SqliteConnection.ClearAllPools();
            Quarantine();
            using var db = Connect();
            Migrate(db);
        }
    }

    public ClipItem? Record(CapturedClip captured)
    {
        if (captured.Raw.Length == 0) return null;
        var hash = ClipboardCapture.Hash(captured.Raw);
        using var db = Connect();
        using (var existing = db.CreateCommand())
        {
            existing.CommandText = "SELECT id FROM clips WHERE content_hash = $hash LIMIT 1";
            existing.Parameters.AddWithValue("$hash", hash);
            var found = existing.ExecuteScalar() as string;
            if (found is not null)
            {
                using var bump = db.CreateCommand();
                bump.CommandText = """
                    UPDATE clips
                    SET copied_at = $at, source_app = $app, source_bundle = $bundle
                    WHERE id = $id
                    """;
                bump.Parameters.AddWithValue("$at", DateTimeOffset.Now.ToString("O", CultureInfo.InvariantCulture));
                bump.Parameters.AddWithValue("$app", (object?)captured.SourceApp ?? DBNull.Value);
                bump.Parameters.AddWithValue("$bundle", (object?)captured.SourceBundle ?? DBNull.Value);
                bump.Parameters.AddWithValue("$id", found);
                bump.ExecuteNonQuery();
                Trim(db);
                return Load(db, Guid.Parse(found));
            }
        }

        var item = new ClipItem
        {
            Id = Guid.NewGuid(),
            ContentType = captured.ContentType,
            Raw = captured.Raw,
            Text = captured.Text,
            SourceApp = captured.SourceApp,
            SourceBundle = captured.SourceBundle,
            Hash = hash,
            CopiedAt = DateTimeOffset.Now,
        };
        InsertClip(db, item);
        Trim(db);
        return item;
    }

    public void StoreThumbnail(Guid id, byte[] png)
    {
        using var db = Connect();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "UPDATE clips SET thumbnail_data = $png WHERE id = $id";
        cmd.Parameters.AddWithValue("$png", png);
        cmd.Parameters.AddWithValue("$id", id.ToString());
        cmd.ExecuteNonQuery();
    }

    public IReadOnlyList<ClipItem> Search(string query, Guid? pinboardId, string? type)
    {
        using var db = Connect();
        using var cmd = db.CreateCommand();
        var sql = pinboardId is null
            ? "SELECT c.* FROM clips c WHERE 1=1"
            : "SELECT c.* FROM clips c JOIN entries e ON e.item_id = c.id WHERE e.pinboard_id = $board";
        if (!string.IsNullOrWhiteSpace(type) && type != "all") sql += " AND c.content_type = $type";
        if (!string.IsNullOrWhiteSpace(query))
        {
            sql += " AND (c.text_content LIKE $q ESCAPE '\\' OR IFNULL(c.user_title,'') LIKE $q ESCAPE '\\' OR IFNULL(c.source_app,'') LIKE $q ESCAPE '\\')";
        }
        sql += pinboardId is null
            ? " ORDER BY c.copied_at DESC"
            : " ORDER BY e.display_order, c.copied_at DESC";
        cmd.CommandText = sql;
        if (pinboardId is Guid board) cmd.Parameters.AddWithValue("$board", board.ToString());
        if (!string.IsNullOrWhiteSpace(type) && type != "all") cmd.Parameters.AddWithValue("$type", type);
        if (!string.IsNullOrWhiteSpace(query)) cmd.Parameters.AddWithValue("$q", "%" + EscapeLike(query) + "%");
        return ReadClips(cmd);
    }

    public IReadOnlyList<ClipItem> Latest(int count)
    {
        using var db = Connect();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT * FROM clips ORDER BY copied_at DESC LIMIT $n";
        cmd.Parameters.AddWithValue("$n", count);
        return ReadClips(cmd);
    }

    public IReadOnlyList<Pinboard> Boards()
    {
        using var db = Connect();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT id, name, display_order, color, created_at FROM pinboards ORDER BY display_order, name";
        var list = new List<Pinboard>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(new Pinboard
            {
                Id = Guid.Parse(reader.GetString(0)),
                Name = reader.GetString(1),
                Order = reader.GetInt32(2),
                Color = reader.IsDBNull(3) ? null : reader.GetString(3),
                CreatedAt = reader.IsDBNull(4) ? DateTimeOffset.Now : ParseTime(reader.GetString(4)),
            });
        }
        return list;
    }

    public Pinboard AddBoard(string name)
    {
        var trimmed = name.Trim();
        if (trimmed.Length == 0) throw new InvalidOperationException("empty name");
        var board = new Pinboard
        {
            Id = Guid.NewGuid(),
            Name = trimmed,
            Order = Boards().Count,
            CreatedAt = DateTimeOffset.Now,
        };
        using var db = Connect();
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            INSERT INTO pinboards (id, name, display_order, color, created_at)
            VALUES ($id, $name, $order, NULL, $at)
            """;
        cmd.Parameters.AddWithValue("$id", board.Id.ToString());
        cmd.Parameters.AddWithValue("$name", board.Name);
        cmd.Parameters.AddWithValue("$order", board.Order);
        cmd.Parameters.AddWithValue("$at", board.CreatedAt.ToString("O", CultureInfo.InvariantCulture));
        cmd.ExecuteNonQuery();
        return board;
    }

    public void ReorderBoards(IReadOnlyList<Guid> ids)
    {
        using var db = Connect();
        for (var i = 0; i < ids.Count; i++)
        {
            using var cmd = db.CreateCommand();
            cmd.CommandText = "UPDATE pinboards SET display_order = $order WHERE id = $id";
            cmd.Parameters.AddWithValue("$order", i);
            cmd.Parameters.AddWithValue("$id", ids[i].ToString());
            cmd.ExecuteNonQuery();
        }
    }

    public void SetBoardColor(Guid id, string? color)
    {
        using var db = Connect();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "UPDATE pinboards SET color = $color WHERE id = $id";
        cmd.Parameters.AddWithValue("$color", (object?)color ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$id", id.ToString());
        cmd.ExecuteNonQuery();
    }

    public void Touch(IReadOnlyList<Guid> ids)
    {
        if (ids.Count == 0) return;
        using var db = Connect();
        var now = DateTimeOffset.Now;
        for (var i = 0; i < ids.Count; i++)
        {
            using var cmd = db.CreateCommand();
            cmd.CommandText = "UPDATE clips SET copied_at = $at WHERE id = $id";
            cmd.Parameters.AddWithValue("$at", now.AddMilliseconds(ids.Count - i).ToString("O", CultureInfo.InvariantCulture));
            cmd.Parameters.AddWithValue("$id", ids[i].ToString());
            cmd.ExecuteNonQuery();
        }
    }

    public bool BoardContains(Guid boardId, Guid itemId)
    {
        using var db = Connect();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM entries WHERE pinboard_id = $board AND item_id = $item";
        cmd.Parameters.AddWithValue("$board", boardId.ToString());
        cmd.Parameters.AddWithValue("$item", itemId.ToString());
        return cmd.ExecuteScalar() is not null;
    }

    public void AddToBoard(Guid boardId, Guid itemId)
    {
        using var db = Connect();
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            INSERT OR IGNORE INTO entries (pinboard_id, item_id, display_order, added_at)
            VALUES ($board, $item, (SELECT COUNT(*) FROM entries WHERE pinboard_id = $board), $at)
            """;
        cmd.Parameters.AddWithValue("$board", boardId.ToString());
        cmd.Parameters.AddWithValue("$item", itemId.ToString());
        cmd.Parameters.AddWithValue("$at", DateTimeOffset.Now.ToString("O", CultureInfo.InvariantCulture));
        cmd.ExecuteNonQuery();
        using var pin = db.CreateCommand();
        pin.CommandText = "UPDATE clips SET is_pinned = 1 WHERE id = $id";
        pin.Parameters.AddWithValue("$id", itemId.ToString());
        pin.ExecuteNonQuery();
    }

    public void DeleteClips(IEnumerable<Guid> ids)
    {
        var list = ids.Distinct().ToList();
        if (list.Count == 0) return;
        var batch = new UndoBatch();
        using var db = Connect();
        foreach (var id in list)
        {
            var clip = Load(db, id);
            if (clip is null) continue;
            batch.Clips.Add(clip);
            batch.Entries.AddRange(EntriesFor(db, id, null));
            using var cmd = db.CreateCommand();
            cmd.CommandText = "DELETE FROM entries WHERE item_id = $id; DELETE FROM clips WHERE id = $id;";
            cmd.Parameters.AddWithValue("$id", id.ToString());
            cmd.ExecuteNonQuery();
        }
        if (batch.Clips.Count > 0) _undo = batch;
    }

    public void RemoveFromBoard(Guid boardId, Guid itemId)
    {
        using var db = Connect();
        var entries = EntriesFor(db, itemId, boardId);
        if (entries.Count == 0) return;
        using var cmd = db.CreateCommand();
        cmd.CommandText = "DELETE FROM entries WHERE pinboard_id = $board AND item_id = $item";
        cmd.Parameters.AddWithValue("$board", boardId.ToString());
        cmd.Parameters.AddWithValue("$item", itemId.ToString());
        cmd.ExecuteNonQuery();
        _undo = new UndoBatch { Entries = entries };
    }

    public bool Undo()
    {
        var batch = _undo;
        _undo = null;
        if (batch is null) return false;
        using var db = Connect();
        foreach (var clip in batch.Clips)
        {
            if (Load(db, clip.Id) is null) InsertClip(db, clip);
        }
        foreach (var entry in batch.Entries)
        {
            using var cmd = db.CreateCommand();
            cmd.CommandText = """
                INSERT OR IGNORE INTO entries (pinboard_id, item_id, display_order, added_at)
                VALUES ($board, $item, $order, $at)
                """;
            cmd.Parameters.AddWithValue("$board", entry.BoardId.ToString());
            cmd.Parameters.AddWithValue("$item", entry.ItemId.ToString());
            cmd.Parameters.AddWithValue("$order", entry.Order);
            cmd.Parameters.AddWithValue("$at", entry.AddedAt.ToString("O", CultureInfo.InvariantCulture));
            cmd.ExecuteNonQuery();
        }
        return true;
    }

    public void RenameBoard(Guid id, string name)
    {
        using var db = Connect();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "UPDATE pinboards SET name = $name WHERE id = $id";
        cmd.Parameters.AddWithValue("$name", name.Trim());
        cmd.Parameters.AddWithValue("$id", id.ToString());
        cmd.ExecuteNonQuery();
    }

    public void DeleteBoard(Guid id)
    {
        using var db = Connect();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "DELETE FROM entries WHERE pinboard_id = $id; DELETE FROM pinboards WHERE id = $id;";
        cmd.Parameters.AddWithValue("$id", id.ToString());
        cmd.ExecuteNonQuery();
    }

    public int CountUnpinned()
    {
        using var db = Connect();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM clips WHERE id NOT IN (SELECT item_id FROM entries)";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    public void ClearUnpinned()
    {
        using var db = Connect();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "DELETE FROM clips WHERE id NOT IN (SELECT item_id FROM entries)";
        cmd.ExecuteNonQuery();
        _undo = null;
    }

    public void UpdateText(Guid id, string text)
    {
        var raw = System.Text.Encoding.UTF8.GetBytes(text);
        using var db = Connect();
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            UPDATE clips
            SET text_content = $text, raw_data = $raw, content_hash = $hash, content_type = CASE
                WHEN content_type IN ('richText', 'html', 'url') THEN 'plainText' ELSE content_type END
            WHERE id = $id
            """;
        cmd.Parameters.AddWithValue("$text", text);
        cmd.Parameters.AddWithValue("$raw", raw);
        cmd.Parameters.AddWithValue("$hash", ClipboardCapture.Hash(raw));
        cmd.Parameters.AddWithValue("$id", id.ToString());
        cmd.ExecuteNonQuery();
    }

    public void Rename(Guid id, string? title)
    {
        var value = string.IsNullOrWhiteSpace(title) ? null : title.Trim();
        using var db = Connect();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "UPDATE clips SET user_title = $title WHERE id = $id";
        cmd.Parameters.AddWithValue("$title", (object?)value ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$id", id.ToString());
        cmd.ExecuteNonQuery();
    }

    public bool IsExcluded(string processName)
    {
        using var db = Connect();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM exclusions WHERE process_name = $name COLLATE NOCASE";
        cmd.Parameters.AddWithValue("$name", processName);
        return cmd.ExecuteScalar() is not null;
    }

    public IReadOnlyList<(string Process, string Name)> Exclusions()
    {
        using var db = Connect();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT process_name, app_name FROM exclusions ORDER BY app_name";
        var list = new List<(string, string)>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) list.Add((reader.GetString(0), reader.GetString(1)));
        return list;
    }

    public void Exclude(string processName, string appName)
    {
        using var db = Connect();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "INSERT OR REPLACE INTO exclusions (process_name, app_name) VALUES ($p, $n)";
        cmd.Parameters.AddWithValue("$p", processName);
        cmd.Parameters.AddWithValue("$n", appName);
        cmd.ExecuteNonQuery();
    }

    public void Unexclude(string processName)
    {
        using var db = Connect();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "DELETE FROM exclusions WHERE process_name = $p COLLATE NOCASE";
        cmd.Parameters.AddWithValue("$p", processName);
        cmd.ExecuteNonQuery();
    }

    public byte[] ExportJson()
    {
        using var db = Connect();
        using var clips = db.CreateCommand();
        clips.CommandText = "SELECT * FROM clips ORDER BY copied_at DESC";
        using var boardsCmd = db.CreateCommand();
        boardsCmd.CommandText = "SELECT id, name, display_order, created_at FROM pinboards ORDER BY display_order, name";
        var boards = new List<TransferBoard>();
        using (var reader = boardsCmd.ExecuteReader())
        {
            while (reader.Read())
            {
                boards.Add(new TransferBoard
                {
                    Id = Guid.Parse(reader.GetString(0)),
                    Name = reader.GetString(1),
                    DisplayOrder = reader.GetInt32(2),
                    CreatedAt = reader.IsDBNull(3) ? DateTimeOffset.Now : ParseTime(reader.GetString(3)),
                });
            }
        }
        using var entryCmd = db.CreateCommand();
        entryCmd.CommandText = "SELECT pinboard_id, item_id, display_order, added_at FROM entries";
        var entries = new List<TransferEntry>();
        using (var reader = entryCmd.ExecuteReader())
        {
            while (reader.Read())
            {
                entries.Add(new TransferEntry
                {
                    PinboardId = Guid.Parse(reader.GetString(0)),
                    ItemId = Guid.Parse(reader.GetString(1)),
                    DisplayOrder = reader.GetInt32(2),
                    AddedAt = reader.IsDBNull(3) ? DateTimeOffset.Now : ParseTime(reader.GetString(3)),
                });
            }
        }
        var doc = new TransferDocument
        {
            Version = 1,
            ExportedAt = DateTimeOffset.Now,
            Items = ReadClips(clips).Select(item => new TransferItem
            {
                Id = item.Id,
                ContentType = item.ContentType,
                RawData = item.Raw,
                TextContent = item.Text,
                ThumbnailData = item.Thumbnail,
                SourceAppName = item.SourceApp,
                SourceAppBundleId = item.SourceBundle,
                ContentHash = item.Hash,
                CopiedAt = item.CopiedAt,
                UserTitle = item.Title,
                IsPinned = item.IsPinned || entries.Any(entry => entry.ItemId == item.Id),
            }).ToList(),
            Pinboards = boards,
            Entries = entries,
            Exclusions = Exclusions().Select(item => new TransferExclusion
            {
                BundleId = item.Process,
                AppName = item.Name,
            }).ToList(),
        };
        return JsonSerializer.SerializeToUtf8Bytes(doc, Json);
    }

    public ImportSummary ImportJson(byte[] data)
    {
        var doc = JsonSerializer.Deserialize<TransferDocument>(data, Json) ?? throw new InvalidDataException("empty");
        using var db = Connect();
        var hashToId = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
        using (var hashes = db.CreateCommand())
        {
            hashes.CommandText = "SELECT id, content_hash FROM clips";
            using var reader = hashes.ExecuteReader();
            while (reader.Read()) hashToId[reader.GetString(1)] = Guid.Parse(reader.GetString(0));
        }
        var itemMap = new Dictionary<Guid, Guid>();
        var imported = 0;
        var skipped = 0;
        foreach (var record in doc.Items)
        {
            if (hashToId.TryGetValue(record.ContentHash, out var existing))
            {
                itemMap[record.Id] = existing;
                skipped++;
                continue;
            }
            var id = record.Id == Guid.Empty ? Guid.NewGuid() : record.Id;
            if (Load(db, id) is not null) id = Guid.NewGuid();
            var item = new ClipItem
            {
                Id = id,
                ContentType = string.IsNullOrWhiteSpace(record.ContentType) ? "plainText" : record.ContentType,
                Raw = record.RawData ?? [],
                Text = record.TextContent,
                Title = record.UserTitle,
                SourceApp = record.SourceAppName,
                SourceBundle = record.SourceAppBundleId,
                Thumbnail = record.ThumbnailData,
                Hash = string.IsNullOrWhiteSpace(record.ContentHash) ? ClipboardCapture.Hash(record.RawData ?? []) : record.ContentHash,
                CopiedAt = record.CopiedAt == default ? DateTimeOffset.Now : record.CopiedAt,
                IsPinned = record.IsPinned,
            };
            InsertClip(db, item);
            hashToId[item.Hash] = item.Id;
            itemMap[record.Id] = item.Id;
            imported++;
        }

        var names = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
        foreach (var board in Boards()) names[board.Name] = board.Id;
        var boardMap = new Dictionary<Guid, Guid>();
        var importedBoards = 0;
        foreach (var record in doc.Pinboards)
        {
            var name = record.Name?.Trim() ?? "";
            if (name.Length == 0) continue;
            if (names.TryGetValue(name, out var existing))
            {
                boardMap[record.Id] = existing;
                continue;
            }
            var created = AddBoard(name);
            names[name] = created.Id;
            boardMap[record.Id] = created.Id;
            importedBoards++;
        }

        foreach (var record in doc.Entries)
        {
            if (!boardMap.TryGetValue(record.PinboardId, out var boardId)) continue;
            if (!itemMap.TryGetValue(record.ItemId, out var itemId)) continue;
            if (BoardContains(boardId, itemId)) continue;
            AddToBoard(boardId, itemId);
        }

        var importedExclusions = 0;
        foreach (var record in doc.Exclusions)
        {
            var process = record.BundleId?.Trim() ?? "";
            if (process.Length == 0 || IsExcluded(process)) continue;
            Exclude(process, string.IsNullOrWhiteSpace(record.AppName) ? process : record.AppName.Trim());
            importedExclusions++;
        }
        Trim(db);
        return new ImportSummary(imported, skipped, importedBoards, importedExclusions);
    }

    private void Quarantine()
    {
        var root = Path.GetDirectoryName(_path)!;
        var folder = Path.Combine(root, "quarantine");
        Directory.CreateDirectory(folder);
        SqliteConnection.ClearAllPools();
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            var source = _path + suffix;
            if (!File.Exists(source)) continue;
            var dest = Path.Combine(folder, $"sharpclip-{stamp}{suffix}");
            for (var attempt = 0; attempt < 5; attempt++)
            {
                try
                {
                    File.Move(source, dest);
                    break;
                }
                catch (IOException) when (attempt < 4)
                {
                    Thread.Sleep(50);
                }
            }
        }
    }

    private static void Migrate(SqliteConnection db)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS clips (
              id TEXT PRIMARY KEY,
              content_type TEXT NOT NULL,
              raw_data BLOB NOT NULL,
              text_content TEXT,
              user_title TEXT,
              source_app TEXT,
              content_hash TEXT NOT NULL,
              copied_at TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS pinboards (
              id TEXT PRIMARY KEY,
              name TEXT NOT NULL,
              display_order INTEGER NOT NULL
            );
            CREATE TABLE IF NOT EXISTS entries (
              pinboard_id TEXT NOT NULL,
              item_id TEXT NOT NULL,
              display_order INTEGER NOT NULL,
              PRIMARY KEY (pinboard_id, item_id)
            );
            CREATE TABLE IF NOT EXISTS exclusions (
              process_name TEXT PRIMARY KEY,
              app_name TEXT NOT NULL
            );
            """;
        cmd.ExecuteNonQuery();
        EnsureColumn(db, "clips", "thumbnail_data", "BLOB");
        EnsureColumn(db, "clips", "source_bundle", "TEXT");
        EnsureColumn(db, "clips", "is_pinned", "INTEGER NOT NULL DEFAULT 0");
        EnsureColumn(db, "pinboards", "color", "TEXT");
        EnsureColumn(db, "pinboards", "created_at", "TEXT");
        EnsureColumn(db, "entries", "added_at", "TEXT");
    }

    private static void EnsureColumn(SqliteConnection db, string table, string column, string type)
    {
        using var info = db.CreateCommand();
        info.CommandText = $"PRAGMA table_info({table})";
        using var reader = info.ExecuteReader();
        while (reader.Read())
        {
            if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase)) return;
        }
        using var alter = db.CreateCommand();
        alter.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {type}";
        alter.ExecuteNonQuery();
    }

    private void Trim(SqliteConnection db)
    {
        var limit = SettingsStore.Current.HistoryLimit;
        if (limit <= 0) return;
        using var count = db.CreateCommand();
        count.CommandText = """
            SELECT COUNT(*) FROM clips
            WHERE IFNULL(is_pinned, 0) = 0
              AND id NOT IN (SELECT item_id FROM entries)
            """;
        var unpinned = Convert.ToInt32(count.ExecuteScalar());
        var extra = unpinned - limit;
        if (extra <= 0) return;
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            DELETE FROM clips WHERE id IN (
              SELECT id FROM clips
              WHERE IFNULL(is_pinned, 0) = 0
                AND id NOT IN (SELECT item_id FROM entries)
              ORDER BY copied_at ASC
              LIMIT $n
            )
            """;
        cmd.Parameters.AddWithValue("$n", extra);
        cmd.ExecuteNonQuery();
    }

    private static void InsertClip(SqliteConnection db, ClipItem item)
    {
        using var insert = db.CreateCommand();
        insert.CommandText = """
            INSERT INTO clips (
              id, content_type, raw_data, text_content, thumbnail_data, user_title,
              source_app, source_bundle, content_hash, copied_at, is_pinned)
            VALUES ($id, $type, $raw, $text, $thumb, $title, $app, $bundle, $hash, $at, $pin)
            """;
        insert.Parameters.AddWithValue("$id", item.Id.ToString());
        insert.Parameters.AddWithValue("$type", item.ContentType);
        insert.Parameters.AddWithValue("$raw", item.Raw);
        insert.Parameters.AddWithValue("$text", (object?)item.Text ?? DBNull.Value);
        insert.Parameters.AddWithValue("$thumb", (object?)item.Thumbnail ?? DBNull.Value);
        insert.Parameters.AddWithValue("$title", (object?)item.Title ?? DBNull.Value);
        insert.Parameters.AddWithValue("$app", (object?)item.SourceApp ?? DBNull.Value);
        insert.Parameters.AddWithValue("$bundle", (object?)item.SourceBundle ?? DBNull.Value);
        insert.Parameters.AddWithValue("$hash", item.Hash);
        insert.Parameters.AddWithValue("$at", item.CopiedAt.ToString("O", CultureInfo.InvariantCulture));
        insert.Parameters.AddWithValue("$pin", item.IsPinned ? 1 : 0);
        insert.ExecuteNonQuery();
    }

    private ClipItem? Load(SqliteConnection db, Guid id)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT * FROM clips WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id.ToString());
        return ReadClips(cmd).FirstOrDefault();
    }

    private static List<EntryRow> EntriesFor(SqliteConnection db, Guid itemId, Guid? boardId)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = boardId is null
            ? "SELECT pinboard_id, item_id, display_order, added_at FROM entries WHERE item_id = $item"
            : "SELECT pinboard_id, item_id, display_order, added_at FROM entries WHERE item_id = $item AND pinboard_id = $board";
        cmd.Parameters.AddWithValue("$item", itemId.ToString());
        if (boardId is Guid board) cmd.Parameters.AddWithValue("$board", board.ToString());
        var list = new List<EntryRow>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(new EntryRow(
                Guid.Parse(reader.GetString(0)),
                Guid.Parse(reader.GetString(1)),
                reader.GetInt32(2),
                reader.IsDBNull(3) ? DateTimeOffset.Now : ParseTime(reader.GetString(3))));
        }
        return list;
    }

    private static List<ClipItem> ReadClips(SqliteCommand cmd)
    {
        var list = new List<ClipItem>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(new ClipItem
            {
                Id = Guid.Parse(reader.GetString(reader.GetOrdinal("id"))),
                ContentType = reader.GetString(reader.GetOrdinal("content_type")),
                Raw = (byte[])reader["raw_data"],
                Text = OptionalString(reader, "text_content"),
                Title = OptionalString(reader, "user_title"),
                SourceApp = OptionalString(reader, "source_app"),
                SourceBundle = OptionalString(reader, "source_bundle"),
                Thumbnail = OptionalBytes(reader, "thumbnail_data"),
                Hash = reader.GetString(reader.GetOrdinal("content_hash")),
                CopiedAt = ParseTime(reader.GetString(reader.GetOrdinal("copied_at"))),
                IsPinned = HasColumn(reader, "is_pinned") && !reader.IsDBNull(reader.GetOrdinal("is_pinned")) && reader.GetInt32(reader.GetOrdinal("is_pinned")) != 0,
            });
        }
        return list;
    }

    private static bool HasColumn(SqliteDataReader reader, string name)
    {
        for (var i = 0; i < reader.FieldCount; i++)
        {
            if (string.Equals(reader.GetName(i), name, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    private static string? OptionalString(SqliteDataReader reader, string name)
    {
        if (!HasColumn(reader, name)) return null;
        var ordinal = reader.GetOrdinal(name);
        return reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    }

    private static byte[]? OptionalBytes(SqliteDataReader reader, string name)
    {
        if (!HasColumn(reader, name)) return null;
        var ordinal = reader.GetOrdinal(name);
        return reader.IsDBNull(ordinal) ? null : (byte[])reader.GetValue(ordinal);
    }

    private static DateTimeOffset ParseTime(string text) =>
        DateTimeOffset.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    private static string EscapeLike(string text) =>
        text.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

    private SqliteConnection Connect()
    {
        var db = new SqliteConnection($"Data Source={_path}");
        db.Open();
        using var pragma = db.CreateCommand();
        pragma.CommandText = "PRAGMA foreign_keys = ON;";
        pragma.ExecuteNonQuery();
        return db;
    }

    private sealed class UndoBatch
    {
        public List<ClipItem> Clips { get; } = [];
        public List<EntryRow> Entries { get; set; } = [];
    }

    private readonly record struct EntryRow(Guid BoardId, Guid ItemId, int Order, DateTimeOffset AddedAt);
}
