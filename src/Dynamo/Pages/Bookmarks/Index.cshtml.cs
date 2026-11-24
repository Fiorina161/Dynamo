using System.Text.Json;
using Dynamo.Modules.Bookmarks;
using Dynamo.Modules.Settings;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Dynamo.Pages.Bookmarks;

/// <summary>Model of the bookmark dialog.</summary>
public sealed record BookmarkFormModel(
    Bookmark Bookmark, IReadOnlyList<BookmarkGroup> Groups, string? Error = null);

/// <summary>Model of the group dialog.</summary>
public sealed record GroupFormModel(BookmarkGroup Group, string? Error = null);

/// <summary>
/// The page is rendered once; every other handler answers an htmx request
/// with the fragment to swap in (the board or a dialog form).
/// </summary>
public class IndexModel(BookmarkRepository repo) : PageModel
{
    public Board Board { get; private set; } = new();

    public void OnGet()
    {
        Board = repo.GetBoard();
    }

    // ---- Dialogs ---------------------------------------------------------

    public IActionResult OnGetBookmarkForm(long id, long? groupId)
    {
        var bookmark = id == 0
            ? new Bookmark { GroupId = groupId }
            : repo.GetBookmark(id);
        if (bookmark is null)
            return NotFound();
        return Partial("_BookmarkForm", new BookmarkFormModel(bookmark, repo.GetBoard().Groups));
    }

    public IActionResult OnGetGroupForm(long id)
    {
        var group = id == 0 ? new BookmarkGroup() : repo.GetGroup(id);
        if (group is null)
            return NotFound();
        return Partial("_GroupForm", new GroupFormModel(group));
    }

    // ---- Bookmarks -------------------------------------------------------

    public IActionResult OnPostSaveBookmark(long id, string? label, string? target, long? groupId)
    {
        label = label?.Trim();
        target = target?.Trim().Trim('"');
        if (string.IsNullOrEmpty(target))
        {
            var draft = new Bookmark { Id = id, Label = label ?? "", GroupId = groupId };
            Response.Headers["HX-Retarget"] = "#dlg";
            Response.Headers["HX-Reswap"] = "innerHTML";
            return Partial("_BookmarkForm", new BookmarkFormModel(
                draft, repo.GetBoard().Groups, "The target is required."));
        }

        repo.SaveBookmark(id, string.IsNullOrEmpty(label) ? target : label, target, groupId);
        return BoardAndClose();
    }

    public IActionResult OnPostDeleteBookmark(long id)
    {
        repo.DeleteBookmark(id);
        return BoardAndClose();
    }

    /// <summary>
    /// Drag and drop: the page sends the group a bookmark was dropped in and
    /// that group's bookmarks in their final order.
    /// </summary>
    public IActionResult OnPostArrange(long? groupId, string? ids)
    {
        repo.Arrange(groupId, ParseIds(ids));
        return BoardAndClose();
    }

    private static List<long> ParseIds(string? ids) =>
        (ids ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(long.Parse)
            .ToList();

    /// <summary>Drag and drop of a group: the groups in their final order.</summary>
    public IActionResult OnPostArrangeGroups(string? ids)
    {
        repo.ArrangeGroups(ParseIds(ids));
        return BoardAndClose();
    }

    /// <summary>
    /// Opens a local target on this machine. Web links never come here. Only for
    /// someone sitting at this computer: from another computer it would start
    /// programs on this one's desktop, and a bookmark can point at any program.
    /// </summary>
    public IActionResult OnPostOpen(long id)
    {
        var bookmark = repo.GetBookmark(id);
        if (bookmark is null)
            return NotFound();

        if (!ClientAccess.IsLocal(HttpContext))
        {
            Response.Headers["HX-Trigger"] = JsonSerializer.Serialize(new
            {
                toast = "Files, folders and programs can only be opened from the computer where Dynamo runs.",
            });
            return new NoContentResult();
        }

        var error = Launcher.Open(bookmark.Target);
        if (error is not null)
            Response.Headers["HX-Trigger"] = JsonSerializer.Serialize(new { toast = error });
        return new NoContentResult();
    }

    /// <summary>Web links: the browser follows the redirect in a new tab.</summary>
    public IActionResult OnGetGo(long id)
    {
        var bookmark = repo.GetBookmark(id);
        return bookmark is { IsWeb: true } ? Redirect(bookmark.Target) : NotFound();
    }

    // ---- Groups ----------------------------------------------------------

    public IActionResult OnPostSaveGroup(long id, string? name, string? color)
    {
        name = name?.Trim();
        if (string.IsNullOrEmpty(name))
        {
            Response.Headers["HX-Retarget"] = "#dlg";
            Response.Headers["HX-Reswap"] = "innerHTML";
            return Partial("_GroupForm", new GroupFormModel(
                new BookmarkGroup
                {
                    Id = id,
                    Color = GroupColors.Normalize(color),
                },
                "The name is required."));
        }

        repo.SaveGroup(id, name, color);
        return BoardAndClose();
    }

    public IActionResult OnPostDeleteGroup(long id)
    {
        repo.DeleteGroup(id);
        return BoardAndClose();
    }

    public IActionResult OnPostToggleGroup(long id)
    {
        repo.ToggleGroup(id);
        return BoardAndClose();
    }

    private PartialViewResult BoardAndClose()
    {
        Response.Headers["HX-Trigger"] = "closeDialog";
        return Partial("_Board", repo.GetBoard());
    }
}
