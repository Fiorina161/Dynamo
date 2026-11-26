using System.Data;
using System.Diagnostics;
using System.Globalization;
using Dynamo.Modules.OracleConnections;
using Oracle.ManagedDataAccess.Client;
using Oracle.ManagedDataAccess.Types;

namespace Dynamo.Modules.Queries;

/// <summary>A refusal the user can act on (bad SQL, no connection chosen...).</summary>
public sealed class QueryException(string message) : Exception(message);

/// <param name="Source">
/// Where the column was in the query's own order, once the columns were rearranged (see <see cref="ColumnOrder"/>);
/// -1 when they were not (its place in the list is then its place in the query).
/// </param>
public sealed record ResultColumn(string Name, bool Numeric, int Source = -1);

/// <summary>Values are already formatted for display; null is a SQL NULL.</summary>
public sealed record QueryResult(
    IReadOnlyList<ResultColumn> Columns,
    IReadOnlyList<string?[]> Rows,
    bool Truncated,
    long ElapsedMs);

/// <summary>Runs a query; an interface so the pages can be tested without Oracle.</summary>
public interface IQueryRunner
{
    Task<QueryResult> RunAsync(
        string connectionName,
        string sql,
        IReadOnlyDictionary<string, string> values,
        int maxRows,
        CancellationToken ct);
}

/// <summary>
/// Runs a query on a named Oracle connection. It can only read: the
/// text must be a single SELECT (see <see cref="SqlScanner"/>) and it runs in
/// a read-only transaction that is always rolled back.
/// </summary>
public sealed class QueryRunner(OracleConnector connector) : IQueryRunner
{
    public const int DefaultMaxRows = 500;
    public const int MaxRowsLimit = 10_000;
    public const int ExportLimit = 100_000;
    public const int TimeoutSeconds = 120;

    /// <summary>Longest text kept for one cell (a CLOB can be huge).</summary>
    private const int MaxCellChars = 10_000;

    public async Task<QueryResult> RunAsync(
        string connectionName,
        string sql,
        IReadOnlyDictionary<string, string> values,
        int maxRows,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(connectionName))
            throw new QueryException("Choose a connection.");

        var analysis = SqlScanner.Analyze(sql);
        if (analysis.Error is not null)
            throw new QueryException(analysis.Error);

        maxRows = Math.Clamp(maxRows, 1, ExportLimit);
        var clock = Stopwatch.StartNew();

        await using var conn = await connector.OpenAsync(connectionName, ct);
        try
        {
            await Execute(conn, "SET TRANSACTION READ ONLY", ct);

            using var cmd = conn.CreateCommand();
            cmd.BindByName = true;
            cmd.CommandText = analysis.Sql;
            cmd.CommandTimeout = TimeoutSeconds;
            foreach (var bind in analysis.Binds)
            {
                // Always text: Oracle converts it to what the column needs
                // (a number column compared to '5' works, the reverse would not).
                object value = values.TryGetValue(bind, out var v) && v.Length > 0
                    ? v
                    : DBNull.Value;
                cmd.Parameters.Add(
                    new OracleParameter(bind, OracleDbType.Varchar2, value, ParameterDirection.Input));
            }

            // Leaving the page cancels the statement on the server.
            using var registration = ct.Register(() =>
            {
                try { cmd.Cancel(); } catch { /* already finished */ }
            });

            using var reader = await cmd.ExecuteReaderAsync(ct);
            var columns = Enumerable.Range(0, reader.FieldCount)
                .Select(i => new ResultColumn(reader.GetName(i), IsNumeric(reader.GetFieldType(i))))
                .ToList();

            var rows = new List<string?[]>();
            while (rows.Count < maxRows && await reader.ReadAsync(ct))
                rows.Add(ReadRow(reader));
            var truncated = rows.Count == maxRows && await reader.ReadAsync(ct);

            return new QueryResult(columns, rows, truncated, clock.ElapsedMilliseconds);
        }
        finally
        {
            // The connection goes back to the pool: end the read-only
            // transaction so the next user of it starts clean.
            try { await Execute(conn, "ROLLBACK", CancellationToken.None); }
            catch { /* the connection is already unusable */ }
        }
    }

    private static async Task Execute(OracleConnection conn, string sql, CancellationToken ct)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static bool IsNumeric(Type t) =>
        t == typeof(decimal) || t == typeof(double) || t == typeof(float)
        || t == typeof(int) || t == typeof(long) || t == typeof(short) || t == typeof(byte);

    private static string?[] ReadRow(OracleDataReader reader)
    {
        var row = new string?[reader.FieldCount];
        for (var i = 0; i < row.Length; i++)
            row[i] = Format(reader, i);
        return row;
    }

    private static string? Format(OracleDataReader reader, int i)
    {
        if (reader.IsDBNull(i))
            return null;

        object value;
        try
        {
            value = reader.GetValue(i);
        }
        catch (OverflowException)
        {
            // A NUMBER with more digits than a .NET decimal holds.
            return reader.GetOracleValue(i).ToString();
        }

        return value switch
        {
            string s => Cap(s),
            DateTime d => d.TimeOfDay == TimeSpan.Zero
                ? d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
                : d.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
            IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
            byte[] b => b.Length <= 64 ? Convert.ToHexString(b) : $"(RAW, {b.Length} bytes)",
            OracleClob clob => Cap(clob.Value),
            OracleBlob => "(BLOB)",
            _ => Cap(value.ToString() ?? ""),
        };
    }

    private static string Cap(string s) =>
        s.Length <= MaxCellChars ? s : s[..MaxCellChars] + "…";
}
