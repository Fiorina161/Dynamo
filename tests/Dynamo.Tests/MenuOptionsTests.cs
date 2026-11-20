using System.Net;
using System.Text.RegularExpressions;
using Dynamo.Core;
using Dynamo.Modules.Settings;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Dynamo.Tests;

/// <summary>The Settings option that shows or hides Bookmarks in the menu.</summary>
public class MenuOptionsTests
{
    private string _home = "";
    private string? _previousHome;
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;

    [SetUp]
    public void SetUp()
    {
        _previousHome = Environment.GetEnvironmentVariable("DYNAMO_HOME");
        _home = Path.Combine(Path.GetTempPath(), "dynamo-menu-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("DYNAMO_HOME", _home);
        _factory = new WebApplicationFactory<Program>();
        _client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
    }

    [TearDown]
    public void TearDown()
    {
        _client.Dispose();
        _factory.Dispose();
        Environment.SetEnvironmentVariable("DYNAMO_HOME", _previousHome);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_home, true); } catch { /* best effort */ }
    }

    private async Task<string> SaveOption(bool showBookmarks)
    {
        var html = await _client.GetStringAsync("/Settings");
        var token = Regex.Match(html, "__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value;
        var fields = new List<KeyValuePair<string, string>> { new("__RequestVerificationToken", token) };
        if (showBookmarks)
            fields.Add(new("showBookmarks", "on"));
        var response = await _client.PostAsync("/Settings?handler=MenuOptions", new FormUrlEncodedContent(fields));
        return await response.Content.ReadAsStringAsync();
    }

    private static bool MenuHas(string html, string href) => html.Contains($"<a href=\"{href}\"");

    [Test]
    public async Task Bookmarks_IsShownByDefault_AndTheLandingPageOpensIt()
    {
        var page = await _client.GetStringAsync("/Queries");
        Assert.That(MenuHas(page, "/Bookmarks"), Is.True);
        Assert.That(await _client.GetStringAsync("/Settings"), Does.Match("name=\"showBookmarks\"[^>]*checked"));

        var landing = await _client.GetAsync("/");
        Assert.That(landing.Headers.Location!.OriginalString, Is.EqualTo("/Bookmarks"));
    }

    [Test]
    public async Task HidingBookmarks_RemovesItFromTheMenu_AndTheLandingPageGoesToTheNextSection()
    {
        var answer = await SaveOption(showBookmarks: false);

        Assert.That(answer, Does.Contain("Bookmarks is hidden from the menu"));
        var page = await _client.GetStringAsync("/Queries");
        Assert.That(MenuHas(page, "/Bookmarks"), Is.False, "gone from the menu");
        Assert.That(MenuHas(page, "/Queries"), Is.True, "the other sections stay");
        Assert.That(MenuHas(page, "/Settings"), Is.True, "Settings can never be hidden");

        var next = _factory.Services.GetServices<IModule>().OrderBy(m => m.Order).First(m => m.Id != "bookmarks");
        var landing = await _client.GetAsync("/");
        Assert.That(landing.Headers.Location!.OriginalString, Is.EqualTo(next.Href));
        Assert.That((await _client.GetAsync("/Bookmarks")).StatusCode, Is.EqualTo(HttpStatusCode.OK),
            "the page itself still opens, so nothing that points to it breaks");
        Assert.That(await _client.GetStringAsync("/Settings"), Does.Not.Match("name=\"showBookmarks\"[^>]*checked"));
    }

    [Test]
    public async Task ShowingBookmarksAgain_PutsItBack()
    {
        await SaveOption(showBookmarks: false);
        var answer = await SaveOption(showBookmarks: true);

        Assert.That(answer, Does.Contain("Bookmarks is shown in the menu"));
        Assert.That(MenuHas(await _client.GetStringAsync("/Queries"), "/Bookmarks"), Is.True);
        Assert.That((await _client.GetAsync("/")).Headers.Location!.OriginalString, Is.EqualTo("/Bookmarks"));
    }

    [Test]
    public async Task TheChoice_IsKeptInTheDatabase_NotJustInMemory()
    {
        await SaveOption(showBookmarks: false);

        var fresh = new MenuOptions(_factory.Services.GetRequiredService<SettingsRepository>());

        Assert.That(fresh.IsVisible("bookmarks"), Is.False);
        Assert.That(fresh.IsVisible("queries"), Is.True);
        fresh.SetVisible("settings", visible: false);
        Assert.That(fresh.IsVisible("settings"), Is.True, "Settings cannot be hidden");
    }
}
