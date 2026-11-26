using Dynamo.Modules.Shares;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Dynamo.Pages.Shares;

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

    public string Query { get; private set; } = "";
    public string Sort { get; private set; } = "name";
    public bool Descending { get; private set; }

    public async Task<IActionResult> OnGetAsync(
        long id, string? path, string? q, string? sort, string? dir, int p, CancellationToken ct)
    {
        if (repo.Get(id) is not { } folder)
            return NotFound();
        Folder = folder;
        Query = (q ?? "").Trim();
        Sort = sort is "size" or "date" ? sort : "name";
        Descending = dir == "desc";
        try
        {
            Listing = await browser.ListAsync(folder, path, Query, Sort, Descending, p < 1 ? 1 : p, ct);
        }
        catch (ShareException ex)
        {
            Error = ex.Message;
        }
        return Page();
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
