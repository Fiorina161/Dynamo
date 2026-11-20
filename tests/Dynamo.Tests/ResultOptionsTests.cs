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

    /// <summary>How many highlight boxes the Queries page shows for a saved query.</summary>
    private async Task<int> BoxesOnTheQueriesPage()
    {
        var token = Regex.Match(await _client.GetStringAsync("/Queries"), @"RequestVerificationToken""\s*:\s*""([^""]+)""").Groups[1].Value;
        var save = new HttpRequestMessage(HttpMethod.Post, "/Queries?handler=Save")
        {
            Content = new FormUrlEncodedContent(
            [
                KeyValuePair.Create("id", "0"), KeyValuePair.Create("name", "Q" + Guid.NewGuid().ToString("N")[..6]),
                KeyValuePair.Create("category", ""), KeyValuePair.Create("connection", ""),
                KeyValuePair.Create("sql", "select 1 from dual"), KeyValuePair.Create("notes", ""),
            ]),
        };
        save.Headers.Add("RequestVerificationToken", token);
        await _client.SendAsync(save);
        var id = Regex.Matches(await _client.GetStringAsync("/Queries"), @"href=""/Queries/(\d+)""")
            .Max(m => long.Parse(m.Groups[1].Value));
        var page = await _client.GetStringAsync($"/Queries/{id}");
        return Regex.Matches(page, "data-highlight-scope=").Count;
    }

    [Test]
    public async Task OneBox_IsTheDefault()
    {
        Assert.That(_factory.Services.GetRequiredService<ResultOptions>().HighlightBoxes, Is.EqualTo(1));
        Assert.That(await BoxesOnTheQueriesPage(), Is.EqualTo(1));
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
        Assert.That(await BoxesOnTheQueriesPage(), Is.EqualTo(expected));
        Assert.That(await _client.GetStringAsync("/Settings"), Does.Match($"<option value=\"{expected}\" selected"));
        if (expected == 0)
            Assert.That(await _client.GetStringAsync("/Queries"), Does.Not.Contain("highlight-box"));
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
}
