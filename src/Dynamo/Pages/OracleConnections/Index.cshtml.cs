using System.Text.Json;
using Dynamo.Modules.OracleConnections;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Dynamo.Pages.OracleConnections;

/// <summary>
/// Model of the connection dialog. <paramref name="CopyFrom"/> is the id of the
/// connection being duplicated (0 otherwise): the password is never sent to the
/// browser, so a blank password on the copy means "the original's".
/// </summary>
public sealed record ConnectionFormModel(
    OracleConnectionInfo Connection,
    string? Error = null,
    long CopyFrom = 0,
    string? CopyOf = null);

/// <summary>Model of the dialog that picks the tnsnames.ora aliases to import.</summary>
public sealed record ImportModel(
    string File, IReadOnlyList<TnsNamesImporter.Candidate> Candidates, int AlreadyThere);

/// <summary>Model of the confirmation of a batch delete.</summary>
public sealed record DeleteManyModel(IReadOnlyList<OracleConnectionInfo> Connections);

/// <summary>
/// Rendered once; every other handler answers an htmx request with a
/// fragment (the list, or the dialog form) or a toast.
/// </summary>
public class IndexModel(
    OracleConnectionRepository repo, OracleConnector connector, TnsNamesImporter importer)
    : PageModel
{
    public List<OracleConnectionInfo> Connections { get; private set; } = [];

    /// <summary>The user's tnsnames.ora, when there is one: the Import button needs it.</summary>
    public string? TnsFile { get; private set; }

    public void OnGet()
    {
        Connections = repo.GetAll();
        TnsFile = TnsNamesLocator.Find();
    }

    public IActionResult OnGetForm(long id)
    {
        var connection = id == 0 ? new OracleConnectionInfo() : repo.Get(id);
        return connection is null
            ? NotFound()
            : Partial("_Form", new ConnectionFormModel(connection));
    }

    /// <summary>
    /// The new-connection form prefilled from an existing one: same data source and
    /// schema, a free name. Nothing is created until it is saved.
    /// </summary>
    public IActionResult OnGetDuplicate(long id)
    {
        var source = repo.Get(id);
        if (source is null)
            return NotFound();

        var copy = new OracleConnectionInfo
        {
            Name = UniqueCopyName(source.Name),
            DataSource = source.DataSource,
            Username = source.Username,
            // The password stays on the server: see ConnectionFormModel.CopyFrom.
        };
        return Partial("_Form", new ConnectionFormModel(copy, null, source.Id, source.Name));
    }

    public IActionResult OnPostSave(
        long id, string? name, string? dataSource, string? username, string? password,
        long copyFrom)
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
            // Left blank: keep the stored password (an edit) or take the original's (a copy).
            Password = string.IsNullOrEmpty(password) ? StoredPassword(id, copyFrom) : password,
        };

        var error = Validate(c);
        if (error is not null)
        {
            Response.Headers["HX-Retarget"] = "#dlg";
            Response.Headers["HX-Reswap"] = "innerHTML";
            c.Password = "";
            return Partial("_Form", new ConnectionFormModel(
                c, error, copyFrom, copyFrom == 0 ? null : repo.Get(copyFrom)?.Name));
        }

        repo.Save(c);
        return ListAndClose();
    }

    /// <summary>Pins or unpins a connection; pinned ones are listed first.</summary>
    public IActionResult OnPostTogglePin(long id)
    {
        var c = repo.Get(id);
        if (c is null)
            return NotFound();
        repo.SetPinned(id, !c.Pinned);
        return Partial("_List", repo.GetAll());
    }

    /// <summary>
    /// Lists the aliases of the user's tnsnames.ora that are not connections yet,
    /// to pick from. Nothing is imported here.
    /// </summary>
    public IActionResult OnGetImportForm()
    {
        if (TnsNamesLocator.Find() is not { } file)
            return Toast("No tnsnames.ora found (looked in TNS_ADMIN and ORACLE_HOME).");
        try
        {
            var preview = importer.Inspect(file);
            return Partial("_ImportForm", new ImportModel(file, preview.New, preview.AlreadyThere));
        }
        catch (IOException ex)
        {
            return Toast($"Could not read {file}: {ex.Message}");
        }
    }

    /// <summary>Imports just the picked aliases (read again from the file).</summary>
    public IActionResult OnPostImport(List<string>? aliases)
    {
        if (aliases is not { Count: > 0 })
            return Toast("Nothing selected.");
        if (TnsNamesLocator.Find() is not { } file)
            return Toast("No tnsnames.ora found (looked in TNS_ADMIN and ORACLE_HOME).");

        TnsNamesImporter.Result result;
        try
        {
            result = importer.Import(file, aliases);
        }
        catch (IOException ex)
        {
            return Toast($"Could not read {file}: {ex.Message}");
        }

        var message = result.Added == 0
            ? "Nothing imported: the selected names are already connections."
            : $"Imported {result.Added} {(result.Added == 1 ? "connection" : "connections")}. " +
              "Fill in the schema and password before using them.";
        if (result.Conflicting.Count > 0)
            message += $" Defined twice with different targets, the first kept: {string.Join(", ", result.Conflicting)}.";
        Response.Headers["HX-Trigger"] = JsonSerializer.Serialize(new { closeDialog = "", toast = message });
        return Partial("_List", repo.GetAll());
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
        long id, string? name, string? dataSource, string? username, string? password,
        long copyFrom)
    {
        var c = new OracleConnectionInfo
        {
            Name = string.IsNullOrWhiteSpace(name) ? "(unsaved)" : name.Trim(),
            DataSource = (dataSource ?? "").Trim(),
            Username = (username ?? "").Trim(),
            Password = string.IsNullOrEmpty(password) ? StoredPassword(id, copyFrom) : password,
        };
        if (c.DataSource.Length == 0 || c.Username.Length == 0)
            return Toast("Enter a data source and a schema first.");
        return await ToastResult(c);
    }

    /// <summary>The password already stored for this edit (id) or this copy (copyFrom).</summary>
    private string StoredPassword(long id, long copyFrom) =>
        repo.Get(id != 0 ? id : copyFrom)?.Password ?? "";

    /// <summary>"PROD copy", then "PROD copy 2", "PROD copy 3"... the first free name.</summary>
    private string UniqueCopyName(string name)
    {
        // Leave room for the suffix: names are limited to 64 characters.
        var stem = (name.Length > 52 ? name[..52] : name) + " copy";
        var candidate = stem;
        for (var n = 2; repo.NameTaken(candidate, 0); n++)
            candidate = $"{stem} {n}";
        return candidate;
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
