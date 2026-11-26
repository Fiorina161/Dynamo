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

    /// <summary>
    /// A secret setting (a certificate with its private key): protected in the database
    /// (<see cref="Secrets"/>). Null when there is none, or when it cannot be decrypted on this computer.
    /// </summary>
    public string? GetSecret(string key)
    {
        var stored = Get(key);
        return Secrets.TryUnprotect(stored, out var clear) ? clear : null;
    }

    public void SetSecret(string key, string value) => Set(key, Secrets.Protect(value));

    public void Delete(string key)
    {
        using var conn = db.Open();
        conn.Execute("DELETE FROM setting WHERE key = @key", new { key });
    }
}
