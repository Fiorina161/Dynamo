using System.Security.Cryptography;
using System.Text;
using Dapper;

namespace Dynamo.Core;

/// <summary>
/// Protects the secrets kept in the database (the Oracle passwords, the private keys of the https
/// certificates) with Windows DPAPI at machine scope: Windows holds the key, so there is nothing to
/// manage and nothing to type, and a copy of <c>dynamo.db</c> read on another computer shows nothing
/// useful. Every Windows account of this machine can decrypt (the service runs as LocalSystem, the
/// console as the user: both must), so it protects the file, not the running machine, and it does not
/// protect a stolen disk or VM image (the machine key is on it).
///
/// A protected value is <c>dpapi1:</c> and Base64. A value without that prefix is read as it is (a
/// database from before the protection, or a test), and <see cref="ProtectStored"/> turns those into
/// protected ones once, at start. A protected value that cannot be decrypted here (the database was
/// copied from another computer, Windows was reinstalled) reads as unreadable: the owner enters it again.
/// </summary>
public static class Secrets
{
    public const string Prefix = "dpapi1:";

    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Dynamo secrets v1");

    public static bool IsProtected(string? stored) =>
        stored is not null && stored.StartsWith(Prefix, StringComparison.Ordinal);

    /// <summary>The value ready to store: protected, or empty when there is nothing to protect.</summary>
    public static string Protect(string? plain)
    {
        if (string.IsNullOrEmpty(plain))
            return "";
        if (!OperatingSystem.IsWindows())
            return plain;
        var bytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(plain), Entropy, DataProtectionScope.LocalMachine);
        return Prefix + Convert.ToBase64String(bytes);
    }

    /// <summary>
    /// The clear text of a stored value. A value that was never protected is returned as it is; false
    /// (and an empty text) when it is protected but cannot be decrypted on this computer.
    /// </summary>
    public static bool TryUnprotect(string? stored, out string plain)
    {
        plain = "";
        if (string.IsNullOrEmpty(stored))
            return true;
        if (!IsProtected(stored))
        {
            plain = stored;
            return true;
        }
        if (!OperatingSystem.IsWindows())
            return false;
        try
        {
            var bytes = ProtectedData.Unprotect(
                Convert.FromBase64String(stored[Prefix.Length..]), Entropy, DataProtectionScope.LocalMachine);
            plain = Encoding.UTF8.GetString(bytes);
            return true;
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            return false;
        }
    }

    /// <summary>The settings whose value is a secret (the certificates, with their private keys).</summary>
    public static readonly IReadOnlyList<string> SecretSettings = ["tls.ca", "tls.leaf"];

    /// <summary>
    /// Protects, once, what is still stored in clear: the Oracle passwords and the secret settings. Each
    /// value is read back before it replaces the old one, and left as it was if it does not come back
    /// identical. Idempotent: values already protected are left alone. Returns how many were done.
    /// </summary>
    public static (int Passwords, int Settings) ProtectStored(Db db)
    {
        if (!OperatingSystem.IsWindows())
            return (0, 0);
        using var conn = db.Open();
        using var tx = conn.BeginTransaction();
        var passwords = 0;
        foreach (var row in conn.Query<(long Id, string Password)>(
                     "SELECT id, password FROM oracle_connection WHERE password <> ''", transaction: tx).ToList())
        {
            if (IsProtected(row.Password) || !Verified(row.Password, out var protectedText))
                continue;
            conn.Execute("UPDATE oracle_connection SET password = @protectedText WHERE id = @Id",
                new { protectedText, row.Id }, tx);
            passwords++;
        }

        var settings = 0;
        foreach (var key in SecretSettings)
        {
            var value = conn.ExecuteScalar<string?>("SELECT value FROM setting WHERE key = @key", new { key }, tx);
            if (string.IsNullOrEmpty(value) || IsProtected(value) || !Verified(value, out var protectedText))
                continue;
            conn.Execute("UPDATE setting SET value = @protectedText WHERE key = @key", new { protectedText, key }, tx);
            settings++;
        }
        tx.Commit();
        return (passwords, settings);
    }

    private static bool Verified(string plain, out string protectedText)
    {
        protectedText = Protect(plain);
        return TryUnprotect(protectedText, out var back) && back == plain;
    }
}
