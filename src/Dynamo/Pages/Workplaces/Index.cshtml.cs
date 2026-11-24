using Dynamo.Modules.OracleConnections;
using Dynamo.Modules.Workplaces;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Dynamo.Pages.Workplaces;

/// <summary>
/// Model of the dialog that names a workplace: a new one (Id 0), a rename, or a
/// copy of another (<c>DuplicateOf</c> = the original's id).
/// </summary>
public sealed record WorkplaceNameModel(
    long Id, string Name, string? Error = null, long DuplicateOf = 0);

/// <summary>The list of saved workplaces, and the dialog to create one.</summary>
public class IndexModel(WorkplaceRepository repo, OracleConnector connector) : PageModel
{
    public List<Workplace> Workplaces { get; private set; } = [];

    /// <summary>True when a DBA connection exists: the DBA workspace button needs one.</summary>
    public bool HasDbaConnection { get; private set; }

    public void OnGet()
    {
        Workplaces = repo.GetAll();
        HasDbaConnection = connector.Choices().Any(c => c.Dba);
    }

    /// <summary>The dialog to create a workspace from DBA templates.</summary>
    public IActionResult OnGetDbaForm() =>
        Partial("_DbaForm", DbaFormModel.Open(null, connector.Choices()));

    /// <summary>Creates a workspace holding the ticked DBA templates, on a DBA connection.</summary>
    public IActionResult OnPostCreateDba(string? name, string? connection, List<string>? template)
    {
        name = name?.Trim() ?? "";
        connection = (connection ?? "").Trim();
        var choices = connector.Choices();
        var error = Validate(repo, name, 0) ?? ViewModel.DbaProblem(choices, connection, template);
        if (error is not null)
        {
            Response.Headers["HX-Retarget"] = "#dlg";
            Response.Headers["HX-Reswap"] = "innerHTML";
            return Partial("_DbaForm", new DbaFormModel(
                null, name, connection, choices.Where(c => c.Dba).ToList(),
                (template ?? []).ToHashSet(), error));
        }

        var id = repo.Create(name);
        repo.AddTemplates(id, connection, template!);
        Response.Headers["HX-Redirect"] = Url.Page("/Workplaces/View", new { id })!;
        return new NoContentResult();
    }

    public IActionResult OnGetNewForm() => Partial("_NameForm", new WorkplaceNameModel(0, ""));

    public IActionResult OnPostCreate(string? name)
    {
        name = name?.Trim() ?? "";
        var error = Validate(repo, name, 0);
        if (error is not null)
        {
            Response.Headers["HX-Retarget"] = "#dlg";
            Response.Headers["HX-Reswap"] = "innerHTML";
            return Partial("_NameForm", new WorkplaceNameModel(0, name, error));
        }

        var id = repo.Create(name);
        // htmx would swap a 302's page into the dialog; its own header
        // navigates instead.
        Response.Headers["HX-Redirect"] = Url.Page("/Workplaces/View", new { id })!;
        return new NoContentResult();
    }

    /// <summary>Why this name cannot be used, or null.</summary>
    public static string? Validate(WorkplaceRepository repo, string name, long exceptId) =>
        name.Length == 0 ? "The name is required."
        : name.Length > 64 ? "The name is too long (max 64)."
        : repo.NameTaken(name, exceptId) ? $"A workspace named “{name}” already exists."
        : null;
}
