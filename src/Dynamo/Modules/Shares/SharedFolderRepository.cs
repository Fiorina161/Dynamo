using Dapper;
using Dynamo.Core;

namespace Dynamo.Modules.Shares;

public sealed class SharedFolder
{
    public long Id { get; set; }

    /// <summary>The name shown in the list (unique, case-insensitive).</summary>
    public string Name { get; set; } = "";

    /// <summary>The network folder, <c>\\server\share</c> or deeper (see <see cref="SharePath"/>).</summary>
    public string Path { get; set; } = "";

    /// <summary>The account the folder is read with, <c>user</c> or <c>DOMAIN\user</c>.</summary>
    public string Username { get; set; } = "";

    /// <summary>
    /// In clear in memory only: in the database it is protected (<see cref="Secrets"/>, Windows DPAPI at machine
    /// scope, like the Oracle passwords), and it is never rendered back in a page.
    /// </summary>
    public string Password { get; set; } = "";

    /// <summary>The stored password cannot be decrypted on this computer: it must be entered again.</summary>
    public bool PasswordUnreadable { get; set; }

    /// <summary>Files may be uploaded to this folder (the account must be allowed to write there too).</summary>
    public bool AllowUpload { get; set; } = true;
}

/// <summary>One substring of a folder's filter list; ticked (active) ones narrow the file list.</summary>
public sealed class SharedFolderFilter
{
    public long Id { get; set; }
    public long FolderId { get; set; }
    public string Text { get; set; } = "";
    public bool Active { get; set; }
}

public sealed class SharedFolderRepository(Db db)
{
    public const int MaxFilterLength = 100;

    private const string Columns = "id, name, path, username, password, allow_upload AS AllowUpload";

    private static SharedFolder? Reveal(SharedFolder? f)
    {
        if (f is null)
            return null;
        if (Secrets.TryUnprotect(f.Password, out var clear))
            f.Password = clear;
        else
        {
            f.Password = "";
            f.PasswordUnreadable = true;
        }
        return f;
    }

    public List<SharedFolder> GetAll()
    {
        using var conn = db.Open();
        return conn.Query<SharedFolder>($"SELECT {Columns} FROM shared_folder ORDER BY name")
            .Select(f => Reveal(f)!).ToList();
    }

    public SharedFolder? Get(long id)
    {
        using var conn = db.Open();
        return Reveal(conn.QuerySingleOrDefault<SharedFolder>(
            $"SELECT {Columns} FROM shared_folder WHERE id = @id", new { id }));
    }

    /// <summary>Inserts when Id is 0, otherwise updates. Returns the id.</summary>
    public long Save(SharedFolder f)
    {
        var stored = new { f.Id, f.Name, f.Path, f.Username, Password = Secrets.Protect(f.Password), f.AllowUpload };
        using var conn = db.Open();
        if (f.Id != 0)
        {
            conn.Execute("""
                UPDATE shared_folder
                   SET name = @Name, path = @Path, username = @Username, password = @Password,
                       allow_upload = @AllowUpload
                 WHERE id = @Id
                """, stored);
            return f.Id;
        }
        return conn.ExecuteScalar<long>("""
            INSERT INTO shared_folder (name, path, username, password, allow_upload)
            VALUES (@Name, @Path, @Username, @Password, @AllowUpload);
            SELECT last_insert_rowid();
            """, stored);
    }

    public void Delete(long id)
    {
        using var conn = db.Open();
        conn.Execute("DELETE FROM shared_folder WHERE id = @id", new { id });
    }

    // ---- The substring filters of a folder ---------------------------------------------------------------

    public List<SharedFolderFilter> GetFilters(long folderId)
    {
        using var conn = db.Open();
        return conn.Query<SharedFolderFilter>("""
            SELECT id, folder_id AS FolderId, text, active
              FROM shared_folder_filter WHERE folder_id = @folderId ORDER BY position, id
            """, new { folderId }).ToList();
    }

    public SharedFolderFilter? GetFilter(long id)
    {
        using var conn = db.Open();
        return conn.QuerySingleOrDefault<SharedFolderFilter>(
            "SELECT id, folder_id AS FolderId, text, active FROM shared_folder_filter WHERE id = @id", new { id });
    }

    /// <summary>Why this text cannot be a filter of the folder (empty, too long, already there), or null.</summary>
    public string? FilterProblem(long folderId, string text, long exceptId)
    {
        if (text.Length == 0) return "Type a substring.";
        if (text.Length > MaxFilterLength) return $"The substring is too long (max {MaxFilterLength}).";
        // Case and accents do not matter when filtering, so "été" and "ETE" are the same substring.
        var folded = TextMatch.Fold(text);
        var same = GetFilters(folderId).FirstOrDefault(f => f.Id != exceptId && TextMatch.Fold(f.Text) == folded);
        return same is null ? null : $"“{same.Text}” is already in the list (case and accents do not matter).";
    }

    /// <summary>Adds (id 0) or changes a filter, keeping its place and its tick; returns its id.</summary>
    public long SaveFilter(long folderId, long id, string text)
    {
        using var conn = db.Open();
        if (id != 0)
        {
            conn.Execute("UPDATE shared_folder_filter SET text = @text WHERE id = @id AND folder_id = @folderId",
                new { id, folderId, text });
            return id;
        }
        return conn.ExecuteScalar<long>("""
            INSERT INTO shared_folder_filter (folder_id, text, position)
            VALUES (@folderId, @text,
                    (SELECT COALESCE(MAX(position), 0) + 1 FROM shared_folder_filter WHERE folder_id = @folderId));
            SELECT last_insert_rowid();
            """, new { folderId, text });
    }

    public void DeleteFilter(long id)
    {
        using var conn = db.Open();
        conn.Execute("DELETE FROM shared_folder_filter WHERE id = @id", new { id });
    }

    public void SetFilterActive(long id, bool active)
    {
        using var conn = db.Open();
        conn.Execute("UPDATE shared_folder_filter SET active = @active WHERE id = @id",
            new { id, active = active ? 1 : 0 });
    }

    /// <summary>Unticks every filter of the folder (the whole list shows again).</summary>
    public void ClearActiveFilters(long folderId)
    {
        using var conn = db.Open();
        conn.Execute("UPDATE shared_folder_filter SET active = 0 WHERE folder_id = @folderId", new { folderId });
    }

    /// <summary>True if another folder already has this name.</summary>
    public bool NameTaken(string name, long exceptId)
    {
        using var conn = db.Open();
        return conn.ExecuteScalar<long>(
            "SELECT COUNT(*) FROM shared_folder WHERE name = @name AND id <> @exceptId",
            new { name, exceptId }) > 0;
    }
}
