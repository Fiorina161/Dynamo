using System.Net;
using System.Text.RegularExpressions;
using Dynamo.Modules.Bookmarks;
using Dynamo.Modules.Settings;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Dynamo.Tests;

/// <summary>
/// The password asked from other computers, and only from them. The in-memory
/// server has no real socket: a test header (X-Test-Client) gives a request the
/// address of a remote computer.
/// </summary>
public class RemoteAccessTests
{
    private sealed class FakeClientAddress : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (context, following) =>
            {
                if (context.Request.Headers.TryGetValue("X-Test-Client", out var ip))
                {
                    context.Connection.RemoteIpAddress = IPAddress.Parse(ip.ToString());
                    context.Connection.LocalIpAddress = IPAddress.Parse("10.0.0.5");
                }
                await following(context);
            });
            next(app);
        };
    }

    private const string Secret = "correct horse battery";

    private string _home = "";
    private string? _previousHome;
    private WebApplicationFactory<Program> _factory = null!;
    private PasswordService _passwords = null!;
    private SettingsRepository _settings = null!;
    private int _ipCounter;

    [OneTimeSetUp]
    public void SetUp()
    {
        _previousHome = Environment.GetEnvironmentVariable("DYNAMO_HOME");
        _home = Path.Combine(Path.GetTempPath(), "dynamo-access-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("DYNAMO_HOME", _home);
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            b.ConfigureServices(s =>
            {
                s.AddTransient<IStartupFilter, FakeClientAddress>();
                // These tests are about the password over http; https has its own tests.
                s.AddSingleton(new HttpsOptions(0));
                // A cheap hash: the tests set and check many passwords.
                s.AddSingleton(sp => new PasswordService(sp.GetRequiredService<SettingsRepository>(), 1000));
            }));
        _factory.CreateClient().Dispose();   // starts the host
        _passwords = _factory.Services.GetRequiredService<PasswordService>();
        _settings = _factory.Services.GetRequiredService<SettingsRepository>();
    }

    [OneTimeTearDown]
    public void TearDown()
    {
        _factory.Dispose();
        Environment.SetEnvironmentVariable("DYNAMO_HOME", _previousHome);
        try { Directory.Delete(_home, true); } catch { /* best effort */ }
    }

    [SetUp]
    public void Reset() => _passwords.Clear();

    /// <summary>A browser: its own cookies, no automatic redirects, optionally another computer.</summary>
    private HttpClient Browser(string? remoteIp = null)
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        if (remoteIp is not null)
            client.DefaultRequestHeaders.Add("X-Test-Client", remoteIp);
        return client;
    }

    private string NextIp() => $"192.168.50.{Interlocked.Increment(ref _ipCounter)}";

    /// <summary>The antiforgery token of a page: a form's hidden field, else the one htmx sends.</summary>
    private static async Task<string> Token(HttpClient client, string url)
    {
        var html = await client.GetStringAsync(url);
        var field = Regex.Match(html, "__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"");
        return field.Success
            ? field.Groups[1].Value
            : Regex.Match(html, "\"RequestVerificationToken\": \"([^\"]+)\"").Groups[1].Value;
    }

    private static async Task<HttpResponseMessage> Form(
        HttpClient client, string pageForToken, string url, params (string, string)[] fields)
    {
        var token = await Token(client, pageForToken);
        var all = fields.Append(("__RequestVerificationToken", token))
            .Select(f => KeyValuePair.Create(f.Item1, f.Item2));
        return await client.PostAsync(url, new FormUrlEncodedContent(all));
    }

    private static Task<HttpResponseMessage> LogIn(HttpClient client, string password, string? returnUrl = null) =>
        Form(client, "/Login", "/Login", ("password", password), ("returnUrl", returnUrl ?? "/Bookmarks"));

    private async Task<HttpClient> LoggedInRemote(string? ip = null)
    {
        // Only the first browser sets it: setting it again would end the others.
        if (!_passwords.Verify(Secret))
            _passwords.Set(Secret);
        var client = Browser(ip ?? NextIp());
        var response = await LogIn(client, Secret);
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Redirect));
        return client;
    }

    // ---- This computer is never asked -------------------------------------

    [Test]
    public async Task ThisComputer_IsNeverAsked_WithOrWithoutAPassword()
    {
        var local = Browser();
        _passwords.Clear();
        Assert.That((await local.GetAsync("/Bookmarks")).StatusCode, Is.EqualTo(HttpStatusCode.OK));

        _passwords.Set(Secret);
        Assert.That((await local.GetAsync("/Bookmarks")).StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That((await local.GetAsync("/Settings")).StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var login = await local.GetAsync("/Login");
        Assert.That(login.StatusCode, Is.EqualTo(HttpStatusCode.Redirect), "nothing to log into from here");
        Assert.That(login.Headers.Location!.OriginalString, Is.EqualTo("/"));
        Assert.That(await local.GetStringAsync("/Bookmarks"), Does.Not.Contain("Log out"));
    }

    [Test]
    public async Task TheComputersOwnAddresses_AreLocal_ProxiedRequestsAreNot()
    {
        _passwords.Set(Secret);

        // Loopback, and the machine's own LAN address, are this computer.
        Assert.That((await Browser("127.0.0.1").GetAsync("/Bookmarks")).StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That((await Browser("::1").GetAsync("/Bookmarks")).StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That((await Browser("10.0.0.5").GetAsync("/Bookmarks")).StatusCode, Is.EqualTo(HttpStatusCode.OK));

        // Through a proxy (even on this computer) it is someone else.
        var proxied = Browser("127.0.0.1");
        proxied.DefaultRequestHeaders.Add("X-Forwarded-For", "203.0.113.9");
        var response = await proxied.GetAsync("/Bookmarks");
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Redirect));
        Assert.That(response.Headers.Location!.OriginalString, Does.StartWith("/Login"));
    }

    // ---- Another computer: refused without a password ------------------------

    [Test]
    public async Task AnotherComputer_IsRefused_WhileNoPasswordIsSet()
    {
        var remote = Browser(NextIp());
        foreach (var path in new[] { "/", "/Bookmarks", "/Oracle", "/Login", "/css/site.css" })
        {
            var response = await remote.GetAsync(path);
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden), path);
        }
        var page = await (await remote.GetAsync("/Bookmarks")).Content.ReadAsStringAsync();
        Assert.That(page, Does.Contain("Remote access is off"));
        Assert.That(page, Does.Not.Contain("Bookmarks ·"), "no part of the site is shown");
    }

    // ---- Another computer: logs in with the password ---------------------------

    [Test]
    public async Task AnotherComputer_IsSentToTheLoginPage_UntilItLogsIn()
    {
        _passwords.Set(Secret);
        var remote = Browser(NextIp());

        var page = await remote.GetAsync("/Workplaces?x=1");
        Assert.That(page.StatusCode, Is.EqualTo(HttpStatusCode.Redirect));
        Assert.That(page.Headers.Location!.OriginalString, Is.EqualTo("/Login?returnUrl=%2FWorkplaces%3Fx%3D1"));

        // htmx: a header makes the browser navigate; a plain POST is simply refused.
        var htmx = new HttpRequestMessage(HttpMethod.Post, "/Bookmarks?handler=Open&id=1");
        htmx.Headers.Add("HX-Request", "true");
        var answer = await remote.SendAsync(htmx);
        Assert.That(answer.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
        Assert.That(answer.Headers.GetValues("HX-Redirect").Single(), Is.EqualTo("/Login"));
        Assert.That((await remote.PostAsync("/Bookmarks?handler=Open&id=1", new StringContent(""))).StatusCode,
            Is.EqualTo(HttpStatusCode.Unauthorized));

        // The login page and its assets are open, nothing else.
        Assert.That((await remote.GetAsync("/Login")).StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That((await remote.GetAsync("/css/site.css")).StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That((await remote.GetAsync("/js/site.js")).StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(await remote.GetStringAsync("/Login"), Does.Not.Contain("Bookmarks"));
    }

    [Test]
    public async Task LoggingIn_NeedsTheRightPassword_AndLastsUntilTheBrowserCloses()
    {
        _passwords.Set(Secret);
        var remote = Browser(NextIp());

        var wrong = await LogIn(remote, "not the password");
        Assert.That(wrong.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(await wrong.Content.ReadAsStringAsync(), Does.Contain("Wrong password."));
        Assert.That(wrong.Headers.Contains("Set-Cookie") && wrong.Headers.GetValues("Set-Cookie").Any(c => c.StartsWith("dynamo_session")), Is.False);
        Assert.That((await remote.GetAsync("/Bookmarks")).StatusCode, Is.EqualTo(HttpStatusCode.Redirect));

        var right = await LogIn(remote, Secret, "/Workplaces");
        Assert.That(right.StatusCode, Is.EqualTo(HttpStatusCode.Redirect));
        Assert.That(right.Headers.Location!.OriginalString, Is.EqualTo("/Workplaces"));

        // A session cookie: no expiry of its own, so the browser drops it when closed.
        var cookie = right.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith("dynamo_session="));
        Assert.That(cookie, Does.Contain("httponly").IgnoreCase.And.Contain("samesite=strict").IgnoreCase);
        Assert.That(cookie, Does.Not.Contain("expires").IgnoreCase.And.Not.Contain("max-age").IgnoreCase);

        Assert.That((await remote.GetAsync("/Bookmarks")).StatusCode, Is.EqualTo(HttpStatusCode.OK), "logged in");
        Assert.That(await remote.GetStringAsync("/Bookmarks"), Does.Contain("Log out"));
    }

    [Test]
    public async Task TheLoginPage_CannotSendTheUserToAnotherSite()
    {
        _passwords.Set(Secret);
        foreach (var (given, expected) in new[]
        {
            ("//evil.example/x", "/"), ("https://evil.example", "/"), ("/\\evil.example", "/"),
            ("evil", "/"), ("/Login", "/"), ("/Workplaces?a=1", "/Workplaces?a=1"),
        })
        {
            var remote = Browser(NextIp());
            var response = await LogIn(remote, Secret, given);
            Assert.That(response.Headers.Location!.OriginalString, Is.EqualTo(expected), given);
        }
    }

    [Test]
    public async Task WrongPasswords_AreThrottled_PerComputer()
    {
        _passwords.Set(Secret);
        var attacker = NextIp();
        var browser = Browser(attacker);
        for (var i = 0; i < LoginThrottle.MaxFailures; i++)
            await LogIn(browser, "guess " + i);

        // Locked out: even the right password is refused now.
        var locked = await LogIn(browser, Secret);
        Assert.That(locked.StatusCode, Is.EqualTo(HttpStatusCode.TooManyRequests));
        Assert.That(await locked.Content.ReadAsStringAsync(), Does.Contain("Too many wrong passwords"));
        Assert.That((await browser.GetAsync("/Bookmarks")).StatusCode, Is.EqualTo(HttpStatusCode.Redirect));

        // Another computer is not affected.
        var other = Browser(NextIp());
        Assert.That((await LogIn(other, Secret)).StatusCode, Is.EqualTo(HttpStatusCode.Redirect));
    }

    [Test]
    public async Task LoggingOut_EndsTheSession()
    {
        var remote = await LoggedInRemote();
        Assert.That((await remote.GetAsync("/Bookmarks")).StatusCode, Is.EqualTo(HttpStatusCode.OK));

        var response = await Form(remote, "/Bookmarks", "/Logout");
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Redirect));
        Assert.That(response.Headers.Location!.OriginalString, Is.EqualTo("/Login"));
        Assert.That((await remote.GetAsync("/Bookmarks")).StatusCode, Is.EqualTo(HttpStatusCode.Redirect));
    }

    [Test]
    public async Task ChangingOrRemovingThePassword_EndsEverySession()
    {
        var a = await LoggedInRemote();
        var b = await LoggedInRemote();
        Assert.That((await a.GetAsync("/Bookmarks")).StatusCode, Is.EqualTo(HttpStatusCode.OK));

        _passwords.Set("another password 2");
        Assert.That((await a.GetAsync("/Bookmarks")).StatusCode, Is.EqualTo(HttpStatusCode.Redirect));
        Assert.That((await b.GetAsync("/Bookmarks")).StatusCode, Is.EqualTo(HttpStatusCode.Redirect));
        Assert.That((await LogIn(a, Secret)).StatusCode, Is.EqualTo(HttpStatusCode.OK), "the old one no longer works");
        Assert.That((await LogIn(a, "another password 2")).StatusCode, Is.EqualTo(HttpStatusCode.Redirect));

        _passwords.Clear();
        Assert.That((await a.GetAsync("/Bookmarks")).StatusCode, Is.EqualTo(HttpStatusCode.Forbidden), "no password, no remote access");
    }

    // ---- Settings -------------------------------------------------------------

    [Test]
    public async Task Settings_SetAChangeAndRemoveThePassword_FromThisComputer_WithoutTheCurrentOne()
    {
        var local = Browser();

        var tooShort = await Form(local, "/Settings", "/Settings?handler=SetPassword", ("password", "short"), ("confirm", "short"));
        Assert.That(await tooShort.Content.ReadAsStringAsync(), Does.Contain("at least 8 characters"));
        var different = await Form(local, "/Settings", "/Settings?handler=SetPassword", ("password", Secret), ("confirm", Secret + "x"));
        Assert.That(await different.Content.ReadAsStringAsync(), Does.Contain("not the same"));
        Assert.That(_passwords.IsSet, Is.False);

        var set = await Form(local, "/Settings", "/Settings?handler=SetPassword", ("password", Secret), ("confirm", Secret));
        Assert.That(await set.Content.ReadAsStringAsync(), Does.Contain("Password set"));
        Assert.That(_passwords.Verify(Secret), Is.True);

        // Stored in the same database, as a salted hash, never in clear.
        var stored = _settings.Get("access.password")!;
        Assert.That(stored, Does.StartWith("pbkdf2-sha256$").And.Not.Contain(Secret));

        var changed = await Form(local, "/Settings", "/Settings?handler=SetPassword", ("password", "a new password!"), ("confirm", "a new password!"));
        Assert.That(await changed.Content.ReadAsStringAsync(), Does.Contain("Password changed"));
        Assert.That(_passwords.Verify(Secret), Is.False);
        Assert.That(_passwords.Verify("a new password!"), Is.True);

        var removed = await Form(local, "/Settings", "/Settings?handler=ClearPassword");
        Assert.That(await removed.Content.ReadAsStringAsync(), Does.Contain("Password removed"));
        Assert.That(_passwords.IsSet, Is.False);
        Assert.That(_settings.Get("access.password"), Is.Null);
    }

    [Test]
    public async Task Settings_FromAnotherComputer_NeedTheCurrentPassword()
    {
        var remote = await LoggedInRemote();

        var none = await Form(remote, "/Settings", "/Settings?handler=SetPassword", ("password", "brand new pass"), ("confirm", "brand new pass"));
        Assert.That(await none.Content.ReadAsStringAsync(), Does.Contain("current password is wrong"));
        var wrong = await Form(remote, "/Settings", "/Settings?handler=SetPassword", ("current", "nope nope"), ("password", "brand new pass"), ("confirm", "brand new pass"));
        Assert.That(await wrong.Content.ReadAsStringAsync(), Does.Contain("current password is wrong"));
        Assert.That(_passwords.Verify(Secret), Is.True, "nothing changed");

        var other = await LoggedInRemote();   // a second logged-in computer
        var ok = await Form(remote, "/Settings", "/Settings?handler=SetPassword", ("current", Secret), ("password", "brand new pass"), ("confirm", "brand new pass"));
        Assert.That(await ok.Content.ReadAsStringAsync(), Does.Contain("Password changed"));
        Assert.That(_passwords.Verify("brand new pass"), Is.True);
        Assert.That((await remote.GetAsync("/Settings")).StatusCode, Is.EqualTo(HttpStatusCode.OK), "the one who changed it stays logged in");
        Assert.That((await other.GetAsync("/Bookmarks")).StatusCode, Is.EqualTo(HttpStatusCode.Redirect), "the others have to log in again");

        // Turning remote access off from here asks for the password too, then locks this computer out.
        var refused = await Form(remote, "/Settings", "/Settings?handler=ClearPassword", ("current", "wrong wrong"));
        Assert.That(await refused.Content.ReadAsStringAsync(), Does.Contain("current password is wrong"));
        Assert.That(_passwords.IsSet, Is.True);
        await Form(remote, "/Settings", "/Settings?handler=ClearPassword", ("current", "brand new pass"));
        Assert.That(_passwords.IsSet, Is.False);
        Assert.That((await remote.GetAsync("/Bookmarks")).StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
    }

    [Test]
    public async Task TheSettingsPage_IsInTheMenu_AndSaysWhatTheStateIs()
    {
        var local = Browser();
        var html = await local.GetStringAsync("/Settings");
        Assert.That(html, Does.Contain("<h1>Settings</h1>").And.Contain("href=\"/Settings\""));
        Assert.That(html, Does.Contain("Set a password").And.Contain(">off<"));

        _passwords.Set(Secret);
        html = await local.GetStringAsync("/Settings");
        Assert.That(html, Does.Contain("Change the password").And.Contain("password set"));
        Assert.That(html, Does.Not.Contain("name=\"current\""), "this computer is not asked for the current one");
    }

    // ---- What another computer cannot do ----------------------------------------

    [Test]
    public async Task LocalFileBookmarks_AreOffForAnotherComputer_WebOnesWork()
    {
        var bookmarks = _factory.Services.GetRequiredService<BookmarkRepository>();
        bookmarks.SaveBookmark(0, "A web page", "https://example.com/docs", null);
        bookmarks.SaveBookmark(0, "A local file", @"C:\definitely\not\here.txt", null);
        var local = Browser();
        var remote = await LoggedInRemote();

        var localHtml = await local.GetStringAsync("/Bookmarks");
        Assert.That(localHtml, Does.Contain("handler=Open"), "this computer opens it");

        var remoteHtml = await remote.GetStringAsync("/Bookmarks");
        Assert.That(remoteHtml, Does.Contain("A web page").And.Contain("handler=Go"), "web links keep working");
        var fileRow = Regex.Match(remoteHtml, "(?s)<span class=\"link off\".*?A local file.*?</span>\\s*</span>").Value;
        Assert.That(fileRow, Is.Not.Empty, "shown, but off");
        Assert.That(fileRow, Does.Contain("this computer only"));
        Assert.That(remoteHtml, Does.Not.Match("hx-post=\"[^\"]*handler=Open"), "no way to launch anything from there");
    }

    [Test]
    public async Task OpeningALocalTarget_IsRefusedForAnotherComputer_EvenByHand()
    {
        var bookmarks = _factory.Services.GetRequiredService<BookmarkRepository>();
        bookmarks.SaveBookmark(0, "Never started", @"C:\definitely\not\here.txt", null);
        var id = bookmarks.GetBoard().Ungrouped.Single(b => b.Label == "Never started").Id;
        var remote = await LoggedInRemote();
        var token = await Token(remote, "/Bookmarks");

        var request = new HttpRequestMessage(HttpMethod.Post, $"/Bookmarks?handler=Open&id={id}");
        request.Headers.Add("RequestVerificationToken", token);
        request.Headers.Add("HX-Request", "true");
        var response = await remote.SendAsync(request);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NoContent));
        Assert.That(response.Headers.GetValues("HX-Trigger").Single(), Does.Contain("only be opened from the computer where Dynamo runs"));

        // From this computer the same call is attempted (the file is missing, so nothing starts).
        var local = Browser();
        var localRequest = new HttpRequestMessage(HttpMethod.Post, $"/Bookmarks?handler=Open&id={id}");
        localRequest.Headers.Add("RequestVerificationToken", await Token(local, "/Bookmarks"));
        localRequest.Headers.Add("HX-Request", "true");
        var localResponse = await local.SendAsync(localRequest);
        Assert.That(localResponse.Headers.GetValues("HX-Trigger").Single(), Does.Contain("Not found"));
    }

    // ---- The parts ------------------------------------------------------------

    [Test]
    public void ThePasswordHash_IsSalted_AndOnlyTheRightPasswordMatches()
    {
        Assert.That(_passwords.Verify(Secret), Is.False, "no password set");
        _passwords.Set(Secret);
        var first = _settings.Get("access.password")!;
        _passwords.Set(Secret);
        var second = _settings.Get("access.password")!;

        Assert.That(first, Is.Not.EqualTo(second), "a new salt each time");
        Assert.That(first.Split('$'), Has.Length.EqualTo(4));
        Assert.That(_passwords.Verify(Secret), Is.True);
        Assert.That(_passwords.Verify(Secret + " "), Is.False);
        Assert.That(_passwords.Verify(""), Is.False);
        Assert.That(_passwords.Verify(null), Is.False);

        Assert.That(PasswordService.Problem(null), Is.Not.Null);
        Assert.That(PasswordService.Problem("1234567"), Is.Not.Null);
        Assert.That(PasswordService.Problem("12345678"), Is.Null);
        Assert.That(PasswordService.Problem(new string('x', 201)), Is.Not.Null);
    }

    [Test]
    public void TheThrottle_LocksAfterFiveFailures_AndLetsGoAfterAMinute()
    {
        var now = new DateTime(2026, 11, 16, 9, 0, 0, DateTimeKind.Utc);
        var throttle = new LoginThrottle(() => now);

        for (var i = 0; i < LoginThrottle.MaxFailures - 1; i++)
            throttle.Failed("a");
        Assert.That(throttle.IsBlocked("a", out _), Is.False, "four are tolerated");
        throttle.Failed("a");
        Assert.That(throttle.IsBlocked("a", out var wait), Is.True);
        Assert.That(wait, Is.EqualTo(LoginThrottle.Lockout));
        Assert.That(throttle.IsBlocked("b", out _), Is.False, "per computer");

        now += TimeSpan.FromSeconds(61);
        Assert.That(throttle.IsBlocked("a", out _), Is.False);
        throttle.Failed("a");
        Assert.That(throttle.IsBlocked("a", out _), Is.False, "the count starts again");

        throttle.Succeeded("a");
        for (var i = 0; i < LoginThrottle.MaxFailures - 1; i++)
            throttle.Failed("a");
        Assert.That(throttle.IsBlocked("a", out _), Is.False, "a success clears the count");
    }
}
