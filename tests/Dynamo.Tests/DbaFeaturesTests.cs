using System.Net;
using System.Text.RegularExpressions;
using Dynamo.Modules.OracleConnections;
using Dynamo.Modules.Queries;
using Dynamo.Modules.Workplaces;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Dynamo.Tests;

/// <summary>
/// The DBA features, in memory with fakes (no Oracle): the hard-coded templates, the sessions panel and
/// its Kill button, the execution plan. All of them only on DBA connections.
/// </summary>
public class DbaFeaturesTests
{
    private string _home = "";
    private string? _previousHome;
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;
    private string _token = "";
    private FakeRunner _runner = null!;
    private FakePlanner _planner = null!;
    private FakeKiller _killer = null!;

    private sealed class FakeRunner : IQueryRunner
    {
        public QueryResult Result = Sessions(("11", "42", "7"), ("11", "43", "8"));
        public string? LastConnection;

        public Task<QueryResult> RunAsync(
            string connectionName, string sql, IReadOnlyDictionary<string, string> values, int maxRows, CancellationToken ct)
        {
            LastConnection = connectionName;
            return Task.FromResult(Result);
        }
    }

    private sealed class FakePlanner : IPlanRunner
    {
        public string Text = "Plan hash value: 123\n| Id | Operation |";
        public Exception? Failure;
        public string? LastConnection, LastFormat, LastSql;
        public Dictionary<string, string> LastValues = [];

        public Task<string> ExplainAsync(
            string connectionName, string sql, IReadOnlyDictionary<string, string> values, string format, CancellationToken ct)
        {
            (LastConnection, LastFormat, LastSql) = (connectionName, format, sql);
            LastValues = new Dictionary<string, string>(values);
            return Failure is null ? Task.FromResult(Text) : Task.FromException<string>(Failure);
        }
    }

    private sealed class FakeKiller : ISessionKiller
    {
        public SessionInfo? Session = new(1, 42, 7, "SCOTT", "ACTIVE", "alain", "PC-01", "sqlplus.exe", "abc123", false, false);
        public KillOutcome Outcome = new(true, "Session 42,7 (SCOTT) was killed.");
        public List<string> Killed = [];
        public List<string> Described = [];

        public Task<SessionInfo?> DescribeAsync(string c, long instance, long sid, long serial, CancellationToken ct)
        {
            Described.Add($"{c}:{sid},{serial},@{instance}");
            return Task.FromResult(Session);
        }

        public Task<KillOutcome> KillAsync(string c, long instance, long sid, long serial, CancellationToken ct)
        {
            Killed.Add($"{c}:{sid},{serial},@{instance}");
            return Task.FromResult(Outcome);
        }
    }

    private static QueryResult Sessions(params (string Inst, string Sid, string Serial)[] rows) =>
        new([new ResultColumn("INST_ID", true), new ResultColumn("SID", true), new ResultColumn("SERIAL", true),
                new ResultColumn("USERNAME", false)],
            rows.Select(r => new string?[] { r.Inst, r.Sid, r.Serial, "SCOTT" }).ToList(), false, 5);

    [OneTimeSetUp]
    public void SetUp()
    {
        _previousHome = Environment.GetEnvironmentVariable("DYNAMO_HOME");
        _home = Path.Combine(Path.GetTempPath(), "dynamo-dbafeatures-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("DYNAMO_HOME", _home);
        _runner = new FakeRunner();
        _planner = new FakePlanner();
        _killer = new FakeKiller();
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b => b.ConfigureServices(s =>
        {
            s.AddSingleton<IQueryRunner>(_runner);
            s.AddSingleton<IPlanRunner>(_planner);
            s.AddSingleton<ISessionKiller>(_killer);
        }));
        _client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
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
    public void Reset()
    {
        _planner.Failure = null;
        _planner.LastConnection = null;
        _killer.Killed.Clear();
        _killer.Described.Clear();
        _killer.Session = new(1, 42, 7, "SCOTT", "ACTIVE", "alain", "PC-01", "sqlplus.exe", "abc123", false, false);
        _runner.Result = Sessions(("11", "42", "7"), ("11", "43", "8"));
    }

    private OracleConnectionRepository Connections => _factory.Services.GetRequiredService<OracleConnectionRepository>();
    private WorkplaceRepository Workplaces => _factory.Services.GetRequiredService<WorkplaceRepository>();

    private void Connection(string name, bool dba) =>
        Connections.Save(new OracleConnectionInfo
            { Name = name, DataSource = "h:1521/s", Username = dba ? "sys" : "scott", Password = "p", Dba = dba });

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

    private static string Header(HttpResponseMessage r, string name) =>
        r.Headers.TryGetValues(name, out var v) ? v.First() : "";

    private long NewWorkplace() => Workplaces.Create("DBA " + Guid.NewGuid().ToString("N")[..8]);

    // ---- The catalogue ----------------------------------------------------------------

    [Test]
    public void EveryTemplate_IsASingleSelect_TheRunnerAccepts_AndKeysAreUnique()
    {
        Assert.That(DbaTemplates.All.Select(t => t.Key), Is.Unique);
        Assert.That(DbaTemplates.All, Has.Count.GreaterThanOrEqualTo(15));
        foreach (var t in DbaTemplates.All)
        {
            var analysis = SqlScanner.Analyze(t.Sql);
            Assert.That(analysis.Error, Is.Null, $"{t.Key}: {analysis.Error}");
            Assert.That(analysis.Binds, Is.Empty, $"{t.Key} has bind variables: a template needs no input");
            Assert.That(t.Title, Is.Not.Empty);
            Assert.That(t.Description, Is.Not.Empty);
        }
        var sessions = DbaTemplates.Find("sessions")!;
        Assert.That(sessions.Kind, Is.EqualTo(PanelKinds.Sessions));
        Assert.That(sessions.Sql, Is.EqualTo(DbaTemplates.SessionsSql));
        Assert.That(DbaTemplates.All.Count(t => t.Kind == PanelKinds.Sessions), Is.EqualTo(1));
    }

    [Test]
    public void TheSessionsQuery_HasTheColumnsTheKillButtonNeeds()
    {
        var sql = DbaTemplates.SessionsSql.ToUpperInvariant();
        Assert.That(sql, Does.Contain("S.INST_ID").And.Contain("S.SID").And.Contain("S.SERIAL# AS SERIAL"));
    }

    // ---- Offered on DBA connections only ----------------------------------------------

    [Test]
    public async Task TheDbaButtons_AppearOnlyWhenADbaConnectionExists_AndTheDialogListsDbaConnectionsOnly()
    {
        var home = Path.Combine(Path.GetTempPath(), "dynamo-nodba-" + Guid.NewGuid().ToString("N"));
        var w = NewWorkplace();

        // The shared factory may already have DBA connections from other tests: use a fresh site.
        var previous = Environment.GetEnvironmentVariable("DYNAMO_HOME");
        Environment.SetEnvironmentVariable("DYNAMO_HOME", home);
        try
        {
            await using var fresh = new WebApplicationFactory<Program>();
            using var client = fresh.CreateClient();
            var repo = fresh.Services.GetRequiredService<OracleConnectionRepository>();
            var workplaces = fresh.Services.GetRequiredService<WorkplaceRepository>();
            var id = workplaces.Create("Plain");
            repo.Save(new OracleConnectionInfo { Name = "Plain DB", DataSource = "h:1/s", Username = "u", Password = "p" });

            Assert.That(await client.GetStringAsync($"/Workplaces/{id}"), Does.Not.Contain("Add DBA panels"));
            Assert.That(await client.GetStringAsync("/Workplaces"), Does.Not.Contain("New DBA workspace"));
            Assert.That(await client.GetStringAsync($"/Workplaces/{id}?handler=DbaForm"), Does.Contain("No DBA connection yet"));

            repo.Save(new OracleConnectionInfo { Name = "The DBA", DataSource = "h:1/s", Username = "sys", Password = "p", Dba = true });

            Assert.That(await client.GetStringAsync($"/Workplaces/{id}"), Does.Contain("Add DBA panels"));
            Assert.That(await client.GetStringAsync("/Workplaces"), Does.Contain("New DBA workspace"));
            var dialog = await client.GetStringAsync($"/Workplaces/{id}?handler=DbaForm");
            Assert.That(dialog, Does.Contain("value=\"The DBA\"").And.Not.Contain("value=\"Plain DB\""));
            Assert.That(dialog, Does.Contain("Blocking sessions").And.Contain("Tablespace usage").And.Contain("Active sessions (with Kill)"));
            Assert.That(dialog, Does.Not.Match("name=\"template\"[^>]*checked"), "nothing is ticked at first");
        }
        finally
        {
            Environment.SetEnvironmentVariable("DYNAMO_HOME", previous);
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { Directory.Delete(home, true); } catch { /* best effort */ }
        }
        Assert.That(w, Is.GreaterThan(0));
    }

    // ---- Adding templates ----------------------------------------------------------------

    [Test]
    public async Task AddingTemplates_MakesPanelsOnTheDbaConnection_InCatalogueOrder_WithTheirInterval()
    {
        Connection("Add DBA", true);
        var w = NewWorkplace();
        Workplaces.AddNew(w, "Mine", "select 1 from dual", "");

        var answer = await Post($"/Workplaces/{w}?handler=AddDba", ("connection", "Add DBA"),
            ("template", "tablespaces"), ("template", "sessions"), ("template", "nonsense"));

        Assert.That(Header(answer, "HX-Refresh"), Is.EqualTo("true"));
        var panels = Workplaces.Get(w)!.Panels;
        Assert.That(panels.Select(p => p.QueryName), Is.EqualTo(new[] { "Mine", "Active sessions (with Kill)", "Tablespace usage" }),
            "after the existing panel, in catalogue order; the unknown key is ignored");
        Assert.That(panels.Skip(1).Select(p => p.Connection), Is.All.EqualTo("Add DBA"));
        Assert.That(panels[1].Kind, Is.EqualTo(PanelKinds.Sessions));
        Assert.That(panels[1].RefreshSeconds, Is.EqualTo(10));
        Assert.That(panels[2].Kind, Is.EqualTo(PanelKinds.Query));
        Assert.That(panels[2].Sql, Does.Contain("dba_tablespace_usage_metrics"));
        Assert.That(panels[2].ViewRows, Is.EqualTo(0), "follows the default number of rows");
    }

    [Test]
    public async Task AddingTemplates_IsRefused_WithoutADbaConnection_OrWithoutATemplate()
    {
        Connection("Plain two", false);
        Connection("Dba two", true);
        var w = NewWorkplace();

        var plain = await Post($"/Workplaces/{w}?handler=AddDba", ("connection", "Plain two"), ("template", "blocking"));
        Assert.That(await plain.Content.ReadAsStringAsync(), Does.Contain("Choose a DBA connection"));
        Assert.That(Header(plain, "HX-Retarget"), Is.EqualTo("#dlg"));

        var none = await Post($"/Workplaces/{w}?handler=AddDba", ("connection", "Dba two"));
        Assert.That(await none.Content.ReadAsStringAsync(), Does.Contain("Tick at least one template"));

        var unknown = await Post($"/Workplaces/{w}?handler=AddDba", ("connection", "Dba two"), ("template", "nonsense"));
        Assert.That(await unknown.Content.ReadAsStringAsync(), Does.Contain("Tick at least one template"));

        var missing = await Post($"/Workplaces/{w}?handler=AddDba", ("connection", "Nobody"), ("template", "blocking"));
        Assert.That(await missing.Content.ReadAsStringAsync(), Does.Contain("Choose a DBA connection"));

        Assert.That(Workplaces.Get(w)!.Panels, Is.Empty, "nothing was added");
    }

    [Test]
    public async Task ANewDbaWorkspace_IsCreatedWithItsPanels_AndOpened()
    {
        Connection("Create DBA", true);

        var answer = await Post("/Workplaces?handler=CreateDba", ("name", "DBA PROD"), ("connection", "Create DBA"),
            ("template", "blocking"), ("template", "waits"));

        Assert.That(Header(answer, "HX-Redirect"), Does.Match(@"^/Workplaces/\d+$"));
        var created = Workplaces.GetAll().Single(x => x.Name == "DBA PROD");
        Assert.That(Workplaces.Get(created.Id)!.Panels.Select(p => p.QueryName), Is.EqualTo(new[] { "Blocking sessions", "Wait events" }));

        var duplicate = await Post("/Workplaces?handler=CreateDba", ("name", "dba prod"), ("connection", "Create DBA"), ("template", "waits"));
        Assert.That(await duplicate.Content.ReadAsStringAsync(), Does.Contain("already exists"));
        var empty = await Post("/Workplaces?handler=CreateDba", ("name", "  "), ("connection", "Create DBA"), ("template", "waits"));
        Assert.That(await empty.Content.ReadAsStringAsync(), Does.Contain("required"));
        var plain = await Post("/Workplaces?handler=CreateDba", ("name", "Another"), ("connection", "Nobody"), ("template", "waits"));
        Assert.That(await plain.Content.ReadAsStringAsync(), Does.Contain("Choose a DBA connection"));
        Assert.That(Workplaces.GetAll().Select(x => x.Name), Does.Not.Contain("Another"));
    }

    [Test]
    public void ADuplicate_AndAnImport_KeepTheKindOfAPanel()
    {
        Connection("Kind DBA", true);
        var w = NewWorkplace();
        Workplaces.AddTemplates(w, "Kind DBA", ["sessions", "blocking"]);

        var copy = Workplaces.Duplicate(w, "Kind copy " + Guid.NewGuid().ToString("N")[..6]);
        Assert.That(Workplaces.Get(copy)!.Panels.Select(p => p.Kind), Is.EqualTo(new[] { PanelKinds.Sessions, PanelKinds.Query }));

        var target = NewWorkplace();
        Workplaces.ImportPanels(target, w, Workplaces.Get(w)!.Panels.Select(p => p.Id));
        Assert.That(Workplaces.Get(target)!.Panels.Select(p => p.Kind), Is.EqualTo(new[] { PanelKinds.Sessions, PanelKinds.Query }));
    }

    // ---- The sessions panel and its Kill button --------------------------------------------

    private async Task<(long Workplace, long Panel)> SessionsPanel(string connection)
    {
        var w = NewWorkplace();
        Workplaces.AddTemplates(w, connection, ["sessions"]);
        await Task.CompletedTask;
        return (w, Workplaces.Get(w)!.Panels.Single().Id);
    }

    private async Task<string> Run(long w, long panel)
    {
        var answer = await Post($"/Workplaces/{w}?handler=RunPanel&panel={panel}");
        return await answer.Content.ReadAsStringAsync();
    }

    [Test]
    public async Task TheSessionsPanel_OnADbaConnection_ShowsARedKillButtonPerSession()
    {
        Connection("Kill DBA", true);
        var (w, panel) = await SessionsPanel("Kill DBA");

        var html = await Run(w, panel);

        Assert.That(Regex.Matches(html, "class=\"kill-btn small\"").Count, Is.EqualTo(2));
        Assert.That(html, Does.Contain($"panel={panel}&amp;handler=KillForm&amp;inst=11&amp;sid=42&amp;serial=7"));
        Assert.That(html, Does.Contain("class=\"kill-col\""));
        Assert.That(_runner.LastConnection, Is.EqualTo("Kill DBA"));
    }

    [Test]
    public async Task TheSessionsPanel_OnAConnectionThatIsNotDba_RefusesToRun_AndOffersNoKill()
    {
        Connection("Kill plain", false);
        var (w, panel) = await SessionsPanel("Kill plain");

        var html = await Run(w, panel);

        Assert.That(html, Does.Contain("needs a DBA connection").And.Contain("Kill plain"));
        Assert.That(html, Does.Not.Contain("kill-btn"));
    }

    [Test]
    public async Task AnOrdinaryPanel_NeverGetsAKillButton_EvenWithTheSameColumns()
    {
        Connection("Ordinary DBA", true);
        var w = NewWorkplace();
        var panel = Workplaces.AddNew(w, "Looks like sessions", "select inst_id, sid, serial# serial from gv$session", "Ordinary DBA");

        Assert.That(await Run(w, panel), Does.Not.Contain("kill-btn"));
    }

    [Test]
    public async Task TheKillDialog_DescribesTheSession_AndKillsNothing()
    {
        Connection("Dialog DBA", true);
        var (w, panel) = await SessionsPanel("Dialog DBA");

        var html = await _client.GetStringAsync($"/Workplaces/{w}?handler=KillForm&panel={panel}&inst=1&sid=42&serial=7");

        Assert.That(html, Does.Contain("Kill session 42,7?").And.Contain("SCOTT").And.Contain("PC-01").And.Contain("sqlplus.exe"));
        Assert.That(html, Does.Contain("class=\"kill-btn\"").And.Contain("handler=KillSession"));
        Assert.That(_killer.Described, Is.EqualTo(new[] { "Dialog DBA:42,7,@1" }));
        Assert.That(_killer.Killed, Is.Empty, "opening the dialog kills nothing");
    }

    [Test]
    public async Task TheKillDialog_RefusesAGoneSession_Dynamos_OwnSession_AndABackgroundProcess()
    {
        Connection("Refuse DBA", true);
        var (w, panel) = await SessionsPanel("Refuse DBA");
        string Url() => $"/Workplaces/{w}?handler=KillForm&panel={panel}&inst=1&sid=42&serial=7";

        _killer.Session = null;
        var gone = await _client.GetAsync(Url());
        Assert.That(Header(gone, "HX-Trigger"), Does.Contain("no longer exists"));

        _killer.Session = new(1, 42, 7, "SCOTT", "ACTIVE", null, null, null, null, false, true);
        Assert.That(Header(await _client.GetAsync(Url()), "HX-Trigger"), Does.Contain("own session"));

        _killer.Session = new(1, 42, 7, null, "ACTIVE", null, null, null, null, true, false);
        Assert.That(Header(await _client.GetAsync(Url()), "HX-Trigger"), Does.Contain("background process"));
    }

    [Test]
    public async Task Killing_NeedsASessionsPanel_OnADbaConnection_Whatever_IsPosted()
    {
        Connection("Guard DBA", true);
        Connection("Guard plain", false);
        var (w, panel) = await SessionsPanel("Guard DBA");

        // An ordinary panel of the same workspace: not a sessions panel.
        var ordinary = Workplaces.AddNew(w, "Ordinary", "select 1 from dual", "Guard DBA");
        var notSessions = await Post($"/Workplaces/{w}?handler=KillSession&panel={ordinary}&inst=1&sid=42&serial=7");
        Assert.That(Header(notSessions, "HX-Trigger"), Does.Contain("not a sessions panel"));

        // A sessions panel whose connection was made non-DBA behind the page's back.
        var (w2, panel2) = await SessionsPanel("Guard plain");
        var onPlain = await Post($"/Workplaces/{w2}?handler=KillSession&panel={panel2}&inst=1&sid=42&serial=7");
        Assert.That(Header(onPlain, "HX-Trigger"), Does.Contain("needs a DBA connection"));

        // A panel of another workspace, or one that does not exist.
        var foreign = await Post($"/Workplaces/{w2}?handler=KillSession&panel={panel}&inst=1&sid=42&serial=7");
        Assert.That(Header(foreign, "HX-Trigger"), Does.Contain("not a sessions panel"));
        var missing = await Post($"/Workplaces/{w}?handler=KillSession&panel=999999&inst=1&sid=42&serial=7");
        Assert.That(Header(missing, "HX-Trigger"), Does.Contain("not a sessions panel"));

        Assert.That(_killer.Killed, Is.Empty, "nothing reached the killer");
        Assert.That(panel, Is.GreaterThan(0));
    }

    [Test]
    public async Task Killing_AConfirmedSession_CallsTheKiller_ClosesTheDialog_AndRunsThePanelsAgain()
    {
        Connection("Go DBA", true);
        var (w, panel) = await SessionsPanel("Go DBA");

        var answer = await Post($"/Workplaces/{w}?handler=KillSession&panel={panel}&inst=3&sid=42&serial=7");

        Assert.That(_killer.Killed, Is.EqualTo(new[] { "Go DBA:42,7,@3" }));
        var trigger = Header(answer, "HX-Trigger");
        Assert.That(trigger, Does.Contain("closeDialog").And.Contain("runAll").And.Contain("was killed"));
    }

    [Test]
    public async Task AFailedKill_IsReportedInTheToast()
    {
        Connection("Fail DBA", true);
        var (w, panel) = await SessionsPanel("Fail DBA");
        _killer.Outcome = new KillOutcome(false, "ORA-01031: insufficient privileges");

        var answer = await Post($"/Workplaces/{w}?handler=KillSession&panel={panel}&inst=1&sid=42&serial=7");

        Assert.That(Header(answer, "HX-Trigger"), Does.Contain("insufficient privileges"));
        _killer.Outcome = new KillOutcome(true, "Session 42,7 (SCOTT) was killed.");
    }

    [Test]
    public async Task TheSessionsPanel_OnlyOffersDbaConnections_CannotBeEdited_AndCannotMoveToANonDbaConnection()
    {
        Connection("Offer DBA", true);
        Connection("Offer plain", false);
        var (w, panel) = await SessionsPanel("Offer DBA");

        var page = await _client.GetStringAsync($"/Workplaces/{w}");
        var select = Regex.Match(page, $"data-panel=\"{panel}\".*?<select name=\"connection\".*?</select>", RegexOptions.Singleline).Value;
        Assert.That(select, Does.Contain("Offer DBA").And.Not.Contain("Offer plain"));
        Assert.That(page, Does.Not.Contain($"panel={panel}&amp;handler=EditQueryForm"));
        Assert.That(page, Does.Not.Contain($"panel={panel}&amp;handler=Plan"));

        var edit = await _client.GetAsync($"/Workplaces/{w}?handler=EditQueryForm&panel={panel}");
        Assert.That(edit.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));

        var move = await Post($"/Workplaces/{w}?handler=PanelSettings&panel={panel}", ("connection", "Offer plain"), ("seconds", "10"));
        Assert.That(move.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        Assert.That(Workplaces.GetPanel(panel)!.Connection, Is.EqualTo("Offer DBA"));
    }

    // ---- Result viewing --------------------------------------------------------------------

    [Test]
    public async Task EveryPanel_HasTheResultToolsButton_AndALongCellIsMarkedForThePopup()
    {
        var w = NewWorkplace();
        var panel = Workplaces.AddNew(w, "Long text", "select 1 from dual", "");
        var page = await _client.GetStringAsync($"/Workplaces/{w}");
        Assert.That(Regex.Matches(page, "data-result-tools").Count, Is.EqualTo(1));
        Assert.That(page, Does.Contain("aria-label=\"Result tools of Long text\""));

        Connection("Long conn", false);
        Workplaces.SetPanel(panel, "Long conn", 0);
        var longText = new string('x', 130);
        _runner.Result = new QueryResult([new ResultColumn("SIZE_BYTES", true), new ResultColumn("NOTE", false)],
            [["1536", longText], ["2", "short"]], false, 3);

        var html = await Run(w, panel);

        Assert.That(html, Does.Contain("title=\"" + longText));
        Assert.That(Regex.Matches(html, "class=\"[^\"]* long\"").Count, Is.EqualTo(1), "only the cell over 120 characters");
        Assert.That(html, Does.Contain("<th class=\"num\""), "the numeric header the byte-size toggle looks for");
    }

    // ---- The execution plan ------------------------------------------------------------------

    [Test]
    public async Task ThePlanButton_ShowsThePlanText_ForTheConnectionAndBindsOfThePanel()
    {
        Connection("Plan conn", false);
        var w = NewWorkplace();
        var panel = Workplaces.AddNew(w, "Orders", "select * from orders where id = :id", "Plan conn");

        var page = await _client.GetStringAsync($"/Workplaces/{w}");
        Assert.That(page, Does.Contain($"panel={panel}&amp;handler=Plan"));

        var answer = await Post($"/Workplaces/{w}?handler=Plan&panel={panel}", ("connection", "Plan conn"), ("format", "ALL"), ("param.id", "5"));
        var html = await answer.Content.ReadAsStringAsync();

        Assert.That(html, Does.Contain("class=\"plan-text\"").And.Contain("Plan hash value: 123"));
        Assert.That(html, Does.Contain("Execution plan: Orders").And.Contain("data-copy-target=\"#plan-text\""));
        Assert.That(Regex.IsMatch(html, "<option value=\"ALL\" selected"), Is.True);
        Assert.That((_planner.LastConnection, _planner.LastFormat), Is.EqualTo(("Plan conn", "ALL")));
        Assert.That(_planner.LastSql, Is.EqualTo("select * from orders where id = :id"));
        Assert.That(_planner.LastValues["id"], Is.EqualTo("5"));
    }

    [Test]
    public async Task ThePlan_UsesTheConnectionOfThePanelWhenNoneIsPosted_AndAnUnknownFormatBecomesTypical()
    {
        Connection("Plan default", false);
        var w = NewWorkplace();
        var panel = Workplaces.AddNew(w, "P", "select 1 from dual", "Plan default");

        var html = await (await Post($"/Workplaces/{w}?handler=Plan&panel={panel}", ("format", "'; drop table x; --"))).Content.ReadAsStringAsync();

        Assert.That(_planner.LastConnection, Is.EqualTo("Plan default"));
        Assert.That(_planner.LastFormat, Is.EqualTo("TYPICAL"));
        Assert.That(html, Does.Contain("plan-text"));
        Assert.That(PlanRunner.NormalizeFormat("basic"), Is.EqualTo("BASIC"));
    }

    [Test]
    public async Task APlanThatFails_ShowsTheReason_AndNoConnectionIsAMessage()
    {
        Connection("Plan fail", false);
        var w = NewWorkplace();
        var panel = Workplaces.AddNew(w, "P", "select 1 from dual", "Plan fail");
        var none = Workplaces.AddNew(w, "NoConn", "select 1 from dual", "");

        _planner.Failure = new QueryException("PLAN_TABLE is not there");
        var failed = await (await Post($"/Workplaces/{w}?handler=Plan&panel={panel}")).Content.ReadAsStringAsync();
        Assert.That(failed, Does.Contain("PLAN_TABLE is not there").And.Not.Contain("class=\"plan-text\""));

        _planner.Failure = null;
        var noConnection = await (await Post($"/Workplaces/{w}?handler=Plan&panel={none}")).Content.ReadAsStringAsync();
        Assert.That(noConnection, Does.Contain("No connection chosen"));
    }

    [Test]
    public async Task APlanIsOnlyForPanelsOfTheWorkspace()
    {
        var w = NewWorkplace();
        var other = NewWorkplace();
        var panel = Workplaces.AddNew(other, "Elsewhere", "select 1 from dual", "");

        var answer = await Post($"/Workplaces/{w}?handler=Plan&panel={panel}");

        Assert.That(answer.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
    }
}
