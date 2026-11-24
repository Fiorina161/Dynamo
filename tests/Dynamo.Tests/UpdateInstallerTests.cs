using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Dynamo.Core;
using Dynamo.Modules.Settings;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Dynamo.Tests;

/// <summary>
/// "Update now": what it refuses (so that nothing odd is ever installed by a
/// service running as LocalSystem), what it hands to the script when all is well,
/// and the script itself, run on temporary folders with no service.
/// </summary>
public class UpdateInstallerTests
{
    private sealed class FakeGitHub : HttpMessageHandler, IHttpClientFactory
    {
        public Func<HttpRequestMessage, HttpResponseMessage> Respond = _ => new HttpResponseMessage(HttpStatusCode.NotFound);
        public List<string> Urls = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Urls.Add(request.RequestUri!.ToString());
            return Task.FromResult(Respond(request));
        }

        public HttpClient CreateClient(string name) => new(this, disposeHandler: false);
    }

    private sealed class FakeLauncher : IUpdateLauncher
    {
        public string? Script;
        public List<string> Arguments = [];

        public void Start(string scriptPath, IReadOnlyList<string> arguments)
        {
            Script = scriptPath;
            Arguments = [.. arguments];
        }
    }

    private sealed class FakeClientAddress : IStartupFilter
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
                await following(context);
            });
            next(app);
        };
    }

    private const string Asset = "Dynamo-1.3.0.zip";
    private const string AssetUrl = "https://github.com/Fiorina161/Dynamo/releases/download/v1.3.0/" + Asset;

    private string _home = "";
    private string? _previousHome;
    private FakeGitHub _github = null!;
    private FakeLauncher _launcher = null!;
    private byte[] _zip = [];
    private SettingsRepository _settings = null!;

    [SetUp]
    public void SetUp()
    {
        _previousHome = Environment.GetEnvironmentVariable("DYNAMO_HOME");
        _home = Path.Combine(Path.GetTempPath(), "dynamo-installer-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("DYNAMO_HOME", _home);
        var db = new Db(DynamoHome.Resolve());
        db.Migrate([new SettingsModule()]);
        _settings = new SettingsRepository(db);
        _github = new FakeGitHub();
        _launcher = new FakeLauncher();
        _zip = MakeZip();
        Serve(Sha(_zip));
    }

    [TearDown]
    public void TearDown()
    {
        Environment.SetEnvironmentVariable("DYNAMO_HOME", _previousHome);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_home, true); } catch { /* best effort */ }
    }

    private static byte[] MakeZip(string exePath = "Dynamo/app/Dynamo.exe")
    {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var name in new[] { exePath, "Dynamo/app/other.dll", "Dynamo/LISEZMOI.txt" })
            {
                using var writer = new StreamWriter(zip.CreateEntry(name).Open());
                writer.Write("content of " + name);
            }
        }
        return stream.ToArray();
    }

    private static string Sha(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    /// <summary>GitHub answers: the release (with the notes' checksum line) and the zip.</summary>
    private void Serve(string? checksumInNotes, string downloadUrl = AssetUrl, string? digest = null)
    {
        var notes = checksumInNotes is null ? "No checksum here." : $"### Checksum\\n`{checksumInNotes}`  `{Asset}`";
        var digestField = digest is null ? "" : $",\"digest\":\"{digest}\"";
        _github.Respond = request => request.RequestUri!.AbsoluteUri.Contains("api.github.com")
            ? new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent($$"""
                    {"tag_name":"v1.3.0","name":"Dynamo 1.3.0","body":"{{notes}}",
                     "html_url":"https://github.com/Fiorina161/Dynamo/releases/tag/v1.3.0",
                     "assets":[{"name":"{{Asset}}","browser_download_url":"{{downloadUrl}}"{{digestField}}}]}
                    """, System.Text.Encoding.UTF8, "application/json"),
            }
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(_zip) };
    }

    private UpdateInstaller Installer(
        bool service = true, string running = "1.2.0", Version? builtVersion = null, UpdateChecker? checker = null)
    {
        checker ??= new UpdateChecker(_github, null, Version.Parse(running));
        return new UpdateInstaller(
            _github, checker, DynamoHome.Resolve(), _launcher, new UpdateInstallerOptions(service, 4040),
            NullLogger<UpdateInstaller>.Instance, _ => builtVersion ?? new Version(1, 3, 0));
    }

    // ---- The checksum -----------------------------------------------------------------

    [Test]
    public void TheChecksum_IsReadFromTheReleaseNotes_ForTheRightFile()
    {
        var hash = new string('a', 64);
        var other = new string('b', 64);
        var notes = $"### Checksum (SHA-256)\n`{other}`  `Dynamo-1.2.0.zip`\n`{hash}`  `{Asset}`\n";

        Assert.That(UpdateChecker.ChecksumInNotes(notes, Asset), Is.EqualTo(hash));
        Assert.That(UpdateChecker.ChecksumInNotes(notes, "Dynamo-9.9.9.zip"), Is.Null);
        Assert.That(UpdateChecker.ChecksumInNotes(null, Asset), Is.Null);
        Assert.That(UpdateChecker.ChecksumInNotes($"{hash}0 {Asset}", Asset), Is.Null, "65 hex digits are not a SHA-256");
    }

    [Test]
    public async Task TheChecksum_GitHubGivesForTheAsset_IsPreferred()
    {
        var digest = new string('c', 64);
        Serve(new string('d', 64), digest: "sha256:" + digest);

        var status = await new UpdateChecker(_github, null, new Version(1, 2, 0)).CheckAsync();

        Assert.That(status.Latest!.Sha256, Is.EqualTo(digest));
    }

    // ---- What it installs ---------------------------------------------------------------

    [Test]
    public async Task AGoodRelease_IsDownloaded_Checked_AndHandedToTheScript()
    {
        var outcome = await Installer().InstallAsync();

        Assert.That(outcome.Error, Is.Null);
        Assert.That(outcome.Started, Is.True);
        Assert.That(outcome.Version, Is.EqualTo("1.3.0"));
        Assert.That(File.Exists(_launcher.Script), Is.True, "the script was written");
        Assert.That(File.ReadAllText(_launcher.Script!), Does.Contain("robocopy"));
        var args = _launcher.Arguments;
        string Value(string name) => args[args.IndexOf(name) + 1];
        Assert.That(Value("-NewVersion"), Is.EqualTo("1.3.0"));
        Assert.That(Value("-OldVersion"), Is.EqualTo("1.2.0"));
        Assert.That(Value("-Port"), Is.EqualTo("4040"));
        Assert.That(Value("-DataDir"), Is.EqualTo(DynamoHome.Resolve().Path));
        Assert.That(File.Exists(Path.Combine(Value("-Source"), "Dynamo.exe")), Is.True, "the new program is extracted");
        Assert.That(_github.Urls, Does.Contain(AssetUrl));
    }

    [Test]
    public async Task ANotesChecksum_IsEnough_WhenGitHubGivesNone()
    {
        Serve(Sha(_zip).ToUpperInvariant());

        Assert.That((await Installer().InstallAsync()).Started, Is.True, "case does not matter");
    }

    [Test]
    public async Task AFileThatDoesNotMatchTheChecksum_IsNeverInstalled()
    {
        Serve(new string('0', 64));

        var outcome = await Installer().InstallAsync();

        Assert.That(outcome.Started, Is.False);
        Assert.That(outcome.Error, Does.Contain("does not match"));
        Assert.That(_launcher.Script, Is.Null);
        Assert.That(Directory.GetFiles(DynamoHome.Resolve().Path, "*.zip", SearchOption.AllDirectories), Is.Empty,
            "the bad download is deleted");
    }

    [Test]
    public async Task ARelease_WithoutAChecksum_IsNeverInstalledAutomatically()
    {
        Serve(null);

        var outcome = await Installer().InstallAsync();

        Assert.That(outcome.Started, Is.False);
        Assert.That(outcome.Error, Does.Contain("checksum"));
        Assert.That(_github.Urls.Any(u => u.EndsWith(".zip")), Is.False, "nothing was even downloaded");
    }

    [Test]
    public async Task OnlyThisProjectsOwnZipAddress_IsAccepted()
    {
        foreach (var bad in new[]
        {
            "https://github.com/Fiorina161/Dynamo/releases/download/v1.3.0/other.zip",
            "https://github.com/Fiorina161/Dynamo/releases/download/v1.3.0/Dynamo-9.9.9.zip",
            "https://github.com/Fiorina161/Dynamo/archive/refs/tags/v1.3.0.zip",
            "http://github.com/Fiorina161/Dynamo/releases/download/v1.3.0/" + Asset,
            "https://evil.example/Fiorina161/Dynamo/releases/download/v1.3.0/" + Asset,
            "https://github.com/someone-else/Dynamo/releases/download/v1.3.0/" + Asset,
        })
        {
            Serve(Sha(_zip), downloadUrl: bad);
            var outcome = await Installer().InstallAsync();
            Assert.That(outcome.Started, Is.False, bad);
            Assert.That(_launcher.Script, Is.Null, bad);
        }
    }

    [Test]
    public async Task NoOlderOrSameVersion_IsEverInstalled()
    {
        var outcome = await Installer(running: "1.3.0").InstallAsync();
        Assert.That(outcome.Started, Is.False);
        Assert.That(outcome.Error, Does.Contain("up to date"));

        outcome = await Installer(running: "2.0.0").InstallAsync();
        Assert.That(outcome.Started, Is.False, "a downgrade is refused too");
        Assert.That(_launcher.Script, Is.Null);
    }

    [Test]
    public async Task APackage_WhoseProgramIsAnotherVersion_IsRefused()
    {
        var outcome = await Installer(builtVersion: new Version(1, 2, 9)).InstallAsync();

        Assert.That(outcome.Started, Is.False);
        Assert.That(outcome.Error, Does.Contain("not version 1.3.0"));
        Assert.That(_launcher.Script, Is.Null);
    }

    [Test]
    public async Task APackage_WithoutTheProgram_IsRefused()
    {
        _zip = MakeZip(exePath: "Dynamo/elsewhere/Dynamo.exe");
        Serve(Sha(_zip));

        var outcome = await Installer().InstallAsync();

        Assert.That(outcome.Started, Is.False);
        Assert.That(outcome.Error, Does.Contain("app\\Dynamo.exe"));
    }

    [Test]
    public async Task AConsoleRun_CannotReplaceItself_AndAsksGitHubForNothing()
    {
        var outcome = await Installer(service: false).InstallAsync();

        Assert.That(outcome.Started, Is.False);
        Assert.That(outcome.Error, Does.Contain("not running as a Windows service"));
        Assert.That(_github.Urls, Is.Empty);
    }

    [Test]
    public async Task AFailedDownload_IsAMessage_NotACrash()
    {
        var real = _github.Respond;
        _github.Respond = r => r.RequestUri!.AbsoluteUri.Contains("api.github.com")
            ? real(r) : new HttpResponseMessage(HttpStatusCode.InternalServerError);

        var outcome = await Installer().InstallAsync();

        Assert.That(outcome.Started, Is.False);
        Assert.That(outcome.Error, Does.Contain("could not be prepared"));
    }

    // ---- The Settings page --------------------------------------------------------------

    private async Task<(WebApplicationFactory<Program> Factory, HttpClient Local, HttpClient Remote)> Site(bool service)
    {
        var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b => b.ConfigureServices(s =>
        {
            s.AddTransient<IStartupFilter, FakeClientAddress>();
            s.AddSingleton(new HttpsOptions(0));       // no http -> https redirect for the other computer
            s.AddSingleton<IHttpClientFactory>(_github);
            s.AddSingleton(sp => new UpdateChecker(_github, null, new Version(1, 2, 0)));
            s.AddSingleton<IUpdateLauncher>(_launcher);
            s.AddSingleton(new UpdateInstallerOptions(service, 4040));
            s.AddSingleton(sp => new UpdateInstaller(
                _github, sp.GetRequiredService<UpdateChecker>(), sp.GetRequiredService<DynamoHome>(), _launcher,
                sp.GetRequiredService<UpdateInstallerOptions>(), NullLogger<UpdateInstaller>.Instance,
                _ => new Version(1, 3, 0)));
        }));
        var local = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        await local.GetStringAsync("/Bookmarks");
        var remote = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        remote.DefaultRequestHeaders.Add("X-Test-Client", "192.168.50.77");
        return (factory, local, remote);
    }

    private static async Task<HttpResponseMessage> PostSettings(HttpClient client, string handler)
    {
        var html = await client.GetStringAsync("/Settings");
        var token = Regex.Match(html, "__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value;
        return await client.PostAsync($"/Settings?handler={handler}", new FormUrlEncodedContent(
            [KeyValuePair.Create("__RequestVerificationToken", token)]));
    }

    [Test]
    public async Task TheUpdateButton_IsOnlyOfferedHere_AndWorks()
    {
        var (factory, local, remote) = await Site(service: true);
        using var _ = factory; using var __ = local; using var ___ = remote;
        await PostSettings(local, "CheckUpdates");                // the new version becomes known

        var page = await local.GetStringAsync("/Settings");
        Assert.That(page, Does.Contain("handler=InstallUpdate").And.Contain("Update now to 1.3.0"));
        Assert.That(page, Does.Contain("Download 1.3.0"), "the manual download stays");
        Assert.That(page, Does.Contain("github.com/Fiorina161/Dynamo</a>"), "so does the link to the repository");

        var started = await PostSettings(local, "InstallUpdate");
        var html = await started.Content.ReadAsStringAsync();
        Assert.That(started.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(html, Does.Contain("data-wait-restart").And.Contain("Installing Dynamo 1.3.0"));
        // The page finishes by the version the new site reports, not by catching the restart.
        Assert.That(html, Does.Contain("data-wait-version=\"1.3.0\""));
        Assert.That(html, Does.Not.Contain("Update now to"), "no second button while it installs");
        Assert.That(_launcher.Script, Is.Not.Null);
    }

    [Test]
    public async Task AnotherComputer_CannotStartAnUpdate_AndIsNotOfferedTheButton()
    {
        var (factory, local, remote) = await Site(service: true);
        using var _ = factory; using var __ = local; using var ___ = remote;
        await PostSettings(local, "CheckUpdates");
        // A password so the other computer may log in at all, then let it in.
        var passwords = factory.Services.GetRequiredService<PasswordService>();
        passwords.Set("correct horse battery");
        var login = await remote.GetStringAsync("/Login");
        var token = Regex.Match(login, "__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value;
        var logged = await remote.PostAsync("/Login", new FormUrlEncodedContent(
        [
            KeyValuePair.Create("password", "correct horse battery"),
            KeyValuePair.Create("__RequestVerificationToken", token),
        ]));
        Assert.That((int)logged.StatusCode, Is.AnyOf(200, 302), "the other computer is logged in");

        var page = await remote.GetStringAsync("/Settings");
        Assert.That(page, Does.Not.Contain("handler=InstallUpdate"));
        Assert.That(page, Does.Contain("Updating is done from the computer where Dynamo runs"));

        var attempt = await PostSettings(remote, "InstallUpdate");
        Assert.That(attempt.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
        Assert.That(_launcher.Script, Is.Null, "nothing was started");
    }

    [Test]
    public async Task AConsoleRun_ExplainsItself_InsteadOfOfferingTheButton()
    {
        var (factory, local, remote) = await Site(service: false);
        using var _ = factory; using var __ = local; using var ___ = remote;
        await PostSettings(local, "CheckUpdates");

        var page = await local.GetStringAsync("/Settings");

        Assert.That(page, Does.Not.Contain("handler=InstallUpdate"));
        Assert.That(page, Does.Contain("console program").And.Contain("Download 1.3.0"));
    }

    // ---- The script, on temporary folders (no service) ----------------------------------

    private string Script() => Path.Combine(AppContext.BaseDirectory, "apply-update.ps1");

    private (string Install, string Data, string Source) Folders(string oldContent = "OLD", string newContent = "NEW")
    {
        var root = Path.Combine(_home, "script-" + Guid.NewGuid().ToString("N"));
        var install = Path.Combine(root, "install");
        var data = Path.Combine(root, "data");
        var source = Path.Combine(root, "source");
        foreach (var d in new[] { install, data, source })
            Directory.CreateDirectory(d);
        File.WriteAllText(Path.Combine(install, "Dynamo.exe"), oldContent);
        File.WriteAllText(Path.Combine(install, "stale.dll"), "stale");
        File.WriteAllText(Path.Combine(source, "Dynamo.exe"), newContent);
        File.WriteAllText(Path.Combine(source, "fresh.dll"), "fresh");
        File.WriteAllText(Path.Combine(data, "dynamo.db"), "data");
        return (install, data, source);
    }

    private static (int Exit, string Output) RunScript(string script, params string[] arguments)
    {
        var info = new System.Diagnostics.ProcessStartInfo("powershell.exe")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", script })
            info.ArgumentList.Add(a);
        foreach (var a in arguments)
            info.ArgumentList.Add(a);
        using var process = System.Diagnostics.Process.Start(info)!;
        var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit(120_000);
        return (process.ExitCode, output);
    }

    [Test]
    public async Task TheScriptIsEmbeddedAndMatchesTheFileInTheSource()
    {
        // The installer writes the embedded copy out: it must be the script in the repository.
        var outcome = await Installer().InstallAsync();
        Assert.That(outcome.Started, Is.True);
        var embedded = File.ReadAllText(_launcher.Script!).TrimStart('﻿');
        var source = File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "Dynamo", "Modules", "Settings", "apply-update.ps1"))
            .TrimStart('﻿');
        Assert.That(embedded.ReplaceLineEndings(), Is.EqualTo(source.ReplaceLineEndings()));
    }

    [Test]
    public async Task TheScript_ReplacesTheFiles_KeepsABackupOfTheDatabase_AndReportsOk()
    {
        var (install, data, source) = Folders();
        await Installer().InstallAsync();                         // gives the script a place to be written
        using var listener = new HttpListener();
        var port = FreePort();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        _ = Task.Run(() =>
        {
            while (listener.IsListening)
            {
                try { listener.GetContext().Response.Close(); } catch { return; }
            }
        });

        var (exit, output) = RunScript(_launcher.Script!,
            "-ServicePid", "1", "-Source", source, "-InstallDir", install, "-DataDir", data,
            "-OldVersion", "1.2.0", "-NewVersion", "1.3.0", "-NoService", "-DelaySeconds", "0",
            "-HealthUrl", $"http://127.0.0.1:{port}/", "-HealthSeconds", "10");

        Assert.That(exit, Is.EqualTo(0), output);
        Assert.That(File.ReadAllText(Path.Combine(install, "Dynamo.exe")), Is.EqualTo("NEW"));
        Assert.That(File.Exists(Path.Combine(install, "fresh.dll")), Is.True);
        Assert.That(File.Exists(Path.Combine(install, "stale.dll")), Is.False, "a mirror: old leftovers go");
        Assert.That(File.ReadAllText(Path.Combine(data, "update", "last-result.txt")), Does.StartWith("ok 1.3.0"));
        Assert.That(File.ReadAllText(Path.Combine(data, "dynamo.db")), Is.EqualTo("data"), "the database is untouched");
        Assert.That(File.ReadAllText(Path.Combine(data, "update.log")), Does.Contain("Updated to 1.3.0"));
    }

    [Test]
    public async Task TheScript_PutsTheOldVersionBack_WhenTheNewOneDoesNotAnswer()
    {
        var (install, data, source) = Folders();
        await Installer().InstallAsync();

        var (exit, output) = RunScript(_launcher.Script!,
            "-ServicePid", "1", "-Source", source, "-InstallDir", install, "-DataDir", data,
            "-OldVersion", "1.2.0", "-NewVersion", "1.3.0", "-NoService", "-DelaySeconds", "0",
            "-HealthUrl", $"http://127.0.0.1:{FreePort()}/", "-HealthSeconds", "3");

        Assert.That(exit, Is.Not.EqualTo(0));
        Assert.That(File.ReadAllText(Path.Combine(install, "Dynamo.exe")), Is.EqualTo("OLD"), output);
        Assert.That(File.Exists(Path.Combine(install, "stale.dll")), Is.True, "the old files are back");
        Assert.That(File.Exists(Path.Combine(install, "fresh.dll")), Is.False);
        var result = File.ReadAllText(Path.Combine(data, "update", "last-result.txt"));
        Assert.That(result, Does.StartWith("failed").And.Contain("previous version is running again"));
        // The Settings page reads the same file from the program's own data folder.
        Environment.SetEnvironmentVariable("DYNAMO_HOME", data);
        var seen = new UpdateInstaller(_github, new UpdateChecker(_github), DynamoHome.Resolve(), _launcher,
            new UpdateInstallerOptions(true, 4040), NullLogger<UpdateInstaller>.Instance).LastFailure;
        Assert.That(seen, Does.Contain("not installed"), "the Settings page can tell the user");
    }

    [Test]
    public async Task TheScript_RefusesASourceWithoutTheProgram_AndChangesNothing()
    {
        var (install, data, source) = Folders();
        File.Delete(Path.Combine(source, "Dynamo.exe"));
        await Installer().InstallAsync();

        var (exit, _) = RunScript(_launcher.Script!,
            "-ServicePid", "1", "-Source", source, "-InstallDir", install, "-DataDir", data,
            "-NewVersion", "1.3.0", "-NoService", "-DelaySeconds", "0");

        Assert.That(exit, Is.Not.EqualTo(0));
        Assert.That(File.ReadAllText(Path.Combine(install, "Dynamo.exe")), Is.EqualTo("OLD"));
    }

    private static int FreePort()
    {
        var l = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }
}
