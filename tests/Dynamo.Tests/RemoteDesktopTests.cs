using System.Net;
using System.Security.Principal;
using System.Text.RegularExpressions;
using Dapper;
using Dynamo.Core;
using Dynamo.Modules.RemoteDesktops;
using Dynamo.Modules.Settings;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Dynamo.Tests;

/// <summary>
/// Remote desktops: the host rule, the stored account (encrypted, re-entered when it changed), Connect and Forget
/// (a fake launcher stands in for Windows and the Remote Desktop client; only from the computer Dynamo runs on), and
/// the real Windows Credential Manager round trip for the current user.
/// </summary>
public class RemoteDesktopTests
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

    private sealed class FakeLauncher : IRemoteDesktopLauncher
    {
        public List<RemoteDesktop> Connected = [];
        public List<RemoteDesktop> Forgotten = [];
        public string? ConnectProblem;
        public string? ForgetProblem;

        public string? Connect(RemoteDesktop desktop)
        {
            Connected.Add(desktop);
            return ConnectProblem;
        }

        public string? Forget(RemoteDesktop desktop)
        {
            Forgotten.Add(desktop);
            return ForgetProblem;
        }
    }

    private const string Secret = "correct horse battery";

    private string _home = "";
    private string? _previousHome;
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;
    private string _token = "";
    private FakeLauncher _launcher = null!;

    [OneTimeSetUp]
    public void SetUp()
    {
        _previousHome = Environment.GetEnvironmentVariable("DYNAMO_HOME");
        _home = Path.Combine(Path.GetTempPath(), "dynamo-rdp-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("DYNAMO_HOME", _home);
        _launcher = new FakeLauncher();
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b => b.ConfigureServices(s =>
        {
            s.AddTransient<IStartupFilter, FakeClientAddress>();
            s.AddSingleton(new HttpsOptions(0));
            s.AddSingleton(sp => new PasswordService(sp.GetRequiredService<SettingsRepository>(), 1000));
            s.AddSingleton<IRemoteDesktopLauncher>(_launcher);
        }));
        _client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        _token = Regex.Match(_client.GetStringAsync("/RemoteDesktops").Result,
            "RequestVerificationToken\": \"([^\"]+)\"").Groups[1].Value;
    }

    [OneTimeTearDown]
    public void TearDown()
    {
        _client.Dispose();
        _factory.Dispose();
        Environment.SetEnvironmentVariable("DYNAMO_HOME", _previousHome);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_home, true); } catch { /* best effort */ }
    }

    [SetUp]
    public void Reset()
    {
        _launcher.Connected.Clear();
        _launcher.Forgotten.Clear();
        _launcher.ConnectProblem = null;
        _launcher.ForgetProblem = null;
    }

    private RemoteDesktopRepository Repo => _factory.Services.GetRequiredService<RemoteDesktopRepository>();

    private async Task<HttpResponseMessage> Post(HttpClient client, string token, string url, params (string, string)[] fields)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new FormUrlEncodedContent(fields.Select(f => KeyValuePair.Create(f.Item1, f.Item2))),
        };
        request.Headers.Add("RequestVerificationToken", token);
        request.Headers.Add("HX-Request", "true");
        return await client.SendAsync(request);
    }

    private Task<HttpResponseMessage> Post(string url, params (string, string)[] fields) => Post(_client, _token, url, fields);

    private static string Header(HttpResponseMessage r, string name) => r.Headers.TryGetValues(name, out var v) ? v.First() : "";

    private long Add(string name, string host = "server01", string user = @"CORP\alain", string password = "pw-1")
    {
        var id = Repo.Save(new RemoteDesktop { Name = name, Host = host, Username = user, Password = password });
        return id;
    }

    /// <summary>A browser on another computer, logged in with the Dynamo password.</summary>
    private async Task<(HttpClient Client, string Token)> RemoteBrowser()
    {
        _factory.Services.GetRequiredService<PasswordService>().Set(Secret);
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add("X-Test-Client", "192.168.50.77");
        var login = await client.GetStringAsync("/Login");
        var formToken = Regex.Match(login, "__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value;
        var answer = await client.PostAsync("/Login", new FormUrlEncodedContent(
            [KeyValuePair.Create("password", Secret), KeyValuePair.Create("returnUrl", "/RemoteDesktops"), KeyValuePair.Create("__RequestVerificationToken", formToken)]));
        Assert.That((int)answer.StatusCode, Is.EqualTo(302), "logged in");
        var page = await client.GetStringAsync("/RemoteDesktops");
        return (client, Regex.Match(page, "RequestVerificationToken\": \"([^\"]+)\"").Groups[1].Value);
    }

    // ---- The host ------------------------------------------------------------------------------------------

    [TestCase("server01", "server01")]
    [TestCase("  build-01.corp.example  ", "build-01.corp.example")]
    [TestCase("10.0.0.5", "10.0.0.5")]
    [TestCase("10.0.0.5:3390", "10.0.0.5:3390")]
    [TestCase("host_name", "host_name")]
    public void AHostIsAComputerNameOrAnAddress_WithAnOptionalPort(string typed, string? expected)
    {
        var (host, error) = RemoteDesktopHost.Parse(typed);

        if (expected is null)
        {
            Assert.That(host, Is.Null);
            Assert.That(error, Is.Not.Null);
        }
        else
        {
            Assert.That(error, Is.Null);
            Assert.That(host, Is.EqualTo(expected));
        }
    }

    [TestCase("")]
    [TestCase("   ")]
    [TestCase("rdp://server")]
    [TestCase("server 01")]
    [TestCase("server01 /admin")]
    [TestCase("server01\" /f")]
    [TestCase("server01&calc")]
    [TestCase("server01|x")]
    [TestCase("a:b")]
    [TestCase("host:0")]
    [TestCase("host:70000")]
    [TestCase("host:123456")]
    [TestCase("-leading")]
    [TestCase("trailing-")]
    [TestCase(@"\\server")]
    [TestCase("server\nname")]
    [TestCase("[::1]")]
    public void ADashHostWithAnythingElseInIt_IsRefused_BecauseItGoesOnACommandLine(string typed)
    {
        var (host, error) = RemoteDesktopHost.Parse(typed);

        Assert.That(host, Is.Null);
        Assert.That(error, Is.Not.Null.And.Not.Empty);
    }

    [Test]
    public void TheSavedCredentialIsNamedTermsrvHost_WithoutThePort()
    {
        Assert.That(RemoteDesktopHost.CredentialTarget("server01"), Is.EqualTo("TERMSRV/server01"));
        Assert.That(RemoteDesktopHost.CredentialTarget("10.0.0.5:3390"), Is.EqualTo("TERMSRV/10.0.0.5"));
    }

    // ---- The dialog and the stored account ---------------------------------------------------------------

    [Test]
    public async Task TheDialog_AsksForNameHostUserAndPassword_WithHintsThatLookLikeHints()
    {
        var html = await _client.GetStringAsync("/RemoteDesktops?handler=Form");

        Assert.That(html, Does.Contain("name=\"name\"").And.Contain("name=\"host\"").And.Contain("name=\"username\"").And.Contain("name=\"password\""));
        Assert.That(html, Does.Contain("placeholder=\"e.g. server01 or 10.0.0.5:3390\"").And.Contain("placeholder=\"e.g. Build server\""));
        Assert.That(html, Does.Contain("type=\"password\"").And.Not.Contain("handler=Forget"), "nothing to forget for a new one");
        Assert.That(await _client.GetStringAsync("/RemoteDesktops"), Does.Contain("href=\"/RemoteDesktops\"").And.Contain("Remote desktops"));
    }

    [Test]
    public async Task ASavedRemoteDesktop_HasItsPasswordEncrypted_AndNeverShownAgain()
    {
        var answer = await Post("/RemoteDesktops?handler=Save", ("id", "0"), ("name", "Build server"), ("host", "build-01"),
            ("username", @"CORP\alain"), ("password", "s3cret-rdp"));
        var html = await answer.Content.ReadAsStringAsync();

        var saved = Repo.GetAll().Single(d => d.Name == "Build server");
        Assert.That((saved.Host, saved.Username, saved.Password), Is.EqualTo(("build-01", @"CORP\alain", "s3cret-rdp")));
        Assert.That(html, Does.Contain("Build server").And.Not.Contain("s3cret-rdp"));
        Assert.That(Header(answer, "HX-Trigger"), Is.EqualTo("closeDialog"));
        using (var conn = _factory.Services.GetRequiredService<Db>().Open())
            Assert.That(conn.ExecuteScalar<string>("SELECT password FROM remote_desktop WHERE id = @id", new { id = saved.Id }),
                Does.StartWith(Secrets.Prefix).And.Not.Contain("s3cret-rdp"));
        Assert.That(await _client.GetStringAsync($"/RemoteDesktops?handler=Form&id={saved.Id}"), Does.Not.Contain("s3cret-rdp"));
    }

    [Test]
    public async Task ReEnteringTheCredentials_ReplacesThem_AndABlankPasswordKeepsTheOldOne()
    {
        var id = Add("Re-enter");

        // The password changed on the remote computer: type the new one (and the account may change too).
        await Post("/RemoteDesktops?handler=Save", ("id", id.ToString()), ("name", "Re-enter"), ("host", "server01"), ("username", @"CORP\alain"), ("password", "new-pw"));
        Assert.That(Repo.Get(id)!.Password, Is.EqualTo("new-pw"));

        // Editing something else with the password left blank keeps what is stored.
        await Post("/RemoteDesktops?handler=Save", ("id", id.ToString()), ("name", "Re-enter"), ("host", "server02:3390"), ("username", @"CORP\other"), ("password", ""));
        var after = Repo.Get(id)!;
        Assert.That((after.Host, after.Username, after.Password), Is.EqualTo(("server02:3390", @"CORP\other", "new-pw")));
        Assert.That(await _client.GetStringAsync($"/RemoteDesktops?handler=Form&id={id}"), Does.Contain("placeholder=\"unchanged\"").And.Contain("handler=Forget"));
    }

    [Test]
    public async Task TheDialog_RefusesWhatCannotBeSaved()
    {
        async Task<string> Try(params (string, string)[] f)
        {
            var answer = await Post("/RemoteDesktops?handler=Save", f);
            Assert.That(Header(answer, "HX-Retarget"), Is.EqualTo("#dlg"));
            return await answer.Content.ReadAsStringAsync();
        }

        Assert.That(await Try(("id", "0"), ("name", ""), ("host", "h"), ("username", "u"), ("password", "p")), Does.Contain("name is required"));
        Assert.That(await Try(("id", "0"), ("name", "N"), ("host", "rdp://h"), ("username", "u"), ("password", "p")), Does.Contain("computer name or an IPv4"));
        Assert.That(await Try(("id", "0"), ("name", "N"), ("host", "h"), ("username", ""), ("password", "p")), Does.Contain("user is required"));
        Assert.That(await Try(("id", "0"), ("name", "N"), ("host", "h"), ("username", "u"), ("password", "")), Does.Contain("password is required"));
        Assert.That(Repo.GetAll().Select(d => d.Name), Does.Not.Contain("N"));

        Add("Taken name");
        Assert.That(await Try(("id", "0"), ("name", "taken NAME"), ("host", "h"), ("username", "u"), ("password", "p")), Does.Contain("already exists"));
    }

    [Test]
    public async Task APasswordThatCannotBeRead_MustBeEnteredAgain_AndCannotBeUsedToConnect()
    {
        long id;
        using (var conn = _factory.Services.GetRequiredService<Db>().Open())
            id = conn.ExecuteScalar<long>("""
                INSERT INTO remote_desktop (name, host, username, password) VALUES ('Lost pw', 'h1', 'u', 'dpapi1:AAAAAAAA');
                SELECT last_insert_rowid();
                """);

        Assert.That(Repo.Get(id)!.PasswordUnreadable, Is.True);
        var list = await _client.GetStringAsync("/RemoteDesktops");
        Assert.That(list, Does.Contain("password cannot be read here"));
        var connect = await Post($"/RemoteDesktops?handler=Connect&id={id}");
        Assert.That(Header(connect, "HX-Trigger"), Does.Contain("no usable password"));
        Assert.That(_launcher.Connected, Is.Empty);

        var refused = await Post("/RemoteDesktops?handler=Save", ("id", id.ToString()), ("name", "Lost pw"), ("host", "h1"), ("username", "u"), ("password", ""));
        Assert.That(await refused.Content.ReadAsStringAsync(), Does.Contain("password is required"));
        await Post("/RemoteDesktops?handler=Save", ("id", id.ToString()), ("name", "Lost pw"), ("host", "h1"), ("username", "u"), ("password", "again"));
        Assert.That(Repo.Get(id)!.Password, Is.EqualTo("again"));
    }

    [Test]
    public async Task ADeletedRemoteDesktop_IsGone()
    {
        var id = Add("To delete");

        await Post($"/RemoteDesktops?handler=Delete&id={id}");

        Assert.That(Repo.Get(id), Is.Null);
    }

    // ---- Connect and Forget: only from this computer ------------------------------------------------------

    [Test]
    public async Task Connect_HandsTheAccountToTheLauncher_AndSaysWhatToDoIfWindowsStillAsks()
    {
        var id = Add("Connect me", "10.0.0.8:3390", @"CORP\alain", "the-password");

        var answer = await Post($"/RemoteDesktops?handler=Connect&id={id}");

        var sent = _launcher.Connected.Single();
        Assert.That((sent.Name, sent.Host, sent.Username, sent.Password), Is.EqualTo(("Connect me", "10.0.0.8:3390", @"CORP\alain", "the-password")));
        var toast = Header(answer, "HX-Trigger");
        Assert.That(toast, Does.Contain("Opening").And.Contain("Connect me").And.Contain("use Edit to enter the new one"));
        Assert.That(toast, Does.Not.Contain("the-password"), "the password is never in an answer");
    }

    [Test]
    public async Task AFailedConnect_ShowsTheReason()
    {
        var id = Add("Fails");
        _launcher.ConnectProblem = "No user is signed in on this computer.";

        var answer = await Post($"/RemoteDesktops?handler=Connect&id={id}");

        Assert.That(Header(answer, "HX-Trigger"), Does.Contain("Fails").And.Contain("No user is signed in"));
        Assert.That(Header(answer, "HX-Trigger"), Does.Not.Contain("Opening"));
    }

    [Test]
    public async Task Connect_AndForget_AreRefusedToAnotherComputer_ThoughItCanStillEdit()
    {
        var id = Add("From afar");
        var (remote, token) = await RemoteBrowser();

        var page = await remote.GetStringAsync("/RemoteDesktops");
        Assert.That(page, Does.Contain("Connect works only from the computer where Dynamo runs"));
        Assert.That(Regex.IsMatch(page, "<button[^>]*disabled[^>]*>Connect</button>"), Is.True, "the button is off");

        var connect = await Post(remote, token, $"/RemoteDesktops?handler=Connect&id={id}");
        Assert.That(Header(connect, "HX-Trigger"), Does.Contain("only from the computer where Dynamo runs"));
        var forget = await Post(remote, token, $"/RemoteDesktops?handler=Forget&id={id}");
        Assert.That(Header(forget, "HX-Trigger"), Does.Contain("only from the computer where Dynamo runs"));
        Assert.That(_launcher.Connected, Is.Empty);
        Assert.That(_launcher.Forgotten, Is.Empty);

        var dialog = await remote.GetStringAsync($"/RemoteDesktops?handler=Form&id={id}");
        Assert.That(dialog, Does.Not.Contain("handler=Forget"), "no Forget button from another computer");
        var saved = await Post(remote, token, "/RemoteDesktops?handler=Save", ("id", id.ToString()), ("name", "From afar"), ("host", "server09"), ("username", "u"), ("password", ""));
        Assert.That(Repo.Get(id)!.Host, Is.EqualTo("server09"), "editing works from anywhere");
        Assert.That(saved.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        _factory.Services.GetRequiredService<PasswordService>().Clear();
    }

    [Test]
    public async Task ThisComputer_SeesAnEnabledConnectButton()
    {
        Add("Local one");

        var page = await _client.GetStringAsync("/RemoteDesktops");

        Assert.That(page, Does.Not.Contain("Connect works only from the computer"));
        Assert.That(Regex.IsMatch(page, "<button[^>]*disabled[^>]*>Connect</button>"), Is.False);
    }

    [Test]
    public async Task Forget_AsksTheLauncherToRemoveTheSavedWindowsCredential()
    {
        var id = Add("Forget me", "server07");

        var answer = await Post($"/RemoteDesktops?handler=Forget&id={id}");

        Assert.That(_launcher.Forgotten.Single().Host, Is.EqualTo("server07"));
        Assert.That(Header(answer, "HX-Trigger"), Does.Contain("was removed").And.Contain("saved again at the next Connect"));
        _launcher.ForgetProblem = "Access is denied.";
        Assert.That(Header(await Post($"/RemoteDesktops?handler=Forget&id={id}"), "HX-Trigger"), Does.Contain("Access is denied."));
    }

    [Test]
    public async Task UnknownRemoteDesktops_AreNotFound()
    {
        Assert.That((await Post("/RemoteDesktops?handler=Connect&id=999999")).StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
        Assert.That((await Post("/RemoteDesktops?handler=Forget&id=999999")).StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
        Assert.That((await _client.GetAsync("/RemoteDesktops?handler=Form&id=999999")).StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
    }

    // ---- The real Windows Credential Manager, for the user running the tests --------------------------------

    [Test]
    public void TheWindowsCredentialStore_SavesReplacesReadsAndRemoves_ForTheCurrentUser()
    {
        var target = "TERMSRV/dynamo-test-" + Guid.NewGuid().ToString("N")[..10];
        try
        {
            Assert.That(WindowsCredentials.Write(IntPtr.Zero, target, @"CORP\tester", "first-pw ✓ with \"quotes\""), Is.Null);
            Assert.That(WindowsCredentials.Read(IntPtr.Zero, target), Is.EqualTo((@"CORP\tester", "first-pw ✓ with \"quotes\"")), "what was saved, exactly (no command line involved)");

            Assert.That(WindowsCredentials.Write(IntPtr.Zero, target, @"CORP\tester", "second-pw"), Is.Null);
            Assert.That(WindowsCredentials.Read(IntPtr.Zero, target)!.Value.Password, Is.EqualTo("second-pw"), "saving again replaces it");

            Assert.That(WindowsCredentials.Delete(IntPtr.Zero, target, out var found), Is.Null);
            Assert.That(found, Is.True);
            Assert.That(WindowsCredentials.Read(IntPtr.Zero, target), Is.Null);
            Assert.That(WindowsCredentials.Delete(IntPtr.Zero, target, out var again), Is.Null, "removing what is not there is not an error");
            Assert.That(again, Is.False);
        }
        finally
        {
            WindowsCredentials.Delete(IntPtr.Zero, target, out _);
        }
    }

    [Test]
    public void TheCredentialIsWrittenWhileImpersonating_TheWayTheServiceDoesIt()
    {
        // The service writes with the signed-in user's token (WTSQueryUserToken). The same code path, with this
        // process's own token standing in for it: duplicate, impersonate, CredWrite.
        var target = "TERMSRV/dynamo-test-imp-" + Guid.NewGuid().ToString("N")[..8];
        var token = WindowsIdentity.GetCurrent().AccessToken.DangerousGetHandle();
        try
        {
            Assert.That(WindowsCredentials.Write(token, target, "imp-user", "imp-pw"), Is.Null);
            Assert.That(WindowsCredentials.Read(IntPtr.Zero, target), Is.EqualTo(("imp-user", "imp-pw")), "it is in this user's store");
            Assert.That(WindowsCredentials.Delete(token, target, out var found), Is.Null);
            Assert.That(found, Is.True);
            Assert.That(WindowsCredentials.Read(IntPtr.Zero, target), Is.Null);
        }
        finally
        {
            WindowsCredentials.Delete(IntPtr.Zero, target, out _);
        }
    }

    [Test]
    public void TheRealLauncher_ForgetsWhatWasSaved_ForThisUser()
    {
        var host = "dynamo-test-" + Guid.NewGuid().ToString("N")[..8];
        var target = RemoteDesktopHost.CredentialTarget(host + ":3390");
        try
        {
            Assert.That(WindowsCredentials.Write(IntPtr.Zero, target, "u", "p"), Is.Null);

            var problem = new RemoteDesktopLauncher().Forget(new RemoteDesktop { Name = "x", Host = host + ":3390", Username = "u" });

            Assert.That(problem, Is.Null);
            Assert.That(WindowsCredentials.Read(IntPtr.Zero, target), Is.Null);
            Assert.That(new RemoteDesktopLauncher().Forget(new RemoteDesktop { Name = "x", Host = host }), Is.Null, "again, with nothing saved: fine");
        }
        finally
        {
            WindowsCredentials.Delete(IntPtr.Zero, target, out _);
        }
    }

    [Test]
    public async Task TheMenuListsRemoteDesktopsAfterSharedFolders_AndBeforeSettings()
    {
        var html = await _client.GetStringAsync("/RemoteDesktops");

        var shares = html.IndexOf("href=\"/Shares\"", StringComparison.Ordinal);
        var rdp = html.IndexOf("href=\"/RemoteDesktops\"", StringComparison.Ordinal);
        var settings = html.IndexOf("href=\"/Settings\"", StringComparison.Ordinal);
        Assert.That(shares, Is.GreaterThan(0));
        Assert.That(rdp, Is.GreaterThan(shares));
        Assert.That(settings, Is.GreaterThan(rdp));
    }
}
