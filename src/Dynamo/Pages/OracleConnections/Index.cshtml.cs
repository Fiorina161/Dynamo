using System.Text.Json;
using Dynamo.Modules.OracleConnections;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Dynamo.Pages.OracleConnections;

/// <summary>Model of the connection dialog.</summary>
public sealed record ConnectionFormModel(OracleConnectionInfo Connection, string? Error = null);

/// <summary>Model of the confirmation of a batch delete.</summary>
public sealed record DeleteManyModel(IReadOnlyList<OracleConnectionInfo> Connections);

/// <summary>
/// Rendered once; every other handler answers an htmx request with a
/// fragment (the list, or the dialog form) or a toast.
/// </summary>
public class IndexModel(OracleConnectionRepository repo, OracleConnector connector) : PageModel
{
    public List<OracleConnectionInfo> Connections { get; private set; } = [];

    public void OnGet()
    {
        Connections = repo.GetAll();
    }

    public IActionResult OnGetForm(long id)
    {
        var connection = id == 0 ? new OracleConnectionInfo() : repo.Get(id);
        return connection is null
            ? NotFound()
            : Partial("_Form", new ConnectionFormModel(connection));
    }

    public IActionResult OnPostSave(
        long id, string? name, string? dataSource, string? username, string? password)
    {
        var existing = id == 0 ? null : repo.Get(id);
        if (id != 0 && existing is null)
            return NotFound();

        var c = new OracleConnectionInfo
        {
            Id = id,
            Name = (name ?? "").Trim(),
            DataSource = (dataSource ?? "").Trim(),
            Username = (username ?? "").Trim(),
            // Left blank on an existing connection: keep the stored password.
            Password = string.IsNullOrEmpty(password) ? existing?.Password ?? "" : password,
        };

        var error = Validate(c);
        if (error is not null)
        {
            Response.Headers["HX-Retarget"] = "#dlg";
            Response.Headers["HX-Reswap"] = "innerHTML";
            c.Password = "";
            return Partial("_Form", new ConnectionFormModel(c, error));
        }

        repo.Save(c);
        return ListAndClose();
    }

    public IActionResult OnPostDelete(long id)
    {
        repo.Delete(id);
        return ListAndClose();
    }

    /// <summary>
    /// First step of a batch delete: shows exactly which connections are about to
    /// go, and asks. Nothing is deleted here.
    /// </summary>
    public IActionResult OnPostConfirmDelete(List<long>? ids)
    {
        var connections = repo.GetMany(ids ?? []);
        return connections.Count == 0
            ? Toast("Nothing selected.")
            : Partial("_DeleteMany", new DeleteManyModel(connections));
    }

    /// <summary>Second step: deletes the confirmed connections.</summary>
    public IActionResult OnPostDeleteMany(List<long>? ids)
    {
        repo.DeleteMany(ids ?? []);
        return ListAndClose();
    }

    /// <summary>Tests a saved connection (the Test button of a row).</summary>
    public async Task<IActionResult> OnPostTest(long id)
    {
        var connection = repo.Get(id);
        return connection is null ? NotFound() : await ToastResult(connection);
    }

    /// <summary>Tests what is typed in the dialog, saved or not.</summary>
    public async Task<IActionResult> OnPostTestForm(
        long id, string? name, string? dataSource, string? username, string? password)
    {
        var c = new OracleConnectionInfo
        {
            Name = string.IsNullOrWhiteSpace(name) ? "(unsaved)" : name.Trim(),
            DataSource = (dataSource ?? "").Trim(),
            Username = (username ?? "").Trim(),
            Password = string.IsNullOrEmpty(password) ? repo.Get(id)?.Password ?? "" : password,
        };
        if (c.DataSource.Length == 0 || c.Username.Length == 0)
            return Toast("Enter a data source and a schema first.");
        return await ToastResult(c);
    }

    private static string? ValidateText(string label, string value, int max) =>
        value.Length == 0 ? $"{label} is required."
        : value.Length > max ? $"{label} is too long (max {max})."
        : null;

    private string? Validate(OracleConnectionInfo c)
    {
        var error = ValidateText("The name", c.Name, 64)
            ?? ValidateText("The data source", c.DataSource, 2000)
            ?? ValidateText("The schema", c.Username, 128);
        if (error is not null)
            return error;
        if (c.Password.Length == 0)
            return "The password is required.";
        if (repo.NameTaken(c.Name, c.Id))
            return $"A connection named “{c.Name}” already exists.";
        return null;
    }

    private async Task<IActionResult> ToastResult(OracleConnectionInfo c) =>
        Toast((await connector.TestAsync(c)).Message);

    private IActionResult Toast(string message)
    {
        Response.Headers["HX-Trigger"] = JsonSerializer.Serialize(new { toast = message });
        return new NoContentResult();
    }

    private PartialViewResult ListAndClose()
    {
        Response.Headers["HX-Trigger"] = "closeDialog";
        return Partial("_List", repo.GetAll());
    }
}
