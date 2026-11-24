using System.Text.Json;
using Dynamo.Modules.OracleConnections;
using Dynamo.Modules.Queries;
using Dynamo.Modules.Settings;
using Dynamo.Modules.Workplaces;
using Dynamo.Pages.Shared;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Dynamo.Pages.Workplaces;

/// <summary>
/// Model of the dialog that copies queries from another workspace into this one:
/// the workspaces that have something to import, and the one whose panels are
/// listed (null when there is none).
/// </summary>
public sealed record ImportModel(Workplace Target, IReadOnlyList<Workplace> Others, Workplace? Source);

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
    WorkplaceRepository repo, OracleConnector connector,
    IQueryRunner runner, ResultOptions resultOptions, ILogger<ViewModel> log) : PageModel
{
    /// <summary>How many rows a panel shows when its own Visible rows is 0 (Settings).</summary>
    public int DefaultRows => resultOptions.DefaultRows;


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

    /// <summary>
    /// The dialog to import queries from another workspace. <paramref name="from"/>
    /// picks the workspace whose panels are listed (the first one when missing);
    /// changing the list in the dialog asks for this again with another value.
    /// </summary>
    public IActionResult OnGetImportForm(long id, long? from)
    {
        var target = repo.Get(id);
        if (target is null)
            return NotFound();
        var others = repo.GetAll().Where(w => w.Id != id && w.PanelCount > 0).ToList();
        var source = others.FirstOrDefault(w => w.Id == from) ?? others.FirstOrDefault();
        return Partial("_ImportForm", new ImportModel(target, others, source is null ? null : repo.Get(source.Id)));
    }

    /// <summary>Copies the ticked panels of another workspace into this one.</summary>
    public IActionResult OnPostImport(long id, long from, List<long>? panel)
    {
        if (repo.Get(id) is null || repo.Get(from) is null)
            return NotFound();
        repo.ImportPanels(id, from, panel ?? []);
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

    /// <summary>Creates a panel with a query written here.</summary>
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

    /// <summary>The dialog to change a panel's own query (other panels it was copied from or to are not affected).</summary>
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

    /// <summary>A name, and one SELECT or WITH.</summary>
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

    /// <summary>
    /// Saves the order the panels were dragged into: <c>order</c> is every panel
    /// id of the workspace, comma separated, in the new order. A list that is not
    /// exactly the workspace's panels (the page is out of date) is refused with
    /// 409, and the page reloads. No content: the page already shows the order.
    /// </summary>
    public IActionResult OnPostReorderPanels(long id, string? order)
    {
        if (repo.Get(id) is null)
            return NotFound();
        var ids = new List<long>();
        foreach (var part in (order ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!long.TryParse(part, out var panelId))
                return BadRequest();
            ids.Add(panelId);
        }
        return repo.ReorderPanels(id, ids) ? new NoContentResult() : new ConflictResult();
    }

    /// <summary>Collapses a panel to its title line, or expands it. No content.</summary>
    public IActionResult OnPostCollapsePanel(long id, long panel, bool collapsed)
    {
        if (repo.GetPanel(panel) is not { } p || p.WorkplaceId != id)
            return NotFound();
        repo.SetCollapsed(panel, collapsed);
        return new NoContentResult();
    }

    /// <summary>Collapses every panel of the workspace, or expands them all. No content.</summary>
    public IActionResult OnPostCollapseAll(long id, bool collapsed)
    {
        if (repo.Get(id) is null)
            return NotFound();
        repo.SetAllCollapsed(id, collapsed);
        return new NoContentResult();
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
        long id, long panel, string? connection, long? seconds, long? maxRows, long? viewRows)
    {
        if (repo.GetPanel(panel) is not { } p || p.WorkplaceId != id)
            return NotFound();
        repo.SetPanel(panel, string.IsNullOrWhiteSpace(connection) ? null : connection.Trim(),
            seconds ?? 0, maxRows, viewRows);
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
        repo.SaveBinds(id, QueryActions.ParamsOf(Request.Form));
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
        var results = name.Length == 0
            ? new ResultsModel(null, "No connection chosen: open the panel's settings (⚙) and pick one.")
            : await QueryActions.RunAsync(
                runner, log, name, p.Sql, QueryActions.ParamsOf(Request.Form), rows, ct);
        return Partial("_PanelResults", new PanelResultsModel(results, panel));
    }

    /// <summary>
    /// Runs the panel's query again and sends the whole result (up to the export
    /// limit) as a CSV file.
    /// </summary>
    public async Task<IActionResult> OnPostCsvPanelAsync(
        long id, long panel, string? connection, CancellationToken ct)
    {
        if (repo.GetPanel(panel) is not { } p || p.WorkplaceId != id)
            return NotFound();

        var name = string.IsNullOrWhiteSpace(connection) ? p.Connection : connection.Trim();
        return await QueryActions.CsvAsync(
            runner, log, name, p.Sql, QueryActions.ParamsOf(Request.Form), p.QueryName, ct);
    }

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
