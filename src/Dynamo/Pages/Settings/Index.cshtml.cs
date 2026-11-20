using Dynamo.Modules.Settings;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Dynamo.Pages.Settings;

/// <summary>
/// Preferences and options. For now: the password other computers must give.
/// From this computer it is changed freely; from another one the current
/// password is needed first (a stolen session must not be able to replace it).
/// </summary>
public class IndexModel(
    PasswordService passwords, SessionStore sessions, LoginThrottle throttle,
    CertificateService certificates, HttpsOptions https, UpdateChecker updates,
    UpdateInstaller installer, MenuOptions menu) : PageModel
{
    public string CurrentVersion => AppVersion.Format(updates.Current);
    public UpdateStatus UpdateStatus => updates.Status;
    public bool UpdateAvailable => updates.Available;
    public bool CheckAutomatically => updates.Enabled;
    public bool HttpsEnabled => https.Enabled;
    public int HttpsPort => https.Port;
    public bool RequireHttps => certificates.RequireHttpsForRemote;
    public DateTimeOffset? CertificateExpires => certificates.Expires;
    public string? Fingerprint => certificates.AuthorityFingerprint;
    public IReadOnlyList<string> CertificateNames => certificates.IssuedFor;

    /// <summary>This computer, running as a service: the "Update now" button can work.</summary>
    public bool CanInstallUpdate => installer.Supported && !IsRemote;
    public bool UpdateSupported => installer.Supported;
    public string? LastUpdateFailure => installer.LastFailure;

    /// <summary>The update was handed over: the service is about to restart.</summary>
    public string? Installing { get; private set; }

    public bool ShowBookmarks => menu.IsVisible("bookmarks");

    public bool HasPassword { get; private set; }
    public bool IsRemote { get; private set; }
    public string? Message { get; private set; }
    public string? Error { get; private set; }

    public void OnGet() => Fill();

    public IActionResult OnPostSetPassword(string? current, string? password, string? confirm)
    {
        Fill();
        if (!CurrentIsFine(current))
            return Page();
        if (PasswordService.Problem(password) is { } problem)
        {
            Error = problem;
            return Page();
        }
        if (password != confirm)
        {
            Error = "The two passwords are not the same.";
            return Page();
        }

        var changed = passwords.IsSet;
        passwords.Set(password!);
        // Changing the password ends every session; the one asking stays logged in.
        if (IsRemote)
            LoginModel.OpenSession(HttpContext, sessions, passwords);
        Fill();
        Message = changed
            ? "Password changed. The other computers have to log in again."
            : "Password set. Other computers can now log in with it.";
        return Page();
    }

    public IActionResult OnPostClearPassword(string? current)
    {
        Fill();
        if (!passwords.IsSet)
            return Page();
        if (!CurrentIsFine(current))
            return Page();

        passwords.Clear();
        Fill();
        Message = "Password removed. Other computers are refused again.";
        return Page();
    }

    /// <summary>The checkbox: show Bookmarks in the menu.</summary>
    public IActionResult OnPostMenuOptions(string? showBookmarks)
    {
        Fill();
        menu.SetVisible("bookmarks", showBookmarks == "on");
        Message = menu.IsVisible("bookmarks")
            ? "Bookmarks is shown in the menu."
            : "Bookmarks is hidden from the menu. Its page still opens at /Bookmarks.";
        return Page();
    }

    /// <summary>The checkbox: send another computer on plain http to https.</summary>
    public IActionResult OnPostHttpsOptions(string? requireHttps)
    {
        Fill();
        certificates.RequireHttpsForRemote = requireHttps == "on";
        Message = certificates.RequireHttpsForRemote
            ? "Other computers using http are now sent to https."
            : "Other computers may use plain http: the password and the session cookie travel in clear.";
        return Page();
    }

    /// <summary>
    /// A new server certificate, signed by the same authority: whoever trusts the
    /// authority does not have to do anything again.
    /// </summary>
    public IActionResult OnPostRenewCertificate()
    {
        Fill();
        if (https.Enabled)
        {
            certificates.Ensure(force: true);
            Message = $"A new server certificate was issued, valid until {certificates.Expires:yyyy-MM-dd}.";
        }
        return Page();
    }

    /// <summary>"Check now": asks GitHub for the latest release.</summary>
    public async Task<IActionResult> OnPostCheckUpdatesAsync(CancellationToken ct)
    {
        Fill();
        var status = await updates.CheckAsync(ct);
        if (status.Error is null && !updates.Available)
            Message = "Dynamo is up to date.";
        return Page();
    }

    /// <summary>
    /// "Update now": downloads the new version, checks it and has the service
    /// replace itself. Only from the computer Dynamo runs on: what is installed
    /// runs with the service's rights, so a browser elsewhere cannot ask for it.
    /// </summary>
    public async Task<IActionResult> OnPostInstallUpdateAsync(CancellationToken ct)
    {
        Fill();
        if (IsRemote)
        {
            Response.StatusCode = StatusCodes.Status403Forbidden;
            Error = "Updating is done from the computer where Dynamo runs, not from another one.";
            return Page();
        }

        var outcome = await installer.InstallAsync(ct);
        if (!outcome.Started)
        {
            Error = outcome.Error;
            return Page();
        }
        Installing = outcome.Version;
        return Page();
    }

    /// <summary>The checkbox: look for new versions once a day on its own.</summary>
    public IActionResult OnPostUpdateOptions(string? checkAutomatically)
    {
        Fill();
        updates.Enabled = checkAutomatically == "on";
        Message = updates.Enabled
            ? "Dynamo will look for new versions once a day."
            : "Dynamo will not look for new versions by itself.";
        return Page();
    }

    private void Fill()    {
        HasPassword = passwords.IsSet;
        IsRemote = !ClientAccess.IsLocal(HttpContext);
    }

    /// <summary>This computer needs nothing; another computer must give the current password.</summary>
    private bool CurrentIsFine(string? current)
    {
        if (!passwords.IsSet || !IsRemote)
            return true;

        var client = ClientAccess.ClientKey(HttpContext);
        if (throttle.IsBlocked(client, out var wait))
        {
            Response.StatusCode = StatusCodes.Status429TooManyRequests;
            Error = $"Too many wrong passwords. Try again in {(int)Math.Ceiling(wait.TotalSeconds)} seconds.";
            return false;
        }
        if (!passwords.Verify(current))
        {
            throttle.Failed(client);
            Error = "The current password is wrong.";
            return false;
        }
        throttle.Succeeded(client);
        return true;
    }
}
