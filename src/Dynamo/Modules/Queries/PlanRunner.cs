using System.Data;
using Dynamo.Modules.OracleConnections;
using Oracle.ManagedDataAccess.Client;

namespace Dynamo.Modules.Queries;

/// <summary>Shows how Oracle would run a query; an interface so the pages can be tested without Oracle.</summary>
public interface IPlanRunner
{
    /// <summary>The execution plan of the query as text, in the DBMS_XPLAN format asked for.</summary>
    Task<string> ExplainAsync(
        string connectionName, string sql, IReadOnlyDictionary<string, string> values,
        string format, CancellationToken ct);
}

/// <summary>
/// The execution plan of a panel's query: EXPLAIN PLAN, then DBMS_XPLAN.DISPLAY, on the panel's
/// connection. The query is the same single SELECT the panel runs (see <see cref="SqlScanner"/>) and
/// it is not executed. EXPLAIN PLAN writes a row set into the session's plan table, so unlike the
/// panels this is not a read-only transaction; it is rolled back at the end and nothing is kept.
/// </summary>
public sealed class PlanRunner(OracleConnector connector) : IPlanRunner
{
    /// <summary>The DBMS_XPLAN formats offered; anything else becomes TYPICAL.</summary>
    public static readonly IReadOnlyList<string> Formats = ["BASIC", "TYPICAL", "ALL"];

    public static string NormalizeFormat(string? format) =>
        Formats.FirstOrDefault(f => string.Equals(f, format, StringComparison.OrdinalIgnoreCase)) ?? "TYPICAL";

    public async Task<string> ExplainAsync(
        string connectionName, string sql, IReadOnlyDictionary<string, string> values,
        string format, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(connectionName))
            throw new QueryException("Choose a connection.");
        var analysis = SqlScanner.Analyze(sql);
        if (analysis.Error is not null)
            throw new QueryException(analysis.Error);
        format = NormalizeFormat(format);
        var statementId = "DYN" + Guid.NewGuid().ToString("N")[..12].ToUpperInvariant();

        await using var conn = await connector.OpenAsync(connectionName, ct);
        try
        {
            using (var explain = conn.CreateCommand())
            {
                explain.BindByName = true;
                // The id is made here from hex digits only, so it is safe in the statement text.
                explain.CommandText = $"EXPLAIN PLAN SET STATEMENT_ID = '{statementId}' FOR {analysis.Sql}";
                explain.CommandTimeout = 30;
                foreach (var bind in analysis.Binds)
                {
                    // Nothing runs, but Oracle wants every placeholder bound: as text, like the panels do.
                    object value = values.TryGetValue(bind, out var v) && v.Length > 0 ? v : DBNull.Value;
                    explain.Parameters.Add(new OracleParameter(bind, OracleDbType.Varchar2, value, ParameterDirection.Input));
                }
                await explain.ExecuteNonQueryAsync(ct);
            }

            using var display = conn.CreateCommand();
            display.BindByName = true;
            display.CommandText =
                "SELECT plan_table_output FROM TABLE(DBMS_XPLAN.DISPLAY('PLAN_TABLE', :statement_id, :format))";
            display.Parameters.Add(new OracleParameter("statement_id", OracleDbType.Varchar2, statementId, ParameterDirection.Input));
            display.Parameters.Add(new OracleParameter("format", OracleDbType.Varchar2, format, ParameterDirection.Input));
            display.CommandTimeout = 30;

            var lines = new List<string>();
            using var reader = await display.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                lines.Add(reader.IsDBNull(0) ? "" : reader.GetString(0));
            return string.Join("\n", lines);
        }
        finally
        {
            // Nothing of the plan is kept (and the pooled connection goes back clean).
            try
            {
                using var rollback = conn.CreateCommand();
                rollback.CommandText = "ROLLBACK";
                await rollback.ExecuteNonQueryAsync(CancellationToken.None);
            }
            catch { /* the connection is already unusable */ }
        }
    }
}
