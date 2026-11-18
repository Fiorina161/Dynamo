using Dynamo.Modules.OracleConnections;
using Dynamo.Modules.Queries;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Oracle.ManagedDataAccess.Client;

namespace Dynamo.Pages.Queries;

public class RunModel(
    QueryRepository repo, OracleConnector connector, IQueryRunner runner,
    ILogger<RunModel> log) : PageModel
{
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

        var values = QueryActions.ParamsOf(Request.Form);
        var results = await QueryActions.RunAsync(
            runner, log, connection ?? "", query.Sql, values,
            Math.Clamp(maxRows ?? QueryRunner.DefaultMaxRows, 1, QueryRunner.MaxRowsLimit), ct);
        if (results.Result is not null)
            repo.SaveLastParams(id, values);
        return Partial("_Results", results);
    }

    /// <summary>Runs the query again and sends the whole result as a CSV file.</summary>
    public async Task<IActionResult> OnPostCsvAsync(
        long id, string? connection, CancellationToken ct)
    {
        var query = repo.Get(id);
        if (query is null)
            return NotFound();

        return await QueryActions.CsvAsync(
            runner, log, connection ?? "", query.Sql, QueryActions.ParamsOf(Request.Form), query.Name, ct);
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
}
