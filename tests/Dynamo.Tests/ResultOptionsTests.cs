using System.Text.RegularExpressions;
using Dynamo.Modules.Settings;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Dynamo.Tests;

/// <summary>The Settings option that says how many highlight boxes show (0 to 3).</summary>
public class ResultOptionsTests
{
    private string _home = "";
    private string? _previousHome;
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;

    [SetUp]
    public void SetUp()
    {
        _previousHome = Environment.GetEnvironmentVariable("DYNAMO_HOME");
        _home = Path.Combine(Path.GetTempPath(), "dynamo-results-" + Guid.NewGuid().ToString("N"));
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

    private async Task<string> Save(string boxes)
    {
        var html = await _client.GetStringAsync("/Settings");
        var token = Regex.Match(html, "__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value;
        var response = await _client.PostAsync("/Settings?handler=ResultOptions", new FormUrlEncodedContent(
        [
            KeyValuePair.Create("highlightBoxes", boxes),
            KeyValuePair.Create("__RequestVerificationToken", token),
        ]));
        return await response.Content.ReadAsStringAsync();
    }

    /// <summary>How many highlight boxes a workspace page (with one panel) shows.</summary>
    private async Task<int> BoxesOnAWorkspacePage()
    {
        var repo = _factory.Services.GetRequiredService<Dynamo.Modules.Workplaces.WorkplaceRepository>();
        var id = repo.Create("Boxes " + Guid.NewGuid().ToString("N")[..6]);
        repo.AddNew(id, "Q", "select 1 from dual", "");
        var page = await _client.GetStringAsync($"/Workplaces/{id}");
        return Regex.Matches(page, "data-highlight-scope=").Count;
    }

    [Test]
    public async Task OneBox_IsTheDefault()
    {
        Assert.That(_factory.Services.GetRequiredService<ResultOptions>().HighlightBoxes, Is.EqualTo(1));
        Assert.That(await BoxesOnAWorkspacePage(), Is.EqualTo(1));
        Assert.That(await _client.GetStringAsync("/Settings"),
            Does.Match("<option value=\"1\" selected").And.Contain("Highlight boxes above the results"));
    }

    [TestCase("0", 0)]
    [TestCase("1", 1)]
    [TestCase("2", 2)]
    [TestCase("3", 3)]
    public async Task TheNumberChosen_IsTheNumberRendered(string chosen, int expected)
    {
        var answer = await Save(chosen);

        Assert.That(answer, Does.Contain("highlight box"));
        Assert.That(await BoxesOnAWorkspacePage(), Is.EqualTo(expected));
        Assert.That(await _client.GetStringAsync("/Settings"), Does.Match($"<option value=\"{expected}\" selected"));
    }

    [Test]
    public async Task OutOfRangeAndMissingValues_AreKeptInsideTheLimits()
    {
        await Save("7");
        Assert.That(_factory.Services.GetRequiredService<ResultOptions>().HighlightBoxes, Is.EqualTo(3));
        await Save("-2");
        Assert.That(_factory.Services.GetRequiredService<ResultOptions>().HighlightBoxes, Is.EqualTo(0));
    }

    [Test]
    public async Task TheChoice_IsKeptInTheDatabase()
    {
        await Save("2");

        var fresh = new ResultOptions(_factory.Services.GetRequiredService<SettingsRepository>());

        Assert.That(fresh.HighlightBoxes, Is.EqualTo(2));
    }

    private async Task<string> SaveRows(string rows)
    {
        var html = await _client.GetStringAsync("/Settings");
        var token = Regex.Match(html, "__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value;
        var response = await _client.PostAsync("/Settings?handler=ResultOptions", new FormUrlEncodedContent(
        [
            KeyValuePair.Create("defaultRows", rows),
            KeyValuePair.Create("__RequestVerificationToken", token),
        ]));
        return await response.Content.ReadAsStringAsync();
    }

    [Test]
    public async Task TheDefaultNumberOfRows_Is10_AndIsInSettings()
    {
        Assert.That(_factory.Services.GetRequiredService<ResultOptions>().DefaultRows, Is.EqualTo(10), "what used to be hard-coded");

        var page = await _client.GetStringAsync("/Settings");

        Assert.That(page, Does.Contain("Default number of rows displayed"));
        Assert.That(page, Does.Match("name=\"defaultRows\"[^>]*value=\"10\"").Or.Match("value=\"10\"[^>]*name=\"defaultRows\""));
        Assert.That(page, Does.Contain("bar at the bottom of a panel"), "says that resizing a panel gives it its own number");
    }

    [Test]
    public async Task TheDefaultNumberOfRows_CanBeChanged_AndIsKept()
    {
        var answer = await SaveRows("25");

        Assert.That(answer, Does.Contain("shows 25 rows"));
        Assert.That(_factory.Services.GetRequiredService<ResultOptions>().DefaultRows, Is.EqualTo(25));
        Assert.That(await _client.GetStringAsync("/Settings"),
            Does.Match("name=\"defaultRows\"[^>]*value=\"25\"").Or.Match("value=\"25\"[^>]*name=\"defaultRows\""));
        Assert.That(new ResultOptions(_factory.Services.GetRequiredService<SettingsRepository>()).DefaultRows, Is.EqualTo(25),
            "kept in the database");

        // Saving only the highlight boxes leaves it alone.
        await Save("2");
        Assert.That(_factory.Services.GetRequiredService<ResultOptions>().DefaultRows, Is.EqualTo(25));
    }

    [TestCase("0", 1)]
    [TestCase("-8", 1)]
    [TestCase("5000", 1000)]
    public async Task TheDefaultNumberOfRows_StaysBetween1And1000(string sent, int expected)
    {
        await SaveRows(sent);

        Assert.That(_factory.Services.GetRequiredService<ResultOptions>().DefaultRows, Is.EqualTo(expected));
    }
}
