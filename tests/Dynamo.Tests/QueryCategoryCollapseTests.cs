using System.Text.RegularExpressions;
using Dynamo.Modules.Queries;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Dynamo.Tests;

/// <summary>Collapsing a category on the queries list, and remembering it.</summary>
public class QueryCategoryCollapseTests
{
    private string _home = "";
    private string? _previousHome;
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;
    private string _token = "";
    private QueryRepository _repo = null!;

    [OneTimeSetUp]
    public void SetUp()
    {
        _previousHome = Environment.GetEnvironmentVariable("DYNAMO_HOME");
        _home = Path.Combine(Path.GetTempPath(), "dynamo-cat-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("DYNAMO_HOME", _home);
        _factory = new WebApplicationFactory<Program>();
        _client = _factory.CreateClient();
        _token = Regex.Match(_client.GetStringAsync("/Queries").Result,
            "RequestVerificationToken\": \"([^\"]+)\"").Groups[1].Value;
        _repo = _factory.Services.GetRequiredService<QueryRepository>();
        _repo.Save(new SavedQuery { Name = "Q1", Category = "Billing", Sql = "select 1 from dual" });
        _repo.Save(new SavedQuery { Name = "Q2", Category = "", Sql = "select 2 from dual" });
    }

    [OneTimeTearDown]
    public void TearDown()
    {
        _client.Dispose();
        _factory.Dispose();
        Environment.SetEnvironmentVariable("DYNAMO_HOME", _previousHome);
        try { Directory.Delete(_home, true); } catch { /* best effort */ }
    }

    private async Task<string> Toggle(string category)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/Queries?handler=ToggleCategory")
        {
            Content = new FormUrlEncodedContent([KeyValuePair.Create("category", category)]),
        };
        request.Headers.Add("RequestVerificationToken", _token);
        request.Headers.Add("HX-Request", "true");
        var response = await _client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync();
    }

    [Test]
    public async Task ACategory_CollapsesAndExpands_AndIsRemembered()
    {
        Assert.That(await _client.GetStringAsync("/Queries"), Does.Not.Contain("group collapsed"));

        var html = await Toggle("Billing");
        Assert.That(Regex.Matches(html, "group collapsed").Count, Is.EqualTo(1));
        Assert.That(_repo.CollapsedCategories(), Does.Contain("Billing"));
        // A fresh load of the page still has it collapsed.
        Assert.That(await _client.GetStringAsync("/Queries"), Does.Contain("group collapsed"));

        html = await Toggle("Billing");
        Assert.That(html, Does.Not.Contain("group collapsed"));
        Assert.That(_repo.CollapsedCategories(), Is.Empty);
    }

    [Test]
    public async Task TheUncategorizedGroup_CanBeCollapsedToo()
    {
        var html = await Toggle("");
        Assert.That(Regex.Matches(html, "group collapsed").Count, Is.EqualTo(1));
        Assert.That(html, Does.Contain("Uncategorized"));
        await Toggle("");
        Assert.That(_repo.CollapsedCategories(), Is.Empty);
    }
}
