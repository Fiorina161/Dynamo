using System.Security.Cryptography;

namespace Dynamo.Modules.Settings;

/// <summary>
/// The password other computers must give to use Dynamo. It is kept in the
/// settings table as a salted PBKDF2 hash, never in clear (unlike the Oracle
/// passwords, which have to be sent to Oracle): "pbkdf2-sha256$iterations$
/// salt$hash". Without a password, remote access is refused.
/// </summary>
/// <param name="iterations">PBKDF2 cost for new passwords (tests use a cheap one).</param>
public sealed class PasswordService(SettingsRepository settings, int iterations = PasswordService.DefaultIterations)
{
    public const int DefaultIterations = 600_000;
    public const int MinLength = 8;
    public const int MaxLength = 200;

    private const string Key = "access.password";
    private const string Scheme = "pbkdf2-sha256";
    private const int SaltBytes = 16;
    private const int HashBytes = 32;

    private readonly object _gate = new();
    private string? _stored;
    private bool _loaded;

    private string Stored
    {
        get
        {
            lock (_gate)
            {
                if (!_loaded)
                {
                    _stored = settings.Get(Key) ?? "";
                    _loaded = true;
                }
                return _stored!;
            }
        }
    }

    public bool IsSet => Stored.Length > 0;

    /// <summary>
    /// Changes whenever the password does: a session remembers the one it was
    /// opened with, so changing or removing the password ends every session.
    /// </summary>
    public string Stamp => Stored;

    /// <summary>Why this password cannot be used, or null.</summary>
    public static string? Problem(string? password) =>
        string.IsNullOrEmpty(password) ? "The password is required."
        : password.Length < MinLength ? $"The password must have at least {MinLength} characters."
        : password.Length > MaxLength ? $"The password is too long (max {MaxLength})."
        : null;

    public void Set(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, HashBytes);
        var value = $"{Scheme}${iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
        settings.Set(Key, value);
        lock (_gate)
        {
            _stored = value;
            _loaded = true;
        }
    }

    public void Clear()
    {
        settings.Delete(Key);
        lock (_gate)
        {
            _stored = "";
            _loaded = true;
        }
    }

    /// <summary>True when a password is set and this is it (constant-time compare).</summary>
    public bool Verify(string? password)
    {
        var parts = Stored.Split('$');
        if (parts.Length != 4 || parts[0] != Scheme || !int.TryParse(parts[1], out var iterations))
            return false;
        byte[] salt, expected;
        try
        {
            salt = Convert.FromBase64String(parts[2]);
            expected = Convert.FromBase64String(parts[3]);
        }
        catch (FormatException)
        {
            return false;
        }

        var actual = Rfc2898DeriveBytes.Pbkdf2(
            password ?? "", salt, iterations, HashAlgorithmName.SHA256, expected.Length);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }
}
