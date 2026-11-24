using System.Diagnostics;
using Oracle.ManagedDataAccess.Client;

namespace Dynamo.Modules.OracleConnections;

/// <summary>A connection as a picker offers it.</summary>
public sealed record ConnectionChoice(
    string Name, bool Pinned, bool Dba = false, string? Color = null);

/// <summary>A defined connection that cannot log in yet (no schema or password).</summary>
public sealed class ConnectionNotReadyException(string message) : Exception(message);

/// <summary>
/// The entry point other features use to reach Oracle, always by connection
/// name:
///
///     using var conn = await connector.OpenAsync("PROD");
///
/// The name is the one chosen on the Oracle page; the data source, schema and
/// password never leave this module.
/// </summary>
public sealed class OracleConnector(OracleConnectionRepository repo)
{
    /// <summary>Seconds to wait for a connection before giving up.</summary>
    public const int ConnectTimeoutSeconds = 15;

    public sealed record TestResult(bool Ok, string Message);

    /// <summary>
    /// What a probe found: whether it logged in, the server version, how long the login took and how long
    /// one round trip took, or why it failed (Oracle's first message line, never the password).
    /// <c>NotReady</c> is a connection that cannot log in yet (no schema or password).
    /// </summary>
    public sealed record ProbeResult(
        bool Ok, string? Version, long ConnectMs, long RoundTripMs, string? Error, bool NotReady = false);

    /// <summary>
    /// The names of the defined connections, for pickers: pinned ones first,
    /// then by name.
    /// </summary>
    public IReadOnlyList<string> Names() => repo.GetAll().Select(c => c.Name).ToList();

    /// <summary>Like <see cref="Names"/>, telling which ones are pinned.</summary>
    public IReadOnlyList<ConnectionChoice> Choices() =>
        repo.GetAll().Select(c => new ConnectionChoice(c.Name, c.Pinned, c.Dba, c.Color)).ToList();

    /// <summary>True when the named connection exists and logs in AS SYSDBA.</summary>
    public bool IsDba(string name) => repo.GetByName(name)?.Dba == true;

    /// <summary>Opens the named connection. Throws if the name is unknown.</summary>
    public async Task<OracleConnection> OpenAsync(string name, CancellationToken ct = default)
    {
        var info = repo.GetByName(name)
            ?? throw new KeyNotFoundException($"No Oracle connection named '{name}'.");
        if (NotReady(info) is { } reason)
            throw new ConnectionNotReadyException(reason);
        var conn = new OracleConnection(ConnectionStringOf(info));
        try
        {
            await conn.OpenAsync(ct);
            return conn;
        }
        catch
        {
            await conn.DisposeAsync();
            throw;
        }
    }

    /// <summary>
    /// Logs in (a real login each time, never a pooled connection, so the time is the truth) and
    /// runs one trivial query. Never throws: a failure is in the result.
    /// </summary>
    public async Task<ProbeResult> ProbeAsync(OracleConnectionInfo info, CancellationToken ct = default)
    {
        if (NotReady(info) is { } reason)
            return new ProbeResult(false, null, 0, 0, reason, NotReady: true);

        var clock = Stopwatch.StartNew();
        try
        {
            await using var conn = new OracleConnection(ConnectionStringOf(info, pooling: false));
            await conn.OpenAsync(ct);
            var connectMs = clock.ElapsedMilliseconds;
            var version = conn.ServerVersion;

            clock.Restart();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT 1 FROM DUAL";
            cmd.CommandTimeout = ConnectTimeoutSeconds;
            await cmd.ExecuteScalarAsync(ct);
            return new ProbeResult(true, version, connectMs, clock.ElapsedMilliseconds, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Oracle's message ("ORA-01017: invalid username/password...") is
            // what the user needs to see; it never contains the password.
            return new ProbeResult(false, null, clock.ElapsedMilliseconds, 0, FirstLine(ex.Message));
        }
    }

    /// <summary>Tries to log in and reports the outcome for the user.</summary>
    public async Task<TestResult> TestAsync(OracleConnectionInfo info)
    {
        var probe = await ProbeAsync(info);
        if (probe.NotReady)
            return new TestResult(false, probe.Error!);
        return probe.Ok
            ? new TestResult(true,
                $"Connected to “{info.Name}” as {info.Username.ToUpperInvariant()}" +
                (info.Dba ? " AS SYSDBA " : " ") +
                $"(Oracle {probe.Version}) in {probe.ConnectMs} ms.")
            : new TestResult(false, $"“{info.Name}”: {probe.Error}");
    }

    /// <summary>Why this connection cannot log in yet, or null when it can.</summary>
    private static string? NotReady(OracleConnectionInfo info)
    {
        if (info.Username.Length == 0)
            return $"“{info.Name}” has no schema yet: fill it in on the Oracle page.";
        if (info.Password.Length == 0)
            return $"“{info.Name}” has no password yet: fill it in on the Oracle page.";
        return null;
    }

    /// <summary>The connection string of a connection (public for the tests).</summary>
    public static string ConnectionStringOf(OracleConnectionInfo info, bool pooling = true)
    {
        var builder = new OracleConnectionStringBuilder
        {
            DataSource = info.DataSource,
            UserID = info.Username,
            Password = info.Password,
            ConnectionTimeout = ConnectTimeoutSeconds,
            Pooling = pooling,
        };
        // A DBA connection: the role is part of the login, never granted later.
        if (info.Dba)
            builder.DBAPrivilege = "SYSDBA";
        return builder.ConnectionString;
    }

    private static string FirstLine(string message) =>
        message.Split('\n', 2)[0].Trim();
}
