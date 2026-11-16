using Dynamo.Modules.OracleConnections;
using Dynamo.Modules.Queries;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Oracle.ManagedDataAccess.Client;

namespace Dynamo.Pages.Queries;

/// <summary>What the results area shows: a result, or why there is none.</summary>
public sealed record ResultsModel(QueryResult? Result, string? Error);

public class RunModel(
    QueryRepository repo, OracleConnector connector, IQueryRunner runner,
    ILogger<RunModel> log) : PageModel
{
    private const string ParamPrefix = "param.";

    public SavedQuery Query { get; private set; } = new();
    public IReadOnlyList<string> Binds { get; private set; } = [];
    public IReadOnlyList<ConnectionChoice> Connections { get; private set; } = [];

    public IActionResult OnGet(long id)
    {
        if (!Load(id))
            return NotFound();
        return Page();
    }

    /// <summary>Runs the query and answers the results area.</summary>
    public async Task<IActionResult> OnPostRunAsync(
        long id, string? connection, int? maxRows, CancellationToken ct)
    {
        var query = repo.Get(id);
        if (query is null)
            return NotFound();

        var values = ParamsOf(Request.Form);
        try
        {
            var result = await runner.RunAsync(
                connection ?? "", query.Sql, values,
                Math.Clamp(maxRows ?? QueryRunner.DefaultMaxRows, 1, QueryRunner.MaxRowsLimit), ct);
            repo.SaveLastParams(id, values);
            return Partial("_Results", new ResultsModel(result, null));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Partial("_Results", new ResultsModel(null, ErrorText(ex)));
        }
    }

    /// <summary>Runs the query again and sends the whole result as a CSV file.</summary>
    public async Task<IActionResult> OnPostCsvAsync(
        long id, string? connection, CancellationToken ct)
    {
        var query = repo.Get(id);
        if (query is null)
            return NotFound();

        try
        {
            var result = await runner.RunAsync(
                connection ?? "", query.Sql, ParamsOf(Request.Form), QueryRunner.ExportLimit, ct);
            return File(QueryCsv.Write(result), "text/csv; charset=utf-8", QueryCsv.FileName(query.Name));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A download has nowhere to show an error: say it as plain text.
            return new ContentResult
            {
                Content = $"Export failed: {ErrorText(ex)}",
                ContentType = "text/plain; charset=utf-8",
                StatusCode = StatusCodes.Status422UnprocessableEntity,
            };
        }
    }

    private bool Load(long id)
    {
        var query = repo.Get(id);
        if (query is null)
            return false;
        Query = query;
        Binds = SqlScanner.Analyze(query.Sql).Binds;
        Connections = connector.Choices();
        return true;
    }

    /// <summary>The bind values of the form (fields named param.NAME).</summary>
    private static Dictionary<string, string> ParamsOf(IFormCollection form) =>
        form.Keys
            .Where(k => k.StartsWith(ParamPrefix, StringComparison.Ordinal))
            .ToDictionary(k => k[ParamPrefix.Length..], k => form[k].ToString().Trim(),
                StringComparer.OrdinalIgnoreCase);

    private string ErrorText(Exception ex) => QueryErrorText.Of(ex, log);
}
