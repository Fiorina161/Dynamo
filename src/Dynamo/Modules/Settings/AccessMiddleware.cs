namespace Dynamo.Modules.Settings;

/// <summary>
/// The gate in front of the whole site. This computer is never asked for
/// anything. Another computer is refused when no password is set, and has to
/// log in (password, then a session cookie) when one is.
/// </summary>
public sealed class AccessMiddleware(
    RequestDelegate next, PasswordService passwords, SessionStore sessions,
    CertificateService certificates, HttpsOptions https)
{
    /// <summary>The authority's public certificate, for the browser to trust. Always on http.</summary>
    public const string CertificatePath = "/dynamo-certificate.cer";

    // What the login page itself needs; nothing in them is private.
    private static readonly string[] OpenPrefixes = ["/css/", "/js/", "/lib/", "/favicon.svg", CertificatePath];

    public async Task InvokeAsync(HttpContext context)
    {
        if (ClientAccess.IsLocal(context))
        {
            await next(context);
            return;
        }

        if (!passwords.IsSet)
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            context.Response.ContentType = "text/html; charset=utf-8";
            await context.Response.WriteAsync(RemoteOffPage);
            return;
        }

        var path = context.Request.Path;
        var value = path.Value ?? "/";

        // Another computer on plain http is sent to https (the password and the
        // session cookie must not travel in clear), except to fetch the certificate
        // it needs in order to trust https in the first place.
        if (https.Enabled && certificates.Current is not null && !context.Request.IsHttps
            && certificates.RequireHttpsForRemote
            && !path.Equals(CertificatePath, StringComparison.OrdinalIgnoreCase))
        {
            var host = new HostString(context.Request.Host.Host, https.Port == 443 ? 443 : https.Port);
            var target = $"https://{host.ToUriComponent()}{context.Request.PathBase}{path}{context.Request.QueryString}";
            context.Response.StatusCode = StatusCodes.Status307TemporaryRedirect;
            context.Response.Headers.Location = target;
            return;
        }

        var isOpen = path.Equals("/Login", StringComparison.OrdinalIgnoreCase)
            || OpenPrefixes.Any(p => value.StartsWith(p, StringComparison.OrdinalIgnoreCase));
        if (isOpen || sessions.IsValid(context.Request.Cookies[ClientAccess.CookieName], passwords.Stamp))
        {
            await next(context);
            return;
        }

        // Not logged in. htmx would swap a redirect into the page: its own
        // header makes the browser navigate instead.
        var login = "/Login?returnUrl=" + Uri.EscapeDataString(path + context.Request.QueryString);
        if (context.Request.Headers.ContainsKey("HX-Request"))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            context.Response.Headers["HX-Redirect"] = "/Login";
        }
        else if (HttpMethods.IsGet(context.Request.Method))
        {
            context.Response.Redirect(login);
        }
        else
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        }
    }

    private const string RemoteOffPage = """
        <!DOCTYPE html>
        <html lang="en"><head><meta charset="utf-8" /><title>Dynamo</title>
        <meta name="viewport" content="width=device-width, initial-scale=1" />
        <style>body{font:16px system-ui,"Segoe UI",sans-serif;max-width:34em;margin:4em auto;padding:0 1em;color:#1d2033}
        h1{font-size:22px}p{color:#555}</style></head>
        <body><h1>Dynamo</h1>
        <p>Remote access is off. Dynamo only answers other computers once a password
        is set: open <b>Settings</b> on the computer where Dynamo runs and choose one.</p>
        </body></html>
        """;
}
