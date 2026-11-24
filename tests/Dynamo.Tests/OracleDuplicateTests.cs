using System.Net;
using System.Text.RegularExpressions;
using Dynamo.Modules.OracleConnections;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Dynamo.Tests;

/// <summary>Duplicating an Oracle connection, through the pages, in memory.</summary>
public class OracleDuplicateTests
{
    private const string Secret = "s3cr3t-pw";

    private string _home = "";
    private string? _previousHome;
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;
    private OracleConnectionRepository _repo = null!;
    private string _token = "";

    [OneTimeSetUp]
    public void SetUp()
    {
        _previousHome = Environment.GetEnvironmentVariable("DYNAMO_HOME");
        _home = Path.Combine(Path.GetTempPath(), "dynamo-dup-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("DYNAMO_HOME", _home);
        _factory = new WebApplicationFactory<Program>();
        _client = _factory.CreateClient();
        _repo = _factory.Services.GetRequiredService<OracleConnectionRepository>();
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

    private long Original(string name, string password = Secret) =>
        _repo.Save(new OracleConnectionInfo
        {
            Name = name, DataSource = "(DESCRIPTION=(ADDRESS=(HOST=h)(PORT=1521)))",
            Username = "CAD_OWNER", Password = password,
        });

    private Task<string> DuplicateForm(long id) => _client.GetStringAsync($"/Oracle?handler=Duplicate&id={id}");

    private Task<HttpResponseMessage> SaveCopy(long copyFrom, string name, string password = "") =>
        Post("/Oracle?handler=Save",
            ("id", "0"), ("copyFrom", copyFrom.ToString()), ("name", name),
            ("dataSource", "(DESCRIPTION=(ADDRESS=(HOST=h)(PORT=1521)))"), ("username", "CAD_READ"),
            ("password", password));

    [Test]
    public async Task Duplicate_PrefillsANewForm_WithoutTheSecret_AndCreatesNothing()
    {
        var id = Original("DUP_SRC");
        var before = _repo.GetAll().Count;

        var html = await DuplicateForm(id);

        Assert.That(html, Does.Contain("Duplicate connection"));
        Assert.That(html, Does.Contain("value=\"DUP_SRC copy\""));
        Assert.That(html, Does.Contain("value=\"CAD_OWNER\""));
        Assert.That(html, Does.Contain("(DESCRIPTION=(ADDRESS=(HOST=h)(PORT=1521)))"));
        Assert.That(html, Does.Contain($"name=\"copyFrom\" value=\"{id}\""));
        Assert.That(html, Does.Contain("name=\"id\" value=\"0\""));
        Assert.That(html, Does.Contain("same as “DUP_SRC”").Or.Contain("same as &#x201C;DUP_SRC&#x201D;"));
        Assert.That(html, Does.Not.Contain(Secret));
        Assert.That(_repo.GetAll(), Has.Count.EqualTo(before), "nothing is created until Save");
    }

    [Test]
    public async Task Saving_WithABlankPassword_CopiesTheOriginalsPassword()
    {
        var id = Original("PW_SRC");

        var response = await SaveCopy(id, "PW_SRC copy");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var copy = _repo.GetByName("PW_SRC copy")!;
        Assert.That(copy.Password, Is.EqualTo(Secret));
        Assert.That(copy.Username, Is.EqualTo("CAD_READ"), "the edited schema is kept");
        Assert.That(copy.Id, Is.Not.EqualTo(id));
        Assert.That(_repo.Get(id)!.Username, Is.EqualTo("CAD_OWNER"), "the original is untouched");
    }

    [Test]
    public async Task Saving_WithATypedPassword_UsesThatOne()
    {
        var id = Original("PW2_SRC");

        await SaveCopy(id, "PW2_SRC copy", password: "another");

        Assert.That(_repo.GetByName("PW2_SRC copy")!.Password, Is.EqualTo("another"));
    }

    [Test]
    public async Task SuggestedName_IsTheFirstFreeOne()
    {
        var id = Original("NAMES");

        Assert.That(await DuplicateForm(id), Does.Contain("value=\"NAMES copy\""));
        await SaveCopy(id, "NAMES copy");
        Assert.That(await DuplicateForm(id), Does.Contain("value=\"NAMES copy 2\""));
        await SaveCopy(id, "NAMES copy 2");
        Assert.That(await DuplicateForm(id), Does.Contain("value=\"NAMES copy 3\""));
    }

    [Test]
    public async Task SuggestedName_StaysWithinTheLengthLimit()
    {
        var id = Original(new string('N', 64));

        var html = await DuplicateForm(id);
        var name = Regex.Match(html, "name=\"name\" value=\"([^\"]+)\"").Groups[1].Value;

        Assert.That(name.Length, Is.LessThanOrEqualTo(64));
        Assert.That(name, Does.EndWith(" copy"));
    }

    [Test]
    public async Task ANameThatAlreadyExists_IsRefused_AndTheFormKeepsTheLinkToTheOriginal()
    {
        var id = Original("CLASH_SRC");

        var response = await SaveCopy(id, "clash_src");
        var html = await response.Content.ReadAsStringAsync();

        Assert.That(html, Does.Contain("already exists"));
        Assert.That(html, Does.Contain($"name=\"copyFrom\" value=\"{id}\""));
        Assert.That(html, Does.Not.Contain(Secret));
        Assert.That(response.Headers.GetValues("HX-Retarget").Single(), Is.EqualTo("#dlg"));
    }

    [Test]
    public async Task IfTheOriginalIsGoneAndNoPasswordIsTyped_ThePasswordIsRequired()
    {
        var id = Original("GONE_SRC");
        _repo.Delete(id);

        var html = await (await SaveCopy(id, "GONE copy")).Content.ReadAsStringAsync();

        Assert.That(html, Does.Contain("The password is required."));
        Assert.That(_repo.GetByName("GONE copy"), Is.Null);
    }

    [Test]
    public async Task DuplicatingAnUnknownConnection_Is404()
    {
        var response = await _client.GetAsync("/Oracle?handler=Duplicate&id=987654321");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
    }

    [Test]
    public async Task TestingACopy_ConsultsTheOriginalsPassword()
    {
        // The original has a schema but no password yet (imported style): the copy
        // must be told so, which proves the original's password was the one looked at.
        var id = Original("TEST_SRC", password: "");

        var response = await Post("/Oracle?handler=TestForm",
            ("id", "0"), ("copyFrom", id.ToString()), ("name", "TEST copy"),
            ("dataSource", "h:1/s"), ("username", "u"), ("password", ""));

        Assert.That(response.Headers.GetValues("HX-Trigger").Single(), Does.Contain("no password yet"));
    }

    [Test]
    public async Task TheDuplicateButton_IsOnlyOnAnExistingConnectionsDialog()
    {
        var id = Original("BTN_SRC");

        var existing = await _client.GetStringAsync($"/Oracle?handler=Form&id={id}");
        var brandNew = await _client.GetStringAsync("/Oracle?handler=Form");

        // The order of the query parameters is not the point: both must be there.
        var url = Regex.Match(existing, "hx-get=\"(/Oracle\\?[^\"]*handler=Duplicate[^\"]*)\"").Groups[1].Value;
        Assert.That(url, Does.Contain($"id={id}"));
        Assert.That(brandNew, Does.Not.Contain("handler=Duplicate"));
    }
}
