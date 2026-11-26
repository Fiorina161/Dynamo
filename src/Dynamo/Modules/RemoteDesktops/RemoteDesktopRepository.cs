using System.Text.RegularExpressions;
using Dapper;
using Dynamo.Core;

namespace Dynamo.Modules.RemoteDesktops;

public sealed class RemoteDesktop
{
    public long Id { get; set; }

    /// <summary>The name shown in the list (unique, case-insensitive).</summary>
    public string Name { get; set; } = "";

    /// <summary>The computer to connect to: a name or an IPv4 address, optionally with <c>:port</c>.</summary>
    public string Host { get; set; } = "";

    /// <summary>The account to log in with: <c>user</c> or <c>DOMAIN\user</c>.</summary>
    public string Username { get; set; } = "";

    /// <summary>
    /// In clear in memory only: in the database it is protected (<see cref="Secrets"/>, Windows DPAPI at machine
    /// scope, like the Oracle passwords), and it is never rendered back in a page.
    /// </summary>
    public string Password { get; set; } = "";

    /// <summary>The stored password cannot be decrypted on this computer: it must be entered again.</summary>
    public bool PasswordUnreadable { get; set; }
}

/// <summary>What a host may be. It ends up in the command line of the Remote Desktop client, so it is strict.</summary>
public static partial class RemoteDesktopHost
{
    [GeneratedRegex(@"^[A-Za-z0-9]([A-Za-z0-9._-]{0,251}[A-Za-z0-9])?(:\d{1,5})?$")]
    private static partial Regex HostPattern();

    /// <summary>The host as it will be used, or why it is not acceptable.</summary>
    public static (string? Host, string? Error) Parse(string? input)
    {
        var text = (input ?? "").Trim();
        if (text.Length == 0)
            return (null, "The host is required.");
        if (!HostPattern().IsMatch(text))
            return (null, "Give a computer name or an IPv4 address, optionally with :port (for example server01 or 10.0.0.5:3390).");
        var colon = text.IndexOf(':');
        if (colon >= 0 && (!int.TryParse(text[(colon + 1)..], out var port) || port is < 1 or > 65535))
            return (null, "The port must be between 1 and 65535.");
        return (text, null);
    }

    /// <summary>
    /// The name Windows saves the credential under: <c>TERMSRV/host</c>, without the port (that is how the Remote
    /// Desktop client looks a saved credential up).
    /// </summary>
    public static string CredentialTarget(string host)
    {
        var colon = host.IndexOf(':');
        return "TERMSRV/" + (colon >= 0 ? host[..colon] : host);
    }
}

public sealed class RemoteDesktopRepository(Db db)
{
    private const string Columns = "id, name, host, username, password";

    private static RemoteDesktop? Reveal(RemoteDesktop? r)
    {
        if (r is null)
            return null;
        if (Secrets.TryUnprotect(r.Password, out var clear))
            r.Password = clear;
        else
        {
            r.Password = "";
            r.PasswordUnreadable = true;
        }
        return r;
    }

    public List<RemoteDesktop> GetAll()
    {
        using var conn = db.Open();
        return conn.Query<RemoteDesktop>($"SELECT {Columns} FROM remote_desktop ORDER BY name")
            .Select(r => Reveal(r)!).ToList();
    }

    public RemoteDesktop? Get(long id)
    {
        using var conn = db.Open();
        return Reveal(conn.QuerySingleOrDefault<RemoteDesktop>(
            $"SELECT {Columns} FROM remote_desktop WHERE id = @id", new { id }));
    }

    /// <summary>Inserts when Id is 0, otherwise updates. Returns the id.</summary>
    public long Save(RemoteDesktop r)
    {
        var stored = new { r.Id, r.Name, r.Host, r.Username, Password = Secrets.Protect(r.Password) };
        using var conn = db.Open();
        if (r.Id != 0)
        {
            conn.Execute("""
                UPDATE remote_desktop
                   SET name = @Name, host = @Host, username = @Username, password = @Password
                 WHERE id = @Id
                """, stored);
            return r.Id;
        }
        return conn.ExecuteScalar<long>("""
            INSERT INTO remote_desktop (name, host, username, password)
            VALUES (@Name, @Host, @Username, @Password);
            SELECT last_insert_rowid();
            """, stored);
    }

    public void Delete(long id)
    {
        using var conn = db.Open();
        conn.Execute("DELETE FROM remote_desktop WHERE id = @id", new { id });
    }

    /// <summary>True if another remote desktop already has this name.</summary>
    public bool NameTaken(string name, long exceptId)
    {
        using var conn = db.Open();
        return conn.ExecuteScalar<long>(
            "SELECT COUNT(*) FROM remote_desktop WHERE name = @name AND id <> @exceptId",
            new { name, exceptId }) > 0;
    }
}
