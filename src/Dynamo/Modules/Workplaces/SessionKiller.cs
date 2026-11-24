using System.Data;
using Dynamo.Modules.OracleConnections;
using Oracle.ManagedDataAccess.Client;

namespace Dynamo.Modules.Workplaces;

/// <summary>A database session as the Kill dialog describes it.</summary>
public sealed record SessionInfo(
    long InstanceId, long Sid, long Serial, string? Username, string? Status, string? OsUser,
    string? Machine, string? Program, string? SqlId, bool IsBackground, bool IsOwn);

/// <summary>The outcome of a kill, in words for the toast.</summary>
public sealed record KillOutcome(bool Ok, string Message);

/// <summary>Looks at a session and kills it; an interface so the pages can be tested without Oracle.</summary>
public interface ISessionKiller
{
    /// <summary>The session, or null when it no longer exists (the serial number must still match).</summary>
    Task<SessionInfo?> DescribeAsync(string connectionName, long instance, long sid, long serial, CancellationToken ct);

    Task<KillOutcome> KillAsync(string connectionName, long instance, long sid, long serial, CancellationToken ct);
}

/// <summary>
/// ALTER SYSTEM KILL SESSION, the one thing in Dynamo that changes the database. It is guarded:
/// only on a DBA connection (logging in AS SYSDBA), only for a session that still exists with that
/// exact serial number, never a background process, never Dynamo's own session, and the three numbers
/// are integers by construction (ALTER SYSTEM takes no bind variables, so the text is built from them).
/// </summary>
public sealed class SessionKiller(OracleConnector connector, ILogger<SessionKiller> log) : ISessionKiller
{
    public async Task<SessionInfo?> DescribeAsync(
        string connectionName, long instance, long sid, long serial, CancellationToken ct)
    {
        if (!connector.IsDba(connectionName))
            return null;
        await using var conn = await connector.OpenAsync(connectionName, ct);
        return await DescribeOn(conn, instance, sid, serial, ct);
    }

    public async Task<KillOutcome> KillAsync(
        string connectionName, long instance, long sid, long serial, CancellationToken ct)
    {
        if (!connector.IsDba(connectionName))
            return new KillOutcome(false, $"“{connectionName}” is not a DBA connection: nothing was killed.");

        try
        {
            await using var conn = await connector.OpenAsync(connectionName, ct);
            var session = await DescribeOn(conn, instance, sid, serial, ct);
            if (session is null)
                return new KillOutcome(false, "That session no longer exists (it ended, or its number was reused).");
            if (session.IsOwn)
                return new KillOutcome(false, "That is Dynamo's own session: it is not killed.");
            if (session.IsBackground)
                return new KillOutcome(false, "That is a background process: it is not killed.");

            using var cmd = conn.CreateCommand();
            // Integers only (the parameters are long): nothing else can get into the statement.
            cmd.CommandText = $"ALTER SYSTEM KILL SESSION '{sid},{serial},@{instance}' IMMEDIATE";
            cmd.CommandTimeout = 60;
            try
            {
                await cmd.ExecuteNonQueryAsync(ct);
            }
            catch (OracleException ex) when (ex.Number == 31)
            {
                // ORA-00031: marked for kill, it goes when it finishes what it is doing.
                log.LogWarning("Session {Sid},{Serial},@{Instance} marked for kill on {Connection}", sid, serial, instance, connectionName);
                return new KillOutcome(true, $"Session {sid},{serial} is marked for kill: it ends when it finishes its current work.");
            }
            catch (OracleException ex) when (ex.Number == 30)
            {
                return new KillOutcome(false, "That session no longer exists.");
            }

            log.LogWarning("Session {Sid},{Serial},@{Instance} ({User}) killed on {Connection}",
                sid, serial, instance, session.Username, connectionName);
            return new KillOutcome(true, $"Session {sid},{serial} ({session.Username}) was killed.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new KillOutcome(false, FirstLine(ex.Message));
        }
    }

    private static async Task<SessionInfo?> DescribeOn(
        OracleConnection conn, long instance, long sid, long serial, CancellationToken ct)
    {
        using var cmd = conn.CreateCommand();
        cmd.BindByName = true;
        cmd.CommandText = """
            select inst_id, sid, serial#, username, status, osuser, machine, program, sql_id, type,
                   case when inst_id = to_number(sys_context('USERENV', 'INSTANCE'))
                         and sid = to_number(sys_context('USERENV', 'SID')) then 1 else 0 end as own
              from gv$session
             where inst_id = :p_instance and sid = :p_sid and serial# = :p_serial
            """;
        cmd.Parameters.Add(new OracleParameter("p_instance", OracleDbType.Int64, instance, ParameterDirection.Input));
        cmd.Parameters.Add(new OracleParameter("p_sid", OracleDbType.Int64, sid, ParameterDirection.Input));
        cmd.Parameters.Add(new OracleParameter("p_serial", OracleDbType.Int64, serial, ParameterDirection.Input));
        using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            return null;
        string? Text(int i) => reader.IsDBNull(i) ? null : Convert.ToString(reader.GetValue(i));
        return new SessionInfo(
            Convert.ToInt64(reader.GetValue(0)), Convert.ToInt64(reader.GetValue(1)), Convert.ToInt64(reader.GetValue(2)),
            Text(3), Text(4), Text(5), Text(6), Text(7), Text(8),
            IsBackground: string.Equals(Text(9), "BACKGROUND", StringComparison.OrdinalIgnoreCase),
            IsOwn: Convert.ToInt32(reader.GetValue(10)) == 1);
    }

    private static string FirstLine(string message) => message.Split('\n', 2)[0].Trim();
}
