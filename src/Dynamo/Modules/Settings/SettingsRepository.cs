using Dapper;
using Dynamo.Core;

namespace Dynamo.Modules.Settings;

/// <summary>Key/value settings in the <c>setting</c> table.</summary>
public sealed class SettingsRepository(Db db)
{
    public string? Get(string key)
    {
        using var conn = db.Open();
        return conn.ExecuteScalar<string?>("SELECT value FROM setting WHERE key = @key", new { key });
    }

    public void Set(string key, string value)
    {
        using var conn = db.Open();
        conn.Execute("""
            INSERT INTO setting (key, value) VALUES (@key, @value)
            ON CONFLICT(key) DO UPDATE SET value = excluded.value
            """, new { key, value });
    }

    public void Delete(string key)
    {
        using var conn = db.Open();
        conn.Execute("DELETE FROM setting WHERE key = @key", new { key });
    }
}
