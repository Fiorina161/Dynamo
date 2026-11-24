using System.Net;
using System.Text.RegularExpressions;
using Dynamo.Core;
using Dynamo.Modules.Settings;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Dynamo.Tests;

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

    [SetUp]
    public void SetUp()
    {
        _previousHome = Environment.GetEnvironmentVariable("DYNAMO_HOME");
        _home = Path.Combine(Path.GetTempPath(), "dynamo-update-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("DYNAMO_HOME", _home);
        var db = new Db(DynamoHome.Resolve());
        db.Migrate([new SettingsModule()]);
        _github = new FakeGitHub();
    }

    [TearDown]
    public void TearDown()
    {
        Environment.SetEnvironmentVariable("DYNAMO_HOME", _previousHome);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_home, true); } catch { /* best effort */ }
    }

    /// <summary>How many times the release API was asked (an available update also reads the changelog).</summary>
    private int ApiRequests() => _github.Requests.Count(r => r.RequestUri!.AbsoluteUri == UpdateChecker.ApiUrl);

    private UpdateChecker Checker(string current = "1.0.0") =>
        new(_github, () => new DateTimeOffset(2026, 11, 20, 12, 0, 0, TimeSpan.Zero), Version.Parse(current));

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
        Assert.That(UpdateChecker.Trusted("https://github.com/Fiorina161/Dynamo-fake"), Is.Null, "a look-alike repository name");
        Assert.That(UpdateChecker.Trusted("https://github.com/Fiorina161/Dynamo"), Is.Not.Null, "the repository itself");
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

    // ---- What's new: the changes since the running version ---------------------------

    private const string Changelog = """
        # Changelog

        ## 2.0.0 - 2026-11-23

        - Remove the Queries page
        - Import queries from another workspace

        ## 1.5.0 - 2026-11-20

        - Add three highlight boxes
        - Pin rows

        ## 1.4.0 - 2026-11-19

        - Something older

        ## 9.9.9 - 2099-01-01

        - A version that is not published (newer than the latest release)

        - A line that belongs to no entry
        """;

    [Test]
    public void TheChangelog_GivesTheEntriesAfterTheRunningVersion_UpToTheLatest_NewestFirst()
    {
        var entries = UpdateChecker.ParseChangelog(Changelog, new Version(1, 4, 0), new Version(2, 0, 0));

        Assert.That(entries.Select(e => e.Version.ToString()), Is.EqualTo(new[] { "2.0.0", "1.5.0" }),
            "not the running version itself, not older ones, not a version newer than the latest");
        Assert.That(entries[0].Date, Is.EqualTo("2026-11-23"));
        Assert.That(entries[0].Items, Is.EqualTo(new[] { "Remove the Queries page", "Import queries from another workspace" }));
        Assert.That(entries[1].Items, Has.Count.EqualTo(2));
        Assert.That(UpdateChecker.ParseChangelog(Changelog, new Version(2, 0, 0), new Version(2, 0, 0)), Is.Empty, "nothing newer");
        Assert.That(UpdateChecker.ParseChangelog("garbage\n- not an entry\n## not a version\n- nor this", new Version(1, 0), new Version(9, 0)), Is.Empty);
    }

    [Test]
    public void TheChangelog_IsReadDefensively()
    {
        var long400 = new string('x', 900);
        var text = $"## 1.1.0 - 2026-01-01\n- a\u0007b\u001bc\n- {long400}\n" +
                   string.Concat(Enumerable.Range(0, 100).Select(i => $"- item {i}\n")) +
                   "## 1.0.5 - " + new string('d', 100) + "\n- dated oddly\n";

        var entries = UpdateChecker.ParseChangelog(text, new Version(1, 0), new Version(2, 0));

        Assert.That(entries[0].Items[0], Is.EqualTo("abc"), "control characters are dropped");
        Assert.That(entries[0].Items[1].Length, Is.LessThanOrEqualTo(401), "a line is cut at a sane length");
        Assert.That(entries[0].Items, Has.Count.LessThanOrEqualTo(60), "and an entry keeps a bounded number of lines");
        Assert.That(entries.Single(e => e.Version == new Version(1, 0, 5)).Date, Is.Null, "an absurd date is not kept");
        var many = string.Concat(Enumerable.Range(1, 200).Select(i => $"## 1.{i}.0 - 2026-01-01\n- x\n"));
        Assert.That(UpdateChecker.ParseChangelog(many, new Version(1, 0), new Version(9, 0)), Has.Count.LessThanOrEqualTo(40), "and so is the number of entries");
    }

    [Test]
    public async Task ANewerRelease_ComesWithEverythingThatChangedSinceTheRunningVersion()
    {
        _github.Respond = r => r.RequestUri!.AbsoluteUri == UpdateChecker.ChangelogUrl
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Changelog) }
            : Json(Release("v2.0.0", name: "Dynamo 2.0"));

        var status = await Checker("1.4.0").CheckAsync();

        Assert.That(status.Error, Is.Null);
        var changes = status.Latest!.Changes!;
        Assert.That(changes.Select(c => c.Version.ToString()), Is.EqualTo(new[] { "2.0.0", "1.5.0" }), "the skipped version's changes too");
        Assert.That(changes.SelectMany(c => c.Items), Does.Contain("Pin rows").And.Contain("Remove the Queries page"));
        Assert.That(_github.Requests.Select(r => r.RequestUri!.AbsoluteUri),
            Is.EqualTo(new[] { UpdateChecker.ApiUrl, UpdateChecker.ChangelogUrl }), "two fixed addresses, nothing else is asked");
    }

    [Test]
    public async Task WhenTheChangelogCannotBeRead_TheReleasesOwnNotesAreUsed()
    {
        var body = "## Dynamo 2.0.0\\n\\n- First change\\n- Second change\\n\\n### Install or update\\nDownload the zip - not a change";
        _github.Respond = r => r.RequestUri!.AbsoluteUri == UpdateChecker.ChangelogUrl
            ? new HttpResponseMessage(HttpStatusCode.InternalServerError)
            : Json($$"""{"tag_name":"v2.0.0","name":"Dynamo 2.0","body":"{{body}}","published_at":"2026-11-23T10:00:00Z","html_url":"https://github.com/Fiorina161/Dynamo/releases/tag/v2.0.0","assets":[]}""");

        var status = await Checker("1.0.0").CheckAsync();

        Assert.That(status.Error, Is.Null, "a changelog that cannot be read is not an error");
        var changes = status.Latest!.Changes!;
        Assert.That(changes, Has.Count.EqualTo(1));
        Assert.That(changes[0].Items, Is.EqualTo(new[] { "First change", "Second change" }), "the install instructions are not a change");
        Assert.That(changes[0].Date, Is.EqualTo("2026-11-23"));

        _github.Respond = r => r.RequestUri!.AbsoluteUri == UpdateChecker.ChangelogUrl
            ? throw new HttpRequestException("offline")
            : Json(Release("v2.0.0"));
        Assert.That((await Checker("1.0.0").CheckAsync()).Error, Is.Null, "nor is a network failure on that file");
    }

    [Test]
    public async Task WhenThereIsNothingNewer_TheChangelogIsNotEvenAsked()
    {
        _github.Respond = _ => Json(Release("v1.0.0"));

        var status = await Checker("1.0.0").CheckAsync();

        Assert.That(status.Latest!.Changes, Is.Null.Or.Empty);
        Assert.That(_github.Requests.Select(r => r.RequestUri!.AbsoluteUri), Is.EqualTo(new[] { UpdateChecker.ApiUrl }));
    }

    [Test]
    public async Task SettingsShowsWhatChanged_AsPlainText()
    {
        var (factory, client, _) = await Site("1.4.0");
        using var _ = factory; using var __ = client;
        var evil = Changelog.Replace("- Pin rows", "- <script>alert(1)</script> <a href=\"https://evil.example\">x</a>");
        _github.Respond = r => r.RequestUri!.AbsoluteUri == UpdateChecker.ChangelogUrl
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(evil) }
            : Json(Release("v2.0.0", name: "Dynamo 2.0"));

        var html = await PostSettings(client, "CheckUpdates");

        Assert.That(html, Does.Contain("new since 1.4.0"));
        Assert.That(html, Does.Match(@"<b>2\.0\.0</b>\s*(·|&#xB7;)\s*2026-11-23").And.Contain("<b>1.5.0</b>"));
        Assert.That(html, Does.Contain("<li>Remove the Queries page</li>").And.Contain("<li>Import queries from another workspace</li>"));
        var block = Regex.Match(html, "class=\"whats-new\".*?</div>", RegexOptions.Singleline).Value;
        Assert.That(block, Does.Not.Contain("<b>1.4.0</b>"), "the running version is not listed among the changes");
        Assert.That(block, Does.Contain("<b>2.0.0</b>"));
        Assert.That(html, Does.Not.Contain("<script>alert(1)</script>").And.Not.Contain("href=\"https://evil.example\""),
            "text from the changelog is never markup");
        Assert.That(html, Does.Contain("&lt;script&gt;"));
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
            s.AddSingleton(sp => new UpdateChecker(_github, null, Version.Parse(running)));
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
        // The sentence itself links to the release page, so the user can jump straight there.
        Assert.That(answer, Does.Match("A new version is available:\\s*<a href=\"https://github.com/Fiorina161/Dynamo/releases/tag/v1.4.0\"[^>]*target=\"_blank\"[^>]*rel=\"noopener\"[^>]*><b>Dynamo 1.4</b></a>"));
        Assert.That(answer, Does.Contain("href=\"https://github.com/Fiorina161/Dynamo/releases/download/v1.4.0/Dynamo-x.zip\""));
        Assert.That(answer, Does.Contain("Release notes").And.Contain("Installer.cmd"));
        Assert.That(answer, Does.Contain("Installed version: <b>1.0.0</b>"));

        var menu = await client.GetStringAsync("/Workplaces");
        Assert.That(menu, Does.Contain("class=\"update-note\" href=\"/Settings#updates\">Version 1.4.0 available"));
        Assert.That(checker.Available, Is.True);
    }

    [Test]
    public async Task TheUpdatesSection_AlwaysLinksToTheRepositoryAndItsReleases()
    {
        var (factory, client, _) = await Site("1.0.0");
        using var _ = factory; using var __ = client;

        // Before anything was checked, and with nothing newer: the links are there either way.
        var before = await client.GetStringAsync("/Settings");
        Assert.That(before, Does.Contain("href=\"https://github.com/Fiorina161/Dynamo\"").And.Contain("github.com/Fiorina161/Dynamo</a>"));
        Assert.That(before, Does.Contain("href=\"https://github.com/Fiorina161/Dynamo/releases\"").And.Contain(">All releases</a>"));

        _github.Respond = _ => throw new HttpRequestException("offline");
        var failed = await PostSettings(client, "CheckUpdates");
        Assert.That(failed, Does.Contain("Could not reach GitHub").And.Contain("href=\"https://github.com/Fiorina161/Dynamo\""),
            "a failed check still leaves the way to the project");
        Assert.That(UpdateChecker.Trusted(UpdateChecker.RepositoryUrl), Is.Not.Null);
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
        Assert.That(await client.GetStringAsync("/Workplaces"), Does.Not.Contain("class=\"update-note\""));
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
    public async Task TheMenuNotice_ShowsForWhoeverSitsAtThisComputer()
    {
        var (factory, _, checker) = await Site("1.0.0");
        using var _ = factory;
        _github.Respond = _ => Json(Release("v1.4.0"));
        await checker.CheckAsync();

        using var local = factory.CreateClient();
        Assert.That(await local.GetStringAsync("/Workplaces"), Does.Contain("update-note"));
    }
}
