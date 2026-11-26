using Dynamo.Modules.Shares;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Dynamo.Pages.Shares;

/// <summary>Model of the dialog that adds or changes a substring of a folder's filter list.</summary>
public sealed record FilterFormModel(SharedFolder Folder, SharedFolderFilter Filter, string? Error = null);

/// <summary>
/// One shared folder: its files and sub-folders (a page of them, filtered and sorted), and the download of a file.
/// Nothing outside the folder is reachable: the path in the address is resolved inside it or refused.
/// </summary>
public class BrowseModel(SharedFolderRepository repo, IShareBrowser browser, ILogger<BrowseModel> log) : PageModel
{
    public SharedFolder Folder { get; private set; } = new();
    public ShareListing? Listing { get; private set; }

    /// <summary>Why the folder could not be read (wrong password, not found...), or null.</summary>
    public string? Error { get; private set; }

    /// <summary>The folder's substring filters; the ticked ones narrow the list of files.</summary>
    public List<SharedFolderFilter> Filters { get; private set; } = [];

    public string Query { get; private set; } = "";
    public string Sort { get; private set; } = "name";
    public bool Descending { get; private set; }

    public async Task<IActionResult> OnGetAsync(
        long id, string? path, string? q, string? sort, string? dir, int p, CancellationToken ct)
    {
        if (repo.Get(id) is not { } folder)
            return NotFound();
        Folder = folder;
        Filters = repo.GetFilters(id);
        Query = (q ?? "").Trim();
        Sort = sort is "size" or "date" ? sort : "name";
        Descending = dir == "desc";
        try
        {
            Listing = await browser.ListAsync(
                folder, path, Query, Sort, Descending, p < 1 ? 1 : p, ct,
                Filters.Where(x => x.Active).Select(x => x.Text).ToList());
        }
        catch (ShareException ex)
        {
            Error = ex.Message;
        }
        return Page();
    }

    // ---- The substring filters (the box above the files) ------------------------------------------------

    /// <summary>The dialog to add a substring (<c>filter</c> 0) or to change or delete one.</summary>
    public IActionResult OnGetFilterForm(long id, long filter)
    {
        if (repo.Get(id) is not { } folder)
            return NotFound();
        var current = filter == 0 ? new SharedFolderFilter { FolderId = id } : repo.GetFilter(filter);
        return current is null || current.FolderId != id
            ? NotFound()
            : Partial("_FilterForm", new FilterFormModel(folder, current));
    }

    public IActionResult OnPostSaveFilter(long id, long filter, string? text)
    {
        if (repo.Get(id) is not { } folder)
            return NotFound();
        var existing = filter == 0 ? null : repo.GetFilter(filter);
        if (filter != 0 && (existing is null || existing.FolderId != id))
            return NotFound();
        text = (text ?? "").Trim();
        if (repo.FilterProblem(id, text, filter) is { } error)
        {
            Response.Headers["HX-Retarget"] = "#dlg";
            Response.Headers["HX-Reswap"] = "innerHTML";
            return Partial("_FilterForm", new FilterFormModel(
                folder, new SharedFolderFilter { Id = filter, FolderId = id, Text = text }, error));
        }
        repo.SaveFilter(id, filter, text);
        return Reload();
    }

    public IActionResult OnPostDeleteFilter(long id, long filter)
    {
        if (repo.GetFilter(filter) is not { } existing || existing.FolderId != id)
            return NotFound();
        repo.DeleteFilter(filter);
        return Reload();
    }

    /// <summary>Ticks or unticks a substring: the checkbox sends <c>active=true</c> only when ticked.</summary>
    public IActionResult OnPostToggleFilter(long id, long filter, bool active)
    {
        if (repo.GetFilter(filter) is not { } existing || existing.FolderId != id)
            return NotFound();
        repo.SetFilterActive(filter, active);
        return Reload();
    }

    /// <summary>Unticks every substring: all the files show again.</summary>
    public IActionResult OnPostClearFilters(long id)
    {
        if (repo.Get(id) is null)
            return NotFound();
        repo.ClearActiveFilters(id);
        return Reload();
    }

    /// <summary>The list is different now: the page reloads (same address, so the folder, sort and page stay).</summary>
    private IActionResult Reload()
    {
        Response.Headers["HX-Refresh"] = "true";
        return new NoContentResult();
    }

    /// <summary>Sends a file to the browser (read only; a download can be resumed).</summary>
    public async Task<IActionResult> OnGetDownloadAsync(long id, string? path, CancellationToken ct)
    {
        if (repo.Get(id) is not { } folder)
            return NotFound();
        try
        {
            var file = await browser.OpenAsync(folder, path, ct);
            log.LogInformation("Download of {File} from the shared folder {Folder} by {Client}",
                file.Name, folder.Name, HttpContext.Connection.RemoteIpAddress);
            return new FileStreamResult(file.Stream, "application/octet-stream")
            {
                FileDownloadName = file.Name,
                EnableRangeProcessing = true,
            };
        }
        catch (ShareException ex)
        {
            return new ContentResult
            {
                Content = ex.Message,
                ContentType = "text/plain; charset=utf-8",
                StatusCode = StatusCodes.Status422UnprocessableEntity,
            };
        }
    }
}
