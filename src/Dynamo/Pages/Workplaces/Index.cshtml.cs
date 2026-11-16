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
public class IndexModel(WorkplaceRepository repo) : PageModel
{
    public List<Workplace> Workplaces { get; private set; } = [];

    public void OnGet()
    {
        Workplaces = repo.GetAll();
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
