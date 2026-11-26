using Dapper;
using Dynamo.Core;

namespace Dynamo.Modules.OracleConnections;

public sealed class OracleConnectionInfo
{
    public long Id { get; set; }

    /// <summary>The name other features use to ask for this connection.</summary>
    public string Name { get; set; } = "";

    /// <summary>
    /// What to connect to: a TNS alias (resolved through TNS_ADMIN), an Easy
    /// Connect string (host:port/service) or a full descriptor.
    /// </summary>
    public string DataSource { get; set; } = "";

    /// <summary>The schema (database user) to log in as.</summary>
    public string Username { get; set; } = "";

    /// <summary>
    /// The password in clear in memory only: in the database it is protected (<see cref="Secrets"/>,
    /// Windows DPAPI at machine scope), and it is never rendered back in a page.
    /// </summary>
    public string Password { get; set; } = "";

    /// <summary>
    /// The stored password is protected and cannot be decrypted on this computer (the database was
    /// copied from another one, or Windows was reinstalled): the connection is not ready until the
    /// password is entered again.
    /// </summary>
    public bool PasswordUnreadable { get; set; }

    /// <summary>Pinned connections come first in lists and pickers.</summary>
    public bool Pinned { get; set; }

    /// <summary>
    /// A DBA connection: Dynamo logs in AS SYSDBA (the schema is usually SYS), and the
    /// DBA features (workspace templates, the sessions panel) are offered on it.
    /// </summary>
    public bool Dba { get; set; }

    /// <summary>A key of <see cref="ConnectionColors"/>, or null for no colour.</summary>
    public string? Color { get; set; }

    /// <summary>The data source in a short, readable form (for lists).</summary>
    public string Summary => DataSourceSummary.Of(DataSource);

    /// <summary>
    /// False for a connection imported from tnsnames.ora whose schema and
    /// password have not been filled in yet: it cannot log in.
    /// </summary>
    public bool Complete => Username.Length > 0 && Password.Length > 0;
}

public sealed class OracleConnectionRepository(Db db)
{
    private const string Columns =
        "id, name, data_source AS DataSource, username, password, pinned, dba, color";

    /// <summary>The stored password becomes the clear one (or unreadable, see the property).</summary>
    private static OracleConnectionInfo? Reveal(OracleConnectionInfo? c)
    {
        if (c is null)
            return null;
        if (Secrets.TryUnprotect(c.Password, out var clear))
            c.Password = clear;
        else
        {
            c.Password = "";
            c.PasswordUnreadable = true;
        }
        return c;
    }

    public List<OracleConnectionInfo> GetAll()
    {
        using var conn = db.Open();
        return conn.Query<OracleConnectionInfo>(
            $"SELECT {Columns} FROM oracle_connection ORDER BY pinned DESC, name").Select(c => Reveal(c)!).ToList();
    }

    public OracleConnectionInfo? Get(long id)
    {
        using var conn = db.Open();
        return Reveal(conn.QuerySingleOrDefault<OracleConnectionInfo>(
            $"SELECT {Columns} FROM oracle_connection WHERE id = @id", new { id }));
    }

    /// <summary>Case-insensitive, like the unique index on the name.</summary>
    public OracleConnectionInfo? GetByName(string name)
    {
        using var conn = db.Open();
        return Reveal(conn.QuerySingleOrDefault<OracleConnectionInfo>(
            $"SELECT {Columns} FROM oracle_connection WHERE name = @name", new { name }));
    }

    /// <summary>The values to write: the password protected, never in clear.</summary>
    private static object ToStore(OracleConnectionInfo c) => new
    {
        c.Id, c.Name, c.DataSource, c.Username, Password = Secrets.Protect(c.Password), c.Dba, c.Color,
    };

    /// <summary>Inserts when Id is 0, otherwise updates. Returns the id.</summary>
    public long Save(OracleConnectionInfo c)
    {
        using var conn = db.Open();
        if (c.Id != 0)
        {
            conn.Execute("""
                UPDATE oracle_connection
                   SET name = @Name, data_source = @DataSource,
                       username = @Username, password = @Password,
                       dba = @Dba, color = @Color
                 WHERE id = @Id
                """, ToStore(c));
            return c.Id;
        }

        return conn.ExecuteScalar<long>("""
            INSERT INTO oracle_connection (name, data_source, username, password, dba, color)
            VALUES (@Name, @DataSource, @Username, @Password, @Dba, @Color);
            SELECT last_insert_rowid();
            """, ToStore(c));
    }

    /// <summary>Pins or unpins a connection (a quick toggle, no other change).</summary>
    public void SetPinned(long id, bool pinned)
    {
        using var conn = db.Open();
        conn.Execute("UPDATE oracle_connection SET pinned = @pinned WHERE id = @id",
            new { id, pinned = pinned ? 1 : 0 });
    }

    public void Delete(long id)
    {
        using var conn = db.Open();
        conn.Execute("DELETE FROM oracle_connection WHERE id = @id", new { id });
    }

    /// <summary>The connections with these ids (unknown ids are ignored), by name.</summary>
    public List<OracleConnectionInfo> GetMany(IEnumerable<long> ids)
    {
        var wanted = ids.Distinct().ToList();
        if (wanted.Count == 0)
            return [];
        using var conn = db.Open();
        return conn.Query<OracleConnectionInfo>(
            $"SELECT {Columns} FROM oracle_connection WHERE id IN @wanted ORDER BY pinned DESC, name",
            new { wanted }).Select(c => Reveal(c)!).ToList();
    }

    /// <summary>Deletes these connections in one transaction; returns how many existed.</summary>
    public int DeleteMany(IEnumerable<long> ids)
    {
        var wanted = ids.Distinct().ToList();
        if (wanted.Count == 0)
            return 0;
        using var conn = db.Open();
        using var tx = conn.BeginTransaction();
        var deleted = 0;
        // In slices, well under SQLite's limit on bound parameters.
        foreach (var slice in wanted.Chunk(500))
            deleted += conn.Execute(
                "DELETE FROM oracle_connection WHERE id IN @slice", new { slice }, tx);
        tx.Commit();
        return deleted;
    }

    /// <summary>True if another connection already has this name.</summary>
    public bool NameTaken(string name, long exceptId)
    {
        using var conn = db.Open();
        return conn.ExecuteScalar<long>(
            "SELECT COUNT(*) FROM oracle_connection WHERE name = @name AND id <> @exceptId",
            new { name, exceptId }) > 0;
    }
}
