using System.Net;
using System.Text.RegularExpressions;
using Dynamo.Modules.Queries;
using Dynamo.Modules.Workplaces;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Dynamo.Tests;

/// <summary>
/// Workplaces end to end, in memory, with a fake runner (no Oracle): creating and
/// filling a workplace, the bind values the panels share, running a panel, and the
/// settings of a panel.
/// </summary>
public class WorkplacesTests
{
    private string _home = "";
    private string? _previousHome;
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;
    private string _token = "";
    private FakeRunner _runner = null!;
    private WorkplaceRepository _workplaces = null!;
    private QueryRepository _queries = null!;

    private sealed class FakeRunner : IQueryRunner
    {
        public QueryResult Result = new(
            [new ResultColumn("ID", true), new ResultColumn("NAME", false)],
            [["1", "alpha"], ["2", null]], false, 7);
        public Exception? Failure;
        public string? LastConnection;
        public string? LastSql;
        public int? LastMaxRows;
        public Dictionary<string, string> LastValues = [];

        public Task<QueryResult> RunAsync(
            string connectionName, string sql, IReadOnlyDictionary<string, string> values,
            int maxRows, CancellationToken ct)
        {
            LastConnection = connectionName;
            LastSql = sql;
            LastMaxRows = maxRows;
            LastValues = new Dictionary<string, string>(values);
            return Failure is null ? Task.FromResult(Result) : Task.FromException<QueryResult>(Failure);
        }
    }

    [OneTimeSetUp]
    public void SetUp()
    {
        _previousHome = Environment.GetEnvironmentVariable("DYNAMO_HOME");
        _home = Path.Combine(Path.GetTempPath(), "dynamo-workplace-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("DYNAMO_HOME", _home);
        _runner = new FakeRunner();
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            b.ConfigureServices(s => s.AddSingleton<IQueryRunner>(_runner)));
        _client = _factory.CreateClient();
        _token = Regex.Match(_client.GetStringAsync("/Workplaces").Result,
            "RequestVerificationToken\": \"([^\"]+)\"").Groups[1].Value;
        _workplaces = _factory.Services.GetRequiredService<WorkplaceRepository>();
        _queries = _factory.Services.GetRequiredService<QueryRepository>();
    }

    [OneTimeTearDown]
    public void TearDown()
    {
        _client.Dispose();
        _factory.Dispose();
        Environment.SetEnvironmentVariable("DYNAMO_HOME", _previousHome);
        try { Directory.Delete(_home, true); } catch { /* best effort */ }
    }

    [SetUp]
    public void Reset()
    {
        _runner.Failure = null;
        _runner.LastConnection = null;
        _runner.LastValues = [];
    }

    private Task<HttpResponseMessage> Post(string url, params (string, string)[] fields)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new FormUrlEncodedContent(fields.Select(f => KeyValuePair.Create(f.Item1, f.Item2))),
        };
        request.Headers.Add("RequestVerificationToken", _token);
        request.Headers.Add("HX-Request", "true");
        return _client.SendAsync(request);
    }

    private long NewQuery(string name, string sql, string connection = "", string category = "") =>
        _queries.Save(new SavedQuery
        {
            Name = name, Sql = sql, Connection = connection, Category = category,
        });

    private long NewWorkplace(string name) => _workplaces.Create(name);

    private static string Header(HttpResponseMessage r, string name) =>
        r.Headers.TryGetValues(name, out var v) ? v.First() : "";

    [Test]
    public async Task ANewWorkplace_IsCreatedAndOpened_ABadNameIsRefused()
    {
        var created = await Post("/Workplaces?handler=Create", ("name", "Order debug"));
        Assert.That(Header(created, "HX-Redirect"), Does.Match(@"^/Workplaces/\d+$"));
        Assert.That(_workplaces.GetAll().Select(w => w.Name), Does.Contain("Order debug"));

        var duplicate = await Post("/Workplaces?handler=Create", ("name", "order DEBUG"));
        Assert.That(await duplicate.Content.ReadAsStringAsync(), Does.Contain("already exists"));
        Assert.That(Header(duplicate, "HX-Retarget"), Is.EqualTo("#dlg"));

        var empty = await Post("/Workplaces?handler=Create", ("name", "  "));
        Assert.That(await empty.Content.ReadAsStringAsync(), Does.Contain("required"));
    }

    [Test]
    public async Task Queries_AreAddedAsPanels_AndTheSameQueryMayBeAddedTwice()
    {
        var w = NewWorkplace("Panels");
        var q1 = NewQuery("Stuck orders", "select 1 from dual");
        var q2 = NewQuery("Queue depth", "select 2 from dual");

        var added = await Post($"/Workplaces/{w}?handler=Add", ("ids", q1.ToString()), ("ids", q2.ToString()), ("ids", q1.ToString()), ("ids", "99999"));
        Assert.That(Header(added, "HX-Refresh"), Is.EqualTo("true"));

        var workplace = _workplaces.Get(w)!;
        Assert.That(workplace.Panels.Select(p => p.QueryName),
            Is.EqualTo(new[] { "Stuck orders", "Queue depth", "Stuck orders" }));

        var html = await _client.GetStringAsync($"/Workplaces/{w}");
        Assert.That(Regex.Matches(html, "data-panel=\"").Count, Is.EqualTo(3));
    }

    [Test]
    public async Task ThePanelsShareOneInputPerBindName_AndItsValueIsRemembered()
    {
        var w = NewWorkplace("Shared");
        var q1 = NewQuery("A", "select * from t where order_id = :order_id");
        var q2 = NewQuery("B", "select * from u where ORDER_ID = :ORDER_ID and st = :status");
        _workplaces.AddCopies(w, [q1, q2]);

        var html = await _client.GetStringAsync($"/Workplaces/{w}");
        Assert.That(Regex.Matches(html, "name=\"param\\.").Count, Is.EqualTo(2));
        Assert.That(html, Does.Contain("name=\"param.order_id\""));
        Assert.That(html, Does.Contain("name=\"param.status\""));

        var saved = await Post($"/Workplaces/{w}?handler=SaveBinds",
            ("param.order_id", "42"), ("param.status", "OPEN"));
        Assert.That(Header(saved, "HX-Trigger"), Is.EqualTo("runAll"));

        html = await _client.GetStringAsync($"/Workplaces/{w}");
        Assert.That(html, Does.Match("name=\"param.order_id\" value=\"42\""));
        Assert.That(html, Does.Match("name=\"param.status\" value=\"OPEN\""));
    }

    [Test]
    public async Task ABindWithNoWorkplaceValue_StartsFromTheQuerysLastValue()
    {
        var w = NewWorkplace("Seeded");
        var q = NewQuery("C", "select * from t where k = :k");
        _queries.SaveLastParams(q, new Dictionary<string, string> { ["k"] = "from-query" });
        _workplaces.AddCopies(w, [q]);

        Assert.That(await _client.GetStringAsync($"/Workplaces/{w}"),
            Does.Match("name=\"param.k\" value=\"from-query\""));
    }

    [Test]
    public async Task RunningAPanel_ShowsTheTable_WithItsNumbers_AndSendsTheSharedValues()
    {
        var w = NewWorkplace("Run");
        var q = NewQuery("D", "select id, name from t where k = :k", connection: "PROD");
        _workplaces.AddCopies(w, [q]);
        var panel = _workplaces.Get(w)!.Panels[0];

        var response = await Post($"/Workplaces/{w}?handler=RunPanel&panel={panel.Id}",
            ("connection", ""), ("param.k", " 7 "));
        var html = await response.Content.ReadAsStringAsync();

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(html, Does.Contain("<td class=\"\">alpha</td>"));
        Assert.That(html, Does.Contain("class=\"null\">NULL"));
        Assert.That(html, Does.Contain("data-rows=\"2\"").And.Contain("data-ms=\"7\""));
        Assert.That(_runner.LastConnection, Is.EqualTo("PROD"), "the query's default connection");
        Assert.That(_runner.LastValues["k"], Is.EqualTo("7"));
        Assert.That(_runner.LastMaxRows, Is.GreaterThan(0));
    }

    [Test]
    public async Task APanelAndTheQueriesPage_ShowTheSameSummary_AndTheSameExportButton()
    {
        var w = NewWorkplace("Same");
        var q = NewQuery("Twin", "select id, name from t", connection: "PROD");
        _workplaces.AddCopies(w, [q]);
        var panel = _workplaces.Get(w)!.Panels[0];

        var onPage = await (await Post($"/Queries/{q}?handler=Run", ("connection", "PROD")))
            .Content.ReadAsStringAsync();
        var inPanel = await (await Post($"/Workplaces/{w}?handler=RunPanel&panel={panel.Id}", ("connection", "")))
            .Content.ReadAsStringAsync();

        // The summary line (time · rows · duration) is one partial: same text, sans the clock.
        static string Summary(string html) =>
            System.Text.RegularExpressions.Regex.Match(
                html, "class=\"ran-at\"[^>]*>\\d{2}:\\d{2}:\\d{2}</span>\\s*·\\s*([^<]+)<").Groups[1].Value.Trim();
        Assert.That(Summary(onPage), Is.EqualTo("2 rows · 7 ms"));
        Assert.That(Summary(inPanel), Is.EqualTo(Summary(onPage)));

        // In the panel it goes to the title bar (an out-of-band swap), not the body.
        Assert.That(inPanel, Does.Contain($"id=\"panel-count-{panel.Id}\"").And.Contain("hx-swap-oob"));

        // The export button is one partial too: it is in the page's form on both.
        var page = await _client.GetStringAsync($"/Workplaces/{w}");
        var run = await _client.GetStringAsync($"/Queries/{q}");
        foreach (var html in new[] { page, run })
            Assert.That(html, Does.Contain("data-post-form=").And.Contain(">Export CSV</button>"));

        // The "full width" button is one partial too: in the result's title bar on both.
        Assert.That(onPage, Does.Contain("data-wide-toggle").And.Contain("aria-label=\"Full width: the results\""));
        Assert.That(page, Does.Contain("data-wide-toggle").And.Contain("aria-label=\"Full width: Twin\""));
    }

    [Test]
    public async Task APanelThatFails_EmptiesItsTitleSummary()
    {
        var w = NewWorkplace("Title summary on failure");
        _workplaces.AddCopies(w, [NewQuery("Broken", "select 1 from dual")]);
        var panel = _workplaces.Get(w)!.Panels[0];

        var html = await (await Post($"/Workplaces/{w}?handler=RunPanel&panel={panel.Id}", ("connection", "")))
            .Content.ReadAsStringAsync();

        Assert.That(html, Does.Match($"id=\"panel-count-{panel.Id}\"[^>]*>\\s*</span>"));
        Assert.That(html, Does.Contain("data-failed=\"1\""));
    }

    [Test]
    public async Task ExportingAPanel_DownloadsEveryRow_WithTheSharedValues()
    {
        var w = NewWorkplace("Export");
        var q = NewQuery("Orders", "select id, name from t where k = :k", connection: "PROD");
        _workplaces.AddCopies(w, [q]);
        var panel = _workplaces.Get(w)!.Panels[0];

        var response = await Post($"/Workplaces/{w}?handler=CsvPanel&panel={panel.Id}",
            ("connection", ""), ("param.k", " 9 "));
        var bytes = await response.Content.ReadAsByteArrayAsync();

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(response.Content.Headers.ContentType!.MediaType, Is.EqualTo("text/csv"));
        Assert.That(response.Content.Headers.ContentDisposition!.FileNameStar
            ?? response.Content.Headers.ContentDisposition.FileName, Does.Contain("Orders"));
        Assert.That(System.Text.Encoding.UTF8.GetString(bytes), Does.Contain("ID,NAME\r\n1,alpha\r\n"));
        Assert.That(_runner.LastConnection, Is.EqualTo("PROD"));
        Assert.That(_runner.LastValues["k"], Is.EqualTo("9"));
        Assert.That(_runner.LastMaxRows, Is.EqualTo(QueryRunner.ExportLimit));
    }

    [Test]
    public async Task ExportingAPanelOfAnotherWorkspace_IsRefused()
    {
        var a = NewWorkplace("A");
        var b = NewWorkplace("B");
        _workplaces.AddCopies(b, [NewQuery("X", "select 1 from dual")]);
        var foreign = _workplaces.Get(b)!.Panels[0];

        Assert.That((await Post($"/Workplaces/{a}?handler=CsvPanel&panel={foreign.Id}", ("connection", ""))).StatusCode,
            Is.EqualTo(HttpStatusCode.NotFound));
    }

    [Test]
    public async Task TheHighlightBox_IsOnTheQueriesPageAndOnAWorkspaceWithPanels()
    {
        var empty = NewWorkplace("Highlight empty");
        var w = NewWorkplace("Highlight panels");
        var q = NewQuery("Hl", "select 1 from dual");
        _workplaces.AddCopies(w, [q]);

        Assert.That(await _client.GetStringAsync($"/Workplaces/{w}"),
            Does.Contain("data-highlight-scope=\"[data-workplace]\"").And.Contain("data-highlight-count"));
        var panelPage = await _client.GetStringAsync($"/Workplaces/{w}");
        Assert.That(System.Text.RegularExpressions.Regex.Matches(panelPage, "data-highlight-scope=").Count, Is.EqualTo(1),
            "one box by default (a setting goes up to three)");
        Assert.That(System.Text.RegularExpressions.Regex.Matches(panelPage, "placeholder=\"Highlight\"").Count, Is.EqualTo(1),
            "the colour is shown by the stripe on the field, not named");
        Assert.That(panelPage, Does.Not.Contain("Highlight in "));
        Assert.That(await _client.GetStringAsync($"/Queries/{q}"),
            Does.Contain("data-highlight-scope=\"#results\""));
        Assert.That(await _client.GetStringAsync($"/Workplaces/{empty}"),
            Does.Not.Contain("data-highlight-scope"), "nothing to highlight in an empty workspace");
    }

    [Test]
    public async Task APanelStartsOnTheQuerysConnection_AndCanBeGivenAnother()
    {
        var w = NewWorkplace("Conn");
        var q = NewQuery("E", "select 1 from dual", connection: "PROD");
        _workplaces.AddCopies(w, [q]);
        var panel = _workplaces.Get(w)!.Panels[0];
        Assert.That(panel.Connection, Is.EqualTo("PROD"), "copied with the query's default connection");

        await Post($"/Workplaces/{w}?handler=PanelSettings&panel={panel.Id}",
            ("connection", "TEST"), ("seconds", "15"));
        await Post($"/Workplaces/{w}?handler=RunPanel&panel={panel.Id}", ("connection", "TEST"));

        Assert.That(_runner.LastConnection, Is.EqualTo("TEST"));
        var saved = _workplaces.GetPanel(panel.Id)!;
        Assert.That(saved.Connection, Is.EqualTo("TEST"));
        Assert.That(saved.RefreshSeconds, Is.EqualTo(15));
        Assert.That(_queries.Get(q)!.Connection, Is.EqualTo("PROD"), "the saved query is untouched");
    }

    [Test]
    public async Task TheRefreshInterval_IsClamped()
    {
        var w = NewWorkplace("Clamp");
        _workplaces.AddCopies(w, [NewQuery("F", "select 1 from dual")]);
        var panel = _workplaces.Get(w)!.Panels[0];

        await Post($"/Workplaces/{w}?handler=PanelSettings&panel={panel.Id}", ("connection", ""), ("seconds", "999999"));
        Assert.That(_workplaces.GetPanel(panel.Id)!.RefreshSeconds, Is.EqualTo(3600));
        await Post($"/Workplaces/{w}?handler=PanelSettings&panel={panel.Id}", ("connection", ""), ("seconds", "-5"));
        Assert.That(_workplaces.GetPanel(panel.Id)!.RefreshSeconds, Is.EqualTo(0));
    }

    [Test]
    public async Task AFailedRun_ShowsTheErrorInThePanel_AndNoConnectionIsExplained()
    {
        var w = NewWorkplace("Fail");
        var withConn = NewQuery("G", "select 1 from dual", connection: "PROD");
        var noConn = NewQuery("H", "select 1 from dual");
        _workplaces.AddCopies(w, [withConn, noConn]);
        var panels = _workplaces.Get(w)!.Panels;

        _runner.Failure = new QueryException("ORA-01017 invalid credentials");
        var failed = await (await Post($"/Workplaces/{w}?handler=RunPanel&panel={panels[0].Id}", ("connection", "")))
            .Content.ReadAsStringAsync();
        Assert.That(failed, Does.Contain("ORA-01017").And.Contain("class=\"error\""));
        Assert.That(failed, Does.Contain("data-failed"));

        var none = await (await Post($"/Workplaces/{w}?handler=RunPanel&panel={panels[1].Id}", ("connection", "")))
            .Content.ReadAsStringAsync();
        Assert.That(none, Does.Contain("No connection chosen"));
        Assert.That(_runner.LastConnection, Is.EqualTo("PROD"), "nothing ran for the panel without one");
    }

    [Test]
    public async Task APanelOfAnotherWorkplace_IsNotReachable()
    {
        var a = NewWorkplace("Mine");
        var b = NewWorkplace("Theirs");
        _workplaces.AddCopies(b, [NewQuery("I", "select 1 from dual", "PROD")]);
        var foreign = _workplaces.Get(b)!.Panels[0];

        Assert.That((await Post($"/Workplaces/{a}?handler=RunPanel&panel={foreign.Id}", ("connection", ""))).StatusCode,
            Is.EqualTo(HttpStatusCode.NotFound));
        Assert.That((await Post($"/Workplaces/{a}?handler=RemovePanel&panel={foreign.Id}")).StatusCode,
            Is.EqualTo(HttpStatusCode.NotFound));
        Assert.That(_workplaces.GetPanel(foreign.Id), Is.Not.Null);
    }

    [Test]
    public async Task RemovingAPanel_RenamingAndDeletingAWorkplace_Work()
    {
        var w = NewWorkplace("Edit me");
        _workplaces.AddCopies(w, [NewQuery("J", "select 1 from dual"), NewQuery("K", "select 2 from dual")]);
        var first = _workplaces.Get(w)!.Panels[0];

        await Post($"/Workplaces/{w}?handler=RemovePanel&panel={first.Id}");
        Assert.That(_workplaces.Get(w)!.Panels.Select(p => p.QueryName), Is.EqualTo(new[] { "K" }));

        var renamed = await Post($"/Workplaces/{w}?handler=Rename", ("name", "Edited"));
        Assert.That(Header(renamed, "HX-Refresh"), Is.EqualTo("true"));
        Assert.That(_workplaces.Get(w)!.Name, Is.EqualTo("Edited"));

        var deleted = await Post($"/Workplaces/{w}?handler=Delete");
        Assert.That(Header(deleted, "HX-Redirect"), Is.EqualTo("/Workplaces"));
        Assert.That(_workplaces.Get(w), Is.Null);
    }

    [Test]
    public void DeletingASavedQuery_LeavesTheWorkplacePanelsAlone()
    {
        var w = NewWorkplace("Cascade");
        var q = NewQuery("L", "select 1 from dual");
        var keep = NewQuery("M", "select 2 from dual");
        _workplaces.AddCopies(w, [q, keep]);

        _queries.Delete(q);

        Assert.That(_workplaces.Get(w)!.Panels.Select(p => p.QueryName), Is.EqualTo(new[] { "L", "M" }));
        Assert.That(_workplaces.Get(w)!.Panels[0].Sql, Is.EqualTo("select 1 from dual"));
    }

    [Test]
    public async Task TheMenuListsWorkplaces_AndTheListPageShowsThem()
    {
        var w = NewWorkplace("Visible one");
        var html = await _client.GetStringAsync("/Workplaces");
        Assert.That(html, Does.Contain("Visible one").And.Contain($"/Workplaces/{w}"));
        Assert.That(html, Does.Contain("href=\"/Workplaces\""), "nav entry");
    }

    [Test]
    public async Task EachPanelHasItsOwnRowLimit_SavedClamped_AndUsedWhenRunning()
    {
        var w = NewWorkplace("Rows");
        _workplaces.AddCopies(w, [NewQuery("N1", "select 1 from dual", "PROD"), NewQuery("N2", "select 2 from dual", "PROD")]);
        var panels = _workplaces.Get(w)!.Panels;
        Assert.That(panels.Select(p => p.MaxRows), Is.All.EqualTo(500), "default");

        await Post($"/Workplaces/{w}?handler=PanelSettings&panel={panels[0].Id}", ("connection", ""), ("seconds", "0"), ("maxRows", "25"));
        await Post($"/Workplaces/{w}?handler=PanelSettings&panel={panels[1].Id}", ("connection", ""), ("seconds", "0"), ("maxRows", "99999999"));
        Assert.That(_workplaces.GetPanel(panels[0].Id)!.MaxRows, Is.EqualTo(25));
        Assert.That(_workplaces.GetPanel(panels[1].Id)!.MaxRows, Is.EqualTo(10000), "clamped");

        // Another setting changed without a limit keeps the limit.
        await Post($"/Workplaces/{w}?handler=PanelSettings&panel={panels[0].Id}", ("connection", ""), ("seconds", "5"));
        Assert.That(_workplaces.GetPanel(panels[0].Id)!.MaxRows, Is.EqualTo(25));

        await Post($"/Workplaces/{w}?handler=RunPanel&panel={panels[0].Id}", ("connection", ""));
        Assert.That(_runner.LastMaxRows, Is.EqualTo(25));
        await Post($"/Workplaces/{w}?handler=RunPanel&panel={panels[1].Id}", ("connection", ""));
        Assert.That(_runner.LastMaxRows, Is.EqualTo(10000));
        await Post($"/Workplaces/{w}?handler=RunPanel&panel={panels[0].Id}", ("connection", ""), ("maxRows", "7"));
        Assert.That(_runner.LastMaxRows, Is.EqualTo(7), "the value in the form wins");
        Assert.That(await _client.GetStringAsync($"/Workplaces/{w}"), Does.Contain("name=\"maxRows\" type=\"number\" min=\"1\" max=\"10000\" value=\"25\""));
    }

    [Test]
    public async Task ADuplicate_CopiesPanelsSettingsAndValues_UnderANewName()
    {
        var w = NewWorkplace("Original");
        var q1 = NewQuery("O1", "select * from t where k = :k", "PROD");
        var q2 = NewQuery("O2", "select 2 from dual", "PROD");
        _workplaces.AddCopies(w, [q1, q2]);
        var src = _workplaces.Get(w)!.Panels;
        _workplaces.SetPanel(src[0].Id, "TEST", 30, 40);
        _workplaces.SaveBinds(w, new Dictionary<string, string> { ["k"] = "v1" });

        var form = await _client.GetStringAsync($"/Workplaces/{w}?handler=DuplicateForm");
        Assert.That(form, Does.Contain("value=\"Original copy\""));
        Assert.That(_workplaces.GetAll().Select(x => x.Name), Does.Not.Contain("Original copy"), "nothing created yet");

        var response = await Post($"/Workplaces/{w}?handler=Duplicate", ("name", "Original copy"));
        var copyId = long.Parse(Header(response, "HX-Redirect").Split('/').Last());
        var copy = _workplaces.Get(copyId)!;

        Assert.That(copy.Name, Is.EqualTo("Original copy"));
        Assert.That(copy.Panels.Select(p => p.QueryName), Is.EqualTo(new[] { "O1", "O2" }));
        Assert.That(copy.Panels.Select(p => p.Sql), Is.EqualTo(src.Select(p => p.Sql)));
        Assert.That(copy.Panels[0].Connection, Is.EqualTo("TEST"));
        Assert.That(copy.Panels[0].RefreshSeconds, Is.EqualTo(30));
        Assert.That(copy.Panels[0].MaxRows, Is.EqualTo(40));
        Assert.That(copy.Binds["k"], Is.EqualTo("v1"));
        Assert.That(copy.Panels.Select(p => p.Id), Is.Not.EqualTo(src.Select(p => p.Id)), "own panels");

        // The copy is independent of the original: its panels, and their queries.
        _workplaces.RemovePanel(copy.Panels[1].Id);
        Assert.That(_workplaces.Get(w)!.Panels, Has.Count.EqualTo(2));
        _workplaces.SetPanelQuery(copy.Panels[0].Id, "Changed in the copy", "select 9 from dual");
        Assert.That(_workplaces.Get(w)!.Panels[0].QueryName, Is.EqualTo("O1"));
        Assert.That(_workplaces.Get(w)!.Panels[0].Sql, Is.EqualTo("select * from t where k = :k"));

        // Names are checked, and a second copy gets the next free name.
        var taken = await Post($"/Workplaces/{w}?handler=Duplicate", ("name", "original COPY"));
        Assert.That(await taken.Content.ReadAsStringAsync(), Does.Contain("already exists"));
        Assert.That(await _client.GetStringAsync($"/Workplaces/{w}?handler=DuplicateForm"), Does.Contain("value=\"Original copy 2\""));
    }

    [Test]
    public async Task APanelShows10RowsByDefault_AndTheNumberCanBeChanged_AndIsCopied()
    {
        var w = NewWorkplace("Height");
        _workplaces.AddCopies(w, [NewQuery("H", "select 1 from dual")]);
        var panel = _workplaces.Get(w)!.Panels[0];
        Assert.That(panel.ViewRows, Is.EqualTo(10), "the default height");
        Assert.That(await _client.GetStringAsync($"/Workplaces/{w}"),
            Does.Match("name=\"viewRows\"[^>]*value=\"10\"").Or.Match("value=\"10\"[^>]*name=\"viewRows\""));

        await Post($"/Workplaces/{w}?handler=PanelSettings&panel={panel.Id}",
            ("connection", ""), ("seconds", "5"), ("maxRows", "40"), ("viewRows", "25"));
        Assert.That(_workplaces.GetPanel(panel.Id)!.ViewRows, Is.EqualTo(25));

        // Without the field (another setting changed) it is left as it is; out of range is clamped.
        await Post($"/Workplaces/{w}?handler=PanelSettings&panel={panel.Id}", ("connection", ""), ("seconds", "5"));
        Assert.That(_workplaces.GetPanel(panel.Id)!.ViewRows, Is.EqualTo(25));
        _workplaces.SetPanel(panel.Id, null, 5, viewRows: 0);
        Assert.That(_workplaces.GetPanel(panel.Id)!.ViewRows, Is.EqualTo(1));
        _workplaces.SetPanel(panel.Id, null, 5, viewRows: 99999);
        Assert.That(_workplaces.GetPanel(panel.Id)!.ViewRows, Is.EqualTo(WorkplaceRepository.MaxViewRows));

        _workplaces.SetPanel(panel.Id, null, 5, viewRows: 17);
        var copy = _workplaces.Duplicate(w, "Height copy");
        Assert.That(_workplaces.Get(copy)!.Panels[0].ViewRows, Is.EqualTo(17), "a duplicate keeps the height");
    }

    [Test]
    public async Task ResultsHaveARowNumberColumn_OnBothPages_AndTheQueriesPageHasTheHeightInput()
    {
        var w = NewWorkplace("Numbers");
        var q = NewQuery("N", "select id, name from t", connection: "PROD");
        _workplaces.AddCopies(w, [q]);
        var panel = _workplaces.Get(w)!.Panels[0];

        var onPage = await (await Post($"/Queries/{q}?handler=Run", ("connection", "PROD"))).Content.ReadAsStringAsync();
        var inPanel = await (await Post($"/Workplaces/{w}?handler=RunPanel&panel={panel.Id}", ("connection", "")))
            .Content.ReadAsStringAsync();

        foreach (var html in new[] { onPage, inPanel })
        {
            Assert.That(html, Does.Contain("<th class=\"rownum\""));
            Assert.That(System.Text.RegularExpressions.Regex.Matches(html, "<td class=\"rownum\"></td>").Count,
                Is.EqualTo(2), "one number cell per row (the numbers themselves are a CSS counter)");
        }
        Assert.That(await _client.GetStringAsync($"/Queries/{q}"),
            Does.Match("data-view-rows-for=\"#results\"").And.Match("value=\"10\""));
    }

    [Test]
    public async Task Panels_CanBeMovedEarlierAndLater_NotPastTheEnds()
    {
        var w = NewWorkplace("Order");
        _workplaces.AddCopies(w, [NewQuery("P1", "select 1 from dual"), NewQuery("P2", "select 2 from dual"), NewQuery("P3", "select 3 from dual")]);
        string Names() => string.Join(",", _workplaces.Get(w)!.Panels.Select(p => p.QueryName));
        var ids = _workplaces.Get(w)!.Panels.Select(p => p.Id).ToList();

        var moved = await Post($"/Workplaces/{w}?handler=MovePanel&panel={ids[2]}&dir=-1");
        Assert.That(Header(moved, "HX-Refresh"), Is.EqualTo("true"));
        Assert.That(Names(), Is.EqualTo("P1,P3,P2"));
        await Post($"/Workplaces/{w}?handler=MovePanel&panel={ids[2]}&dir=-1");
        Assert.That(Names(), Is.EqualTo("P3,P1,P2"));
        await Post($"/Workplaces/{w}?handler=MovePanel&panel={ids[2]}&dir=-1");
        Assert.That(Names(), Is.EqualTo("P3,P1,P2"), "already first");
        await Post($"/Workplaces/{w}?handler=MovePanel&panel={ids[0]}&dir=1");
        Assert.That(Names(), Is.EqualTo("P3,P2,P1"));
        await Post($"/Workplaces/{w}?handler=MovePanel&panel={ids[0]}&dir=1");
        Assert.That(Names(), Is.EqualTo("P3,P2,P1"), "already last");
        await Post($"/Workplaces/{w}?handler=MovePanel&panel={ids[1]}&dir=1");
        Assert.That(Names(), Is.EqualTo("P3,P1,P2"));
    }

    [Test]
    public async Task APanelCannotBeMovedThroughAnotherWorkplace()
    {
        var a = NewWorkplace("Move A");
        var b = NewWorkplace("Move B");
        _workplaces.AddCopies(a, [NewQuery("M1", "select 1 from dual"), NewQuery("M2", "select 2 from dual")]);
        var panels = _workplaces.Get(a)!.Panels;

        Assert.That((await Post($"/Workplaces/{b}?handler=MovePanel&panel={panels[0].Id}&dir=1")).StatusCode,
            Is.EqualTo(HttpStatusCode.NotFound));
        Assert.That(_workplaces.Get(a)!.Panels.Select(p => p.QueryName), Is.EqualTo(new[] { "M1", "M2" }));
    }

    [Test]
    public async Task EachPanelHasMoveArrows_DisabledAtTheEnds_AndTitlesAreNotDraggable()
    {
        var w = NewWorkplace("Arrow markup");
        _workplaces.AddCopies(w, [NewQuery("A1", "select 1 from dual"), NewQuery("A2", "select 2 from dual"), NewQuery("A3", "select 3 from dual")]);
        var html = await _client.GetStringAsync($"/Workplaces/{w}");

        Assert.That(Regex.Matches(html, "Move this panel earlier").Count, Is.EqualTo(3));
        Assert.That(Regex.Matches(html, "Move this panel later").Count, Is.EqualTo(3));
        // The first panel cannot go earlier, the last cannot go later.
        Assert.That(Regex.Matches(html, "title=\"Move this panel earlier\"[^>]*disabled").Count, Is.EqualTo(1));
        Assert.That(Regex.Matches(html, "title=\"Move this panel later\"[^>]*disabled").Count, Is.EqualTo(1));
        Assert.That(html, Does.Not.Contain("draggable=\"true\"").And.Not.Contain("ArrangePanels"));
    }

    [Test]
    public async Task AddingASavedQuery_MakesAnIndependentCopy_InBothDirections()
    {
        var a = NewWorkplace("Copy A");
        var b = NewWorkplace("Copy B");
        var q = NewQuery("Template", "select 1 from dual", "PROD");
        _workplaces.AddCopies(a, [q]);
        _workplaces.AddCopies(b, [q]);
        var panelA = _workplaces.Get(a)!.Panels[0];
        var panelB = _workplaces.Get(b)!.Panels[0];

        // Edit the copy in A (renamed, new SQL): the saved query and B's copy are untouched.
        var edited = await Post($"/Workplaces/{a}?handler=EditQuery&panel={panelA.Id}",
            ("name", "Renamed in A"), ("sql", "select 2 from dual"));
        Assert.That(Header(edited, "HX-Refresh"), Is.EqualTo("true"));
        Assert.That(_workplaces.GetPanel(panelA.Id)!.QueryName, Is.EqualTo("Renamed in A"));
        Assert.That(_workplaces.GetPanel(panelA.Id)!.Sql, Is.EqualTo("select 2 from dual"));
        Assert.That(_queries.Get(q)!.Name, Is.EqualTo("Template"));
        Assert.That(_queries.Get(q)!.Sql, Is.EqualTo("select 1 from dual"));
        Assert.That(_workplaces.GetPanel(panelB.Id)!.QueryName, Is.EqualTo("Template"));
        Assert.That(_workplaces.GetPanel(panelB.Id)!.Sql, Is.EqualTo("select 1 from dual"));

        // Edit the saved query (the template): the copies do not follow.
        var template = _queries.Get(q)!;
        template.Name = "Template v2";
        template.Sql = "select 3 from dual";
        _queries.Save(template);
        Assert.That(_workplaces.GetPanel(panelA.Id)!.Sql, Is.EqualTo("select 2 from dual"));
        Assert.That(_workplaces.GetPanel(panelB.Id)!.QueryName, Is.EqualTo("Template"));
        Assert.That(_workplaces.GetPanel(panelB.Id)!.Sql, Is.EqualTo("select 1 from dual"));

        // The panel runs its own SQL.
        await Post($"/Workplaces/{a}?handler=RunPanel&panel={panelA.Id}", ("connection", ""));
        Assert.That(_runner.LastSql, Is.EqualTo("select 2 from dual"));
    }

    [Test]
    public async Task AQueryCanBeWrittenInTheWorkplace_WithoutCreatingItInQueries()
    {
        var w = NewWorkplace("From scratch");
        var queriesBefore = _queries.GetAll().Count;

        var form = await _client.GetStringAsync($"/Workplaces/{w}?handler=NewQueryForm");
        Assert.That(form, Does.Contain("name=\"name\"").And.Contain("name=\"sql\"").And.Contain("name=\"connection\""));

        var created = await Post($"/Workplaces/{w}?handler=CreateQuery",
            ("name", "  Live orders "), ("sql", "select id from orders where st = :st"), ("connection", "PROD"));
        Assert.That(Header(created, "HX-Refresh"), Is.EqualTo("true"));

        var panel = _workplaces.Get(w)!.Panels.Single();
        Assert.That(panel.QueryName, Is.EqualTo("Live orders"));
        Assert.That(panel.Sql, Is.EqualTo("select id from orders where st = :st"));
        Assert.That(panel.Connection, Is.EqualTo("PROD"));
        Assert.That(panel.Binds, Is.EqualTo(new[] { "st" }));
        Assert.That(_queries.GetAll(), Has.Count.EqualTo(queriesBefore), "not added to the Queries list");

        // It runs, and its bind is one of the workspace's shared inputs.
        await Post($"/Workplaces/{w}?handler=RunPanel&panel={panel.Id}", ("connection", ""), ("param.st", "OPEN"));
        Assert.That(_runner.LastSql, Is.EqualTo("select id from orders where st = :st"));
        Assert.That(_runner.LastValues["st"], Is.EqualTo("OPEN"));
        Assert.That(await _client.GetStringAsync($"/Workplaces/{w}"), Does.Contain("name=\"param.st\""));
    }

    [Test]
    public async Task ANewOrEditedQuery_FollowsTheSameRulesAsASavedOne()
    {
        var w = NewWorkplace("Rules");
        _workplaces.AddCopies(w, [NewQuery("R", "select 1 from dual", "PROD")]);
        var panel = _workplaces.Get(w)!.Panels[0];

        async Task<(string Html, string Retarget)> Create(string name, string sql)
        {
            var r = await Post($"/Workplaces/{w}?handler=CreateQuery", ("name", name), ("sql", sql), ("connection", ""));
            return (await r.Content.ReadAsStringAsync(), Header(r, "HX-Retarget"));
        }

        var noName = await Create(" ", "select 1 from dual");
        Assert.That(noName.Html, Does.Contain("The name is required."));
        Assert.That(noName.Retarget, Is.EqualTo("#dlg"));
        Assert.That((await Create("X", "")).Html, Does.Contain("The SQL is required."));
        // Read-only by design: anything but one SELECT/WITH is refused.
        Assert.That((await Create("X", "delete from orders")).Html, Does.Contain("class=\"error\""));
        Assert.That(_workplaces.Get(w)!.Panels, Has.Count.EqualTo(1), "nothing was created");

        var bad = await Post($"/Workplaces/{w}?handler=EditQuery&panel={panel.Id}", ("name", "R"), ("sql", "update t set a = 1"));
        Assert.That(await bad.Content.ReadAsStringAsync(), Does.Contain("class=\"error\""));
        Assert.That(_workplaces.GetPanel(panel.Id)!.Sql, Is.EqualTo("select 1 from dual"), "an invalid edit changes nothing");
    }

    [Test]
    public async Task TheEditDialog_ShowsThePanelsOwnQuery_AndIsReachableOnlyThroughItsWorkplace()
    {
        var a = NewWorkplace("Dlg A");
        var b = NewWorkplace("Dlg B");
        _workplaces.AddCopies(a, [NewQuery("Dlg query", "select 41 from dual")]);
        var panel = _workplaces.Get(a)!.Panels[0];

        var html = await _client.GetStringAsync($"/Workplaces/{a}?handler=EditQueryForm&panel={panel.Id}");
        Assert.That(html, Does.Contain("value=\"Dlg query\"").And.Contain("select 41 from dual"));

        Assert.That((await _client.GetAsync($"/Workplaces/{b}?handler=EditQueryForm&panel={panel.Id}")).StatusCode,
            Is.EqualTo(HttpStatusCode.NotFound));
        Assert.That((await Post($"/Workplaces/{b}?handler=EditQuery&panel={panel.Id}", ("name", "x"), ("sql", "select 1 from dual"))).StatusCode,
            Is.EqualTo(HttpStatusCode.NotFound));
    }

    [Test]
    public async Task TheQueryDialog_OnlyTargetsTheListWhenOpenedFromTheList()
    {
        var q = NewQuery("Dialog target", "select 1 from dual");

        var fromList = await _client.GetStringAsync($"/Queries?handler=Form&id={q}");
        Assert.That(fromList, Does.Contain("hx-target=\"#queries\"").And.Contain("hx-swap=\"outerHTML\""));

        // Another page has no #queries: htmx would silently send nothing.
        foreach (var from in new[] { "run" })
        {
            var html = await _client.GetStringAsync($"/Queries?handler=Form&id={q}&from={from}");
            Assert.That(html, Does.Not.Contain("#queries"), from);
            Assert.That(html, Does.Contain("hx-swap=\"none\""), from);
        }
    }

    [Test]
    public async Task TheOldWatchesAddress_RedirectsToTheDataWorkplaces()
    {
        using var noFollow = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var list = await noFollow.GetAsync("/Watches");
        Assert.That(list.StatusCode, Is.EqualTo(HttpStatusCode.MovedPermanently));
        Assert.That(list.Headers.Location!.OriginalString, Is.EqualTo("/Workplaces"));

        var one = await noFollow.GetAsync("/Watches/12");
        Assert.That(one.Headers.Location!.OriginalString, Is.EqualTo("/Workplaces/12"));
    }

    [Test]
    public async Task TheMenuAndHeadings_SayWorkspaces()
    {
        var html = await _client.GetStringAsync("/Workplaces");
        Assert.That(html, Does.Contain(">Workspaces<"));
        Assert.That(html, Does.Not.Contain(">Watches<"));
    }
}
