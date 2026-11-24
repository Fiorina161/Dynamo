using Dynamo.Core;
using Dynamo.Modules.Settings;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Dynamo.Pages;

/// <summary>
/// Landing page: sends the visitor to the first section shown in the menu
/// (Bookmarks, unless it was hidden in Settings).
/// </summary>
public class IndexModel(IEnumerable<IModule> modules, MenuOptions menu) : PageModel
{
    public IActionResult OnGet()
    {
        var first = modules.OrderBy(m => m.Order).FirstOrDefault(m => m.Href.Length > 0 && menu.IsVisible(m));
        return Redirect(first?.Href ?? "/Settings");
    }
}
