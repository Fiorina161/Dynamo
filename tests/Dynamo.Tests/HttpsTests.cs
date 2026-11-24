using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.RegularExpressions;
using Dynamo.Core;
using Dynamo.Modules.Settings;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Dynamo.Tests;

/// <summary>
/// Dynamo's own https certificates: a local authority made once, a server
/// certificate it signs and renews, and what the site does with them.
/// </summary>
public class HttpsTests
{
    private sealed class FakeClient : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (context, following) =>
            {
                if (context.Request.Headers.TryGetValue("X-Test-Client", out var ip))
                {
                    context.Connection.RemoteIpAddress = IPAddress.Parse(ip.ToString());
                    context.Connection.LocalIpAddress = IPAddress.Parse("10.0.0.5");
                }
                if (context.Request.Headers.ContainsKey("X-Test-Https"))
                    context.Request.Scheme = "https";
                await following(context);
            });
            next(app);
        };
    }

    private const string Secret = "correct horse battery";
    private static readonly string[] Names = ["localhost", "127.0.0.1", "testvm", "10.1.2.3"];

    private string _home = "";
    private string? _previousHome;
    private DateTimeOffset _now = new(2026, 11, 16, 9, 0, 0, TimeSpan.Zero);
    private IReadOnlyList<string> _names = Names;
    private SettingsRepository _settings = null!;
    private CertificateService _certificates = null!;
    private PasswordService _passwords = null!;
    private WebApplicationFactory<Program> _factory = null!;
    private int _ipCounter;

    [OneTimeSetUp]
    public void SetUp()
    {
        _previousHome = Environment.GetEnvironmentVariable("DYNAMO_HOME");
        _home = Path.Combine(Path.GetTempPath(), "dynamo-https-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("DYNAMO_HOME", _home);
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            b.ConfigureServices(s =>
            {
                s.AddTransient<IStartupFilter, FakeClient>();
                s.AddSingleton(new HttpsOptions(4343));
                s.AddSingleton(sp => new PasswordService(sp.GetRequiredService<SettingsRepository>(), 1000));
                // The clock and the names are the tests' to move.
                s.AddSingleton(sp => new CertificateService(
                    sp.GetRequiredService<SettingsRepository>(), () => _now, () => _names));
            }));
        _factory.CreateClient().Dispose();   // starts the host: the authority and certificate are made
        _settings = _factory.Services.GetRequiredService<SettingsRepository>();
        _certificates = _factory.Services.GetRequiredService<CertificateService>();
        _passwords = _factory.Services.GetRequiredService<PasswordService>();
    }

    [OneTimeTearDown]
    public void TearDown()
    {
        _factory.Dispose();
        Environment.SetEnvironmentVariable("DYNAMO_HOME", _previousHome);
        try { Directory.Delete(_home, true); } catch { /* best effort */ }
    }

    [SetUp]
    public void Reset()
    {
        _now = new DateTimeOffset(2026, 11, 16, 9, 0, 0, TimeSpan.Zero);
        _names = Names;
        _certificates.Ensure();
        _certificates.RequireHttpsForRemote = true;
        _passwords.Set(Secret);
    }

    private CertificateService Restarted() =>
        new(_settings, () => _now, () => _names);

    private static bool ChainsTo(X509Certificate2 leaf, byte[] authorityDer)
    {
        using var authority = X509CertificateLoader.LoadCertificate(authorityDer);
        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(authority);
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.ChainPolicy.VerificationTime = DateTime.UtcNow;
        return chain.Build(leaf);
    }

    private static string San(X509Certificate2 cert) =>
        string.Join(" | ", cert.Extensions.Where(e => e.Oid?.Value == "2.5.29.17").Select(e => e.Format(false)));

    // ---- The certificates ---------------------------------------------------

    [Test]
    public void TheServerCertificate_IsSignedByTheAuthority_AndNamesTheComputer()
    {
        var leaf = _certificates.Current!;
        var der = _certificates.AuthorityCertificate!;

        Assert.That(leaf.HasPrivateKey, Is.True);
        Assert.That(ChainsTo(leaf, der), Is.True, "trusting only the authority is enough");
        var san = San(leaf);
        Assert.That(san, Does.Contain("DNS Name=localhost").And.Contain("DNS Name=testvm")
            .And.Contain("IP Address=127.0.0.1").And.Contain("IP Address=10.1.2.3"));
        var usage = leaf.Extensions.OfType<X509EnhancedKeyUsageExtension>().Single();
        Assert.That(usage.EnhancedKeyUsages.Cast<Oid>().Select(o => o.Value), Does.Contain("1.3.6.1.5.5.7.3.1"));
        Assert.That(leaf.Extensions.OfType<X509BasicConstraintsExtension>().Single().CertificateAuthority, Is.False);

        // Valid from yesterday, for a little over a year: a browser accepts that.
        Assert.That(leaf.NotAfter.ToUniversalTime() - _now.UtcDateTime,
            Is.EqualTo(TimeSpan.FromDays(CertificateService.LeafValidityDays)).Within(TimeSpan.FromDays(1)));
        Assert.That(_certificates.IssuedFor, Is.EquivalentTo(Names));
    }

    [Test]
    public void TheAuthority_IsAnAuthority_ThatNeverLeavesWithItsKey()
    {
        using var authority = X509CertificateLoader.LoadCertificate(_certificates.AuthorityCertificate!);

        Assert.That(authority.HasPrivateKey, Is.False, "only the public part can be downloaded");
        Assert.That(authority.Extensions.OfType<X509BasicConstraintsExtension>().Single().CertificateAuthority, Is.True);
        Assert.That(authority.NotAfter.ToUniversalTime() - _now.UtcDateTime, Is.GreaterThan(TimeSpan.FromDays(3600)));
        Assert.That(_certificates.AuthorityFingerprint, Does.Match("^([0-9A-F]{2}:){31}[0-9A-F]{2}$"));
    }

    [Test]
    public void AfterARestart_TheSameAuthorityAndCertificateAreUsed()
    {
        var before = _certificates.Current!.Thumbprint;
        var authority = _certificates.AuthorityFingerprint;

        var restarted = Restarted();
        var issued = restarted.Ensure();

        Assert.That(issued, Is.False, "nothing changed: nothing to issue");
        Assert.That(restarted.Current!.Thumbprint, Is.EqualTo(before));
        Assert.That(restarted.AuthorityFingerprint, Is.EqualTo(authority), "what the user trusted stays valid");
    }

    [Test]
    public void ItRenewsBeforeItExpires_WithoutChangingTheAuthority()
    {
        var first = _certificates.Current!.Thumbprint;
        var authority = _certificates.AuthorityFingerprint;

        _now = _now.AddDays(CertificateService.LeafValidityDays - CertificateService.RenewDaysBefore - 5);
        Assert.That(_certificates.Ensure(), Is.False, "still more than 30 days left");
        Assert.That(_certificates.Current!.Thumbprint, Is.EqualTo(first));

        _now = _now.AddDays(10);                 // now within the last 30 days
        Assert.That(_certificates.Ensure(), Is.True);
        var renewed = _certificates.Current!;
        Assert.That(renewed.Thumbprint, Is.Not.EqualTo(first));
        Assert.That(_certificates.AuthorityFingerprint, Is.EqualTo(authority), "no new trust to give");
        Assert.That(renewed.NotAfter.ToUniversalTime(), Is.GreaterThan(_now.UtcDateTime.AddDays(300)));
        using var chainCheck = new X509Chain();
        chainCheck.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        using var ca = X509CertificateLoader.LoadCertificate(_certificates.AuthorityCertificate!);
        chainCheck.ChainPolicy.CustomTrustStore.Add(ca);
        chainCheck.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chainCheck.ChainPolicy.VerificationTime = _now.UtcDateTime;
        Assert.That(chainCheck.Build(renewed), Is.True, "the renewed one is trusted too");

        // A restart after the renewal loads the renewed one.
        var again = Restarted();
        Assert.That(again.Ensure(), Is.False);
        Assert.That(again.Current!.Thumbprint, Is.EqualTo(renewed.Thumbprint));
    }

    [Test]
    public void WhenTheComputersNamesOrAddressesChange_ANewCertificateCoversThem()
    {
        var first = _certificates.Current!.Thumbprint;
        _names = [.. Names, "10.9.9.9", "newname"];

        Assert.That(_certificates.Ensure(), Is.True);

        Assert.That(_certificates.Current!.Thumbprint, Is.Not.EqualTo(first));
        Assert.That(San(_certificates.Current), Does.Contain("IP Address=10.9.9.9").And.Contain("DNS Name=newname"));
        Assert.That(ChainsTo(_certificates.Current, _certificates.AuthorityCertificate!), Is.True);

        _names = Names;                           // back to normal for the other tests
        Assert.That(_certificates.Ensure(), Is.True);
    }

    [Test]
    public void ANewCertificateCanBeAskedFor_AndStaysSignedByTheSameAuthority()
    {
        var first = _certificates.Current!.Thumbprint;
        var authority = _certificates.AuthorityFingerprint;

        Assert.That(_certificates.Ensure(force: true), Is.True);

        Assert.That(_certificates.Current!.Thumbprint, Is.Not.EqualTo(first));
        Assert.That(_certificates.AuthorityFingerprint, Is.EqualTo(authority));
    }

    [Test]
    public void RequiringHttps_IsOnByDefault_AndRemembered()
    {
        Assert.That(_certificates.RequireHttpsForRemote, Is.True);
        _certificates.RequireHttpsForRemote = false;
        Assert.That(Restarted().RequireHttpsForRemote, Is.False, "stored in the database");
        _certificates.RequireHttpsForRemote = true;
        Assert.That(Restarted().RequireHttpsForRemote, Is.True);
    }

    // ---- The site ---------------------------------------------------------------

    private HttpClient Browser(string? remoteIp = null, bool https = false)
    {
        // An https browser really uses an https address: it only sends Secure cookies back there.
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            BaseAddress = new Uri(https ? "https://localhost" : "http://localhost"),
        });
        if (remoteIp is not null)
            client.DefaultRequestHeaders.Add("X-Test-Client", remoteIp);
        return client;
    }

    private string NextIp() => $"192.168.60.{Interlocked.Increment(ref _ipCounter)}";

    [Test]
    public async Task AnotherComputerOnPlainHttp_IsSentToHttps_ThisComputerIsNot()
    {
        var remote = Browser(NextIp());
        var response = await remote.GetAsync("/Workplaces?a=1");
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.TemporaryRedirect));
        Assert.That(response.Headers.Location!.OriginalString, Is.EqualTo("https://localhost:4343/Workplaces?a=1"));

        // A POST is redirected as a POST (307), not turned into a GET.
        var post = await remote.PostAsync("/Login", new StringContent(""));
        Assert.That(post.StatusCode, Is.EqualTo(HttpStatusCode.TemporaryRedirect));

        var local = Browser();
        Assert.That((await local.GetAsync("/Workplaces?a=1")).StatusCode, Is.EqualTo(HttpStatusCode.OK));
    }

    [Test]
    public async Task OverHttps_AnotherComputerLogsIn_AndTheCookieIsSecure()
    {
        var remote = Browser(NextIp(), https: true);
        var page = await remote.GetAsync("/Login");
        Assert.That(page.StatusCode, Is.EqualTo(HttpStatusCode.OK), "no redirect loop");

        var html = await page.Content.ReadAsStringAsync();
        var token = Regex.Match(html, "__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value;
        var login = await remote.PostAsync("/Login", new FormUrlEncodedContent(
        [
            KeyValuePair.Create("password", Secret), KeyValuePair.Create("returnUrl", "/Bookmarks"),
            KeyValuePair.Create("__RequestVerificationToken", token),
        ]));

        Assert.That(login.StatusCode, Is.EqualTo(HttpStatusCode.Redirect));
        var cookie = login.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith("dynamo_session="));
        Assert.That(cookie, Does.Contain("secure").IgnoreCase.And.Contain("httponly").IgnoreCase);
        Assert.That(cookie, Does.Not.Contain("expires").IgnoreCase, "still gone when the browser closes");
        Assert.That((await remote.GetAsync("/Bookmarks")).StatusCode, Is.EqualTo(HttpStatusCode.OK));
    }

    [Test]
    public async Task TheCertificate_CanBeDownloadedOverHttp_WithoutItsKey()
    {
        var remote = Browser(NextIp());              // plain http, not logged in
        var response = await remote.GetAsync(AccessMiddleware.CertificatePath);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK), "not redirected: it is what makes https trusted");
        Assert.That(response.Content.Headers.ContentType!.MediaType, Is.EqualTo("application/x-x509-ca-cert"));
        var bytes = await response.Content.ReadAsByteArrayAsync();
        using var downloaded = X509CertificateLoader.LoadCertificate(bytes);
        Assert.That(downloaded.HasPrivateKey, Is.False);
        Assert.That(downloaded.RawData, Is.EqualTo(_certificates.AuthorityCertificate));
        Assert.That(ChainsTo(_certificates.Current!, bytes), Is.True, "what was downloaded is enough to trust https");

        // Anything else is still behind the redirect and the login.
        Assert.That((await remote.GetAsync("/Bookmarks")).StatusCode, Is.EqualTo(HttpStatusCode.TemporaryRedirect));

        // And it is there for this computer too.
        Assert.That((await Browser().GetAsync(AccessMiddleware.CertificatePath)).StatusCode, Is.EqualTo(HttpStatusCode.OK));
    }

    [Test]
    public async Task ThePasswordIsStillRequired_AndNoPasswordStillMeansRefused()
    {
        _passwords.Clear();
        var remote = Browser(NextIp(), https: true);
        Assert.That((await remote.GetAsync("/Bookmarks")).StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
        Assert.That((await Browser(NextIp()).GetAsync(AccessMiddleware.CertificatePath)).StatusCode,
            Is.EqualTo(HttpStatusCode.Forbidden), "nothing is given to other computers before a password exists");
    }

    [Test]
    public async Task TheRedirect_CanBeTurnedOff()
    {
        _certificates.RequireHttpsForRemote = false;
        var remote = Browser(NextIp());

        var response = await remote.GetAsync("/Bookmarks");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Redirect), "plain http: to the login page, not to https");
        Assert.That(response.Headers.Location!.OriginalString, Does.StartWith("/Login"));
    }

    [Test]
    public async Task TheSettingsPage_ShowsTheCertificate_AndItsFingerprint()
    {
        var local = Browser();
        var html = await local.GetStringAsync("/Settings");

        Assert.That(html, Does.Contain("Secure connection (https)").And.Contain("port 4343"));
        Assert.That(html, Does.Contain(_certificates.AuthorityFingerprint!));
        Assert.That(html, Does.Contain("href=\"/dynamo-certificate.cer\""));
        Assert.That(html, Does.Contain("DNS Name").Or.Contain("testvm"));
        Assert.That(html, Does.Match("name=\"requireHttps\"[^>]*checked"));
    }

    [Test]
    public async Task TheSettingsPage_CanTurnTheRedirectOff_AndIssueANewCertificate()
    {
        var local = Browser();
        async Task<HttpResponseMessage> Post(string handler, params (string, string)[] fields)
        {
            var html = await local.GetStringAsync("/Settings");
            var token = Regex.Match(html, "__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value;
            return await local.PostAsync($"/Settings?handler={handler}", new FormUrlEncodedContent(
                fields.Append(("__RequestVerificationToken", token)).Select(f => KeyValuePair.Create(f.Item1, f.Item2))));
        }

        var off = await Post("HttpsOptions");                       // the box is unticked: nothing sent
        Assert.That(await off.Content.ReadAsStringAsync(), Does.Contain("may use plain http"));
        Assert.That(_certificates.RequireHttpsForRemote, Is.False);
        var on = await Post("HttpsOptions", ("requireHttps", "on"));
        Assert.That(await on.Content.ReadAsStringAsync(), Does.Contain("now sent to https"));
        Assert.That(_certificates.RequireHttpsForRemote, Is.True);

        var before = _certificates.Current!.Thumbprint;
        var authority = _certificates.AuthorityFingerprint;
        var renewed = await Post("RenewCertificate");
        Assert.That(await renewed.Content.ReadAsStringAsync(), Does.Contain("A new server certificate was issued"));
        Assert.That(_certificates.Current!.Thumbprint, Is.Not.EqualTo(before));
        Assert.That(_certificates.AuthorityFingerprint, Is.EqualTo(authority));
    }

    [Test]
    public void AnUnreadableStoredCertificate_IsMadeAgain()
    {
        var home = Path.Combine(Path.GetTempPath(), "dynamo-https-bad-" + Guid.NewGuid().ToString("N"));
        var previous = Environment.GetEnvironmentVariable("DYNAMO_HOME");
        Environment.SetEnvironmentVariable("DYNAMO_HOME", home);
        try
        {
            var db = new Db(DynamoHome.Resolve());
            db.Migrate([new SettingsModule()]);
            var settings = new SettingsRepository(db);
            settings.Set("tls.ca", "not a certificate");
            settings.Set("tls.leaf", "!!!");

            var service = new CertificateService(settings, () => _now, () => Names);

            Assert.That(service.Ensure(), Is.True);
            Assert.That(ChainsTo(service.Current!, service.AuthorityCertificate!), Is.True);
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        }
        finally
        {
            Environment.SetEnvironmentVariable("DYNAMO_HOME", previous);
            try { Directory.Delete(home, true); } catch { /* best effort */ }
        }
    }
}
