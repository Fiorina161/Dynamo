using Dapper;
using Dynamo.Core;

namespace Dynamo.Modules.Bookmarks;

public sealed class Bookmark
{
    public long Id { get; set; }
    public long? GroupId { get; set; }
    public string Label { get; set; } = "";
    public string Target { get; set; } = "";
    public long Position { get; set; }

    /// <summary>True for http(s) links, which the browser opens itself.</summary>
    public bool IsWeb => Launcher.IsWeb(Target);

    /// <summary>
    /// Muted second line of a row: the site for a web link, otherwise what
    /// kind of thing it opens. The full target is never shown.
    /// </summary>
    public string Hint =>
        Uri.TryCreate(Target, UriKind.Absolute, out var uri) && uri.Scheme.Length > 1
            ? (IsWeb ? uri.Host : uri.IsFile ? "local file" : $"{uri.Scheme}:")
            : "local file";
}

public sealed class BookmarkGroup
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
    public long Position { get; set; }
    public bool Collapsed { get; set; }
    public List<Bookmark> Bookmarks { get; set; } = [];
}

/// <summary>Everything the bookmarks page shows.</summary>
public sealed class Board
{
    public List<Bookmark> Ungrouped { get; set; } = [];
    public List<BookmarkGroup> Groups { get; set; } = [];
}

public sealed class BookmarkRepository(Db db)
{
    public Board GetBoard()
    {
        using var conn = db.Open();
        var groups = conn.Query<BookmarkGroup>("""
            SELECT id, name, position, collapsed FROM bookmark_group
            ORDER BY position, id
            """).ToList();
        var bookmarks = conn.Query<Bookmark>("""
            SELECT id, group_id AS GroupId, label, target, position FROM bookmark
            ORDER BY position, id
            """).ToList();

        var byId = groups.ToDictionary(g => g.Id);
        var board = new Board { Groups = groups };
        foreach (var b in bookmarks)
        {
            if (b.GroupId is { } gid && byId.TryGetValue(gid, out var g))
                g.Bookmarks.Add(b);
            else
                board.Ungrouped.Add(b);
        }
        return board;
    }

    public Bookmark? GetBookmark(long id)
    {
        using var conn = db.Open();
        return conn.QuerySingleOrDefault<Bookmark>(
            "SELECT id, group_id AS GroupId, label, target, position FROM bookmark WHERE id = @id",
            new { id });
    }

    public BookmarkGroup? GetGroup(long id)
    {
        using var conn = db.Open();
        return conn.QuerySingleOrDefault<BookmarkGroup>(
            "SELECT id, name, position, collapsed FROM bookmark_group WHERE id = @id",
            new { id });
    }

    /// <summary>Inserts when id is 0, otherwise updates.</summary>
    public void SaveBookmark(long id, string label, string target, long? groupId)
    {
        using var conn = db.Open();
        if (id == 0)
        {
            conn.Execute("""
                INSERT INTO bookmark (group_id, label, target, position)
                VALUES (@groupId, @label, @target,
                        (SELECT COALESCE(MAX(position), 0) + 1 FROM bookmark
                         WHERE group_id IS @groupId))
                """, new { groupId, label, target });
            return;
        }

        // A change of group puts the bookmark at the end of its new group.
        conn.Execute("""
            UPDATE bookmark
               SET label = @label, target = @target,
                   position = CASE WHEN group_id IS @groupId THEN position
                                   ELSE (SELECT COALESCE(MAX(position), 0) + 1
                                         FROM bookmark WHERE group_id IS @groupId) END,
                   group_id = @groupId
             WHERE id = @id
            """, new { id, label, target, groupId });
    }

    public void DeleteBookmark(long id)
    {
        using var conn = db.Open();
        conn.Execute("DELETE FROM bookmark WHERE id = @id", new { id });
    }

    /// <summary>
    /// Puts the given bookmarks, in the given order, in a group (null =
    /// ungrouped). Used by drag and drop: the page sends the final order of
    /// the group the bookmark was dropped in.
    /// </summary>
    public void Arrange(long? groupId, IReadOnlyList<long> orderedIds)
    {
        using var conn = db.Open();
        using var tx = conn.BeginTransaction();
        for (var i = 0; i < orderedIds.Count; i++)
            conn.Execute(
                "UPDATE bookmark SET group_id = @groupId, position = @position WHERE id = @id",
                new { groupId, position = i + 1, id = orderedIds[i] }, tx);
        tx.Commit();
    }

    /// <summary>Inserts when id is 0, otherwise renames. Returns the id.</summary>
    public long SaveGroup(long id, string name)
    {
        using var conn = db.Open();
        if (id != 0)
        {
            conn.Execute("UPDATE bookmark_group SET name = @name WHERE id = @id",
                new { id, name });
            return id;
        }

        return conn.ExecuteScalar<long>("""
            INSERT INTO bookmark_group (name, position)
            VALUES (@name, (SELECT COALESCE(MAX(position), 0) + 1 FROM bookmark_group));
            SELECT last_insert_rowid();
            """, new { name });
    }

    /// <summary>Gives the groups, in the given order, their new positions.</summary>
    public void ArrangeGroups(IReadOnlyList<long> orderedIds)
    {
        using var conn = db.Open();
        using var tx = conn.BeginTransaction();
        for (var i = 0; i < orderedIds.Count; i++)
            conn.Execute("UPDATE bookmark_group SET position = @position WHERE id = @id",
                new { position = i + 1, id = orderedIds[i] }, tx);
        tx.Commit();
    }

    /// <summary>Deletes the group; its bookmarks become ungrouped.</summary>
    public void DeleteGroup(long id)
    {
        using var conn = db.Open();
        conn.Execute("DELETE FROM bookmark_group WHERE id = @id", new { id });
    }

    public void ToggleGroup(long id)
    {
        using var conn = db.Open();
        conn.Execute("UPDATE bookmark_group SET collapsed = 1 - collapsed WHERE id = @id",
            new { id });
    }
}
