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
}

public sealed class SharedFolderRepository(Db db)
{
    private const string Columns = "id, name, path, username, password";

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
        var stored = new { f.Id, f.Name, f.Path, f.Username, Password = Secrets.Protect(f.Password) };
        using var conn = db.Open();
        if (f.Id != 0)
        {
            conn.Execute("""
                UPDATE shared_folder
                   SET name = @Name, path = @Path, username = @Username, password = @Password
                 WHERE id = @Id
                """, stored);
            return f.Id;
        }
        return conn.ExecuteScalar<long>("""
            INSERT INTO shared_folder (name, path, username, password)
            VALUES (@Name, @Path, @Username, @Password);
            SELECT last_insert_rowid();
            """, stored);
    }

    public void Delete(long id)
    {
        using var conn = db.Open();
        conn.Execute("DELETE FROM shared_folder WHERE id = @id", new { id });
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
