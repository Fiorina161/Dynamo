using System.Net;
using System.Text.RegularExpressions;
using Dynamo.Core;
using Dynamo.Modules.OracleConnections;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Dynamo.Tests;

/// <summary>The batch delete of Oracle connections: repository and pages.</summary>
public class OracleBatchDeleteTests
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
        _home = Path.Combine(Path.GetTempPath(), "dynamo-batch-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("DYNAMO_HOME", _home);
        _factory = new WebApplicationFactory<Program>();
        _client = _factory.CreateClient();
        _token = Regex.Match(_client.GetStringAsync("/Oracle").Result,
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

    private async Task<HttpResponseMessage> Post(string url, IEnumerable<KeyValuePair<string, string>> fields)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new FormUrlEncodedContent(fields),
        };
        request.Headers.Add("RequestVerificationToken", _token);
        request.Headers.Add("HX-Request", "true");
        return await _client.SendAsync(request);
    }

    private async Task Create(string name) =>
        await Post("/Oracle?handler=Save",
        [
            KeyValuePair.Create("id", "0"), KeyValuePair.Create("name", name),
            KeyValuePair.Create("dataSource", "h:1521/s"), KeyValuePair.Create("username", "u"),
            KeyValuePair.Create("password", "p"),
        ]);

    /// <summary>Id of each listed connection, by name.</summary>
    private async Task<Dictionary<string, long>> Listed()
    {
        var page = await _client.GetStringAsync("/Oracle");
        return Regex.Matches(page,
                "name=\"ids\" value=\"(\\d+)\"[^>]*aria-label=\"Select ([^\"]+)\"")
            .ToDictionary(m => m.Groups[2].Value, m => long.Parse(m.Groups[1].Value));
    }

    private static IEnumerable<KeyValuePair<string, string>> Ids(params long[] ids) =>
        ids.Select(i => KeyValuePair.Create("ids", i.ToString()));

    [Test]
    public async Task List_HasACheckboxPerRow_ASelectAll_AndADisabledDeleteButton()
    {
        await Create("LIST1");
        await Create("LIST2");

        var page = await _client.GetStringAsync("/Oracle");

        Assert.That(Regex.Matches(page, "data-select-item").Count, Is.GreaterThanOrEqualTo(2));
        Assert.That(page, Does.Contain("data-select-all"));
        Assert.That(page, Does.Match("<button[^>]*data-select-action[^>]*disabled"));
    }

    [Test]
    public async Task ConfirmDelete_ListsExactlyTheSelected_AndDeletesNothing()
    {
        await Create("CONF_A");
        await Create("CONF_B");
        await Create("CONF_KEEP");
        var ids = await Listed();

        var response = await Post("/Oracle?handler=ConfirmDelete", Ids(ids["CONF_A"], ids["CONF_B"]));
        var html = await response.Content.ReadAsStringAsync();

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(html, Does.Contain("Delete 2 connections?"));
        Assert.That(html, Does.Contain("CONF_A").And.Contain("CONF_B"));
        Assert.That(html, Does.Not.Contain("CONF_KEEP"));
        Assert.That((await Listed()).Keys, Is.SupersetOf(new[] { "CONF_A", "CONF_B", "CONF_KEEP" }));
    }

    [Test]
    public async Task ConfirmDelete_OfOne_SaysConnectionInTheSingular()
    {
        await Create("SINGLE");
        var ids = await Listed();

        var html = await (await Post("/Oracle?handler=ConfirmDelete", Ids(ids["SINGLE"])))
            .Content.ReadAsStringAsync();

        Assert.That(html, Does.Contain("Delete 1 connection?"));
    }

    [Test]
    public async Task ConfirmDelete_WithNothingSelected_ToastsInsteadOfOpeningADialog()
    {
        var response = await Post("/Oracle?handler=ConfirmDelete", []);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NoContent));
        Assert.That(response.Headers.GetValues("HX-Trigger").Single(), Does.Contain("Nothing selected."));
    }

    [Test]
    public async Task DeleteMany_RemovesOnlyTheGivenOnes_AndClosesTheDialog()
    {
        await Create("DEL_1");
        await Create("DEL_2");
        await Create("DEL_KEEP");
        var ids = await Listed();

        var response = await Post("/Oracle?handler=DeleteMany", Ids(ids["DEL_1"], ids["DEL_2"]));
        var html = await response.Content.ReadAsStringAsync();
        var after = await Listed();

        Assert.That(response.Headers.GetValues("HX-Trigger").Single(), Is.EqualTo("closeDialog"));
        Assert.That(html, Does.Contain("DEL_KEEP").And.Not.Contain("DEL_1"));
        Assert.That(after.Keys, Does.Not.Contain("DEL_1").And.Not.Contain("DEL_2"));
        Assert.That(after.Keys, Does.Contain("DEL_KEEP"));
    }

    [Test]
    public async Task DeleteMany_IgnoresUnknownIds_AndAnEmptyRequest()
    {
        await Create("STAYS");

        var unknown = await Post("/Oracle?handler=DeleteMany", Ids(987654321));
        var empty = await Post("/Oracle?handler=DeleteMany", []);

        Assert.That(unknown.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(empty.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That((await Listed()).Keys, Does.Contain("STAYS"));
    }

    [Test]
    public async Task DeleteMany_WithoutTheAntiforgeryToken_IsRefused()
    {
        var response = await _client.PostAsync("/Oracle?handler=DeleteMany",
            new FormUrlEncodedContent(Ids(1)));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
    }

    [Test]
    public void Repository_DeletesMoreRowsThanOneSqlSlice_InOneGo()
    {
        var home = Path.Combine(Path.GetTempPath(), "dynamo-batch-repo-" + Guid.NewGuid().ToString("N"));
        var previous = Environment.GetEnvironmentVariable("DYNAMO_HOME");
        Environment.SetEnvironmentVariable("DYNAMO_HOME", home);
        try
        {
            var db = new Db(DynamoHome.Resolve());
            db.Migrate([new OracleConnectionsModule()]);
            var repo = new OracleConnectionRepository(db);
            var ids = Enumerable.Range(1, 1_200)
                .Select(i => repo.Save(new OracleConnectionInfo { Name = $"C{i:D4}", DataSource = "x" }))
                .ToList();
            var keep = repo.Save(new OracleConnectionInfo { Name = "KEEP", DataSource = "x" });

            var deleted = repo.DeleteMany(ids.Append(keep + 1000).Append(ids[0]));

            Assert.That(deleted, Is.EqualTo(1_200), "unknown and repeated ids count for nothing");
            Assert.That(repo.GetAll().Select(c => c.Name), Is.EqualTo(new[] { "KEEP" }));
            Assert.That(repo.GetMany([keep, keep + 5]).Select(c => c.Name), Is.EqualTo(new[] { "KEEP" }));
            Assert.That(repo.DeleteMany([]), Is.EqualTo(0));
        }
        finally
        {
            Environment.SetEnvironmentVariable("DYNAMO_HOME", previous);
            try { Directory.Delete(home, true); } catch { /* best effort */ }
        }
    }
}
