using Dynamo.Modules.Settings;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Dynamo.Pages;

/// <summary>Ends the session of a remote computer (the button is in the menu).</summary>
public class LogoutModel(SessionStore sessions) : PageModel
{
    public IActionResult OnGet() => Redirect("/");

    public IActionResult OnPost()
    {
        sessions.End(Request.Cookies[ClientAccess.CookieName]);
        Response.Cookies.Delete(ClientAccess.CookieName, new CookieOptions { Path = "/" });
        return Redirect("/Login");
    }
}
