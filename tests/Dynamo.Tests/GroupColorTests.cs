using Microsoft.Extensions.DependencyInjection;
using System.Text.RegularExpressions;
using Dynamo.Core;
using Dynamo.Modules.Bookmarks;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Dynamo.Tests;

/// <summary>The optional tint of a bookmark group: repository and page.</summary>
public class GroupColorTests
{
    private string _home = "";
    private string? _previousHome;
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;
    private string _token = "";

    [OneTimeSetUp]
    public void SetUp()
    {
        _previousHome = Environment.GetEnvironmentVariable("DYNAMO_HOME");
        _home = Path.Combine(Path.GetTempPath(), "dynamo-hue-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("DYNAMO_HOME", _home);
        _factory = new WebApplicationFactory<Program>();
        _client = _factory.CreateClient();
        _token = Regex.Match(_client.GetStringAsync("/Bookmarks").Result,
            "RequestVerificationToken\": \"([^\"]+)\"").Groups[1].Value;
    }

    [OneTimeTearDown]
    public void TearDown()
    {
        _client.Dispose();
        _factory.Dispose();
        Environment.SetEnvironmentVariable("DYNAMO_HOME", _previousHome);
        try { Directory.Delete(_home, true); } catch { /* best effort */ }
    }

    private BookmarkRepository Repo() =>
        _factory.Services.GetRequiredService<BookmarkRepository>();

    private async Task<HttpResponseMessage> SaveGroup(long id, string name, string? color)
    {
        var fields = new List<KeyValuePair<string, string>>
        {
            new("id", id.ToString()), new("name", name),
        };
        if (color is not null)
            fields.Add(new("color", color));
        var request = new HttpRequestMessage(HttpMethod.Post, "/Bookmarks?handler=SaveGroup")
        {
            Content = new FormUrlEncodedContent(fields),
        };
        request.Headers.Add("RequestVerificationToken", _token);
        request.Headers.Add("HX-Request", "true");
        return await _client.SendAsync(request);
    }

    [Test]
    public async Task A_group_has_no_tint_until_one_is_picked()
    {
        await SaveGroup(0, "Plain", "");
        var board = Repo().GetBoard();
        Assert.That(board.Groups.Single(g => g.Name == "Plain").Color, Is.Null);
        var html = await _client.GetStringAsync("/Bookmarks");
        Assert.That(html, Does.Not.Contain("tinted"));
    }

    [Test]
    public async Task A_picked_hue_is_saved_shown_and_can_be_removed()
    {
        await SaveGroup(0, "Work", "teal");
        var group = Repo().GetBoard().Groups.Single(g => g.Name == "Work");
        Assert.That(group.Color, Is.EqualTo("teal"));

        var html = await _client.GetStringAsync("/Bookmarks");
        Assert.That(html, Does.Contain("tinted"));
        Assert.That(html, Does.Contain("--hue:" + GroupColors.CssOf("teal")));

        var form = await _client.GetStringAsync($"/Bookmarks?handler=GroupForm&id={group.Id}");
        Assert.That(form, Does.Match("value=\"teal\"[^>]*checked"));

        await SaveGroup(group.Id, "Work", "");
        Assert.That(Repo().GetGroup(group.Id)!.Color, Is.Null);
    }

    [Test]
    public async Task An_unknown_hue_is_ignored()
    {
        await SaveGroup(0, "Odd", "#ff0000");
        Assert.That(Repo().GetBoard().Groups.Single(g => g.Name == "Odd").Color, Is.Null);
    }

    [Test]
    public void There_are_six_hues()
    {
        Assert.That(GroupColors.All, Has.Count.EqualTo(6));
    }
}
