using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;

namespace Dynamo.Modules.Settings;

/// <summary>Who is asking: this computer, or another one.</summary>
public static class ClientAccess
{
    /// <summary>The name of the session cookie.</summary>
    public const string CookieName = "dynamo_session";

    /// <summary>
    /// True for a request that comes from the computer Dynamo runs on: from the
    /// loopback address, or from one of the machine's own addresses (its own
    /// LAN address). Everything else is remote. A request that went through a
    /// proxy (it carries forwarding headers) is always remote: a reverse proxy
    /// on this computer must not make the whole network look local. (A real
    /// connection always has an address; only the in-memory test server has
    /// none, and counts as local.)
    /// </summary>
    public static bool IsLocal(HttpContext context)
    {
        var headers = context.Request.Headers;
        if (headers.ContainsKey("X-Forwarded-For") || headers.ContainsKey("Forwarded")
            || headers.ContainsKey("X-Real-IP"))
            return false;

        var remote = context.Connection.RemoteIpAddress;
        if (remote is null)
            return true;
        if (remote.IsIPv4MappedToIPv6)
            remote = remote.MapToIPv4();
        if (IPAddress.IsLoopback(remote))
            return true;

        var local = context.Connection.LocalIpAddress;
        if (local is { IsIPv4MappedToIPv6: true })
            local = local.MapToIPv4();
        return local is not null && remote.Equals(local);
    }

    /// <summary>A stable text for the caller, to count its failed attempts.</summary>
    public static string ClientKey(HttpContext context) =>
        context.Connection.RemoteIpAddress?.ToString() ?? "local";
}

/// <summary>
/// The sessions of the computers that gave the password, in memory: a random
/// token in a cookie without an expiry date (so it lasts until the browser is
/// closed). A restart of the service, or any change of the password, ends them.
/// </summary>
public sealed class SessionStore
{
    private readonly ConcurrentDictionary<string, string> _sessions = new();

    /// <summary>Opens a session for the given password stamp; returns its token.</summary>
    public string Create(string stamp)
    {
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        _sessions[token] = stamp;
        return token;
    }

    /// <summary>True for a token of a session opened with the current password.</summary>
    public bool IsValid(string? token, string currentStamp)
    {
        if (string.IsNullOrEmpty(token) || !_sessions.TryGetValue(token, out var stamp))
            return false;
        if (stamp == currentStamp)
            return true;
        _sessions.TryRemove(token, out _);
        return false;
    }

    public void End(string? token)
    {
        if (!string.IsNullOrEmpty(token))
            _sessions.TryRemove(token, out _);
    }
}

/// <summary>
/// Slows down password guessing: after <see cref="MaxFailures"/> wrong
/// passwords in a row from one address, that address is refused for a minute.
/// </summary>
public sealed class LoginThrottle
{
    public const int MaxFailures = 5;
    public static readonly TimeSpan Lockout = TimeSpan.FromMinutes(1);

    private sealed record Entry(int Failures, DateTime LockedUntil);

    private readonly ConcurrentDictionary<string, Entry> _entries = new();
    private readonly Func<DateTime> _now;

    public LoginThrottle() : this(() => DateTime.UtcNow) { }

    /// <summary>For tests: a clock they can move.</summary>
    public LoginThrottle(Func<DateTime> now) => _now = now;

    /// <summary>True (with the time left) while this client is locked out.</summary>
    public bool IsBlocked(string client, out TimeSpan wait)
    {
        wait = TimeSpan.Zero;
        if (!_entries.TryGetValue(client, out var e) || e.LockedUntil <= _now())
            return false;
        wait = e.LockedUntil - _now();
        return true;
    }

    public void Failed(string client) =>
        _entries.AddOrUpdate(
            client,
            _ => new Entry(1, DateTime.MinValue),
            (_, old) =>
            {
                // A lockout that has run out starts the count again.
                var failures = old.LockedUntil != DateTime.MinValue && old.LockedUntil <= _now() ? 1 : old.Failures + 1;
                return failures >= MaxFailures
                    ? new Entry(failures, _now() + Lockout)
                    : new Entry(failures, DateTime.MinValue);
            });

    public void Succeeded(string client) => _entries.TryRemove(client, out _);
}
