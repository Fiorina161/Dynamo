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

    // ---- Uploading ------------------------------------------------------------------------------------

    /// <summary>
    /// Saves a file into the folder you are in: the body of the request is the file itself (the page sends it as it is,
    /// no form encoding, so a big file streams straight through and is never held in memory or in a temporary file),
    /// its name is in <c>name</c>. An existing file is only replaced with <c>overwrite=1</c>. Answers 200 with a small
    /// JSON, or an error status with the reason as text: 409 exists, 413 too large, 403 not allowed, 422 anything else.
    /// </summary>
    public async Task<IActionResult> OnPostUploadAsync(long id, string? path, string? name, string? overwrite, CancellationToken ct)
    {
        if (repo.Get(id) is not { } folder)
            return NotFound();
        // Kestrel stops a request body at 30 MB; a trace archive is bigger. For this request only, the limit is the
        // one Dynamo enforces itself (plus a little for the request's own overhead), and the copy counts the bytes.
        if (HttpContext.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
            limit.MaxRequestBodySize = ShareBrowser.MaxUploadBytes + 1024 * 1024;
        try
        {
            // "1" (what the page sends) or "true": a boolean binder only knows the second.
            var replace = overwrite == "1" || string.Equals(overwrite, "true", StringComparison.OrdinalIgnoreCase);
            var saved = await browser.UploadAsync(folder, path, name, Request.Body, replace, ct);
            log.LogInformation("Upload of {File} ({Bytes} bytes{Replaced}) to the shared folder {Folder} by {Client}",
                saved.Name, saved.Length, saved.Replaced ? ", replaced" : "", folder.Name, HttpContext.Connection.RemoteIpAddress);
            return new JsonResult(new { ok = true, name = saved.Name, size = saved.Length, replaced = saved.Replaced });
        }
        catch (ShareUploadException ex)
        {
            return new ContentResult
            {
                Content = ex.Message,
                ContentType = "text/plain; charset=utf-8",
                StatusCode = ex.Problem switch
                {
                    UploadProblem.Exists => StatusCodes.Status409Conflict,
                    UploadProblem.TooLarge => StatusCodes.Status413PayloadTooLarge,
                    UploadProblem.NotAllowed => StatusCodes.Status403Forbidden,
                    _ => StatusCodes.Status422UnprocessableEntity,
                },
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
