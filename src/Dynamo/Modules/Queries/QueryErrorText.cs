using Dynamo.Modules.OracleConnections;
using Oracle.ManagedDataAccess.Client;

namespace Dynamo.Modules.Queries;

/// <summary>What to tell the user when running a query failed.</summary>
public static class QueryErrorText
{
    public static string Of(Exception ex, ILogger log)
    {
        switch (ex)
        {
            case QueryException:
            case KeyNotFoundException:
            case ConnectionNotReadyException:
                return ex.Message;
            case OracleException:
                // ORA-xxxxx: what the database says is what the user needs.
                return ex.Message.Trim();
            default:
                log.LogError(ex, "Query execution failed");
                return $"Unexpected error: {ex.Message}";
        }
    }
}
