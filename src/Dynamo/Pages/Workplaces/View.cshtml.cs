using System.Text.Json;
using Dynamo.Modules.OracleConnections;
using Dynamo.Modules.Queries;
using Dynamo.Modules.Workplaces;
using Dynamo.Pages.Queries;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Dynamo.Pages.Workplaces;

/// <summary>Model of the dialog that copies saved queries into a workspace.</summary>
public sealed record AddQueriesModel(Workplace Workplace, IReadOnlyList<SavedQuery> Queries);

/// <summary>
/// Model of the dialog that writes a query of a workspace: a new one
/// (<c>PanelId</c> 0, with a connection to pick) or the edit of a panel's own.
/// </summary>
public sealed record PanelQueryModel(
    Workplace Workplace, long PanelId, string Name, string Sql, string Connection,
    IReadOnlyList<ConnectionChoice> Connections, string? Error = null);

/// <summary>
/// One workspace: its panels (each with its own query, refreshing on its own)
/// and the bind values they share. Rendered once; panels run through RunPanel,
/// which answers the results area of one panel.
/// </summary>
public class ViewModel(
    WorkplaceRepository repo, QueryRepository queries, OracleConnector connector,
    IQueryRunner runner, ILogger<ViewModel> log) : PageModel
{
    private const string ParamPrefix = "param.";

    public Workplace Workplace { get; private set; } = new();
    public IReadOnlyList<ConnectionChoice> Connections { get; private set; } = [];

    public IActionResult OnGet(long id)
    {
        var workplace = repo.Get(id);
        if (workplace is null)
            return NotFound();
        Workplace = workplace;
        Connections = connector.Choices();
        return Page();
    }

    // ---- Dialogs and structure ------------------------------------------

    public IActionResult OnGetAddForm(long id)
    {
        var workplace = repo.Get(id);
        return workplace is null
            ? NotFound()
            : Partial("_AddForm", new AddQueriesModel(workplace, queries.GetAll()));
    }

    /// <summary>Copies the picked saved queries into the workspace, one panel each.</summary>
    public IActionResult OnPostAdd(long id, List<long>? ids)
    {
        if (repo.Get(id) is null)
            return NotFound();
        repo.AddCopies(id, ids ?? []);
        return Refresh();
    }

    // ---- The queries of the panels ----------------------------------------

    /// <summary>The dialog to write a new query in the workspace.</summary>
    public IActionResult OnGetNewQueryForm(long id)
    {
        var workplace = repo.Get(id);
        return workplace is null
            ? NotFound()
            : Partial("_PanelForm", new PanelQueryModel(
                workplace, 0, "", "", "", connector.Choices()));
    }

    /// <summary>Creates a panel with a query written here (not saved in Queries).</summary>
    public IActionResult OnPostCreateQuery(long id, string? name, string? sql, string? connection)
    {
        var workplace = repo.Get(id);
        if (workplace is null)
            return NotFound();
        name = name?.Trim() ?? "";
        sql = (sql ?? "").Replace("\r\n", "\n").Trim();
        connection = (connection ?? "").Trim();
        if (QueryProblem(name, sql) is { } error)
            return Reshow(new PanelQueryModel(workplace, 0, name, sql, connection, connector.Choices(), error));

        repo.AddNew(id, name, sql, connection);
        return Refresh();
    }

    /// <summary>The dialog to change a panel's own query (not the saved one it came from).</summary>
    public IActionResult OnGetEditQueryForm(long id, long panel)
    {
        if (repo.Get(id) is not { } workplace || repo.GetPanel(panel) is not { } p || p.WorkplaceId != id)
            return NotFound();
        return Partial("_PanelForm", new PanelQueryModel(
            workplace, p.Id, p.QueryName, p.Sql, p.Connection, connector.Choices()));
    }

    public IActionResult OnPostEditQuery(long id, long panel, string? name, string? sql)
    {
        if (repo.Get(id) is not { } workplace || repo.GetPanel(panel) is not { } p || p.WorkplaceId != id)
            return NotFound();
        name = name?.Trim() ?? "";
        sql = (sql ?? "").Replace("\r\n", "\n").Trim();
        if (QueryProblem(name, sql) is { } error)
            return Reshow(new PanelQueryModel(workplace, panel, name, sql, p.Connection, connector.Choices(), error));

        repo.SetPanelQuery(panel, name, sql);
        return Refresh();
    }

    /// <summary>The same rules as a saved query: a name, one SELECT or WITH.</summary>
    private static string? QueryProblem(string name, string sql) =>
        name.Length == 0 ? "The name is required."
        : name.Length > 100 ? "The name is too long (max 100)."
        : sql.Length == 0 ? "The SQL is required."
        : SqlScanner.Analyze(sql).Error;

    private IActionResult Reshow(PanelQueryModel model)
    {
        Response.Headers["HX-Retarget"] = "#dlg";
        Response.Headers["HX-Reswap"] = "innerHTML";
        return Partial("_PanelForm", model);
    }

    public IActionResult OnGetRenameForm(long id)
    {
        var workplace = repo.Get(id);
        return workplace is null
            ? NotFound()
            : Partial("_NameForm", new WorkplaceNameModel(workplace.Id, workplace.Name));
    }

    public IActionResult OnPostRename(long id, string? name)
    {
        if (repo.Get(id) is null)
            return NotFound();
        name = name?.Trim() ?? "";
        var error = IndexModel.Validate(repo, name, id);
        if (error is not null)
        {
            Response.Headers["HX-Retarget"] = "#dlg";
            Response.Headers["HX-Reswap"] = "innerHTML";
            return Partial("_NameForm", new WorkplaceNameModel(id, name, error));
        }

        repo.Rename(id, name);
        return Refresh();
    }

    public IActionResult OnPostDelete(long id)
    {
        repo.Delete(id);
        Response.Headers["HX-Redirect"] = Url.Page("/Workplaces/Index")!;
        return new NoContentResult();
    }

    /// <summary>The name dialog prefilled for a copy; nothing exists until saved.</summary>
    public IActionResult OnGetDuplicateForm(long id)
    {
        var workplace = repo.Get(id);
        return workplace is null
            ? NotFound()
            : Partial("_NameForm", new WorkplaceNameModel(0, UniqueCopyName(workplace.Name), null, id));
    }

    /// <summary>Copies the workplace (panels, settings, values) and opens the copy.</summary>
    public IActionResult OnPostDuplicate(long id, string? name)
    {
        if (repo.Get(id) is null)
            return NotFound();
        name = name?.Trim() ?? "";
        var error = IndexModel.Validate(repo, name, 0);
        if (error is not null)
        {
            Response.Headers["HX-Retarget"] = "#dlg";
            Response.Headers["HX-Reswap"] = "innerHTML";
            return Partial("_NameForm", new WorkplaceNameModel(0, name, error, id));
        }

        var copy = repo.Duplicate(id, name);
        if (copy == 0)
            return NotFound();
        Response.Headers["HX-Redirect"] = Url.Page("/Workplaces/View", new { id = copy })!;
        return new NoContentResult();
    }

    /// <summary>Moves a panel one place earlier (dir = -1) or later (1).</summary>
    public IActionResult OnPostMovePanel(long id, long panel, int dir)
    {
        if (repo.GetPanel(panel) is not { } p || p.WorkplaceId != id)
            return NotFound();
        repo.MovePanel(panel, dir);
        return Refresh();
    }

    public IActionResult OnPostRemovePanel(long id, long panel)
    {
        if (repo.GetPanel(panel) is not { } p || p.WorkplaceId != id)
            return NotFound();
        repo.RemovePanel(panel);
        return Refresh();
    }

    // ---- Settings and values ---------------------------------------------

    /// <summary>
    /// Saves a panel's connection, refresh interval and row limit (a blank
    /// connection or a missing limit is left as it is). No content.
    /// </summary>
    public IActionResult OnPostPanelSettings(
        long id, long panel, string? connection, long? seconds, long? maxRows)
    {
        if (repo.GetPanel(panel) is not { } p || p.WorkplaceId != id)
            return NotFound();
        repo.SetPanel(panel, string.IsNullOrWhiteSpace(connection) ? null : connection.Trim(),
            seconds ?? 0, maxRows);
        return new NoContentResult();
    }

    /// <summary>
    /// Saves the shared bind values and tells the page (an event) to run every
    /// panel again with them.
    /// </summary>
    public IActionResult OnPostSaveBinds(long id)
    {
        if (repo.Get(id) is null)
            return NotFound();
        repo.SaveBinds(id, ParamsOf(Request.Form));
        Response.Headers["HX-Trigger"] = "runAll";
        return new NoContentResult();
    }

    // ---- Running -----------------------------------------------------------

    /// <summary>Runs one panel's query and answers its results area.</summary>
    public async Task<IActionResult> OnPostRunPanelAsync(
        long id, long panel, string? connection, long? maxRows, CancellationToken ct)
    {
        if (repo.GetPanel(panel) is not { } p || p.WorkplaceId != id)
            return NotFound();
        var rows = (int)Math.Clamp(maxRows ?? p.MaxRows, 1, WorkplaceRepository.MaxRowsLimit);

        var name = string.IsNullOrWhiteSpace(connection) ? p.Connection : connection.Trim();
        if (name.Length == 0)
            return Partial("_PanelResults", new ResultsModel(
                null, "No connection chosen: open the panel's settings (⚙) and pick one."));

        try
        {
            var result = await runner.RunAsync(
                name, p.Sql, ParamsOf(Request.Form), rows, ct);
            return Partial("_PanelResults", new ResultsModel(result, null));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Partial("_PanelResults", new ResultsModel(null, QueryErrorText.Of(ex, log)));
        }
    }

    /// <summary>The bind values of the form (fields named param.NAME).</summary>
    private static Dictionary<string, string> ParamsOf(IFormCollection form) =>
        form.Keys
            .Where(k => k.StartsWith(ParamPrefix, StringComparison.Ordinal))
            .ToDictionary(k => k[ParamPrefix.Length..], k => form[k].ToString().Trim(),
                StringComparer.OrdinalIgnoreCase);

    /// <summary>"X copy", then "X copy 2"...: the first name not taken.</summary>
    private string UniqueCopyName(string name)
    {
        // Leave room for the suffix: names are limited to 64 characters.
        var stem = (name.Length > 52 ? name[..52] : name) + " copy";
        var candidate = stem;
        for (var n = 2; repo.NameTaken(candidate, 0); n++)
            candidate = $"{stem} {n}";
        return candidate;
    }

    /// <summary>A structural change: have the browser reload the page.</summary>
    private IActionResult Refresh()
    {
        Response.Headers["HX-Refresh"] = "true";
        return new NoContentResult();
    }
}
