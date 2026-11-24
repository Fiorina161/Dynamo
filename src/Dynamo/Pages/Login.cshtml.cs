using Dynamo.Modules.Settings;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Dynamo.Pages;

/// <summary>
/// Where another computer gives the password. This computer never comes here:
/// it is never asked for anything.
/// </summary>
public class LoginModel(
    PasswordService passwords, SessionStore sessions, LoginThrottle throttle) : PageModel
{
    public string ReturnUrl { get; private set; } = "/";
    public string? Error { get; private set; }

    /// <summary>No password is set: other computers are refused, there is nothing to type.</summary>
    public bool RemoteOff { get; private set; }

    public IActionResult OnGet(string? returnUrl)
    {
        if (ClientAccess.IsLocal(HttpContext))
            return Redirect("/");
        ReturnUrl = SafeReturn(returnUrl);
        RemoteOff = !passwords.IsSet;
        return Page();
    }

    public IActionResult OnPost(string? password, string? returnUrl)
    {
        if (ClientAccess.IsLocal(HttpContext))
            return Redirect("/");
        ReturnUrl = SafeReturn(returnUrl);
        if (!passwords.IsSet)
        {
            RemoteOff = true;
            return Page();
        }

        var client = ClientAccess.ClientKey(HttpContext);
        if (throttle.IsBlocked(client, out var wait))
        {
            Response.StatusCode = StatusCodes.Status429TooManyRequests;
            Error = $"Too many wrong passwords. Try again in {(int)Math.Ceiling(wait.TotalSeconds)} seconds.";
            return Page();
        }

        if (!passwords.Verify(password))
        {
            throttle.Failed(client);
            Error = "Wrong password.";
            return Page();
        }

        throttle.Succeeded(client);
        OpenSession(HttpContext, sessions, passwords);
        return LocalRedirect(ReturnUrl);
    }

    /// <summary>
    /// A cookie without an expiry date: the browser keeps it until it is closed.
    /// Marked Secure when the login came over https (never sent in clear again).
    /// </summary>
    public static void OpenSession(HttpContext context, SessionStore sessions, PasswordService passwords)
    {
        var token = sessions.Create(passwords.Stamp);
        context.Response.Cookies.Append(ClientAccess.CookieName, token, new CookieOptions
        {
            HttpOnly = true,
            Secure = context.Request.IsHttps,
            SameSite = SameSiteMode.Strict,
            Path = "/",
            IsEssential = true,
        });
    }

    /// <summary>Only a path on this site: never a way to send the user elsewhere.</summary>
    private static string SafeReturn(string? url) =>
        url is { Length: > 0 } && url[0] == '/' && !url.StartsWith("//", StringComparison.Ordinal)
            && !url.StartsWith("/\\", StringComparison.Ordinal) && !url.StartsWith("/Login", StringComparison.OrdinalIgnoreCase)
            ? url
            : "/";
}
