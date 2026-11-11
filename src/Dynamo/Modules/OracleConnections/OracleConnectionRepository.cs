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

    /// <summary>Stored as typed, in clear: Dynamo has no security by design.</summary>
    public string Password { get; set; } = "";
}

public sealed class OracleConnectionRepository(Db db)
{
    private const string Columns =
        "id, name, data_source AS DataSource, username, password";

    public List<OracleConnectionInfo> GetAll()
    {
        using var conn = db.Open();
        return conn.Query<OracleConnectionInfo>(
            $"SELECT {Columns} FROM oracle_connection ORDER BY name").ToList();
    }

    public OracleConnectionInfo? Get(long id)
    {
        using var conn = db.Open();
        return conn.QuerySingleOrDefault<OracleConnectionInfo>(
            $"SELECT {Columns} FROM oracle_connection WHERE id = @id", new { id });
    }

    /// <summary>Case-insensitive, like the unique index on the name.</summary>
    public OracleConnectionInfo? GetByName(string name)
    {
        using var conn = db.Open();
        return conn.QuerySingleOrDefault<OracleConnectionInfo>(
            $"SELECT {Columns} FROM oracle_connection WHERE name = @name", new { name });
    }

    /// <summary>Inserts when Id is 0, otherwise updates. Returns the id.</summary>
    public long Save(OracleConnectionInfo c)
    {
        using var conn = db.Open();
        if (c.Id != 0)
        {
            conn.Execute("""
                UPDATE oracle_connection
                   SET name = @Name, data_source = @DataSource,
                       username = @Username, password = @Password
                 WHERE id = @Id
                """, c);
            return c.Id;
        }

        return conn.ExecuteScalar<long>("""
            INSERT INTO oracle_connection (name, data_source, username, password)
            VALUES (@Name, @DataSource, @Username, @Password);
            SELECT last_insert_rowid();
            """, c);
    }

    public void Delete(long id)
    {
        using var conn = db.Open();
        conn.Execute("DELETE FROM oracle_connection WHERE id = @id", new { id });
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
