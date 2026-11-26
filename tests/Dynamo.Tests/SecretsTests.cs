using System.Text.RegularExpressions;
using Dapper;
using Dynamo.Core;
using Dynamo.Modules.OracleConnections;
using Dynamo.Modules.Settings;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Dynamo.Tests;

/// <summary>
/// The secrets in the database (Oracle passwords, the private keys of the https certificates) are protected
/// with Windows DPAPI at machine scope: what is stored, what is read back, the conversion of old clear
/// values, a value that cannot be decrypted here.
/// </summary>
public class SecretsTests
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
        _home = Path.Combine(Path.GetTempPath(), "dynamo-secrets-" + Guid.NewGuid().ToString("N"));
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

    private Db Database => _factory.Services.GetRequiredService<Db>();
    private OracleConnectionRepository Repo => _factory.Services.GetRequiredService<OracleConnectionRepository>();

    private string RawPassword(long id)
    {
        using var conn = Database.Open();
        return conn.ExecuteScalar<string>("SELECT password FROM oracle_connection WHERE id = @id", new { id })!;
    }

    private long InsertRaw(string name, string rawPassword)
    {
        using var conn = Database.Open();
        return conn.ExecuteScalar<long>("""
            INSERT INTO oracle_connection (name, data_source, username, password)
            VALUES (@name, 'h:1521/s', 'scott', @rawPassword);
            SELECT last_insert_rowid();
            """, new { name, rawPassword });
    }

    private async Task<string> Post(string url, params (string, string)[] fields)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new FormUrlEncodedContent(fields.Select(f => KeyValuePair.Create(f.Item1, f.Item2))),
        };
        request.Headers.Add("RequestVerificationToken", _token);
        request.Headers.Add("HX-Request", "true");
        return await (await _client.SendAsync(request)).Content.ReadAsStringAsync();
    }

    // ---- The helper ---------------------------------------------------------------------------

    [Test]
    public void AProtectedValue_IsNotTheClearText_AndComesBackIdentical()
    {
        var stored = Secrets.Protect("S3cret-pässword ✓");

        Assert.That(stored, Does.StartWith(Secrets.Prefix));
        Assert.That(stored, Does.Not.Contain("S3cret"));
        Assert.That(Secrets.IsProtected(stored), Is.True);
        Assert.That(Secrets.TryUnprotect(stored, out var back), Is.True);
        Assert.That(back, Is.EqualTo("S3cret-pässword ✓"));
        Assert.That(Secrets.Protect("S3cret-pässword ✓"), Is.Not.EqualTo(stored), "a fresh salt each time");
    }

    [Test]
    public void NothingToProtect_StaysEmpty_AndAnOldClearValueIsReadAsItIs()
    {
        Assert.That(Secrets.Protect(""), Is.EqualTo(""));
        Assert.That(Secrets.Protect(null), Is.EqualTo(""));
        Assert.That(Secrets.TryUnprotect("", out var empty) && empty == "", Is.True);
        Assert.That(Secrets.TryUnprotect("an old clear password", out var clear), Is.True);
        Assert.That(clear, Is.EqualTo("an old clear password"));
    }

    [TestCase("dpapi1:not-base64!!")]
    [TestCase("dpapi1:AAAA")]
    [TestCase("dpapi1:")]
    public void AProtectedValueThatCannotBeDecrypted_IsUnreadable_NotAnException(string stored)
    {
        Assert.That(Secrets.TryUnprotect(stored, out var plain), Is.False);
        Assert.That(plain, Is.EqualTo(""));
    }

    // ---- Oracle passwords -------------------------------------------------------------------

    [Test]
    public async Task APasswordSavedFromThePage_IsProtectedInTheDatabase_AndRead_InClear_ByTheApplication()
    {
        await Post("/Oracle?handler=Save", ("id", "0"), ("name", "Secret one"), ("dataSource", "h:1521/s"),
            ("username", "scott"), ("password", "tiger-123"), ("copyFrom", "0"));
        var saved = Repo.GetByName("Secret one")!;

        var raw = RawPassword(saved.Id);
        Assert.That(raw, Does.StartWith(Secrets.Prefix).And.Not.Contain("tiger-123"));
        Assert.That(saved.Password, Is.EqualTo("tiger-123"), "what is sent to Oracle");
        Assert.That(saved.PasswordUnreadable, Is.False);
        Assert.That(Repo.Get(saved.Id)!.Password, Is.EqualTo("tiger-123"));
        Assert.That(Repo.GetAll().Single(c => c.Name == "Secret one").Password, Is.EqualTo("tiger-123"));
        Assert.That(Repo.GetMany([saved.Id]).Single().Password, Is.EqualTo("tiger-123"));
        Assert.That(await _client.GetStringAsync($"/Oracle?handler=Form&id={saved.Id}"), Does.Not.Contain("tiger-123"),
            "never shown again in a page");
    }

    [Test]
    public async Task EditingWithABlankPassword_KeepsTheStoredOne_AndANewOneReplacesIt()
    {
        await Post("/Oracle?handler=Save", ("id", "0"), ("name", "Secret edit"), ("dataSource", "h:1521/s"),
            ("username", "scott"), ("password", "first-pw"), ("copyFrom", "0"));
        var id = Repo.GetByName("Secret edit")!.Id;

        await Post("/Oracle?handler=Save", ("id", id.ToString()), ("name", "Secret edit"), ("dataSource", "h:1521/s2"),
            ("username", "scott"), ("password", ""), ("copyFrom", "0"));
        Assert.That(Repo.Get(id)!.Password, Is.EqualTo("first-pw"));
        Assert.That(Repo.Get(id)!.DataSource, Is.EqualTo("h:1521/s2"));
        Assert.That(RawPassword(id), Does.StartWith(Secrets.Prefix));

        await Post("/Oracle?handler=Save", ("id", id.ToString()), ("name", "Secret edit"), ("dataSource", "h:1521/s2"),
            ("username", "scott"), ("password", "second-pw"), ("copyFrom", "0"));
        Assert.That(Repo.Get(id)!.Password, Is.EqualTo("second-pw"));
    }

    [Test]
    public async Task ADuplicate_TakesTheOriginalsPassword_AndStoresItProtected()
    {
        await Post("/Oracle?handler=Save", ("id", "0"), ("name", "Secret orig"), ("dataSource", "h:1521/s"),
            ("username", "scott"), ("password", "orig-pw"), ("copyFrom", "0"));
        var original = Repo.GetByName("Secret orig")!;

        await Post("/Oracle?handler=Save", ("id", "0"), ("name", "Secret orig copy"), ("dataSource", "h:1521/s"),
            ("username", "scott"), ("password", ""), ("copyFrom", original.Id.ToString()));

        var copy = Repo.GetByName("Secret orig copy")!;
        Assert.That(copy.Password, Is.EqualTo("orig-pw"));
        Assert.That(RawPassword(copy.Id), Does.StartWith(Secrets.Prefix));
    }

    [Test]
    public void AnImportedConnectionWithNoPassword_StaysEmpty()
    {
        var id = Repo.Save(new OracleConnectionInfo { Name = "Secret imported", DataSource = "alias" });

        Assert.That(RawPassword(id), Is.EqualTo(""));
        Assert.That(Repo.Get(id)!.Complete, Is.False);
    }

    // ---- What an older version stored in clear ---------------------------------------------------

    [Test]
    public void Starting_ProtectsWhatWasStoredInClear_Once()
    {
        var a = InsertRaw("Legacy A", "clear-a");
        var b = InsertRaw("Legacy B", "clear-b");
        var empty = InsertRaw("Legacy none", "");
        var already = InsertRaw("Legacy done", Secrets.Protect("done-pw"));

        var (passwords, _) = Secrets.ProtectStored(Database);

        Assert.That(passwords, Is.GreaterThanOrEqualTo(2));
        Assert.That(RawPassword(a), Does.StartWith(Secrets.Prefix).And.Not.Contain("clear-a"));
        Assert.That(RawPassword(b), Does.StartWith(Secrets.Prefix));
        Assert.That(RawPassword(empty), Is.EqualTo(""), "nothing to protect");
        Assert.That(Repo.Get(a)!.Password, Is.EqualTo("clear-a"), "and it still reads the same");
        Assert.That(Repo.Get(b)!.Password, Is.EqualTo("clear-b"));
        Assert.That(Repo.Get(already)!.Password, Is.EqualTo("done-pw"));

        Assert.That(Secrets.ProtectStored(Database), Is.EqualTo((0, 0)), "a second run changes nothing");
    }

    [Test]
    public void TheCertificateKeysInTheSettings_AreProtectedToo()
    {
        var settings = _factory.Services.GetRequiredService<SettingsRepository>();
        settings.Set("tls.ca", "legacy-pfx-in-clear");
        settings.Set("tls.leaf", "legacy-leaf-in-clear");

        var (_, keys) = Secrets.ProtectStored(Database);

        Assert.That(keys, Is.EqualTo(2));
        Assert.That(settings.Get("tls.ca"), Does.StartWith(Secrets.Prefix).And.Not.Contain("legacy"));
        Assert.That(settings.GetSecret("tls.ca"), Is.EqualTo("legacy-pfx-in-clear"));
        Assert.That(settings.GetSecret("tls.leaf"), Is.EqualTo("legacy-leaf-in-clear"));
        settings.Delete("tls.ca");
        settings.Delete("tls.leaf");
    }

    [Test]
    public void ANewCertificate_IsStoredProtected_AndReadBackByAnotherService()
    {
        var home = Path.Combine(Path.GetTempPath(), "dynamo-cert-secret-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(home);
        try
        {
            var before = Environment.GetEnvironmentVariable("DYNAMO_HOME");
            Environment.SetEnvironmentVariable("DYNAMO_HOME", home);
            var db = new Db(DynamoHome.Resolve());
            Environment.SetEnvironmentVariable("DYNAMO_HOME", before);
            db.Migrate([new SettingsModule()]);
            var settings = new SettingsRepository(db);
            var first = new CertificateService(settings, null, () => ["localhost", "127.0.0.1"]);

            Assert.That(first.Ensure(), Is.True);

            Assert.That(settings.Get("tls.ca"), Does.StartWith(Secrets.Prefix));
            Assert.That(settings.Get("tls.leaf"), Does.StartWith(Secrets.Prefix));
            var second = new CertificateService(settings, null, () => ["localhost", "127.0.0.1"]);
            Assert.That(second.Ensure(), Is.False, "the stored certificate is read back: nothing to issue");
            Assert.That(second.Current!.Thumbprint, Is.EqualTo(first.Current!.Thumbprint));
            Assert.That(second.Current.HasPrivateKey, Is.True);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { Directory.Delete(home, true); } catch { /* best effort */ }
        }
    }

    // ---- A value this computer cannot decrypt -----------------------------------------------------

    [Test]
    public async Task APasswordThatCannotBeRead_MakesTheConnectionNotReady_UntilItIsEnteredAgain()
    {
        var id = InsertRaw("Secret lost", "dpapi1:AAAAAAAAAAAA");

        var c = Repo.Get(id)!;
        Assert.That(c.PasswordUnreadable, Is.True);
        Assert.That(c.Password, Is.EqualTo(""));
        Assert.That(c.Complete, Is.False);

        var test = await _factory.Services.GetRequiredService<OracleConnector>().TestAsync(c);
        Assert.That(test.Ok, Is.False);
        Assert.That(test.Message, Does.Contain("cannot be read on this computer").And.Contain("enter it again"));
        Assert.That(await _client.GetStringAsync("/Oracle"), Does.Contain("password cannot be read here"));

        // Saving without a password is refused: it would otherwise keep the lost one.
        var refused = await Post("/Oracle?handler=Save", ("id", id.ToString()), ("name", "Secret lost"),
            ("dataSource", "h:1521/s"), ("username", "scott"), ("password", ""), ("copyFrom", "0"));
        Assert.That(refused, Does.Contain("The password is required"));

        await Post("/Oracle?handler=Save", ("id", id.ToString()), ("name", "Secret lost"),
            ("dataSource", "h:1521/s"), ("username", "scott"), ("password", "back-again"), ("copyFrom", "0"));
        var fixedUp = Repo.Get(id)!;
        Assert.That(fixedUp.PasswordUnreadable, Is.False);
        Assert.That(fixedUp.Password, Is.EqualTo("back-again"));
        Assert.That(RawPassword(id), Does.StartWith(Secrets.Prefix));
    }

    [Test]
    public void AnUnreadableValueIsNeverRewrittenByTheConversion()
    {
        var id = InsertRaw("Secret kept", "dpapi1:AAAAAAAAAAAA");

        Secrets.ProtectStored(Database);

        Assert.That(RawPassword(id), Is.EqualTo("dpapi1:AAAAAAAAAAAA"), "already in the protected format: left alone");
    }
}
