using System.Diagnostics;
using Oracle.ManagedDataAccess.Client;

namespace Dynamo.Modules.OracleConnections;

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

    /// <summary>The names of the defined connections, for pickers.</summary>
    public IReadOnlyList<string> Names() => repo.GetAll().Select(c => c.Name).ToList();

    /// <summary>Opens the named connection. Throws if the name is unknown.</summary>
    public async Task<OracleConnection> OpenAsync(string name, CancellationToken ct = default)
    {
        var info = repo.GetByName(name)
            ?? throw new KeyNotFoundException($"No Oracle connection named '{name}'.");
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

    /// <summary>Tries to log in and reports the outcome for the user.</summary>
    public async Task<TestResult> TestAsync(OracleConnectionInfo info)
    {
        var clock = Stopwatch.StartNew();
        try
        {
            await using var conn = new OracleConnection(ConnectionStringOf(info));
            await conn.OpenAsync();
            return new TestResult(true,
                $"Connected to “{info.Name}” as {info.Username.ToUpperInvariant()} " +
                $"(Oracle {conn.ServerVersion}) in {clock.ElapsedMilliseconds} ms.");
        }
        catch (Exception ex)
        {
            // Oracle's message ("ORA-01017: invalid username/password...") is
            // what the user needs to see; it never contains the password.
            return new TestResult(false, $"“{info.Name}”: {FirstLine(ex.Message)}");
        }
    }

    private static string ConnectionStringOf(OracleConnectionInfo info) =>
        new OracleConnectionStringBuilder
        {
            DataSource = info.DataSource,
            UserID = info.Username,
            Password = info.Password,
            ConnectionTimeout = ConnectTimeoutSeconds,
        }.ConnectionString;

    private static string FirstLine(string message) =>
        message.Split('\n', 2)[0].Trim();
}
