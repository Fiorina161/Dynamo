using System.Text.Json;
using Dynamo.Modules.Shares;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Dynamo.Pages.Shares;

/// <summary>Model of the shared folder dialog.</summary>
public sealed record ShareFormModel(SharedFolder Folder, string? Error = null);

/// <summary>The list of shared folders, and the dialog that asks for a folder and its account.</summary>
public class IndexModel(SharedFolderRepository repo, IShareBrowser browser) : PageModel
{
    public List<SharedFolder> Folders { get; private set; } = [];

    public void OnGet() => Folders = repo.GetAll();

    public IActionResult OnGetForm(long id)
    {
        var folder = id == 0 ? new SharedFolder() : repo.Get(id);
        return folder is null ? NotFound() : Partial("_Form", new ShareFormModel(folder));
    }

    public IActionResult OnPostSave(long id, string? name, string? path, string? username, string? password, string? allowUpload)
    {
        var existing = id == 0 ? null : repo.Get(id);
        if (id != 0 && existing is null)
            return NotFound();

        var (normalized, pathError) = SharePath.Parse(path);
        var folder = new SharedFolder
        {
            Id = id,
            Name = (name ?? "").Trim(),
            Path = normalized ?? (path ?? "").Trim(),
            Username = (username ?? "").Trim(),
            // Left blank: keep the stored password (an edit); a password that could not be read is not kept.
            Password = string.IsNullOrEmpty(password) ? existing?.Password ?? "" : password,
            AllowUpload = allowUpload == "on",
        };

        var error = Validate(folder, pathError);
        if (error is not null)
        {
            Response.Headers["HX-Retarget"] = "#dlg";
            Response.Headers["HX-Reswap"] = "innerHTML";
            folder.Password = "";
            return Partial("_Form", new ShareFormModel(folder, error));
        }

        repo.Save(folder);
        Response.Headers["HX-Trigger"] = "closeDialog";
        return Partial("_List", repo.GetAll());
    }

    public IActionResult OnPostDelete(long id)
    {
        repo.Delete(id);
        Response.Headers["HX-Trigger"] = "closeDialog";
        return Partial("_List", repo.GetAll());
    }

    /// <summary>Tries what is typed in the dialog, saved or not: connects, reads the top of the folder.</summary>
    public async Task<IActionResult> OnPostTestFormAsync(
        long id, string? name, string? path, string? username, string? password, CancellationToken ct)
    {
        var (normalized, pathError) = SharePath.Parse(path);
        if (pathError is not null)
            return Toast(pathError);
        var stored = id == 0 ? null : repo.Get(id);
        var folder = new SharedFolder
        {
            Name = string.IsNullOrWhiteSpace(name) ? "(unsaved)" : name.Trim(),
            Path = normalized!,
            Username = (username ?? "").Trim(),
            Password = string.IsNullOrEmpty(password) ? stored?.Password ?? "" : password,
        };
        if (folder.Username.Length == 0 || folder.Password.Length == 0)
            return Toast("Enter the user and the password first.");
        try
        {
            return Toast(await browser.TestAsync(folder, ct));
        }
        catch (ShareException ex)
        {
            return Toast(ex.Message);
        }
    }

    private string? Validate(SharedFolder f, string? pathError)
    {
        if (f.Name.Length == 0) return "The name is required.";
        if (f.Name.Length > 64) return "The name is too long (max 64).";
        if (pathError is not null) return pathError;
        if (f.Username.Length == 0) return "The user is required: the folder is read with an account.";
        if (f.Username.Length > 128) return "The user is too long (max 128).";
        if (f.Password.Length == 0) return "The password is required.";
        if (repo.NameTaken(f.Name, f.Id)) return $"A shared folder named “{f.Name}” already exists.";
        return null;
    }

    private IActionResult Toast(string message)
    {
        Response.Headers["HX-Trigger"] = JsonSerializer.Serialize(new { toast = message });
        return new NoContentResult();
    }
}
