using System.Text.RegularExpressions;
using Dynamo.Modules.OracleConnections;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Dynamo.Tests;

/// <summary>Pinned Oracle connections: order, the toggle and the pickers.</summary>
public class PinnedConnectionTests
{
    private string _home = "";
    private string? _previousHome;
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;
    private string _token = "";
    private OracleConnectionRepository _repo = null!;

    [OneTimeSetUp]
    public void SetUp()
    {
        _previousHome = Environment.GetEnvironmentVariable("DYNAMO_HOME");
        _home = Path.Combine(Path.GetTempPath(), "dynamo-pin-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("DYNAMO_HOME", _home);
        _factory = new WebApplicationFactory<Program>();
        _client = _factory.CreateClient();
        _token = Regex.Match(_client.GetStringAsync("/Oracle").Result,
            "RequestVerificationToken\": \"([^\"]+)\"").Groups[1].Value;
        _repo = _factory.Services.GetRequiredService<OracleConnectionRepository>();
    }

    /// <summary>Tests share one database: start each with nothing pinned.</summary>
    [SetUp]
    public void UnpinAll()
    {
        foreach (var c in _repo.GetAll().Where(c => c.Pinned))
            _repo.SetPinned(c.Id, false);
    }

    [OneTimeTearDown]
    public void TearDown()
    {
        _client.Dispose();
        _factory.Dispose();
        Environment.SetEnvironmentVariable("DYNAMO_HOME", _previousHome);
        try { Directory.Delete(_home, true); } catch { /* best effort */ }
    }

    private long Add(string name) =>
        _repo.Save(new OracleConnectionInfo { Name = name, DataSource = "h:1/s", Username = "U", Password = "p" });

    private async Task<string> Post(string url, params (string, string)[] fields)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new FormUrlEncodedContent(fields.Select(f => KeyValuePair.Create(f.Item1, f.Item2))),
        };
        request.Headers.Add("RequestVerificationToken", _token);
        request.Headers.Add("HX-Request", "true");
        var response = await _client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync();
    }

    [Test]
    public async Task Pinning_PutsAConnectionFirst_MarksItAndCanBeUndone()
    {
        Add("PIN_A"); Add("PIN_B"); var c = Add("PIN_C");

        var html = await Post($"/Oracle?handler=TogglePin&id={c}");
        Assert.That(_repo.GetAll().Select(x => x.Name).First(), Is.EqualTo("PIN_C"));
        Assert.That(html.IndexOf("PIN_C", StringComparison.Ordinal),
            Is.LessThan(html.IndexOf("PIN_A", StringComparison.Ordinal)));
        Assert.That(html, Does.Contain("item pinned"));

        await Post($"/Oracle?handler=TogglePin&id={c}");
        Assert.That(_repo.GetAll().Select(x => x.Name).First(), Is.EqualTo("PIN_A"));
        Assert.That(_repo.Get(c)!.Pinned, Is.False);
    }

    [Test]
    public async Task EditingAPinnedConnection_KeepsItPinned()
    {
        var id = Add("PIN_EDIT");
        _repo.SetPinned(id, true);

        await Post("/Oracle?handler=Save", ("id", id.ToString()), ("copyFrom", "0"),
            ("name", "PIN_EDIT2"), ("dataSource", "h:1/s"), ("username", "U"), ("password", ""));

        Assert.That(_repo.Get(id)!.Pinned, Is.True);
    }

    [Test]
    public void Pickers_ListPinnedFirst_AndSayWhichArePinned()
    {
        var connector = _factory.Services.GetRequiredService<OracleConnector>();
        var z = Add("ZZ_LAST");
        _repo.SetPinned(z, true);

        var choices = connector.Choices();
        Assert.That(choices.First(c => c.Pinned).Name, Is.EqualTo(choices[0].Name));
        Assert.That(choices.Where(c => c.Pinned).Select(c => c.Name), Does.Contain("ZZ_LAST"));
        Assert.That(connector.Names()[0], Is.EqualTo(choices[0].Name));
    }

    [Test]
    public async Task TheQueryDialog_GroupsPinnedConnectionsFirst()
    {
        Add("AA_PLAIN");
        var p = Add("MM_PINNED");
        _repo.SetPinned(p, true);

        var html = await _client.GetStringAsync("/Queries?handler=Form&id=0");
        Assert.That(html, Does.Contain("optgroup label=\"★ Pinned\""));
        Assert.That(html.IndexOf("MM_PINNED", StringComparison.Ordinal),
            Is.LessThan(html.IndexOf("AA_PLAIN", StringComparison.Ordinal)));
    }

    [Test]
    public async Task TheSection_IsCalledConnections()
    {
        var html = await _client.GetStringAsync("/Oracle");
        Assert.That(html, Does.Contain("<h1>Connections</h1>"));
        Assert.That(html, Does.Contain(">Connections<"), "menu entry");
        Assert.That(html, Does.Not.Contain("Oracle connections"));
    }
}
