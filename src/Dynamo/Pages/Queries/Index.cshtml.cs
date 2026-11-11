using Dynamo.Modules.OracleConnections;
using Dynamo.Modules.Queries;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Dynamo.Pages.Queries;

/// <summary>Model of the query dialog.</summary>
public sealed record QueryFormModel(
    SavedQuery Query,
    IReadOnlyList<string> Connections,
    IReadOnlyList<string> Categories,
    string From,
    string? Error = null);

/// <summary>A category and its queries, as listed.</summary>
public sealed record QueryGroup(string Category, IReadOnlyList<SavedQuery> Queries);

/// <summary>
/// Rendered once; the other handlers answer htmx requests with the list or
/// the dialog form. The dialog is also opened from the run page (From = "run"),
/// which then reloads itself instead of swapping the list.
/// </summary>
public class IndexModel(QueryRepository repo, OracleConnector connector) : PageModel
{
    public IReadOnlyList<QueryGroup> Groups { get; private set; } = [];

    public void OnGet()
    {
        Groups = GroupsOf(repo.GetAll());
    }

    public IActionResult OnGetForm(long id, string? from)
    {
        var query = id == 0 ? new SavedQuery() : repo.Get(id);
        if (query is null)
            return NotFound();
        return Partial("_Form", FormOf(query, from));
    }

    public IActionResult OnPostSave(
        long id, string? name, string? category, string? connection, string? sql,
        string? notes, string? from)
    {
        if (id != 0 && repo.Get(id) is null)
            return NotFound();

        var q = new SavedQuery
        {
            Id = id,
            Name = (name ?? "").Trim(),
            Category = (category ?? "").Trim(),
            Connection = (connection ?? "").Trim(),
            Sql = (sql ?? "").Replace("\r\n", "\n").Trim(),
            Notes = (notes ?? "").Trim(),
        };

        var error = Validate(q);
        if (error is not null)
        {
            Response.Headers["HX-Retarget"] = "#dlg";
            Response.Headers["HX-Reswap"] = "innerHTML";
            return Partial("_Form", FormOf(q, from, error));
        }

        var savedId = repo.Save(q);
        if (from == "run")
            return HxRedirect(Url.Page("/Queries/Run", new { id = savedId })!);
        return ListAndClose();
    }

    public IActionResult OnPostDelete(long id, string? from)
    {
        repo.Delete(id);
        if (from == "run")
            return HxRedirect(Url.Page("/Queries/Index")!);
        return ListAndClose();
    }

    /// <summary>
    /// htmx follows a 302 inside its request and would swap the whole page into
    /// the list; its own header makes the browser navigate instead.
    /// </summary>
    private IActionResult HxRedirect(string url)
    {
        Response.Headers["HX-Redirect"] = url;
        return new NoContentResult();
    }

    private QueryFormModel FormOf(SavedQuery q, string? from, string? error = null) =>
        new(q, connector.Names(), repo.Categories(), from ?? "", error);

    private static string? Validate(SavedQuery q)
    {
        if (q.Name.Length == 0)
            return "The name is required.";
        if (q.Name.Length > 100)
            return "The name is too long (max 100).";
        if (q.Category.Length > 50)
            return "The category is too long (max 50).";
        if (q.Sql.Length == 0)
            return "The SQL is required.";
        return SqlScanner.Analyze(q.Sql).Error;
    }

    private static List<QueryGroup> GroupsOf(IEnumerable<SavedQuery> queries) =>
        queries
            .GroupBy(q => q.Category)
            // Uncategorized last, the others alphabetically (the repository
            // already sorts by category then name).
            .OrderBy(g => g.Key.Length == 0)
            .ThenBy(g => g.Key, StringComparer.CurrentCultureIgnoreCase)
            .Select(g => new QueryGroup(g.Key, g.OrderBy(q => q.Name, StringComparer.CurrentCultureIgnoreCase).ToList()))
            .ToList();

    private PartialViewResult ListAndClose()
    {
        Response.Headers["HX-Trigger"] = "closeDialog";
        return Partial("_List", GroupsOf(repo.GetAll()));
    }
}
