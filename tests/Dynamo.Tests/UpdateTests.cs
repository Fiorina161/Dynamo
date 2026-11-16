using System.Net;
using System.Text.RegularExpressions;
using Dynamo.Core;
using Dynamo.Modules.Settings;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Dynamo.Tests;

/// <summary>No test factory starts the real daily look at GitHub.</summary>
[SetUpFixture]
public class NoRealUpdateChecks
{
    [OneTimeSetUp]
    public void Switch() => Environment.SetEnvironmentVariable("DYNAMO_UPDATE_CHECK", "off");
}

/// <summary>
/// The "a new version is available" notice, with a fake GitHub (no network):
/// what it makes of the answers, which links it trusts, and what the pages show.
/// </summary>
public class UpdateTests
{
    private sealed class FakeGitHub : HttpMessageHandler, IHttpClientFactory
    {
        public Func<HttpRequestMessage, HttpResponseMessage> Respond = _ => new HttpResponseMessage(HttpStatusCode.NotFound);
        public List<HttpRequestMessage> Requests = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request);
            return Task.FromResult(Respond(request));
        }

        public HttpClient CreateClient(string name) => new(this, disposeHandler: false);
    }

    private static HttpResponseMessage Json(string json, HttpStatusCode code = HttpStatusCode.OK) =>
        new(code) { Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json") };

    private static string Release(
        string tag, string? page = null, string? download = null, string name = "", string asset = "Dynamo-x.zip") =>
        $$"""
        {"tag_name":"{{tag}}","name":"{{name}}","html_url":"{{page ?? $"https://github.com/Fiorina161/Dynamo/releases/tag/{tag}"}}",
         "published_at":"2026-11-20T10:00:00Z",
         "assets":[{"name":"notes.txt","browser_download_url":"https://github.com/Fiorina161/Dynamo/releases/download/{{tag}}/notes.txt"},
                   {"name":"{{asset}}","browser_download_url":"{{download ?? $"https://github.com/Fiorina161/Dynamo/releases/download/{tag}/{asset}"}}"}]}
        """;

    private string _home = "";
    private string? _previousHome;
    private FakeGitHub _github = null!;
    private SettingsRepository _settings = null!;

    [SetUp]
    public void SetUp()
    {
        _previousHome = Environment.GetEnvironmentVariable("DYNAMO_HOME");
        _home = Path.Combine(Path.GetTempPath(), "dynamo-update-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("DYNAMO_HOME", _home);
        var db = new Db(DynamoHome.Resolve());
        db.Migrate([new SettingsModule()]);
        _settings = new SettingsRepository(db);
        _github = new FakeGitHub();
    }

    [TearDown]
    public void TearDown()
    {
        Environment.SetEnvironmentVariable("DYNAMO_HOME", _previousHome);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_home, true); } catch { /* best effort */ }
    }

    private UpdateChecker Checker(string current = "1.0.0") =>
        new(_github, _settings, () => new DateTimeOffset(2026, 11, 20, 12, 0, 0, TimeSpan.Zero), Version.Parse(current));

    // ---- What it makes of GitHub's answers -----------------------------------------

    [Test]
    public async Task ANewerRelease_IsAvailable_WithItsZipAndItsPage()
    {
        _github.Respond = _ => Json(Release("v1.2.0", name: "Dynamo 1.2"));
        var checker = Checker();

        var status = await checker.CheckAsync();

        Assert.That(status.Error, Is.Null);
        Assert.That(checker.Available, Is.True);
        Assert.That(status.Latest!.Version, Is.EqualTo(new Version(1, 2, 0)));
        Assert.That(status.Latest.Name, Is.EqualTo("Dynamo 1.2"));
        Assert.That(status.Latest.DownloadUrl, Is.EqualTo("https://github.com/Fiorina161/Dynamo/releases/download/v1.2.0/Dynamo-x.zip"), "the zip, not the notes file");
        Assert.That(status.Latest.PageUrl, Is.EqualTo("https://github.com/Fiorina161/Dynamo/releases/tag/v1.2.0"));
        Assert.That(status.Latest.PublishedAt, Is.EqualTo(new DateTimeOffset(2026, 11, 20, 10, 0, 0, TimeSpan.Zero)));
        Assert.That(status.CheckedAt, Is.Not.Null);
    }

    [TestCase("v1.0.0", "1.0.0", false)]
    [TestCase("v0.9.9", "1.0.0", false)]
    [TestCase("v1.0.1", "1.0.0", true)]
    [TestCase("1.10.0", "1.9.0", true)]        // 1.10 is newer than 1.9, not older
    [TestCase("v2.0", "1.9.9", true)]          // two-part tags work
    [TestCase("v2", "1.9.9", true)]
    public async Task OnlyAStrictlyNewerVersion_IsAnUpdate(string tag, string running, bool expected)
    {
        _github.Respond = _ => Json(Release(tag));
        var checker = Checker(running);

        await checker.CheckAsync();

        Assert.That(checker.Available, Is.EqualTo(expected), $"{tag} vs {running}");
    }

    [Test]
    public async Task ItAsksGitHubsPublicApi_WithoutAnyCredentials()
    {
        _github.Respond = _ => Json(Release("v1.0.0"));
        await Checker().CheckAsync();

        var request = _github.Requests.Single();
        Assert.That(request.RequestUri!.AbsoluteUri, Is.EqualTo("https://api.github.com/repos/Fiorina161/Dynamo/releases/latest"));
        Assert.That(request.Method, Is.EqualTo(HttpMethod.Get));
        Assert.That(request.Headers.Authorization, Is.Null, "the repository is public: no token");
        Assert.That(request.Headers.Contains("Cookie"), Is.False);
    }

    [Test]
    public async Task LinksOutsideTheProjectsGitHubPages_AreNeverShown()
    {
        _github.Respond = _ => Json(Release("v1.5.0", page: "https://evil.example/phish", download: "https://github.com/someone-else/Dynamo/releases/download/v1.5.0/Dynamo.zip"));
        var checker = Checker();

        var latest = (await checker.CheckAsync()).Latest!;

        Assert.That(latest.PageUrl, Is.EqualTo(UpdateChecker.ReleasesPage), "falls back to the project's releases page");
        Assert.That(latest.DownloadUrl, Is.Null);
        Assert.That(UpdateChecker.Trusted("http://github.com/Fiorina161/Dynamo/releases"), Is.Null, "https only");
        Assert.That(UpdateChecker.Trusted("https://github.com.evil.example/Fiorina161/Dynamo/"), Is.Null);
        Assert.That(UpdateChecker.Trusted("https://github.com/Fiorina161/Dynamo-fake/x"), Is.Null);
        Assert.That(UpdateChecker.Trusted("javascript:alert(1)"), Is.Null);
        Assert.That(UpdateChecker.Trusted("https://github.com/fiorina161/dynamo/releases/tag/v1"), Is.Not.Null, "github names are not case sensitive");
    }

    [Test]
    public async Task EveryFailure_BecomesAMessage_NeverAnException()
    {
        var checker = Checker();

        _github.Respond = _ => new HttpResponseMessage(HttpStatusCode.NotFound);
        Assert.That((await checker.CheckAsync()).Error, Does.Contain("No release has been published yet"));
        Assert.That(checker.Available, Is.False);

        _github.Respond = _ => new HttpResponseMessage(HttpStatusCode.Forbidden);
        Assert.That((await checker.CheckAsync()).Error, Does.Contain("rate limit"));

        _github.Respond = _ => new HttpResponseMessage(HttpStatusCode.InternalServerError);
        Assert.That((await checker.CheckAsync()).Error, Does.Contain("500"));

        _github.Respond = _ => throw new HttpRequestException("No such host is known");
        Assert.That((await checker.CheckAsync()).Error, Does.Contain("Could not reach GitHub").And.Contain("No such host"));

        _github.Respond = _ => throw new TaskCanceledException("timed out");
        Assert.That((await checker.CheckAsync()).Error, Does.Contain("did not answer in time"));

        _github.Respond = _ => Json("not json at all");
        Assert.That((await checker.CheckAsync()).Error, Does.Contain("could not be read"));

        _github.Respond = _ => Json("""{"tag_name":"nightly"}""");
        Assert.That((await checker.CheckAsync()).Error, Does.Contain("unexpected version name"));
    }

    [Test]
    public async Task AFailedCheck_KeepsNothingStale()
    {
        var checker = Checker();
        _github.Respond = _ => Json(Release("v3.0.0"));
        await checker.CheckAsync();
        Assert.That(checker.Available, Is.True);

        _github.Respond = _ => throw new HttpRequestException("offline");
        await checker.CheckAsync();

        Assert.That(checker.Available, Is.False, "an unknown answer is not a promise of an update");
        Assert.That(checker.Status.Error, Is.Not.Null);
    }

    [Test]
    public async Task TheAutomaticLook_IsOnByDefault_AndRemembersBeingTurnedOff()
    {
        _github.Respond = _ => Json(Release("v1.1.0"));
        var checker = Checker();
        Assert.That(checker.Enabled, Is.True);

        checker.Enabled = false;
        await checker.CheckIfEnabledAsync();
        Assert.That(_github.Requests, Is.Empty, "off means off: nothing is asked");

        Assert.That(Checker().Enabled, Is.False, "stored in the database");
        await Checker().CheckAsync();                                  // "Check now" still works
        Assert.That(_github.Requests, Has.Count.EqualTo(1));

        checker.Enabled = true;
        await checker.CheckIfEnabledAsync();
        Assert.That(_github.Requests, Has.Count.EqualTo(2));
    }

    [Test]
    public void TheRunningVersion_ComesFromTheProject_AndFormatsWithoutTheCommit()
    {
        Assert.That(AppVersion.Current, Is.GreaterThanOrEqualTo(new Version(1, 0, 0)));
        Assert.That(AppVersion.Format(new Version(1, 2, 3)), Is.EqualTo("1.2.3"));
        Assert.That(AppVersion.Format(new Version(1, 2)), Is.EqualTo("1.2"));
        Assert.That(UpdateChecker.TryParseVersion("v1.2.3", out var v) && v == new Version(1, 2, 3), Is.True);
        Assert.That(UpdateChecker.TryParseVersion("garbage", out _), Is.False);
    }

    // ---- The pages --------------------------------------------------------------------

    private async Task<(WebApplicationFactory<Program> Factory, HttpClient Client, UpdateChecker Checker)> Site(string running)
    {
        var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b => b.ConfigureServices(s =>
        {
            s.AddSingleton<IHttpClientFactory>(_github);
            s.AddSingleton(sp => new UpdateChecker(_github, sp.GetRequiredService<SettingsRepository>(), null, Version.Parse(running)));
        }));
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        await client.GetStringAsync("/Bookmarks");     // starts the host
        return (factory, client, factory.Services.GetRequiredService<UpdateChecker>());
    }

    private static async Task<string> PostSettings(HttpClient client, string handler, params (string, string)[] fields)
    {
        var html = await client.GetStringAsync("/Settings");
        var token = Regex.Match(html, "__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value;
        var response = await client.PostAsync($"/Settings?handler={handler}", new FormUrlEncodedContent(
            fields.Append(("__RequestVerificationToken", token)).Select(f => KeyValuePair.Create(f.Item1, f.Item2))));
        return await response.Content.ReadAsStringAsync();
    }

    [Test]
    public async Task WhenANewerVersionExists_TheMenuAndSettingsSayWhereToDownloadIt()
    {
        var (factory, client, checker) = await Site("1.0.0");
        using var _ = factory; using var __ = client;
        _github.Respond = _ => Json(Release("v1.4.0", name: "Dynamo 1.4"));

        // Nothing is shown before anything was asked.
        var before = await client.GetStringAsync("/Bookmarks");
        Assert.That(before, Does.Not.Contain("update-note\""));

        var answer = await PostSettings(client, "CheckUpdates");
        Assert.That(answer, Does.Contain("A new version is available").And.Contain("Dynamo 1.4"));
        Assert.That(answer, Does.Contain("href=\"https://github.com/Fiorina161/Dynamo/releases/download/v1.4.0/Dynamo-x.zip\""));
        Assert.That(answer, Does.Contain("Release notes").And.Contain("Installer.cmd"));
        Assert.That(answer, Does.Contain("Installed version: <b>1.0.0</b>"));

        var menu = await client.GetStringAsync("/Queries");
        Assert.That(menu, Does.Contain("class=\"update-note\" href=\"/Settings#updates\">Version 1.4.0 available"));
        Assert.That(checker.Available, Is.True);
    }

    [Test]
    public async Task WhenUpToDate_ItSaysSo_AndTheMenuStaysQuiet()
    {
        var (factory, client, _) = await Site("2.0.0");
        using var _ = factory; using var __ = client;
        _github.Respond = _ => Json(Release("v1.9.0"));

        var answer = await PostSettings(client, "CheckUpdates");

        Assert.That(answer, Does.Contain("Dynamo is up to date."));
        Assert.That(answer, Does.Not.Contain("A new version is available"));
        Assert.That(await client.GetStringAsync("/Queries"), Does.Not.Contain("class=\"update-note\""));
    }

    [Test]
    public async Task AFailedCheck_ShowsItsMessage_OnTheSettingsPage()
    {
        var (factory, client, _) = await Site("1.0.0");
        using var _ = factory; using var __ = client;
        _github.Respond = _ => throw new HttpRequestException("offline");

        var answer = await PostSettings(client, "CheckUpdates");

        Assert.That(answer, Does.Contain("Could not reach GitHub"));
        Assert.That(answer, Does.Not.Contain("Dynamo is up to date."), "an unknown answer is not \"up to date\"");
    }

    [Test]
    public async Task TheCheckbox_TurnsTheDailyLookOffAndOn()
    {
        var (factory, client, checker) = await Site("1.0.0");
        using var _ = factory; using var __ = client;
        Assert.That(await client.GetStringAsync("/Settings"), Does.Match("name=\"checkAutomatically\"[^>]*checked"));

        var off = await PostSettings(client, "UpdateOptions");               // unticked: nothing sent
        Assert.That(off, Does.Contain("will not look for new versions by itself"));
        Assert.That(checker.Enabled, Is.False);

        var on = await PostSettings(client, "UpdateOptions", ("checkAutomatically", "on"));
        Assert.That(on, Does.Contain("once a day"));
        Assert.That(checker.Enabled, Is.True);
    }

    [Test]
    public async Task TheMenuNotice_ShowsForWhoeverSitsAtThisComputer()
    {
        var (factory, _, checker) = await Site("1.0.0");
        using var _ = factory;
        _github.Respond = _ => Json(Release("v1.4.0"));
        await checker.CheckAsync();

        using var local = factory.CreateClient();
        Assert.That(await local.GetStringAsync("/Queries"), Does.Contain("update-note"));
    }
}
