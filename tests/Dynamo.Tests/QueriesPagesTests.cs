using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Dynamo.Modules.Queries;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Dynamo.Tests;

/// <summary>
/// The queries pages end to end, in memory, with a fake runner (no Oracle):
/// what the results table and the CSV download look like, and what the run
/// page remembers.
/// </summary>
public class QueriesPagesTests
{
    private string _home = "";
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;
    private FakeRunner _runner = null!;
    private string _token = "";

    private sealed class FakeRunner : IQueryRunner
    {
        public QueryResult Result = new([], [], false, 0);
        public Exception? Failure;
        public string? LastConnection;
        public int? LastMaxRows;
        public Dictionary<string, string> LastValues = [];

        public Task<QueryResult> RunAsync(
            string connectionName, string sql, IReadOnlyDictionary<string, string> values,
            int maxRows, CancellationToken ct)
        {
            LastConnection = connectionName;
            LastMaxRows = maxRows;
            LastValues = new Dictionary<string, string>(values);
            return Failure is null ? Task.FromResult(Result) : Task.FromException<QueryResult>(Failure);
        }
    }

    [OneTimeSetUp]
    public void SetUp()
    {
        _home = Path.Combine(Path.GetTempPath(), "dynamo-tests-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("DYNAMO_HOME", _home);
        _runner = new FakeRunner();
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            b.ConfigureServices(s => s.AddSingleton<IQueryRunner>(_runner)));
        _client = _factory.CreateClient();
        _token = Regex.Match(_client.GetStringAsync("/Queries").Result,
            "RequestVerificationToken\": \"([^\"]+)\"").Groups[1].Value;
    }

    [OneTimeTearDown]
    public void TearDown()
    {
        _client.Dispose();
        _factory.Dispose();
        Environment.SetEnvironmentVariable("DYNAMO_HOME", null);
        try { Directory.Delete(_home, true); } catch { /* best effort */ }
    }

    private async Task<HttpResponseMessage> Post(string url, params (string, string)[] fields)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new FormUrlEncodedContent(fields.Select(f => KeyValuePair.Create(f.Item1, f.Item2))),
        };
        request.Headers.Add("RequestVerificationToken", _token);
        request.Headers.Add("HX-Request", "true");
        return await _client.SendAsync(request);
    }

    /// <summary>Saves a query and returns its id (the list is by name; ids only grow).</summary>
    private async Task<long> SaveQuery(string name, string sql)
    {
        var response = await Post("/Queries?handler=Save",
            ("id", "0"), ("name", name), ("category", "T"), ("connection", ""), ("sql", sql), ("notes", ""));
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var page = await _client.GetStringAsync("/Queries");
        var links = Regex.Matches(page, "href=\"/Queries/(\\d+)\"");
        return links.Max(m => long.Parse(m.Groups[1].Value));
    }

    private static QueryResult Sample(bool truncated = false) => new(
        [new ResultColumn("ID", true), new ResultColumn("NAME", false)],
        [["1", "alpha"], ["2", null], ["3", "<script>alert(1)</script>"], ["4", new string('x', 200)]],
        truncated, 42);

    [Test]
    public async Task Run_ShowsRows_NullsNumbersAndEncodesHtml()
    {
        var id = await SaveQuery("Show", "SELECT id, name FROM t WHERE id > :min");
        _runner.Failure = null;
        _runner.Result = Sample();

        var html = await (await Post($"/Queries/{id}?handler=Run",
            ("connection", "DEV"), ("maxRows", "50"), ("param.min", "7"))).Content.ReadAsStringAsync();

        Assert.That(html, Does.Contain("4 rows"));
        Assert.That(html, Does.Contain("42 ms"));
        Assert.That(html, Does.Contain("<td class=\"null\">NULL</td>"));
        // Headers are the sort buttons: focusable, numeric columns marked, with a hint.
        Assert.That(html, Does.Match("<th class=\"num\" tabindex=\"0\"\\s+title=\"Click to sort[^\"]*\">ID<button[^>]*></button></th>"));
        Assert.That(html, Does.Match("<th class=\"\" tabindex=\"0\"[^>]*>NAME<button[^>]*></button></th>"));
        // Each column (not the row number) has the arrow that opens its list of values.
        Assert.That(System.Text.RegularExpressions.Regex.Matches(html, "class=\"ghost col-filter\"").Count, Is.EqualTo(2));
        Assert.That(html, Does.Contain("<td class=\"num\" title=\"\">1</td>").Or.Contain("<td class=\"num\">1</td>"));
        Assert.That(html, Does.Not.Contain("<script>alert(1)</script>"));
        Assert.That(html, Does.Contain("&lt;script&gt;"));
        Assert.That(html, Does.Not.Contain("more rows exist"));
        Assert.That(_runner.LastConnection, Is.EqualTo("DEV"));
        Assert.That(_runner.LastMaxRows, Is.EqualTo(50));
        Assert.That(_runner.LastValues["min"], Is.EqualTo("7"));
    }

    [Test]
    public async Task Run_WhenTruncated_SaysSo()
    {
        var id = await SaveQuery("Trunc", "SELECT 1 FROM dual");
        _runner.Failure = null;
        _runner.Result = Sample(truncated: true);

        var html = await (await Post($"/Queries/{id}?handler=Run", ("connection", "DEV")))
            .Content.ReadAsStringAsync();

        Assert.That(html, Does.Contain("more rows exist"));
    }

    [Test]
    public async Task Run_RemembersTheLastValues_OnThePage()
    {
        var id = await SaveQuery("Remember", "SELECT 1 FROM dual WHERE a = :a AND b = :b");
        _runner.Failure = null;
        _runner.Result = Sample();

        await Post($"/Queries/{id}?handler=Run", ("connection", "DEV"), ("param.a", "first"), ("param.b", "second"));
        var page = await _client.GetStringAsync($"/Queries/{id}");

        Assert.That(page, Does.Contain("name=\"param.a\"").And.Contain("value=\"first\""));
        Assert.That(page, Does.Contain("value=\"second\""));
    }

    [Test]
    public async Task Run_ShowsTheDatabaseError_AndKeepsTheLastValues()
    {
        var id = await SaveQuery("Broken", "SELECT 1 FROM dual WHERE a = :a");
        _runner.Result = Sample();
        await Post($"/Queries/{id}?handler=Run", ("connection", "DEV"), ("param.a", "good"));
        _runner.Failure = new QueryException("Choose a connection.");

        var html = await (await Post($"/Queries/{id}?handler=Run", ("connection", ""), ("param.a", "bad")))
            .Content.ReadAsStringAsync();
        var page = await _client.GetStringAsync($"/Queries/{id}");

        _runner.Failure = null;
        Assert.That(html, Does.Contain("Choose a connection."));
        Assert.That(page, Does.Contain("value=\"good\""));
    }

    [Test]
    public async Task Csv_DownloadsEveryRow_AsAnAttachment()
    {
        var id = await SaveQuery("My export", "SELECT 1 FROM dual");
        _runner.Failure = null;
        _runner.Result = Sample();

        var response = await Post($"/Queries/{id}?handler=Csv", ("connection", "DEV"));
        var bytes = await response.Content.ReadAsByteArrayAsync();

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(response.Content.Headers.ContentType!.MediaType, Is.EqualTo("text/csv"));
        Assert.That(response.Content.Headers.ContentDisposition!.FileNameStar
            ?? response.Content.Headers.ContentDisposition.FileName, Does.Contain("My export"));
        Assert.That(bytes.Take(3), Is.EqualTo(Encoding.UTF8.GetPreamble()));
        Assert.That(Encoding.UTF8.GetString(bytes), Does.Contain("ID,NAME\r\n1,alpha\r\n2,\r\n"));
        Assert.That(_runner.LastMaxRows, Is.EqualTo(QueryRunner.ExportLimit));
    }

    [Test]
    public async Task Csv_Failure_IsAPlainTextMessage()
    {
        var id = await SaveQuery("Export fails", "SELECT 1 FROM dual");
        _runner.Failure = new QueryException("Choose a connection.");

        var response = await Post($"/Queries/{id}?handler=Csv", ("connection", ""));
        var text = await response.Content.ReadAsStringAsync();
        _runner.Failure = null;

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.UnprocessableEntity));
        Assert.That(text, Does.Contain("Choose a connection."));
    }

    [Test]
    public async Task RunPage_OffersAutoRefresh_WithoutSendingTheIntervalToTheServer()
    {
        var id = await SaveQuery("Auto", "SELECT 1 FROM dual");

        var page = await _client.GetStringAsync($"/Queries/{id}");

        Assert.That(page, Does.Contain("id=\"run\""));
        Assert.That(page, Does.Contain("data-auto-refresh=\"#run\""));
        Assert.That(page, Does.Contain("data-refresh-toggle"));
        var interval = Regex.Match(page, "<input[^>]*data-refresh-seconds[^>]*>").Value;
        Assert.That(interval, Does.Not.Contain(" name="), "the interval is not a form field");
        Assert.That(interval, Does.Contain("min=\"1\"").And.Contain("max=\"3600\""));
    }

    [Test]
    public async Task Results_SayWhenTheyWereFetched()
    {
        var id = await SaveQuery("Stamped", "SELECT 1 FROM dual");
        _runner.Failure = null;
        _runner.Result = Sample();

        var html = await (await Post($"/Queries/{id}?handler=Run", ("connection", "DEV")))
            .Content.ReadAsStringAsync();

        Assert.That(html, Does.Match("class=\"ran-at\"[^>]*>\\d{2}:\\d{2}:\\d{2}<"));
    }

    [Test]
    public async Task Save_RefusesAnythingButASingleSelect()
    {
        var response = await Post("/Queries?handler=Save",
            ("id", "0"), ("name", "Nope"), ("category", ""), ("connection", ""),
            ("sql", "DELETE FROM t"), ("notes", ""));
        var html = await response.Content.ReadAsStringAsync();

        Assert.That(response.Headers.GetValues("HX-Retarget").Single(), Is.EqualTo("#dlg"));
        Assert.That(html, Does.Contain("Only SELECT queries are allowed"));
    }

    [Test]
    public async Task PostWithoutTheAntiforgeryToken_IsRefused()
    {
        var response = await _client.PostAsync("/Queries?handler=Delete&id=1",
            new FormUrlEncodedContent([]));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
    }
}
