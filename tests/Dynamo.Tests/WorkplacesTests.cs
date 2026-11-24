using System.Net;
using System.Text.RegularExpressions;
using Dapper;
using Dynamo.Core;
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
    private Db _db = null!;

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
        _db = _factory.Services.GetRequiredService<Db>();
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

    /// <summary>A query to put in a workspace (there are no saved queries any more).</summary>
    private static TestQuery NewQuery(string name, string sql, string connection = "", string category = "") =>
        new(name, sql, connection);

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
    public async Task ImportingFromAnotherWorkspace_CopiesTheTickedPanels_WithTheirSettings()
    {
        var source = NewWorkplace("Import source");
        var target = NewWorkplace("Import target");
        var other = NewWorkplace("Import other");
        _workplaces.AddCopies(source, [NewQuery("Stuck orders", "select 1 from dual", "PROD"), NewQuery("Queue depth", "select 2 from dual"), NewQuery("Skipped", "select 3 from dual")]);
        _workplaces.AddCopies(other, [NewQuery("Foreign", "select 4 from dual")]);
        var panels = _workplaces.Get(source)!.Panels;
        _workplaces.SetPanel(panels[0].Id, null, 15, 40, 17);
        var foreign = _workplaces.Get(other)!.Panels[0];

        // Ticked: the second, the first (twice), an unknown id, and a panel of a third workspace.
        var imported = await Post($"/Workplaces/{target}?handler=Import", ("from", source.ToString()),
            ("panel", panels[1].Id.ToString()), ("panel", panels[0].Id.ToString()), ("panel", panels[0].Id.ToString()),
            ("panel", "99999"), ("panel", foreign.Id.ToString()));
        Assert.That(Header(imported, "HX-Refresh"), Is.EqualTo("true"));

        var copies = _workplaces.Get(target)!.Panels;
        Assert.That(copies.Select(p => p.QueryName), Is.EqualTo(new[] { "Stuck orders", "Queue depth" }),
            "in the order they have in the source, once each; the foreign and unknown ids are ignored");
        Assert.That(copies[0].Sql, Is.EqualTo("select 1 from dual"));
        Assert.That(copies[0].Connection, Is.EqualTo("PROD"));
        Assert.That(copies[0].RefreshSeconds, Is.EqualTo(15));
        Assert.That(copies[0].MaxRows, Is.EqualTo(40));
        Assert.That(copies[0].ViewRows, Is.EqualTo(17));
        Assert.That(copies.Select(p => p.Position), Is.EqualTo(new long[] { 1, 2 }));
        Assert.That(copies.Select(p => p.Id), Has.None.AnyOf(panels.Select(p => p.Id).Cast<object>().ToArray()), "new panels");
        Assert.That(_workplaces.Get(source)!.Panels, Has.Count.EqualTo(3), "the source keeps its panels");

        // Importing again adds them again (a query may be copied twice, say for another connection).
        await Post($"/Workplaces/{target}?handler=Import", ("from", source.ToString()), ("panel", panels[2].Id.ToString()));
        Assert.That(_workplaces.Get(target)!.Panels.Select(p => p.QueryName), Is.EqualTo(new[] { "Stuck orders", "Queue depth", "Skipped" }));
        Assert.That(Regex.Matches(await _client.GetStringAsync($"/Workplaces/{target}"), "data-panel=\"").Count, Is.EqualTo(3));
    }

    [Test]
    public async Task ImportingFromTheWorkspaceItself_OrAMissingOne_DoesNothing()
    {
        var w = NewWorkplace("Import self");
        _workplaces.AddCopies(w, [NewQuery("Only", "select 1 from dual")]);
        var panel = _workplaces.Get(w)!.Panels[0];

        await Post($"/Workplaces/{w}?handler=Import", ("from", w.ToString()), ("panel", panel.Id.ToString()));
        Assert.That(_workplaces.Get(w)!.Panels, Has.Count.EqualTo(1), "no duplicate of its own panel");

        var missing = await Post($"/Workplaces/{w}?handler=Import", ("from", "99999"), ("panel", panel.Id.ToString()));
        Assert.That(missing.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
        var noTarget = await Post("/Workplaces/99999?handler=Import", ("from", w.ToString()), ("panel", panel.Id.ToString()));
        Assert.That(noTarget.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
    }

    [Test]
    public async Task TheImportDialog_ListsTheOtherWorkspacesAndThePanelsOfTheChosenOne()
    {
        var target = NewWorkplace("Dialog target");
        var a = NewWorkplace("Dialog source A");
        var b = NewWorkplace("Dialog source B");
        var empty = NewWorkplace("Dialog empty");
        // The test database is shared: only these workspaces may have panels, so the order is known.
        foreach (var leftover in _workplaces.GetAll().Where(w => w.PanelCount > 0 && w.Id != a && w.Id != b))
            _workplaces.Delete(leftover.Id);
        _workplaces.AddCopies(a, [NewQuery("From A one", "select 1 from dual", "PROD"), NewQuery("From A two", "select 2 from dual")]);
        _workplaces.AddCopies(b, [NewQuery("From B", "select 3 from dual")]);

        var html = await _client.GetStringAsync($"/Workplaces/{target}?handler=ImportForm");
        Assert.That(html, Does.Contain("Import queries into “Dialog target”"));
        Assert.That(html, Does.Contain(">Dialog source A (2)<").And.Contain(">Dialog source B (1)<"));
        Assert.That(html, Does.Not.Contain("Dialog empty"), "a workspace with no query has nothing to import");
        Assert.That(html, Does.Not.Contain(">Dialog target ("), "not the workspace itself");
        Assert.That(html, Does.Contain("data-select-item").And.Contain("data-select-all"));
        // The first workspace is listed by default (by name), with its panels and connections.
        Assert.That(html, Does.Contain(">From A one<").And.Contain(">From A two<").And.Not.Contain(">From B<"));
        Assert.That(html, Does.Contain("PROD").And.Contain("no connection chosen"));

        // Choosing another workspace asks again with ?from=.
        var other = await _client.GetStringAsync($"/Workplaces/{target}?handler=ImportForm&from={b}");
        Assert.That(other, Does.Contain(">From B<").And.Not.Contain(">From A one<"));
        Assert.That(other, Does.Match("<option value=\"" + b + "\" selected"));
        Assert.That(html, Does.Contain("hx-get=\"/Workplaces/" + target + "?handler=ImportForm\""), "the list re-renders the dialog");
        _ = empty;
    }

    [Test]
    public async Task TheImportDialog_SaysSoWhenNoOtherWorkspaceHasAQuery()
    {
        var alone = NewWorkplace("Alone in the world");
        // Whatever else the shared database holds, this one has no panel; use a database state with none to import.
        foreach (var w in _workplaces.GetAll().Where(w => w.Id != alone && w.PanelCount > 0))
            _workplaces.Delete(w.Id);

        var html = await _client.GetStringAsync($"/Workplaces/{alone}?handler=ImportForm");

        Assert.That(html, Does.Contain("No other workspace has a query to import yet."));
        Assert.That(html, Does.Not.Contain("data-select-item"));
        Assert.That((await _client.GetAsync("/Workplaces/99999?handler=ImportForm")).StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
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
    public async Task ABindWithNoWorkplaceValue_StartsFromThePanelsSeedValue()
    {
        var w = NewWorkplace("Seeded");
        _workplaces.AddCopies(w, [NewQuery("C", "select * from t where k = :k")]);
        using (var conn = _db.Open())
            conn.Execute("UPDATE watch_panel SET last_params = '{\"k\":\"from-query\"}' WHERE watch_id = @w", new { w });

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
    public async Task ARunningPanel_ShowsItsSummaryInTheTitleBar_AndHasTheExportAndFullWidthButtons()
    {
        var w = NewWorkplace("Same");
        _workplaces.AddCopies(w, [NewQuery("Twin", "select id, name from t", connection: "PROD")]);
        var panel = _workplaces.Get(w)!.Panels[0];

        var inPanel = await (await Post($"/Workplaces/{w}?handler=RunPanel&panel={panel.Id}", ("connection", "")))
            .Content.ReadAsStringAsync();

        // The summary line (time · rows · duration) goes to the title bar (an out-of-band swap), not the body.
        var summary = System.Text.RegularExpressions.Regex.Match(
            inPanel, $"id=\"panel-count-{panel.Id}\"[^>]*>\\s*([^<]+)<").Groups[1].Value.Trim();
        Assert.That(summary, Is.EqualTo("2 rows · 7 ms"), "rows and duration, no clock");
        Assert.That(inPanel, Does.Not.Contain("ran-at"), "no time stamp in the title bar");
        Assert.That(inPanel, Does.Contain($"id=\"panel-count-{panel.Id}\"").And.Contain("hx-swap-oob"));

        var page = await _client.GetStringAsync($"/Workplaces/{w}");
        Assert.That(page, Does.Contain("data-post-form=").And.Contain(">Export CSV</button>"));
        Assert.That(page, Does.Contain("data-wide-toggle").And.Contain("aria-label=\"Full width: Twin\""));
    }

    [Test]
    public async Task ATruncatedResult_HasNoHintInTheTitleBar_ButTheDetailsStillKnow()
    {
        var w = NewWorkplace("Truncated");
        _workplaces.AddCopies(w, [NewQuery("Cut", "select id, name from t", connection: "PROD")]);
        var panel = _workplaces.Get(w)!.Panels[0];
        var original = _runner.Result;
        _runner.Result = original with { Truncated = true };
        try
        {
            var html = await (await Post($"/Workplaces/{w}?handler=RunPanel&panel={panel.Id}", ("connection", "")))
                .Content.ReadAsStringAsync();

            var title = System.Text.RegularExpressions.Regex.Match(
                html, $@"id=""panel-count-{panel.Id}""[^>]*>\s*([^<]+)<").Groups[1].Value.Trim();
            Assert.That(title, Is.EqualTo("2 rows · 7 ms"), "only the rows and the duration in the title bar");
            Assert.That(html, Does.Not.Contain("more rows exist").And.Not.Contain("raise Max rows"));
            Assert.That(html, Does.Contain("data-more=\"1\""), "the details still know the limit was reached");
        }
        finally
        {
            _runner.Result = original;
        }
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
        Assert.That(await _client.GetStringAsync($"/Workplaces/{empty}"),
            Does.Not.Contain("data-highlight-scope"), "nothing to highlight in an empty workspace");
    }

    [Test]
    public async Task APanelStartsOnItsConnection_AndCanBeGivenAnother()
    {
        var w = NewWorkplace("Conn");
        var q = NewQuery("E", "select 1 from dual", connection: "PROD");
        _workplaces.AddCopies(w, [q]);
        var panel = _workplaces.Get(w)!.Panels[0];
        Assert.That(panel.Connection, Is.EqualTo("PROD"), "created with the connection of its query");

        await Post($"/Workplaces/{w}?handler=PanelSettings&panel={panel.Id}",
            ("connection", "TEST"), ("seconds", "15"));
        await Post($"/Workplaces/{w}?handler=RunPanel&panel={panel.Id}", ("connection", "TEST"));

        Assert.That(_runner.LastConnection, Is.EqualTo("TEST"));
        var saved = _workplaces.GetPanel(panel.Id)!;
        Assert.That(saved.Connection, Is.EqualTo("TEST"));
        Assert.That(saved.RefreshSeconds, Is.EqualTo(15));
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
    public async Task ANewPanelFollowsTheDefaultRows_UntilItIsGivenANumberOfItsOwn()
    {
        var w = NewWorkplace("Height");
        _workplaces.AddCopies(w, [NewQuery("H", "select 1 from dual")]);
        var panel = _workplaces.Get(w)!.Panels[0];
        Assert.That(panel.ViewRows, Is.EqualTo(0), "a new panel has no number of its own: 0 = the default from Settings");
        var page = await _client.GetStringAsync($"/Workplaces/{w}");
        Assert.That(page, Does.Match("name=\"viewRows\"[^>]*value=\"0\"").Or.Match("value=\"0\"[^>]*name=\"viewRows\""));
        Assert.That(page, Does.Contain("data-view-rows-default=\"10\"").And.Contain("(0 = the default, 10)"));

        // Its own number; 0 goes back to the default; out of range is kept inside 0..1000.
        await Post($"/Workplaces/{w}?handler=PanelSettings&panel={panel.Id}",
            ("connection", ""), ("seconds", "5"), ("maxRows", "40"), ("viewRows", "25"));
        Assert.That(_workplaces.GetPanel(panel.Id)!.ViewRows, Is.EqualTo(25));
        await Post($"/Workplaces/{w}?handler=PanelSettings&panel={panel.Id}", ("connection", ""), ("seconds", "5"));
        Assert.That(_workplaces.GetPanel(panel.Id)!.ViewRows, Is.EqualTo(25), "without the field it is left as it is");
        _workplaces.SetPanel(panel.Id, null, 5, viewRows: 0);
        Assert.That(_workplaces.GetPanel(panel.Id)!.ViewRows, Is.EqualTo(0), "0 is allowed: back to the default");
        _workplaces.SetPanel(panel.Id, null, 5, viewRows: -4);
        Assert.That(_workplaces.GetPanel(panel.Id)!.ViewRows, Is.EqualTo(0));
        _workplaces.SetPanel(panel.Id, null, 5, viewRows: 99999);
        Assert.That(_workplaces.GetPanel(panel.Id)!.ViewRows, Is.EqualTo(WorkplaceRepository.MaxViewRows));

        _workplaces.SetPanel(panel.Id, null, 5, viewRows: 17);
        var copy = _workplaces.Duplicate(w, "Height copy");
        Assert.That(_workplaces.Get(copy)!.Panels[0].ViewRows, Is.EqualTo(17), "a duplicate keeps the number");
        _workplaces.SetPanel(panel.Id, null, 5, viewRows: 0);
        var copy0 = _workplaces.Duplicate(w, "Height copy 0");
        Assert.That(_workplaces.Get(copy0)!.Panels[0].ViewRows, Is.EqualTo(0), "and a 0 stays a 0 (it keeps following the setting)");
    }

    [Test]
    public async Task ThePageCarriesTheDefaultRowsFromSettings()
    {
        var options = _factory.Services.GetRequiredService<Dynamo.Modules.Settings.ResultOptions>();
        var w = NewWorkplace("Default rows page");
        _workplaces.AddCopies(w, [NewQuery("D", "select 1 from dual")]);
        try
        {
            options.SetDefaultRows(7);
            var page = await _client.GetStringAsync($"/Workplaces/{w}");
            Assert.That(page, Does.Contain("data-view-rows-default=\"7\"").And.Contain("(0 = the default, 7)"));
        }
        finally
        {
            options.SetDefaultRows(Dynamo.Modules.Settings.ResultOptions.FactoryDefaultRows);
        }
    }

    [Test]
    public async Task APanelsResult_HasARowNumberColumn_AndThePanelPageHasTheHeightInput()
    {
        var w = NewWorkplace("Numbers");
        _workplaces.AddCopies(w, [NewQuery("N", "select id, name from t", connection: "PROD")]);
        var panel = _workplaces.Get(w)!.Panels[0];

        var inPanel = await (await Post($"/Workplaces/{w}?handler=RunPanel&panel={panel.Id}", ("connection", "")))
            .Content.ReadAsStringAsync();

        Assert.That(inPanel, Does.Contain("<th class=\"rownum\""));
        Assert.That(System.Text.RegularExpressions.Regex.Matches(inPanel, "<td class=\"rownum\"></td>").Count,
            Is.EqualTo(2), "one number cell per row (the numbers themselves are a CSS counter)");
        Assert.That(await _client.GetStringAsync($"/Workplaces/{w}"),
            Does.Match("data-view-rows-for=\"#panel-out-" + panel.Id + "\"").And.Match("name=\"viewRows\"[^>]*value=\"0\""));
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
    public async Task EachPanelHasEarlierAndLaterButtons_DisabledAtTheEnds_AndNothingIsHtml5Draggable()
    {
        var w = NewWorkplace("Arrow markup");
        _workplaces.AddCopies(w, [NewQuery("A1", "select 1 from dual"), NewQuery("A2", "select 2 from dual"), NewQuery("A3", "select 3 from dual")]);
        var html = await _client.GetStringAsync($"/Workplaces/{w}");

        Assert.That(Regex.Matches(html, "Move this panel earlier in the order").Count, Is.EqualTo(3));
        Assert.That(Regex.Matches(html, "Move this panel later in the order").Count, Is.EqualTo(3));
        // The first panel cannot go earlier, the last cannot go later.
        Assert.That(Regex.Matches(html, "title=\"Move this panel earlier in the order\"[^>]*disabled").Count, Is.EqualTo(1));
        Assert.That(Regex.Matches(html, "title=\"Move this panel later in the order\"[^>]*disabled").Count, Is.EqualTo(1));
        // The order is a sequence, not a position on the screen: the buttons say earlier/later, not left/right.
        Assert.That(html, Does.Not.Contain(">←</button>").And.Not.Contain(">→</button>"));
        Assert.That(html, Does.Not.Contain("draggable=\"true\""), "dragging is done with pointer events, from a handle");
    }

    [Test]
    public async Task EveryPanelHasADragHandle_AndThePageKnowsWhereToSaveTheOrder()
    {
        var w = NewWorkplace("Handles");
        _workplaces.AddCopies(w, [NewQuery("H1", "select 1 from dual"), NewQuery("H2", "select 2 from dual")]);

        var html = await _client.GetStringAsync($"/Workplaces/{w}");

        Assert.That(Regex.Matches(html, "data-panel-grip").Count, Is.EqualTo(2));
        Assert.That(html, Does.Contain($"data-reorder-url=\"/Workplaces/{w}?handler=ReorderPanels\""));
        Assert.That(html, Does.Contain("data-move=\"-1\"").And.Contain("data-move=\"1\""));
    }

    [Test]
    public async Task ADraggedOrder_IsSaved_AndShownOnTheNextVisit()
    {
        var w = NewWorkplace("Dragged");
        _workplaces.AddCopies(w, [NewQuery("D1", "select 1 from dual"), NewQuery("D2", "select 2 from dual"),
            NewQuery("D3", "select 3 from dual"), NewQuery("D4", "select 4 from dual")]);
        var ids = _workplaces.Get(w)!.Panels.Select(p => p.Id).ToList();

        // D3 dragged to the top, D1 to the end.
        var order = new[] { ids[2], ids[1], ids[3], ids[0] };
        var response = await Post($"/Workplaces/{w}?handler=ReorderPanels", ("order", string.Join(",", order)));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NoContent));
        Assert.That(_workplaces.Get(w)!.Panels.Select(p => p.QueryName), Is.EqualTo(new[] { "D3", "D2", "D4", "D1" }));
        Assert.That(_workplaces.Get(w)!.Panels.Select(p => p.Position), Is.EqualTo(new long[] { 1, 2, 3, 4 }));
        // (By the panels' own ids: a name such as "D3" can also appear by chance in a hashed file address.)
        var html = await _client.GetStringAsync($"/Workplaces/{w}");
        int At(long id) => html.IndexOf($"data-panel=\"{id}\"", StringComparison.Ordinal);
        Assert.That(new[] { At(ids[2]), At(ids[1]), At(ids[3]), At(ids[0]) }, Is.Ordered.Ascending);
        Assert.That(At(ids[2]), Is.GreaterThan(0));
    }

    [Test]
    public async Task AnOrderThatIsNotExactlyTheWorkspacesPanels_ChangesNothing()
    {
        var w = NewWorkplace("Stale order");
        var other = NewWorkplace("Stale other");
        _workplaces.AddCopies(w, [NewQuery("S1", "select 1 from dual"), NewQuery("S2", "select 2 from dual"), NewQuery("S3", "select 3 from dual")]);
        _workplaces.AddCopies(other, [NewQuery("Foreign", "select 4 from dual")]);
        var ids = _workplaces.Get(w)!.Panels.Select(p => p.Id).ToList();
        var foreign = _workplaces.Get(other)!.Panels[0].Id;
        string Names() => string.Join(",", _workplaces.Get(w)!.Panels.Select(p => p.QueryName));

        async Task<HttpStatusCode> Try(string order) =>
            (await Post($"/Workplaces/{w}?handler=ReorderPanels", ("order", order))).StatusCode;

        Assert.That(await Try($"{ids[2]},{ids[1]}"), Is.EqualTo(HttpStatusCode.Conflict), "a panel is missing (the page is out of date)");
        Assert.That(await Try($"{ids[2]},{ids[1]},{ids[0]},{foreign}"), Is.EqualTo(HttpStatusCode.Conflict), "a panel of another workspace");
        Assert.That(await Try($"{ids[2]},{ids[2]},{ids[0]}"), Is.EqualTo(HttpStatusCode.Conflict), "a panel twice");
        Assert.That(await Try($"{ids[2]},{ids[1]},{foreign}"), Is.EqualTo(HttpStatusCode.Conflict), "the right number but a foreign id");
        Assert.That(await Try(""), Is.EqualTo(HttpStatusCode.Conflict), "nothing");
        Assert.That(await Try("a,b,c"), Is.EqualTo(HttpStatusCode.BadRequest), "not numbers");
        Assert.That(Names(), Is.EqualTo("S1,S2,S3"), "no refused order changed anything");
        Assert.That(_workplaces.Get(other)!.Panels.Single().Position, Is.EqualTo(1));

        Assert.That((await Post("/Workplaces/99999?handler=ReorderPanels", ("order", "1"))).StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
    }

    [Test]
    public async Task SavingAnOrder_NeedsTheAntiforgeryToken()
    {
        var w = NewWorkplace("Forgery");
        _workplaces.AddCopies(w, [NewQuery("F1", "select 1 from dual"), NewQuery("F2", "select 2 from dual")]);
        var ids = _workplaces.Get(w)!.Panels.Select(p => p.Id).Reverse().ToList();

        // The same request without the token header a page sends: refused, nothing changes.
        var response = await _client.PostAsync($"/Workplaces/{w}?handler=ReorderPanels",
            new FormUrlEncodedContent([KeyValuePair.Create("order", string.Join(",", ids))]));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        Assert.That(_workplaces.Get(w)!.Panels.Select(p => p.QueryName), Is.EqualTo(new[] { "F1", "F2" }));
    }

    [Test]
    public async Task APanelCanBeCollapsed_AndExpandedAgain_AndTheChoiceIsKept()
    {
        var w = NewWorkplace("Collapsing");
        _workplaces.AddCopies(w, [NewQuery("C1", "select 1 from dual"), NewQuery("C2", "select 2 from dual")]);
        var panels = _workplaces.Get(w)!.Panels;
        Assert.That(panels.Select(p => p.Collapsed), Is.EqualTo(new[] { false, false }), "panels start open");

        var collapsed = await Post($"/Workplaces/{w}?handler=CollapsePanel&panel={panels[0].Id}", ("collapsed", "true"));
        Assert.That(collapsed.StatusCode, Is.EqualTo(HttpStatusCode.NoContent));
        Assert.That(_workplaces.Get(w)!.Panels.Select(p => p.Collapsed), Is.EqualTo(new[] { true, false }), "only that one");

        // The page shows it collapsed, but the results area is still there: it keeps refreshing.
        var html = await _client.GetStringAsync($"/Workplaces/{w}");
        Assert.That(Regex.Matches(html, "class=\"group panel collapsed\"").Count, Is.EqualTo(1));
        Assert.That(html, Does.Contain($"id=\"panel-out-{panels[0].Id}\""), "a collapsed panel still has its results area");
        Assert.That(Regex.Matches(html, "aria-expanded=\"false\"[^>]*\\n?[^>]*title=\"Expand this panel\"").Count, Is.EqualTo(1));
        Assert.That(html, Does.Contain("data-panel-info").And.Contain("data-panel-title"));

        var expanded = await Post($"/Workplaces/{w}?handler=CollapsePanel&panel={panels[0].Id}", ("collapsed", "false"));
        Assert.That(expanded.StatusCode, Is.EqualTo(HttpStatusCode.NoContent));
        Assert.That(_workplaces.Get(w)!.Panels.Select(p => p.Collapsed), Is.EqualTo(new[] { false, false }));
    }

    [Test]
    public async Task CollapseAll_AndExpandAll_DoEveryPanelOfTheWorkspaceOnly()
    {
        var w = NewWorkplace("Collapse all");
        var other = NewWorkplace("Collapse other");
        _workplaces.AddCopies(w, [NewQuery("A", "select 1 from dual"), NewQuery("B", "select 2 from dual")]);
        _workplaces.AddCopies(other, [NewQuery("Z", "select 3 from dual")]);

        await Post($"/Workplaces/{w}?handler=CollapseAll", ("collapsed", "true"));
        Assert.That(_workplaces.Get(w)!.Panels.Select(p => p.Collapsed), Is.EqualTo(new[] { true, true }));
        Assert.That(_workplaces.Get(other)!.Panels.Single().Collapsed, Is.False, "another workspace is untouched");
        Assert.That(await _client.GetStringAsync($"/Workplaces/{w}"), Does.Contain("data-workplace-collapse-all"));

        await Post($"/Workplaces/{w}?handler=CollapseAll", ("collapsed", "false"));
        Assert.That(_workplaces.Get(w)!.Panels.Select(p => p.Collapsed), Is.EqualTo(new[] { false, false }));
        Assert.That((await Post("/Workplaces/99999?handler=CollapseAll", ("collapsed", "true"))).StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
        Assert.That(await _client.GetStringAsync($"/Workplaces/{NewWorkplace("Empty collapse")}"),
            Does.Not.Contain("data-workplace-collapse-all"), "nothing to collapse in an empty workspace");
    }

    [Test]
    public async Task APanelOfAnotherWorkspace_CannotBeCollapsedThroughThisOne_AndTheTokenIsRequired()
    {
        var a = NewWorkplace("Collapse A");
        var b = NewWorkplace("Collapse B");
        _workplaces.AddCopies(b, [NewQuery("Other", "select 1 from dual")]);
        var panel = _workplaces.Get(b)!.Panels[0];

        var wrong = await Post($"/Workplaces/{a}?handler=CollapsePanel&panel={panel.Id}", ("collapsed", "true"));
        Assert.That(wrong.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
        Assert.That(_workplaces.GetPanel(panel.Id)!.Collapsed, Is.False);

        var noToken = await _client.PostAsync($"/Workplaces/{b}?handler=CollapsePanel&panel={panel.Id}",
            new FormUrlEncodedContent([KeyValuePair.Create("collapsed", "true")]));
        Assert.That(noToken.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        Assert.That(_workplaces.GetPanel(panel.Id)!.Collapsed, Is.False);
    }

    [Test]
    public async Task ACollapsedPanel_StaysCollapsed_InADuplicateAndAnImport()
    {
        var a = NewWorkplace("Keep collapsed");
        var b = NewWorkplace("Import collapsed");
        _workplaces.AddCopies(a, [NewQuery("K1", "select 1 from dual"), NewQuery("K2", "select 2 from dual")]);
        var panels = _workplaces.Get(a)!.Panels;
        _workplaces.SetCollapsed(panels[0].Id, true);

        var copy = _workplaces.Duplicate(a, "Keep collapsed copy");
        Assert.That(_workplaces.Get(copy)!.Panels.Select(p => p.Collapsed), Is.EqualTo(new[] { true, false }));

        await Post($"/Workplaces/{b}?handler=Import", ("from", a.ToString()), ("panel", panels[0].Id.ToString()), ("panel", panels[1].Id.ToString()));
        Assert.That(_workplaces.Get(b)!.Panels.Select(p => p.Collapsed), Is.EqualTo(new[] { true, false }));
    }

    [Test]
    public async Task AnImportedPanel_IsIndependentOfItsOriginal_InBothDirections()
    {
        var a = NewWorkplace("Copy A");
        var b = NewWorkplace("Copy B");
        _workplaces.AddCopies(a, [NewQuery("Template", "select 1 from dual", "PROD")]);
        var original = _workplaces.Get(a)!.Panels[0];
        await Post($"/Workplaces/{b}?handler=Import", ("from", a.ToString()), ("panel", original.Id.ToString()));
        var copy = _workplaces.Get(b)!.Panels[0];

        // Edit the copy in B (renamed, new SQL): the original is untouched.
        var edited = await Post($"/Workplaces/{b}?handler=EditQuery&panel={copy.Id}",
            ("name", "Renamed in B"), ("sql", "select 2 from dual"));
        Assert.That(Header(edited, "HX-Refresh"), Is.EqualTo("true"));
        Assert.That(_workplaces.GetPanel(copy.Id)!.QueryName, Is.EqualTo("Renamed in B"));
        Assert.That(_workplaces.GetPanel(copy.Id)!.Sql, Is.EqualTo("select 2 from dual"));
        Assert.That(_workplaces.GetPanel(original.Id)!.QueryName, Is.EqualTo("Template"));
        Assert.That(_workplaces.GetPanel(original.Id)!.Sql, Is.EqualTo("select 1 from dual"));

        // Edit the original: the copy does not follow.
        _workplaces.SetPanelQuery(original.Id, "Template v2", "select 3 from dual");
        Assert.That(_workplaces.GetPanel(copy.Id)!.Sql, Is.EqualTo("select 2 from dual"));
        Assert.That(_workplaces.GetPanel(copy.Id)!.QueryName, Is.EqualTo("Renamed in B"));

        // Deleting the original's workspace leaves the copy alone.
        _workplaces.Delete(a);
        Assert.That(_workplaces.Get(b)!.Panels.Select(p => p.QueryName), Is.EqualTo(new[] { "Renamed in B" }));

        // The panel runs its own SQL.
        await Post($"/Workplaces/{b}?handler=RunPanel&panel={copy.Id}", ("connection", ""));
        Assert.That(_runner.LastSql, Is.EqualTo("select 2 from dual"));
    }

    [Test]
    public async Task AQueryCanBeWrittenInTheWorkplace()
    {
        var w = NewWorkplace("From scratch");

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

        // It runs, and its bind is one of the workspace's shared inputs.
        await Post($"/Workplaces/{w}?handler=RunPanel&panel={panel.Id}", ("connection", ""), ("param.st", "OPEN"));
        Assert.That(_runner.LastSql, Is.EqualTo("select id from orders where st = :st"));
        Assert.That(_runner.LastValues["st"], Is.EqualTo("OPEN"));
        Assert.That(await _client.GetStringAsync($"/Workplaces/{w}"), Does.Contain("name=\"param.st\""));
    }

    [Test]
    public async Task ANewOrEditedQuery_MustBeASingleSelect_WithANameAndSql()
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

/// <summary>A query to make a panel from, in the tests.</summary>
internal sealed record TestQuery(string Name, string Sql, string Connection = "");

internal static class WorkplacePanelTestExtensions
{
    /// <summary>One new panel per query, at the end of the workspace.</summary>
    public static void AddCopies(this WorkplaceRepository repo, long workplaceId, IEnumerable<TestQuery> queries)
    {
        foreach (var q in queries)
            repo.AddNew(workplaceId, q.Name, q.Sql, q.Connection);
    }
}
