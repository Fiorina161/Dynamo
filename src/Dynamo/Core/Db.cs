using Microsoft.Data.Sqlite;

namespace Dynamo.Core;

/// <summary>
/// Opens connections to the local Sqlite database and brings its schema up
/// to date from the modules' migrations.
/// </summary>
public sealed class Db
{
    private readonly string _connectionString;

    public Db(DynamoHome home)
    {
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = home.DatabaseFile,
            Mode = SqliteOpenMode.ReadWriteCreate,
            ForeignKeys = true,
        }.ToString();
    }

    public SqliteConnection Open()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        return conn;
    }

    /// <summary>Applies the migrations each module has not run yet.</summary>
    public void Migrate(IEnumerable<IModule> modules)
    {
        using var conn = Open();
        Exec(conn, "PRAGMA journal_mode = WAL");
        Exec(conn, """
            CREATE TABLE IF NOT EXISTS schema_migration (
                module     TEXT    NOT NULL,
                version    INTEGER NOT NULL,
                applied_at TEXT    NOT NULL,
                PRIMARY KEY (module, version))
            """);

        foreach (var module in modules)
        {
            var applied = Scalar(conn,
                "SELECT COALESCE(MAX(version), 0) FROM schema_migration WHERE module = $m",
                module.Id);

            for (var version = (int)applied + 1; version <= module.Migrations.Count; version++)
            {
                using var tx = conn.BeginTransaction();
                Exec(conn, module.Migrations[version - 1], tx);
                using var cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = "INSERT INTO schema_migration VALUES ($m, $v, $t)";
                cmd.Parameters.AddWithValue("$m", module.Id);
                cmd.Parameters.AddWithValue("$v", version);
                cmd.Parameters.AddWithValue("$t", DateTime.UtcNow.ToString("o"));
                cmd.ExecuteNonQuery();
                tx.Commit();
            }
        }
    }

    private static void Exec(SqliteConnection conn, string sql, SqliteTransaction? tx = null)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static long Scalar(SqliteConnection conn, string sql, string module)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("$m", module);
        return Convert.ToInt64(cmd.ExecuteScalar());
    }
}
