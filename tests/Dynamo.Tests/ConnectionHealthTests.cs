using System.Text.RegularExpressions;
using Dynamo.Modules.OracleConnections;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Dynamo.Tests;

/// <summary>The connection health page: its rows, the probe of a connection that cannot log in, the links.</summary>
public class ConnectionHealthTests
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
        _home = Path.Combine(Path.GetTempPath(), "dynamo-health-" + Guid.NewGuid().ToString("N"));
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

    private async Task<string> Probe(long id)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/Oracle/Health?handler=Probe&id={id}");
        request.Headers.Add("RequestVerificationToken", _token);
        request.Headers.Add("HX-Request", "true");
        return await (await _client.SendAsync(request)).Content.ReadAsStringAsync();
    }

    [Test]
    public async Task ThePage_ListsEveryConnection_EachRowAsksForItsOwnProbeWhenItLoads()
    {
        var a = Repo.Save(new OracleConnectionInfo { Name = "Health A", DataSource = "h:1/s", Username = "u", Password = "p", Color = "blue" });
        var b = Repo.Save(new OracleConnectionInfo { Name = "Health B", DataSource = "h:1/s", Username = "sys", Password = "p", Dba = true });

        var page = await _client.GetStringAsync("/Oracle/Health");

        Assert.That(page, Does.Contain("Health A").And.Contain("Health B").And.Contain("Test all again"));
        foreach (var id in new[] { a, b })
            Assert.That(page, Does.Contain($"id=\"health-{id}\"").And.Contain($"id={id}&amp;handler=Probe"));
        Assert.That(Regex.Matches(page, "hx-trigger=\"load, retest from:body\"").Count, Is.GreaterThanOrEqualTo(2),
            "an untested row asks for its probe as soon as it loads");
        Assert.That(page, Does.Contain("Testing…").And.Contain("conn-dot conn-blue").And.Contain("badge-dba"));
    }

    [Test]
    public async Task AConnectionNotReadyYet_IsReported_WithoutTryingToConnect()
    {
        var id = Repo.Save(new OracleConnectionInfo { Name = "Health imported", DataSource = "h:1/s", Username = "", Password = "" });

        var row = await Probe(id);

        Assert.That(row, Does.Contain("health-notready").And.Contain("Not ready").And.Contain("has no schema yet"));
        Assert.That(row, Does.Contain("hx-trigger=\"retest from:body\""), "an answered row does not ask again on load");
        Assert.That(row, Does.Not.Contain("load,"));
    }

    [Test]
    public async Task AConnectionThatCannotBeReached_IsDown_WithOraclesFirstLine_AndNeverThePassword()
    {
        // Nothing listens on this port: the login fails quickly and the row says why.
        var id = Repo.Save(new OracleConnectionInfo
            { Name = "Health down", DataSource = "127.0.0.1:1/NOSUCH", Username = "u", Password = "secret-pw-123" });

        var row = await Probe(id);

        Assert.That(row, Does.Contain("health-down").And.Contain(">Down<"));
        Assert.That(row, Does.Not.Contain("secret-pw-123"));
        Assert.That(row, Does.Contain("class=\"hint health-error\""));
    }

    [Test]
    public async Task AnUnknownConnection_IsNotFound()
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/Oracle/Health?handler=Probe&id=999999");
        request.Headers.Add("RequestVerificationToken", _token);
        Assert.That((await _client.SendAsync(request)).StatusCode, Is.EqualTo(System.Net.HttpStatusCode.NotFound));
    }

    [Test]
    public async Task TheConnectionsPage_LinksToIt()
    {
        Assert.That(await _client.GetStringAsync("/Oracle"), Does.Contain("href=\"/Oracle/Health\""));
    }

    [Test]
    public void AProbeConnectionIsNeverPooled_ButTheUsualOneIs()
    {
        var info = new OracleConnectionInfo { Name = "x", DataSource = "h:1/s", Username = "u", Password = "p" };

        Assert.That(OracleConnector.ConnectionStringOf(info, pooling: false), Does.Contain("Pooling=False").IgnoreCase);
        Assert.That(OracleConnector.ConnectionStringOf(info), Does.Not.Contain("Pooling=False").IgnoreCase);
    }
}
