using System.Text.RegularExpressions;
using Dynamo.Modules.OracleConnections;
using Dynamo.Modules.Workplaces;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Dynamo.Tests;

/// <summary>The DBA flag (log in AS SYSDBA) and the pastel colour of a connection.</summary>
public class DbaConnectionTests
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
        _home = Path.Combine(Path.GetTempPath(), "dynamo-dba-" + Guid.NewGuid().ToString("N"));
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
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_home, true); } catch { /* best effort */ }
    }

    private OracleConnectionRepository Repo => _factory.Services.GetRequiredService<OracleConnectionRepository>();

    private async Task<string> Post(string url, params (string Key, string Value)[] fields)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new FormUrlEncodedContent(fields.Select(f => KeyValuePair.Create(f.Key, f.Value))),
        };
        request.Headers.Add("RequestVerificationToken", _token);
        request.Headers.Add("HX-Request", "true");
        return await (await _client.SendAsync(request)).Content.ReadAsStringAsync();
    }

    private Task<string> Save(string name, string? dba, string? color, long id = 0)
    {
        var fields = new List<(string, string)>
        {
            ("id", id.ToString()), ("name", name), ("dataSource", "h:1521/s"), ("username", "sys"),
            ("password", "p"), ("copyFrom", "0"),
        };
        if (dba is not null) fields.Add(("dba", dba));
        if (color is not null) fields.Add(("color", color));
        return Post("/Oracle?handler=Save", fields.ToArray());
    }

    [Test]
    public async Task ADbaConnectionAndItsColour_AreSaved_AndShownInTheList()
    {
        var list = await Save("Prod DBA", "on", "rose");

        var saved = Repo.GetByName("Prod DBA")!;
        Assert.That(saved.Dba, Is.True);
        Assert.That(saved.Color, Is.EqualTo("rose"));
        Assert.That(list, Does.Contain("badge-dba").And.Contain("conn-dot conn-rose"));

        var form = await _client.GetStringAsync($"/Oracle?handler=Form&id={saved.Id}");
        Assert.That(form, Does.Match("name=\"dba\"[^>]*checked"));
        Assert.That(form, Does.Match("name=\"color\" value=\"rose\"[^>]*checked"));
    }

    [Test]
    public async Task ByDefaultAConnectionIsNotDba_AndHasNoColour_AndAnUnknownColourIsDropped()
    {
        await Save("Plain", null, null);
        await Save("Odd colour", null, "hotpink");

        Assert.That(Repo.GetByName("Plain")!.Dba, Is.False);
        Assert.That(Repo.GetByName("Plain")!.Color, Is.Null);
        Assert.That(Repo.GetByName("Odd colour")!.Color, Is.Null, "only the six pastels exist");
        Assert.That(ConnectionColors.All.Select(c => c.Key), Is.EqualTo(new[] { "blue", "teal", "green", "sand", "rose", "violet" }));
    }

    [Test]
    public async Task EditingAConnection_CanTurnTheDbaFlagAndTheColourOff()
    {
        await Save("Flip", "on", "blue");
        var id = Repo.GetByName("Flip")!.Id;

        await Save("Flip", null, "", id);

        var after = Repo.GetByName("Flip")!;
        Assert.That(after.Dba, Is.False);
        Assert.That(after.Color, Is.Null);
    }

    [Test]
    public async Task ADuplicateStartsWithTheSameFlagAndColour()
    {
        await Save("Original", "on", "teal");
        var id = Repo.GetByName("Original")!.Id;

        var form = await _client.GetStringAsync($"/Oracle?handler=Duplicate&id={id}");

        Assert.That(form, Does.Match("name=\"dba\"[^>]*checked"));
        Assert.That(form, Does.Match("name=\"color\" value=\"teal\"[^>]*checked"));
    }

    [Test]
    public void ADbaConnection_LogsInAsSysdba_AndAnOrdinaryOneDoesNot()
    {
        var dba = OracleConnector.ConnectionStringOf(new OracleConnectionInfo
            { Name = "a", DataSource = "h:1521/s", Username = "sys", Password = "p", Dba = true });
        var plain = OracleConnector.ConnectionStringOf(new OracleConnectionInfo
            { Name = "b", DataSource = "h:1521/s", Username = "scott", Password = "p" });

        Assert.That(dba, Does.Contain("DBA PRIVILEGE=SYSDBA").IgnoreCase);
        Assert.That(plain, Does.Not.Contain("DBA PRIVILEGE").IgnoreCase);
    }

    [Test]
    public async Task TheConnector_TellsWhichConnectionsAreDba_AndPickersCarryTheColour()
    {
        await Save("Is DBA", "on", "green");
        await Save("Not DBA", null, "sand");
        var connector = _factory.Services.GetRequiredService<OracleConnector>();

        Assert.That(connector.IsDba("Is DBA"), Is.True);
        Assert.That(connector.IsDba("Not DBA"), Is.False);
        Assert.That(connector.IsDba("Nobody"), Is.False);
        Assert.That(connector.Choices().Single(c => c.Name == "Is DBA"), Is.EqualTo(new ConnectionChoice("Is DBA", false, true, "green")));
    }

    [Test]
    public async Task APanelOnAColouredConnection_HasItsTitleBarColour()
    {
        await Save("Coloured", null, "violet");
        await Save("Colourless", null, null);
        var workplaces = _factory.Services.GetRequiredService<WorkplaceRepository>();
        var w = workplaces.Create("Colours " + Guid.NewGuid().ToString("N")[..6]);
        var a = workplaces.AddNew(w, "On coloured", "select 1 from dual", "Coloured");
        var b = workplaces.AddNew(w, "On colourless", "select 1 from dual", "Colourless");

        var page = await _client.GetStringAsync($"/Workplaces/{w}");

        Assert.That(Regex.IsMatch(page, $"class=\"group panel [^\"]*conn-violet[^\"]*\" data-panel=\"{a}\""), Is.True);
        Assert.That(Regex.IsMatch(page, $"class=\"group panel [^\"]*conn-[^\"]*\" data-panel=\"{b}\""), Is.False);
        Assert.That(page, Does.Contain("data-color=\"violet\""), "the picker carries it for the live change");
    }
}
