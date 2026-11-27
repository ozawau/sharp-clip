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
    public string Hash { get; init; } = "";
    public DateTimeOffset CopiedAt { get; init; }

    public string Display => string.IsNullOrWhiteSpace(Title)
        ? (string.IsNullOrWhiteSpace(Text) ? ContentType : Text!)
        : Title!;
}

internal sealed class Pinboard
{
    public Guid Id { get; init; }
    public string Name { get; set; } = "";
    public int Order { get; set; }
}

internal sealed class Library
{
    public static Library Shared { get; } = new();

    private readonly string _path;
    private readonly JsonSerializerOptions _json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private Library()
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SharpClip");
        Directory.CreateDirectory(dir);
        _path = Path.Combine(dir, "sharpclip.db");
    }

    public void Open()
    {
        using var db = Connect();
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
    }

    public ClipItem? Record(CapturedClip captured)
    {
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
                bump.CommandText = "UPDATE clips SET copied_at = $at, source_app = $app WHERE id = $id";
                bump.Parameters.AddWithValue("$at", DateTimeOffset.Now.ToString("O"));
                bump.Parameters.AddWithValue("$app", (object?)captured.SourceApp ?? DBNull.Value);
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
            Hash = hash,
            CopiedAt = DateTimeOffset.Now,
        };
        using var insert = db.CreateCommand();
        insert.CommandText = """
            INSERT INTO clips (id, content_type, raw_data, text_content, user_title, source_app, content_hash, copied_at)
            VALUES ($id, $type, $raw, $text, NULL, $app, $hash, $at)
            """;
        insert.Parameters.AddWithValue("$id", item.Id.ToString());
        insert.Parameters.AddWithValue("$type", item.ContentType);
        insert.Parameters.AddWithValue("$raw", item.Raw);
        insert.Parameters.AddWithValue("$text", (object?)item.Text ?? DBNull.Value);
        insert.Parameters.AddWithValue("$app", (object?)item.SourceApp ?? DBNull.Value);
        insert.Parameters.AddWithValue("$hash", item.Hash);
        insert.Parameters.AddWithValue("$at", item.CopiedAt.ToString("O"));
        insert.ExecuteNonQuery();
        Trim(db);
        return item;
    }

    public IReadOnlyList<ClipItem> Search(string query, Guid? pinboardId, string? type)
    {
        using var db = Connect();
        using var cmd = db.CreateCommand();
        var sql = pinboardId is null
            ? "SELECT c.* FROM clips c WHERE 1=1"
            : "SELECT c.* FROM clips c JOIN entries e ON e.item_id = c.id WHERE e.pinboard_id = $board";
        if (!string.IsNullOrWhiteSpace(type) && type != "all") sql += " AND c.content_type = $type";
        if (!string.IsNullOrWhiteSpace(query)) sql += " AND (c.text_content LIKE $q OR c.user_title LIKE $q OR c.source_app LIKE $q)";
        sql += pinboardId is null
            ? " ORDER BY c.copied_at DESC LIMIT 500"
            : " ORDER BY e.display_order, c.copied_at DESC LIMIT 500";
        cmd.CommandText = sql;
        if (pinboardId is Guid board) cmd.Parameters.AddWithValue("$board", board.ToString());
        if (!string.IsNullOrWhiteSpace(type) && type != "all") cmd.Parameters.AddWithValue("$type", type);
        if (!string.IsNullOrWhiteSpace(query)) cmd.Parameters.AddWithValue("$q", $"%{query}%");
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
        cmd.CommandText = "SELECT id, name, display_order FROM pinboards ORDER BY display_order, name";
        var list = new List<Pinboard>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(new Pinboard
            {
                Id = Guid.Parse(reader.GetString(0)),
                Name = reader.GetString(1),
                Order = reader.GetInt32(2),
            });
        }
        return list;
    }

    public Pinboard AddBoard(string name)
    {
        var board = new Pinboard { Id = Guid.NewGuid(), Name = name.Trim(), Order = Boards().Count };
        using var db = Connect();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "INSERT INTO pinboards (id, name, display_order) VALUES ($id, $name, $order)";
        cmd.Parameters.AddWithValue("$id", board.Id.ToString());
        cmd.Parameters.AddWithValue("$name", board.Name);
        cmd.Parameters.AddWithValue("$order", board.Order);
        cmd.ExecuteNonQuery();
        return board;
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
            cmd.Parameters.AddWithValue("$at", now.AddMilliseconds(ids.Count - i).ToString("O"));
            cmd.Parameters.AddWithValue("$id", ids[i].ToString());
            cmd.ExecuteNonQuery();
        }
    }

    public void AddToBoard(Guid boardId, Guid itemId)
    {
        using var db = Connect();
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            INSERT OR IGNORE INTO entries (pinboard_id, item_id, display_order)
            VALUES ($board, $item, (SELECT COUNT(*) FROM entries WHERE pinboard_id = $board))
            """;
        cmd.Parameters.AddWithValue("$board", boardId.ToString());
        cmd.Parameters.AddWithValue("$item", itemId.ToString());
        cmd.ExecuteNonQuery();
    }

    public void DeleteClips(IEnumerable<Guid> ids)
    {
        using var db = Connect();
        foreach (var id in ids)
        {
            using var cmd = db.CreateCommand();
            cmd.CommandText = "DELETE FROM entries WHERE item_id = $id; DELETE FROM clips WHERE id = $id;";
            cmd.Parameters.AddWithValue("$id", id.ToString());
            cmd.ExecuteNonQuery();
        }
    }

    public void RemoveFromBoard(Guid boardId, Guid itemId)
    {
        using var db = Connect();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "DELETE FROM entries WHERE pinboard_id = $board AND item_id = $item";
        cmd.Parameters.AddWithValue("$board", boardId.ToString());
        cmd.Parameters.AddWithValue("$item", itemId.ToString());
        cmd.ExecuteNonQuery();
    }

    public void RenameBoard(Guid id, string name)
    {
        using var db = Connect();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "UPDATE pinboards SET name = $name WHERE id = $id";
        cmd.Parameters.AddWithValue("$name", name);
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
    }

    public void UpdateText(Guid id, string text)
    {
        var raw = System.Text.Encoding.UTF8.GetBytes(text);
        using var db = Connect();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "UPDATE clips SET text_content = $text, raw_data = $raw, content_hash = $hash WHERE id = $id";
        cmd.Parameters.AddWithValue("$text", text);
        cmd.Parameters.AddWithValue("$raw", raw);
        cmd.Parameters.AddWithValue("$hash", ClipboardCapture.Hash(raw));
        cmd.Parameters.AddWithValue("$id", id.ToString());
        cmd.ExecuteNonQuery();
    }

    public void Rename(Guid id, string title)
    {
        using var db = Connect();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "UPDATE clips SET user_title = $title WHERE id = $id";
        cmd.Parameters.AddWithValue("$title", title);
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
        cmd.CommandText = "DELETE FROM exclusions WHERE process_name = $p";
        cmd.Parameters.AddWithValue("$p", processName);
        cmd.ExecuteNonQuery();
    }

    public byte[] ExportJson()
    {
        var doc = new TransferDocument
        {
            Version = 1,
            ExportedAt = DateTimeOffset.Now,
            Items = Search("", null, null).Select(item => new TransferItem
            {
                Id = item.Id,
                ContentType = item.ContentType,
                RawData = item.Raw,
                TextContent = item.Text,
                SourceAppName = item.SourceApp,
                ContentHash = item.Hash,
                CopiedAt = item.CopiedAt,
                UserTitle = item.Title,
            }).ToList(),
            Pinboards = Boards().Select(board => new TransferBoard
            {
                Id = board.Id,
                Name = board.Name,
                DisplayOrder = board.Order,
                CreatedAt = DateTimeOffset.Now,
            }).ToList(),
        };
        return JsonSerializer.SerializeToUtf8Bytes(doc, _json);
    }

    public (int Imported, int Skipped) ImportJson(byte[] data)
    {
        var doc = JsonSerializer.Deserialize<TransferDocument>(data, _json);
        if (doc is null) return (0, 0);
        var imported = 0;
        var skipped = 0;
        foreach (var record in doc.Items)
        {
            var captured = new CapturedClip(record.ContentType, record.RawData, record.TextContent, record.SourceAppName);
            var before = Search("", null, null).Count;
            var item = Record(captured);
            if (item is not null && !string.IsNullOrWhiteSpace(record.UserTitle))
            {
                Rename(item.Id, record.UserTitle);
            }
            if (Search("", null, null).Count == before) skipped++;
            else imported++;
        }
        foreach (var board in doc.Pinboards)
        {
            if (Boards().All(existing => !string.Equals(existing.Name, board.Name, StringComparison.OrdinalIgnoreCase)))
            {
                AddBoard(board.Name);
            }
        }
        return (imported, skipped);
    }

    private void Trim(SqliteConnection db)
    {
        var limit = SettingsStore.Current.HistoryLimit;
        if (limit <= 0) return;
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            DELETE FROM clips
            WHERE id NOT IN (SELECT item_id FROM entries)
              AND id NOT IN (SELECT id FROM clips ORDER BY copied_at DESC LIMIT $limit)
            """;
        cmd.Parameters.AddWithValue("$limit", limit);
        cmd.ExecuteNonQuery();
    }

    private ClipItem? Load(SqliteConnection db, Guid id)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT * FROM clips WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id.ToString());
        return ReadClips(cmd).FirstOrDefault();
    }

    private static List<ClipItem> ReadClips(SqliteCommand cmd)
    {
        var list = new List<ClipItem>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(new ClipItem
            {
                Id = Guid.Parse(reader.GetString(0)),
                ContentType = reader.GetString(1),
                Raw = (byte[])reader["raw_data"],
                Text = reader.IsDBNull(3) ? null : reader.GetString(3),
                Title = reader.IsDBNull(4) ? null : reader.GetString(4),
                SourceApp = reader.IsDBNull(5) ? null : reader.GetString(5),
                Hash = reader.GetString(6),
                CopiedAt = DateTimeOffset.Parse(reader.GetString(7)),
            });
        }
        return list;
    }

    private SqliteConnection Connect()
    {
        var db = new SqliteConnection($"Data Source={_path}");
        db.Open();
        return db;
    }
}

internal static class ClipQueue
{
    private static readonly List<ClipItem> Items = [];
    public static bool IsActive { get; private set; }

    public static void Toggle()
    {
        IsActive = !IsActive;
        if (!IsActive) Items.Clear();
    }

    public static void Push(ClipItem item)
    {
        Items.RemoveAll(existing => existing.Id == item.Id);
        Items.Add(item);
    }

    public static ClipItem? Pop()
    {
        if (Items.Count == 0) return null;
        var next = Items[0];
        Items.RemoveAt(0);
        if (Items.Count == 0) IsActive = false;
        return next;
    }
}
