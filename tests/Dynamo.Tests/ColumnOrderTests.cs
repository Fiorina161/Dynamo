using System.Net;
using System.Text.RegularExpressions;
using Dynamo.Modules.OracleConnections;
using Dynamo.Modules.Queries;
using Dynamo.Modules.Workplaces;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Dynamo.Tests;

/// <summary>The order of the columns of a panel's result, as the user dragged it: kept with the panel, applied to every result and to the CSV.</summary>
public class ColumnOrderTests
{
    private string _home = "";
    private string? _previousHome;
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;
    private string _token = "";
    private FakeRunner _runner = null!;

    private sealed class FakeRunner : IQueryRunner
    {
        public QueryResult Result = Abc();

        public Task<QueryResult> RunAsync(
            string connectionName, string sql, IReadOnlyDictionary<string, string> values, int maxRows, CancellationToken ct) =>
            Task.FromResult(Result);
    }

    private static QueryResult Abc() =>
        new([new ResultColumn("A", false), new ResultColumn("B", true), new ResultColumn("C", false)],
            [["a1", "1", "c1"], ["a2", "2", null]], false, 4);

    [OneTimeSetUp]
    public void SetUp()
    {
        _previousHome = Environment.GetEnvironmentVariable("DYNAMO_HOME");
        _home = Path.Combine(Path.GetTempPath(), "dynamo-colorder-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("DYNAMO_HOME", _home);
        _runner = new FakeRunner();
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            b.ConfigureServices(s => s.AddSingleton<IQueryRunner>(_runner)));
        _client = _factory.CreateClient();
        _token = Regex.Match(_client.GetStringAsync("/Workplaces").Result,
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
    public void Reset() => _runner.Result = Abc();

    private WorkplaceRepository Workplaces => _factory.Services.GetRequiredService<WorkplaceRepository>();

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

    private (long Workplace, long Panel) NewPanel(string name = "Cols")
    {
        var w = Workplaces.Create(name + " " + Guid.NewGuid().ToString("N")[..6]);
        var panel = Workplaces.AddNew(w, "Q", "select 1 from dual", "X");
        return (w, panel);
    }

    private async Task<string> Run(long w, long panel) =>
        await (await Post($"/Workplaces/{w}?handler=RunPanel&panel={panel}")).Content.ReadAsStringAsync();

    private static string[] Headers(string html) =>
        Regex.Matches(html, "<th class=\"[^\"]*\" tabindex=\"0\" data-qi=\"\\d+\"[^>]*>([^<]+)<").Select(m => m.Groups[1].Value).ToArray();

    // ---- The rule ---------------------------------------------------------------------------------------

    [Test]
    public void TheColumnsAreRearranged_WithTheirCells_AndRememberWhereTheyCameFrom()
    {
        var result = ColumnOrder.Apply(Abc(), ["C", "A", "B"]);

        Assert.That(result.Columns.Select(c => c.Name), Is.EqualTo(new[] { "C", "A", "B" }));
        Assert.That(result.Columns.Select(c => c.Source), Is.EqualTo(new[] { 2, 0, 1 }));
        Assert.That(result.Columns.Select(c => c.Numeric), Is.EqualTo(new[] { false, false, true }), "a column keeps its kind");
        Assert.That(result.Rows[0], Is.EqualTo(new[] { "c1", "a1", "1" }));
        Assert.That(result.Rows[1], Is.EqualTo(new string?[] { null, "a2", "2" }), "a NULL goes with its column");
        Assert.That((result.Truncated, result.ElapsedMs), Is.EqualTo((false, 4L)));
    }

    [Test]
    public void UnknownNamesAreIgnored_AndNewColumnsComeLast_InTheQuerysOwnOrder()
    {
        var result = ColumnOrder.Apply(Abc(), ["GONE", "C", "ALSO GONE"]);

        Assert.That(result.Columns.Select(c => c.Name), Is.EqualTo(new[] { "C", "A", "B" }), "C first, then the rest as the query has them");
    }

    [Test]
    public void NamesMatchIgnoringCase_AndDuplicateNamesTakeTheirTurns()
    {
        Assert.That(ColumnOrder.Apply(Abc(), ["c", "b"]).Columns.Select(c => c.Name), Is.EqualTo(new[] { "C", "B", "A" }));

        var twins = new QueryResult([new ResultColumn("ID", true), new ResultColumn("NAME", false), new ResultColumn("ID", true)],
            [["1", "x", "2"]], false, 1);
        var moved = ColumnOrder.Apply(twins, ["ID", "ID", "NAME"]);
        Assert.That(moved.Rows[0], Is.EqualTo(new[] { "1", "2", "x" }), "the first ID, the second ID, then NAME");
    }

    [Test]
    public void NothingToDo_ReturnsTheSameResult()
    {
        var result = Abc();

        Assert.That(ColumnOrder.Apply(result, null), Is.SameAs(result));
        Assert.That(ColumnOrder.Apply(result, []), Is.SameAs(result));
        Assert.That(ColumnOrder.Apply(result, ["A", "B", "C"]), Is.SameAs(result), "already in that order");
        var single = new QueryResult([new ResultColumn("A", false)], [["x"]], false, 1);
        Assert.That(ColumnOrder.Apply(single, ["A"]), Is.SameAs(single));
    }

    // ---- In the pages --------------------------------------------------------------------------------------

    [Test]
    public async Task WithoutASavedOrder_TheColumnsComeAsTheQueryGivesThem()
    {
        var (w, panel) = NewPanel();

        var html = await Run(w, panel);

        Assert.That(Headers(html), Is.EqualTo(new[] { "A", "B", "C" }));
        Assert.That(Regex.Matches(html, "data-qi=\"(\\d)\"").Select(m => m.Groups[1].Value), Is.EqualTo(new[] { "0", "1", "2" }));
        Assert.That(html, Does.Not.Contain("data-column-order=\"1\""));
        Assert.That(html, Does.Contain("Drag to move the column"));
    }

    [Test]
    public async Task ASavedOrder_IsAppliedToEveryRun_WithTheCellsFollowingTheirColumns()
    {
        var (w, panel) = NewPanel();

        var saved = await Post($"/Workplaces/{w}?handler=ColumnOrder&panel={panel}", ("order", "[\"C\",\"A\",\"B\"]"));
        Assert.That(saved.StatusCode, Is.EqualTo(HttpStatusCode.NoContent));
        Assert.That(Workplaces.GetPanel(panel)!.ColumnOrder, Is.EqualTo(new[] { "C", "A", "B" }));

        var html = await Run(w, panel);
        Assert.That(Headers(html), Is.EqualTo(new[] { "C", "A", "B" }));
        Assert.That(Regex.Matches(html, "data-qi=\"(\\d)\"").Select(m => m.Groups[1].Value), Is.EqualTo(new[] { "2", "0", "1" }),
            "each header says where it was in the query (the pins follow the query's first column)");
        Assert.That(html, Does.Contain("data-column-order=\"1\""));
        Assert.That(html.IndexOf(">c1<", StringComparison.Ordinal), Is.LessThan(html.IndexOf(">a1<", StringComparison.Ordinal)));
        Assert.That(html.IndexOf(">a1<", StringComparison.Ordinal), Is.LessThan(html.IndexOf(">1<", StringComparison.Ordinal)));

        // New columns of the query still show: after the ones in the saved order.
        _runner.Result = new QueryResult([new ResultColumn("A", false), new ResultColumn("B", true), new ResultColumn("C", false), new ResultColumn("D", false)],
            [["a", "1", "c", "d"]], false, 1);
        Assert.That(Headers(await Run(w, panel)), Is.EqualTo(new[] { "C", "A", "B", "D" }));
    }

    [Test]
    public async Task TheCsvExport_FollowsTheSavedOrder()
    {
        var (w, panel) = NewPanel();
        await Post($"/Workplaces/{w}?handler=ColumnOrder&panel={panel}", ("order", "[\"B\",\"C\",\"A\"]"));

        var csv = await (await Post($"/Workplaces/{w}?handler=CsvPanel&panel={panel}")).Content.ReadAsStringAsync();

        var lines = csv.TrimStart('\uFEFF').Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.That(lines[0], Is.EqualTo("B,C,A"));
        Assert.That(lines[1], Is.EqualTo("1,c1,a1"));
    }

    [Test]
    public async Task TheSavedOrder_IsResetByAnEmptyList()
    {
        var (w, panel) = NewPanel();
        await Post($"/Workplaces/{w}?handler=ColumnOrder&panel={panel}", ("order", "[\"C\",\"A\"]"));

        foreach (var empty in new[] { "[]", "", " " })
        {
            await Post($"/Workplaces/{w}?handler=ColumnOrder&panel={panel}", ("order", "[\"C\",\"A\"]"));
            var reset = await Post($"/Workplaces/{w}?handler=ColumnOrder&panel={panel}", ("order", empty));
            Assert.That(reset.StatusCode, Is.EqualTo(HttpStatusCode.NoContent));
            Assert.That(Workplaces.GetPanel(panel)!.ColumnOrder, Is.Empty, $"'{empty}'");
        }
        Assert.That(Headers(await Run(w, panel)), Is.EqualTo(new[] { "A", "B", "C" }));
    }

    [Test]
    public async Task TheSaveRequest_RefusesWhatIsNotAListOfNames_AndAPanelOfAnotherWorkspace()
    {
        var (w, panel) = NewPanel();
        var (other, _) = NewPanel("Other");

        foreach (var bad in new[] { "not json", "{\"a\":1}", "[1,2]", "[\"\"]", "[null]", $"[\"{new string('x', ColumnOrder.MaxNameLength + 1)}\"]",
                     "[" + string.Join(",", Enumerable.Range(0, ColumnOrder.MaxNames + 1).Select(i => $"\"c{i}\"")) + "]" })
        {
            var answer = await Post($"/Workplaces/{w}?handler=ColumnOrder&panel={panel}", ("order", bad));
            Assert.That(answer.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest), bad.Length > 60 ? bad[..60] : bad);
        }
        Assert.That(Workplaces.GetPanel(panel)!.ColumnOrder, Is.Empty, "nothing was saved");

        Assert.That((await Post($"/Workplaces/{other}?handler=ColumnOrder&panel={panel}", ("order", "[\"A\"]"))).StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
        Assert.That((await Post($"/Workplaces/{w}?handler=ColumnOrder&panel=999999", ("order", "[\"A\"]"))).StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
    }

    [Test]
    public void ADuplicate_AndAnImport_KeepTheColumnOrder()
    {
        var (w, panel) = NewPanel("Copy order");
        Workplaces.SetColumnOrder(panel, ["B", "A"]);

        var copy = Workplaces.Duplicate(w, "Copy order dup " + Guid.NewGuid().ToString("N")[..6]);
        Assert.That(Workplaces.Get(copy)!.Panels.Single().ColumnOrder, Is.EqualTo(new[] { "B", "A" }));

        var target = Workplaces.Create("Copy order target " + Guid.NewGuid().ToString("N")[..6]);
        Workplaces.ImportPanels(target, w, [panel]);
        Assert.That(Workplaces.Get(target)!.Panels.Single().ColumnOrder, Is.EqualTo(new[] { "B", "A" }));
    }

    [Test]
    public async Task ThePageKnowsWhereToSaveTheOrder()
    {
        var (w, _) = NewPanel();

        Assert.That(await _client.GetStringAsync($"/Workplaces/{w}"), Does.Contain($"data-column-order-url=\"/Workplaces/{w}?handler=ColumnOrder\""));
    }

    [Test]
    public async Task TheKillColumnOfTheSessionsPanel_StillWorks_WhenItsColumnsAreRearranged()
    {
        _factory.Services.GetRequiredService<OracleConnectionRepository>()
            .Save(new OracleConnectionInfo { Name = "ColOrder DBA", DataSource = "h:1/s", Username = "sys", Password = "p", Dba = true });
        var w = Workplaces.Create("Sessions order " + Guid.NewGuid().ToString("N")[..6]);
        Workplaces.AddTemplates(w, "ColOrder DBA", ["sessions"]);
        var panel = Workplaces.Get(w)!.Panels.Single().Id;
        _runner.Result = new QueryResult(
            [new ResultColumn("INST_ID", true), new ResultColumn("SID", true), new ResultColumn("SERIAL", true), new ResultColumn("USERNAME", false)],
            [["1", "42", "7", "SCOTT"]], false, 3);
        await Post($"/Workplaces/{w}?handler=ColumnOrder&panel={panel}", ("order", "[\"USERNAME\",\"SERIAL\",\"SID\",\"INST_ID\"]"));

        var html = await Run(w, panel);

        Assert.That(Headers(html), Is.EqualTo(new[] { "USERNAME", "SERIAL", "SID", "INST_ID" }));
        Assert.That(html, Does.Contain("inst=1&amp;sid=42&amp;serial=7"), "the session is found by column name, wherever the columns are");
        Assert.That(html.IndexOf("kill-col", StringComparison.Ordinal), Is.LessThan(html.IndexOf("data-qi=", StringComparison.Ordinal)),
            "and the Kill column stays first");
    }
}
