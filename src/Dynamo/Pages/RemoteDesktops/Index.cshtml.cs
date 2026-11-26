using System.Text.Json;
using Dynamo.Modules.RemoteDesktops;
using Dynamo.Modules.Settings;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Dynamo.Pages.RemoteDesktops;

/// <summary>Model of the list: the remote desktops, and whether this request may use Connect.</summary>
public sealed record RemoteDesktopListModel(IReadOnlyList<RemoteDesktop> Desktops, bool CanConnect);

/// <summary>Model of the dialog that adds or changes a remote desktop (and re-enters its credentials).</summary>
public sealed record RemoteDesktopFormModel(RemoteDesktop Desktop, bool CanConnect, string? Error = null);

/// <summary>
/// The remote desktops. Connect saves the account in the Windows Credential Manager of the person using this
/// computer and opens their Remote Desktop client: both happen on this computer's desktop, so it is only answered to
/// a request that comes from this computer (like Update now). Adding, changing and deleting work from anywhere.
/// </summary>
public class IndexModel(RemoteDesktopRepository repo, IRemoteDesktopLauncher launcher, ILogger<IndexModel> log) : PageModel
{
    public List<RemoteDesktop> Desktops { get; private set; } = [];

    /// <summary>This request comes from the computer Dynamo runs on.</summary>
    public bool CanConnect { get; private set; }

    public void OnGet()
    {
        Desktops = repo.GetAll();
        CanConnect = ClientAccess.IsLocal(HttpContext);
    }

    public IActionResult OnGetForm(long id)
    {
        var desktop = id == 0 ? new RemoteDesktop() : repo.Get(id);
        return desktop is null ? NotFound() : Partial("_Form", new RemoteDesktopFormModel(desktop, ClientAccess.IsLocal(HttpContext)));
    }

    public IActionResult OnPostSave(long id, string? name, string? host, string? username, string? password)
    {
        var existing = id == 0 ? null : repo.Get(id);
        if (id != 0 && existing is null)
            return NotFound();

        var (normalized, hostError) = RemoteDesktopHost.Parse(host);
        var desktop = new RemoteDesktop
        {
            Id = id,
            Name = (name ?? "").Trim(),
            Host = normalized ?? (host ?? "").Trim(),
            Username = (username ?? "").Trim(),
            // Left blank: keep the stored password; one that could not be read is not kept (it must be entered again).
            Password = string.IsNullOrEmpty(password) ? existing?.Password ?? "" : password,
        };

        var error = Validate(desktop, hostError);
        if (error is not null)
        {
            Response.Headers["HX-Retarget"] = "#dlg";
            Response.Headers["HX-Reswap"] = "innerHTML";
            desktop.Password = "";
            return Partial("_Form", new RemoteDesktopFormModel(desktop, ClientAccess.IsLocal(HttpContext), error));
        }

        repo.Save(desktop);
        return ListAndClose();
    }

    public IActionResult OnPostDelete(long id)
    {
        repo.Delete(id);
        return ListAndClose();
    }

    /// <summary>Saves the account in the user's Windows store and opens the client (this computer only).</summary>
    public IActionResult OnPostConnect(long id)
    {
        if (!ClientAccess.IsLocal(HttpContext))
            return Toast("Connect works only from the computer where Dynamo runs.");
        if (repo.Get(id) is not { } desktop)
            return NotFound();
        if (desktop.PasswordUnreadable || desktop.Password.Length == 0)
            return Toast($"“{desktop.Name}” has no usable password: edit it and enter the password again.");

        var problem = launcher.Connect(desktop);
        log.LogInformation("Remote desktop {Name} ({Host}) opened from {Client}: {Result}",
            desktop.Name, desktop.Host, HttpContext.Connection.RemoteIpAddress, problem ?? "ok");
        return Toast(problem is null
            ? $"Opening “{desktop.Name}”… If Windows still asks for a password, the saved one is no longer valid: use Edit to enter the new one."
            : $"“{desktop.Name}”: {problem}");
    }

    /// <summary>Removes the credential Windows saved for this host (this computer only).</summary>
    public IActionResult OnPostForget(long id)
    {
        if (!ClientAccess.IsLocal(HttpContext))
            return Toast("This works only from the computer where Dynamo runs.");
        if (repo.Get(id) is not { } desktop)
            return NotFound();
        var problem = launcher.Forget(desktop);
        return Toast(problem is null
            ? $"The password Windows saved for “{desktop.Host}” was removed. It is saved again at the next Connect."
            : $"“{desktop.Name}”: {problem}");
    }

    private string? Validate(RemoteDesktop d, string? hostError)
    {
        if (d.Name.Length == 0) return "The name is required.";
        if (d.Name.Length > 64) return "The name is too long (max 64).";
        if (hostError is not null) return hostError;
        if (d.Username.Length == 0) return "The user is required.";
        if (d.Username.Length > 128 || d.Username.Any(char.IsControl)) return "The user is not valid (max 128 characters, no control characters).";
        if (d.Password.Length == 0) return "The password is required.";
        if (d.Password.Length > 256) return "The password is too long (max 256).";
        if (repo.NameTaken(d.Name, d.Id)) return $"A remote desktop named “{d.Name}” already exists.";
        return null;
    }

    private IActionResult Toast(string message)
    {
        Response.Headers["HX-Trigger"] = JsonSerializer.Serialize(new { toast = message });
        return new NoContentResult();
    }

    private PartialViewResult ListAndClose()
    {
        Response.Headers["HX-Trigger"] = "closeDialog";
        return Partial("_List", new RemoteDesktopListModel(repo.GetAll(), ClientAccess.IsLocal(HttpContext)));
    }
}
