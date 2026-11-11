using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Dynamo.Pages;

/// <summary>
/// Landing page. Only bookmarks exist for now; it will become a dashboard
/// once there is more than one use case.
/// </summary>
public class IndexModel : PageModel
{
    public IActionResult OnGet() => RedirectToPage("/Bookmarks/Index");
}
