using System.Text.RegularExpressions;
using Dynamo.Modules.OracleConnections;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Dynamo.Tests;

/// <summary>
/// Importing tnsnames.ora aliases from the Connections page: the button only
/// when the file exists, a dialog listing the aliases that are not connections
/// yet (none ticked), and only the picked ones imported.
/// </summary>
public class TnsNamesImportPageTests
{
    private const string Descriptor = "(DESCRIPTION=(ADDRESS=(PROTOCOL=TCP)(HOST=db1)(PORT=1521))(CONNECT_DATA=(SERVICE_NAME=svc)))";

    private string _home = "";
    private string _tns = "";
    private string? _previousHome;
    private string? _previousTns;
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;
    private string _token = "";
    private OracleConnectionRepository _repo = null!;

    [OneTimeSetUp]
    public void SetUp()
    {
        _previousHome = Environment.GetEnvironmentVariable("DYNAMO_HOME");
        _previousTns = Environment.GetEnvironmentVariable("TNS_ADMIN");
        var stem = Path.Combine(Path.GetTempPath(), "dynamo-tnsimport-" + Guid.NewGuid().ToString("N"));
        _home = stem + "-home";
        _tns = stem + "-tns";
        Directory.CreateDirectory(_tns);
        Environment.SetEnvironmentVariable("DYNAMO_HOME", _home);
        Environment.SetEnvironmentVariable("TNS_ADMIN", _tns);
        File.WriteAllText(Path.Combine(_tns, "tnsnames.ora"), $"""
            ALPHA = {Descriptor}
            BRAVO = {Descriptor.Replace("db1", "db2")}
            CHARLIE = {Descriptor.Replace("db1", "db3")}
            CHARLIE = {Descriptor.Replace("db1", "db4")}
            DELTA = {Descriptor.Replace("db1", "db5")}
            """);

        _factory = new WebApplicationFactory<Program>();
        _client = _factory.CreateClient();
        _token = Regex.Match(_client.GetStringAsync("/Oracle").Result,
            "RequestVerificationToken\": \"([^\"]+)\"").Groups[1].Value;
        _repo = _factory.Services.GetRequiredService<OracleConnectionRepository>();
    }

    /// <summary>The tests share one database: each starts from the same state.</summary>
    [SetUp]
    public void Reset()
    {
        foreach (var c in _repo.GetAll())
            _repo.Delete(c.Id);
        // BRAVO is already a connection, with its own schema and password.
        _repo.Save(new OracleConnectionInfo
        {
            Name = "bravo", DataSource = "kept", Username = "ME", Password = "secret",
        });
    }

    [OneTimeTearDown]
    public void TearDown()
    {
        _client.Dispose();
        _factory.Dispose();
        Environment.SetEnvironmentVariable("DYNAMO_HOME", _previousHome);
        Environment.SetEnvironmentVariable("TNS_ADMIN", _previousTns);
        try { Directory.Delete(_home, true); } catch { /* best effort */ }
        try { Directory.Delete(_tns, true); } catch { /* best effort */ }
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

    [Test]
    public async Task TheImportButton_IsThereOnlyWhenATnsnamesFileExists()
    {
        Assert.That(await _client.GetStringAsync("/Oracle"), Does.Contain("Import tnsnames.ora"));

        var other = Path.Combine(_tns, "empty");
        Directory.CreateDirectory(other);
        Environment.SetEnvironmentVariable("TNS_ADMIN", other);
        try
        {
            Assert.That(await _client.GetStringAsync("/Oracle"), Does.Not.Contain("Import tnsnames.ora"));
            // Asking for the dialog anyway says why, and shows nothing.
            var response = await _client.GetAsync("/Oracle?handler=ImportForm");
            Assert.That(response.Headers.GetValues("HX-Trigger").First(), Does.Contain("No tnsnames.ora found"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("TNS_ADMIN", _tns);
        }
    }

    [Test]
    public async Task TheDialog_ListsOnlyNewAliases_NoneTicked_AndFlagsDoubles()
    {
        var html = await _client.GetStringAsync("/Oracle?handler=ImportForm");

        Assert.That(html, Does.Contain("value=\"ALPHA\"").And.Contain("value=\"CHARLIE\"").And.Contain("value=\"DELTA\""));
        Assert.That(html, Does.Not.Contain("value=\"BRAVO\""), "already a connection");
        Assert.That(html, Does.Contain("3 new aliases").And.Contain("1 already defined"));
        Assert.That(html, Does.Contain("defined 2 times with different targets"));
        Assert.That(html, Does.Not.Match("name=\"aliases\"[^>]*checked"), "the user picks");
        Assert.That(html, Does.Contain("data-select-action disabled"));
    }

    [Test]
    public async Task OnlyThePickedAliases_AreImported_NothingElseIsTouched()
    {
        var response = await Post("/Oracle?handler=Import", ("aliases", "ALPHA"), ("aliases", "BRAVO"));
        var html = await response.Content.ReadAsStringAsync();

        var names = _repo.GetAll().Select(c => c.Name).ToList();
        Assert.That(names, Does.Contain("ALPHA"));
        Assert.That(names, Does.Not.Contain("CHARLIE").And.Not.Contain("DELTA"), "not picked");
        var alpha = _repo.GetByName("ALPHA")!;
        Assert.That(alpha.DataSource, Is.EqualTo(Descriptor));
        Assert.That(alpha.Username, Is.Empty, "schema and password left to fill in");
        Assert.That(alpha.Password, Is.Empty);

        var bravo = _repo.GetByName("BRAVO")!;
        Assert.That(bravo.DataSource, Is.EqualTo("kept"));
        Assert.That(bravo.Password, Is.EqualTo("secret"), "an existing connection is never touched");

        var trigger = response.Headers.GetValues("HX-Trigger").First();
        Assert.That(trigger, Does.Contain("closeDialog").And.Contain("Imported 1 connection"));
        Assert.That(html, Does.Contain("ALPHA"));
        Assert.That(html, Does.Not.Contain("DELTA"));

        // What was imported is not offered again; the rest still is.
        var again = await _client.GetStringAsync("/Oracle?handler=ImportForm");
        Assert.That(again, Does.Not.Contain("value=\"ALPHA\""));
        Assert.That(again, Does.Contain("value=\"DELTA\""));
    }

    [Test]
    public async Task AnAliasDefinedTwice_ImportsItsFirstDefinition_AndSaysSo()
    {
        var response = await Post("/Oracle?handler=Import", ("aliases", "CHARLIE"));

        Assert.That(_repo.GetByName("charlie")!.DataSource, Does.Contain("db3"));
        Assert.That(response.Headers.GetValues("HX-Trigger").First(), Does.Contain("different targets"));
    }

    [Test]
    public async Task PickingNothing_ImportsNothing()
    {
        var before = _repo.GetAll().Count;
        var response = await Post("/Oracle?handler=Import");
        Assert.That(response.Headers.GetValues("HX-Trigger").First(), Does.Contain("Nothing selected"));
        Assert.That(_repo.GetAll(), Has.Count.EqualTo(before));
    }

    [Test]
    public void TheImporter_CanBeRestrictedToSomeAliases_AndPreviewChangesNothing()
    {
        var importer = _factory.Services.GetRequiredService<TnsNamesImporter>();
        var file = Path.Combine(_tns, "tnsnames.ora");
        var before = _repo.GetAll().Count;

        var preview = importer.Inspect(file);
        Assert.That(_repo.GetAll(), Has.Count.EqualTo(before), "a preview creates nothing");
        Assert.That(preview.New.Select(c => c.Alias), Does.Contain("CHARLIE"));
        Assert.That(preview.AlreadyThere, Is.GreaterThanOrEqualTo(1));

        var result = importer.Import(file, ["delta", "NOPE"]);
        Assert.That(result.Added, Is.LessThanOrEqualTo(1));
        Assert.That(_repo.GetAll().Select(c => c.Name), Does.Not.Contain("NOPE"));
    }
}
