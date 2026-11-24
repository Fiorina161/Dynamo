using Dynamo.Modules.Queries;
using Microsoft.AspNetCore.Mvc;

namespace Dynamo.Pages.Shared;

/// <summary>What the results area shows: a result, or why there is none.</summary>
public sealed record ResultsModel(QueryResult? Result, string? Error);

/// <summary>
/// A workspace panel's answer: its results, and the panel they belong to (the
/// title bar is updated with the same summary line as the Queries page).
/// </summary>
/// <param name="KillUrl">
/// The sessions panel on a DBA connection: the address of the Kill dialog (the row adds the session);
/// null for every other panel, which gets no Kill column.
/// </param>
public sealed record PanelResultsModel(ResultsModel Results, long PanelId, string? KillUrl = null);

/// <summary>A button that downloads the query's whole result as CSV.</summary>
public sealed record ExportButtonModel(string Url, string? IncludeSelector = null);

/// <summary>
/// What running a query for the screen and exporting it as CSV do, for every
/// page that shows query results (the Queries run page and the workspace
/// panels): one place, so a change applies to both.
/// </summary>
public static class QueryActions
{
    private const string ParamPrefix = "param.";

    /// <summary>The bind values of a form (fields named param.NAME).</summary>
    public static Dictionary<string, string> ParamsOf(IFormCollection form) =>
        form.Keys
            .Where(k => k.StartsWith(ParamPrefix, StringComparison.Ordinal))
            .ToDictionary(k => k[ParamPrefix.Length..], k => form[k].ToString().Trim(),
                StringComparer.OrdinalIgnoreCase);

    /// <summary>Runs the query; a failure becomes the message to show.</summary>
    public static async Task<ResultsModel> RunAsync(
        IQueryRunner runner, ILogger log, string connection, string sql,
        IReadOnlyDictionary<string, string> values, int maxRows, CancellationToken ct)
    {
        try
        {
            return new ResultsModel(await runner.RunAsync(connection, sql, values, maxRows, ct), null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new ResultsModel(null, QueryErrorText.Of(ex, log));
        }
    }

    /// <summary>
    /// Runs the query again with no screen limit and answers the CSV file. A
    /// download has nowhere to show an error: it is plain text, status 422.
    /// </summary>
    public static async Task<IActionResult> CsvAsync(
        IQueryRunner runner, ILogger log, string connection, string sql,
        IReadOnlyDictionary<string, string> values, string name, CancellationToken ct)
    {
        try
        {
            var result = await runner.RunAsync(connection, sql, values, QueryRunner.ExportLimit, ct);
            return new FileContentResult(QueryCsv.Write(result), "text/csv; charset=utf-8")
            {
                FileDownloadName = QueryCsv.FileName(name),
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new ContentResult
            {
                Content = $"Export failed: {QueryErrorText.Of(ex, log)}",
                ContentType = "text/plain; charset=utf-8",
                StatusCode = StatusCodes.Status422UnprocessableEntity,
            };
        }
    }
}
