using System.Net;
using System.Text.RegularExpressions;
using Dapper;
using Dynamo.Core;
using Dynamo.Modules.Shares;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Dynamo.Tests;

/// <summary>
/// Shared folders: what a folder path may be, how a path inside it is made safe, listing, sorting, paging, the
/// download, and the dialog that saves the account. A fake connector stands in for the network login.
/// </summary>
public class SharedFolderTests
{
    private string _home = "";
    private string _files = "";
    private string? _previousHome;
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;
    private string _token = "";
    private FakeConnector _connector = null!;

    private sealed class FakeConnector : IShareConnector
    {
        public List<(string Root, string User, string Password)> Calls = [];
        public string? Failure;

        public string? Connect(string shareRoot, string username, string password)
        {
            Calls.Add((shareRoot, username, password));
            return Failure;
        }
    }

    [OneTimeSetUp]
    public void SetUp()
    {
        _previousHome = Environment.GetEnvironmentVariable("DYNAMO_HOME");
        _home = Path.Combine(Path.GetTempPath(), "dynamo-shares-" + Guid.NewGuid().ToString("N"));
        _files = Path.Combine(Path.GetTempPath(), "dynamo-share-files-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_files);
        Environment.SetEnvironmentVariable("DYNAMO_HOME", _home);
        _connector = new FakeConnector();
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            b.ConfigureServices(s => s.AddSingleton<IShareConnector>(_connector)));
        _client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        _token = Regex.Match(_client.GetStringAsync("/Shares").Result,
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
        try { Directory.Delete(_files, true); } catch { /* best effort */ }
    }

    [SetUp]
    public void Reset()
    {
        _connector.Calls.Clear();
        _connector.Failure = null;
    }

    private SharedFolderRepository Repo => _factory.Services.GetRequiredService<SharedFolderRepository>();
    private IShareBrowser Browser => _factory.Services.GetRequiredService<IShareBrowser>();

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

    /// <summary>A real folder on disk with a few files, registered as a shared folder (a local path: no login).</summary>
    private (SharedFolder Folder, string Dir) Local(string name)
    {
        var dir = Path.Combine(_files, name);
        Directory.CreateDirectory(dir);
        var id = Repo.Save(new SharedFolder { Name = name, Path = dir, Username = "u", Password = "p" });
        return (Repo.Get(id)!, dir);
    }

    private static void File(string dir, string name, int size, DateTime? when = null)
    {
        var path = Path.Combine(dir, name);
        System.IO.File.WriteAllBytes(path, new byte[size]);
        if (when is { } w) System.IO.File.SetLastWriteTime(path, w);
    }

    // ---- What a folder path may be -------------------------------------------------------------------------

    [TestCase(@"\\fileserver\share\traces", @"\\fileserver\share\traces")]
    [TestCase(@"//host/share/", @"\\host\share")]
    [TestCase(@"  \\host\share\sub folder\  ", @"\\host\share\sub folder")]
    [TestCase(@"\\fileserver.corp.example\data$", @"\\fileserver.corp.example\data$")]
    public void ANetworkFolderIsAccepted_AndNormalised(string typed, string expected)
    {
        var (path, error) = SharePath.Parse(typed);

        Assert.That(error, Is.Null);
        Assert.That(path, Is.EqualTo(expected));
    }

    [TestCase("")]
    [TestCase("   ")]
    [TestCase(@"C:\Windows")]
    [TestCase(@"D:\data\share")]
    [TestCase(@"relative\path")]
    [TestCase(@"\\host")]
    [TestCase(@"\\host\")]
    [TestCase(@"\\\host\share")]
    [TestCase(@"\\?\C:\Windows")]
    [TestCase(@"\\.\pipe\x")]
    [TestCase(@"\\host\share\..\other")]
    [TestCase(@"\\host\share\a:b")]
    [TestCase(@"\\host\sh*are")]
    [TestCase(@"\\host\share\a|b")]
    [TestCase(@"\\host\\share")]
    public void ANotNetworkFolder_OrOneWithForbiddenCharacters_IsRefused(string typed)
    {
        var (path, error) = SharePath.Parse(typed);

        Assert.That(path, Is.Null);
        Assert.That(error, Is.Not.Null.And.Not.Empty);
    }

    [Test]
    public void ATooLongPath_IsRefused()
    {
        Assert.That(SharePath.Parse(@"\\host\share\" + new string('a', 500)).Error, Does.Contain("too long"));
    }

    [Test]
    public void TheShareRoot_IsServerAndShare()
    {
        Assert.That(SharePath.ShareRoot(@"\\fileserver\share\traces"), Is.EqualTo(@"\\fileserver\share"));
        Assert.That(SharePath.ShareRoot(@"\\host\share"), Is.EqualTo(@"\\host\share"));
    }

    // ---- A path inside the folder: nothing outside is reachable ---------------------------------------------

    [Test]
    public void APathInsideTheFolder_IsResolvedAndCleaned()
    {
        var root = Path.Combine(_files, "resolve");

        Assert.That(SharePath.Resolve(root, "", out var c0), Is.EqualTo(root));
        Assert.That(c0, Is.EqualTo(""));
        Assert.That(SharePath.Resolve(root, "a/b\\c.txt", out var c1), Is.EqualTo(Path.Combine(root, "a", "b", "c.txt")));
        Assert.That(c1, Is.EqualTo(@"a\b\c.txt"));
        Assert.That(SharePath.Resolve(root + "\\", "\\a\\\\b\\", out var c2), Is.EqualTo(Path.Combine(root, "a", "b")));
        Assert.That(c2, Is.EqualTo(@"a\b"));
    }

    [TestCase("..")]
    [TestCase(@"..\other")]
    [TestCase(@"a\..\..\other")]
    [TestCase("a/../../b")]
    [TestCase(".")]
    [TestCase(@"a\.\b")]
    [TestCase(@"C:\Windows\System32")]
    [TestCase("C:")]
    [TestCase("file.txt:hidden")]
    [TestCase("a?b")]
    [TestCase("a*b")]
    [TestCase("...")]
    [TestCase("a|b")]
    public void APathThatLeavesTheFolder_OrIsNotAFileName_IsRefused(string relative)
    {
        Assert.That(SharePath.Resolve(Path.Combine(_files, "resolve"), relative, out _), Is.Null);
    }

    [Test]
    public void ARootedOrUncPath_IsReadAsRelative_SoItStaysInsideTheFolder()
    {
        var root = Path.Combine(_files, "resolve");

        Assert.That(SharePath.Resolve(root, @"\\other\share\x", out var clean), Is.EqualTo(Path.Combine(root, "other", "share", "x")));
        Assert.That(clean, Is.EqualTo(@"other\share\x"));
    }

    // ---- Listing -------------------------------------------------------------------------------------------

    [Test]
    public async Task AListing_PutsFoldersFirst_ThenSortsByNameSizeOrDate_BothWays()
    {
        var (folder, dir) = Local("sorting");
        Directory.CreateDirectory(Path.Combine(dir, "zeta-dir"));
        Directory.CreateDirectory(Path.Combine(dir, "alpha-dir"));
        foreach (var d in new[] { "zeta-dir", "alpha-dir" })
            Directory.SetLastWriteTime(Path.Combine(dir, d), new DateTime(2026, 1, 1));     // the same: the name decides
        File(dir, "b.log", 300, new DateTime(2026, 1, 3));
        File(dir, "a.log", 100, new DateTime(2026, 1, 2));
        File(dir, "c.log", 200, new DateTime(2026, 1, 1));

        async Task<string> Names(string? sort, bool desc) =>
            string.Join(",", (await Browser.ListAsync(folder, null, null, sort, desc, 1, default)).Entries.Select(e => e.Name));

        Assert.That(await Names(null, false), Is.EqualTo("alpha-dir,zeta-dir,a.log,b.log,c.log"));
        Assert.That(await Names("name", true), Is.EqualTo("zeta-dir,alpha-dir,c.log,b.log,a.log"));
        Assert.That(await Names("size", false), Is.EqualTo("alpha-dir,zeta-dir,a.log,c.log,b.log"));
        Assert.That(await Names("size", true), Is.EqualTo("alpha-dir,zeta-dir,b.log,c.log,a.log"));
        Assert.That(await Names("date", false), Is.EqualTo("alpha-dir,zeta-dir,c.log,a.log,b.log"));
        Assert.That(await Names("date", true), Is.EqualTo("alpha-dir,zeta-dir,b.log,a.log,c.log"));

        var listing = await Browser.ListAsync(folder, null, null, null, false, 1, default);
        Assert.That(listing.Entries.Single(e => e.Name == "b.log").Size, Is.EqualTo(300));
        Assert.That(listing.Entries.Single(e => e.Name == "alpha-dir").IsDirectory, Is.True);
    }

    [Test]
    public async Task TheFilter_KeepsTheNamesThatContainTheText_IgnoringCase()
    {
        var (folder, dir) = Local("filtering");
        File(dir, "trace_2026-11-01.zip", 10);
        File(dir, "TRACE_2026-11-02.zip", 10);
        File(dir, "other.txt", 10);

        var listing = await Browser.ListAsync(folder, null, "trace", null, false, 1, default);

        Assert.That(listing.Entries.Select(e => e.Name), Is.EqualTo(new[] { "trace_2026-11-01.zip", "TRACE_2026-11-02.zip" }));
        Assert.That(listing.Total, Is.EqualTo(2));
    }

    [Test]
    public async Task ALongFolder_IsCutIntoPages_AndAPageOutOfRangeIsClamped()
    {
        var (folder, dir) = Local("paging");
        for (var i = 0; i < 450; i++) File(dir, $"f{i:D4}.txt", 1);

        var first = await Browser.ListAsync(folder, null, null, null, false, 1, default);
        var last = await Browser.ListAsync(folder, null, null, null, false, 3, default);
        var beyond = await Browser.ListAsync(folder, null, null, null, false, 99, default);
        var below = await Browser.ListAsync(folder, null, null, null, false, -4, default);

        Assert.That((first.Total, first.Pages, first.Entries.Count), Is.EqualTo((450, 3, ShareBrowser.PageSize)));
        Assert.That(last.Entries.Count, Is.EqualTo(50));
        Assert.That(last.Entries[0].Name, Is.EqualTo("f0400.txt"));
        Assert.That(beyond.Page, Is.EqualTo(3), "a page too far shows the last one");
        Assert.That(below.Page, Is.EqualTo(1));
    }

    [Test]
    public async Task ASubFolder_IsListedWithItsBreadcrumb_AndEntriesCarryTheirRelativePath()
    {
        var (folder, dir) = Local("nested");
        Directory.CreateDirectory(Path.Combine(dir, "2026", "11"));
        File(Path.Combine(dir, "2026", "11"), "trace.zip", 5);

        var top = await Browser.ListAsync(folder, null, null, null, false, 1, default);
        var inside = await Browser.ListAsync(folder, @"2026\11", null, null, false, 1, default);

        Assert.That(top.Entries.Single().Relative, Is.EqualTo("2026"));
        Assert.That(inside.Entries.Single().Relative, Is.EqualTo(@"2026\11\trace.zip"));
        Assert.That(inside.Breadcrumb, Is.EqualTo(new[] { ("2026", "2026"), ("11", @"2026\11") }));
        Assert.That(inside.Relative, Is.EqualTo(@"2026\11"));
    }

    [Test]
    public async Task AFolderThatDoesNotExist_OrIsOutsideTheRoot_IsAnExplainedRefusal()
    {
        var (folder, _) = Local("refusals");

        var missing = await Assert.ThrowsAsync<ShareException>(() => Browser.ListAsync(folder, "nothing-here", null, null, false, 1, default));
        var outside = await Assert.ThrowsAsync<ShareException>(() => Browser.ListAsync(folder, @"..\other", null, null, false, 1, default));

        Assert.That(missing!.Message, Does.Contain("does not exist"));
        Assert.That(outside!.Message, Does.Contain("not inside the folder"));
    }

    [Test]
    public async Task AnEmptyFolder_IsAnEmptyListing()
    {
        var (folder, _) = Local("empty");

        var listing = await Browser.ListAsync(folder, null, null, null, false, 1, default);

        Assert.That(listing.Total, Is.EqualTo(0));
        Assert.That(listing.Pages, Is.EqualTo(1));
    }

    // ---- Opening a file ---------------------------------------------------------------------------------

    [Test]
    public async Task AFileIsOpenedForReading_WhileSomeoneElseStillHasItOpen()
    {
        var (folder, dir) = Local("opening");
        await System.IO.File.WriteAllTextAsync(Path.Combine(dir, "trace.log"), "hello trace");
        using var writer = new FileStream(Path.Combine(dir, "trace.log"), FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);

        var download = await Browser.OpenAsync(folder, "trace.log", default);
        await using var stream = download.Stream;
        using var reader = new StreamReader(stream);

        Assert.That(download.Name, Is.EqualTo("trace.log"));
        Assert.That(download.Length, Is.EqualTo(11));
        Assert.That(await reader.ReadToEndAsync(), Is.EqualTo("hello trace"));
    }

    [Test]
    public async Task OnlyAFileInsideTheFolderCanBeOpened()
    {
        var (folder, dir) = Local("opening-refused");
        Directory.CreateDirectory(Path.Combine(dir, "sub"));

        await Assert.ThrowsAsync<ShareException>(() => Browser.OpenAsync(folder, "sub", default));            // a directory
        await Assert.ThrowsAsync<ShareException>(() => Browser.OpenAsync(folder, "no-such.txt", default));
        await Assert.ThrowsAsync<ShareException>(() => Browser.OpenAsync(folder, @"..\secret.txt", default));
        await Assert.ThrowsAsync<ShareException>(() => Browser.OpenAsync(folder, @"C:\Windows\win.ini", default));
    }

    // ---- The login ---------------------------------------------------------------------------------------

    [Test]
    public async Task ANetworkFolder_LogsInToItsShare_WithTheSavedAccount_BeforeReading()
    {
        var folder = new SharedFolder { Name = "net", Path = @"\\127.0.0.1\dynamo-test-share\sub", Username = @"CORP\alain", Password = "pw-1" };
        _connector.Failure = "The user name or password is incorrect.";

        var ex = await Assert.ThrowsAsync<ShareException>(() => Browser.ListAsync(folder, null, null, null, false, 1, default));

        Assert.That(_connector.Calls, Is.EqualTo(new[] { (@"\\127.0.0.1\dynamo-test-share", @"CORP\alain", "pw-1") }));
        Assert.That(ex!.Message, Does.Contain(@"Could not connect to \\127.0.0.1\dynamo-test-share").And.Contain("password is incorrect"));
        Assert.That(ex.Message, Does.Not.Contain("pw-1"));
    }

    [Test]
    public async Task AFolderWithoutAnAccount_OrWithAnUnreadablePassword_IsNotTried()
    {
        var noAccount = new SharedFolder { Name = "x", Path = @"\\127.0.0.1\s", Username = "", Password = "" };
        var lost = new SharedFolder { Name = "y", Path = @"\\127.0.0.1\s", Username = "u", PasswordUnreadable = true };

        Assert.That((await Assert.ThrowsAsync<ShareException>(() => Browser.ListAsync(noAccount, null, null, null, false, 1, default)))!.Message,
            Does.Contain("No account"));
        Assert.That((await Assert.ThrowsAsync<ShareException>(() => Browser.ListAsync(lost, null, null, null, false, 1, default)))!.Message,
            Does.Contain("cannot be read on this computer"));
        Assert.That(_connector.Calls, Is.Empty);
    }

    [Test]
    public void TheRealConnector_TalksToWindows_AndReportsAnUnreachableShareAsAMessage()
    {
        // A share that does not exist, with made-up credentials: Windows refuses, and the connector says why
        // (this only proves the call into Windows works; it changes nothing on the machine).
        var message = new ShareConnector().Connect(@"\\127.0.0.1\dynamo-no-such-share-" + Guid.NewGuid().ToString("N")[..6], "nobody", "wrong");

        Assert.That(message, Is.Not.Null.And.Not.Empty);
        Assert.That(ShareConnector.Describe(1326), Does.Contain("password"));
        Assert.That(ShareConnector.Describe(1219), Does.Contain("another account"));
    }

    // ---- The dialog and the list ---------------------------------------------------------------------

    [Test]
    public async Task TheNewFolderDialog_AsksForTheNameFolderUserAndPassword()
    {
        var html = await _client.GetStringAsync("/Shares?handler=Form");

        Assert.That(html, Does.Contain("name=\"name\"").And.Contain("name=\"path\"").And.Contain("name=\"username\"").And.Contain("name=\"password\""));
        Assert.That(html, Does.Contain(@"placeholder=""e.g. \\fileserver\share\traces"""), "the example is a hint, clearly worded as one, not a value");
        Assert.That(html, Does.Not.Match(@"name=""path"" value=""[^""]"), "and the field itself starts empty");
        Assert.That(html, Does.Contain("type=\"password\""));
    }

    [Test]
    public async Task ASavedFolder_KeepsItsAccount_WithThePasswordEncrypted_AndNeverShownAgain()
    {
        var answer = await Post("/Shares?handler=Save", ("id", "0"), ("name", "Trace archives"),
            ("path", @"\\fileserver\share\traces"), ("username", @"CORP\alain"), ("password", "s3cret-pw"));
        var html = await answer.Content.ReadAsStringAsync();

        var saved = Repo.GetAll().Single(f => f.Name == "Trace archives");
        Assert.That(saved.Path, Is.EqualTo(@"\\fileserver\share\traces"));
        Assert.That(saved.Username, Is.EqualTo(@"CORP\alain"));
        Assert.That(saved.Password, Is.EqualTo("s3cret-pw"), "what the connection is made with");
        Assert.That(html, Does.Contain("Trace archives").And.Not.Contain("s3cret-pw"));
        Assert.That(answer.Headers.GetValues("HX-Trigger").Single(), Is.EqualTo("closeDialog"));

        using (var conn = _factory.Services.GetRequiredService<Db>().Open())
        {
            var raw = conn.ExecuteScalar<string>("SELECT password FROM shared_folder WHERE id = @id", new { id = saved.Id });
            Assert.That(raw, Does.StartWith(Secrets.Prefix).And.Not.Contain("s3cret-pw"));
        }
        Assert.That(await _client.GetStringAsync($"/Shares?handler=Form&id={saved.Id}"), Does.Not.Contain("s3cret-pw"));
        Assert.That(await _client.GetStringAsync("/Shares"), Does.Contain("Trace archives").And.Contain("CORP"));
    }

    [Test]
    public async Task EditingWithABlankPassword_KeepsTheStoredOne()
    {
        await Post("/Shares?handler=Save", ("id", "0"), ("name", "Keep pw"), ("path", @"\\h\s"), ("username", "u"), ("password", "first"));
        var id = Repo.GetAll().Single(f => f.Name == "Keep pw").Id;

        await Post("/Shares?handler=Save", ("id", id.ToString()), ("name", "Keep pw"), ("path", @"\\h\s\more"), ("username", "u2"), ("password", ""));

        var after = Repo.Get(id)!;
        Assert.That((after.Password, after.Path, after.Username), Is.EqualTo(("first", @"\\h\s\more", "u2")));
    }

    [Test]
    public async Task TheDialog_RefusesWhatCannotBeSaved()
    {
        async Task<string> Try(params (string, string)[] f)
        {
            var answer = await Post("/Shares?handler=Save", f);
            Assert.That(answer.Headers.GetValues("HX-Retarget").Single(), Is.EqualTo("#dlg"));
            return await answer.Content.ReadAsStringAsync();
        }

        Assert.That(await Try(("id", "0"), ("name", ""), ("path", @"\\h\s"), ("username", "u"), ("password", "p")), Does.Contain("name is required"));
        Assert.That(await Try(("id", "0"), ("name", "N"), ("path", @"C:\Windows"), ("username", "u"), ("password", "p")), Does.Contain("network folder"));
        Assert.That(await Try(("id", "0"), ("name", "N"), ("path", @"\\h\s"), ("username", ""), ("password", "p")), Does.Contain("user is required"));
        Assert.That(await Try(("id", "0"), ("name", "N"), ("path", @"\\h\s"), ("username", "u"), ("password", "")), Does.Contain("password is required"));
        Assert.That(Repo.GetAll().Select(f => f.Name), Does.Not.Contain("N"));

        await Post("/Shares?handler=Save", ("id", "0"), ("name", "Dup name"), ("path", @"\\h\s"), ("username", "u"), ("password", "p"));
        Assert.That(await Try(("id", "0"), ("name", "dup NAME"), ("path", @"\\h\s2"), ("username", "u"), ("password", "p")), Does.Contain("already exists"));
    }

    [Test]
    public async Task APasswordThatCannotBeRead_MustBeEnteredAgain()
    {
        long id;
        using (var conn = _factory.Services.GetRequiredService<Db>().Open())
            id = conn.ExecuteScalar<long>("""
                INSERT INTO shared_folder (name, path, username, password) VALUES ('Lost pw', '\\h\s', 'u', 'dpapi1:AAAAAAAA');
                SELECT last_insert_rowid();
                """);

        Assert.That(Repo.Get(id)!.PasswordUnreadable, Is.True);
        Assert.That(await _client.GetStringAsync("/Shares"), Does.Contain("password cannot be read here"));
        var refused = await Post("/Shares?handler=Save", ("id", id.ToString()), ("name", "Lost pw"), ("path", @"\\h\s"), ("username", "u"), ("password", ""));
        Assert.That(await refused.Content.ReadAsStringAsync(), Does.Contain("password is required"));

        await Post("/Shares?handler=Save", ("id", id.ToString()), ("name", "Lost pw"), ("path", @"\\h\s"), ("username", "u"), ("password", "again"));
        Assert.That(Repo.Get(id)!.PasswordUnreadable, Is.False);
        Assert.That(Repo.Get(id)!.Password, Is.EqualTo("again"));
    }

    [Test]
    public async Task ADeletedFolder_IsGone_AndNothingOnTheShareIsTouched()
    {
        var (folder, dir) = Local("deleting");
        File(dir, "keep.txt", 3);

        await Post($"/Shares?handler=Delete&id={folder.Id}");

        Assert.That(Repo.Get(folder.Id), Is.Null);
        Assert.That(System.IO.File.Exists(Path.Combine(dir, "keep.txt")), Is.True);
    }

    [Test]
    public async Task TheTestButton_ConnectsWithWhatIsTyped_AndReportsInAToast()
    {
        var folder = new SharedFolder { Name = "tmp", Path = @"\\127.0.0.1\s", Username = "u", Password = "p" };
        _connector.Failure = "Access is denied.";

        var failed = await Post("/Shares?handler=TestForm", ("id", "0"), ("name", "N"), ("path", folder.Path), ("username", "u"), ("password", "typed-pw"));
        Assert.That(failed.Headers.GetValues("HX-Trigger").Single(), Does.Contain("Access is denied"));
        Assert.That(_connector.Calls.Single().Password, Is.EqualTo("typed-pw"));

        var noPath = await Post("/Shares?handler=TestForm", ("id", "0"), ("path", "C:\\x"), ("username", "u"), ("password", "p"));
        Assert.That(noPath.Headers.GetValues("HX-Trigger").Single(), Does.Contain("network folder"));
        var noAccount = await Post("/Shares?handler=TestForm", ("id", "0"), ("path", @"\\h\s"), ("username", ""), ("password", ""));
        Assert.That(noAccount.Headers.GetValues("HX-Trigger").Single(), Does.Contain("Enter the user and the password"));
    }

    // ---- Browsing and downloading in the pages ------------------------------------------------------------

    [Test]
    public async Task TheFolderPage_ListsTheFiles_WithDownloadLinks_AndFoldersToOpen()
    {
        var (folder, dir) = Local("page");
        Directory.CreateDirectory(Path.Combine(dir, "2026"));
        File(dir, "trace one.zip", 1536, new DateTime(2026, 11, 5, 14, 30, 0));
        File(Path.Combine(dir, "2026"), "inner.log", 10);

        var page = await _client.GetStringAsync($"/Shares/{folder.Id}");

        Assert.That(page, Does.Contain("trace one.zip").And.Contain("1.5 KB").And.Contain("2026-11-05 14:30"));
        Assert.That(page, Does.Match($"href=\"/Shares/{folder.Id}\\?path=2026\""));
        Assert.That(page, Does.Match($"href=\"/Shares/{folder.Id}\\?path=trace%20one.zip&amp;handler=Download\"").Or.Match($"handler=Download"));
        Assert.That(page, Does.Contain("Filter by name"));

        var inside = await _client.GetStringAsync($"/Shares/{folder.Id}?path=2026");
        Assert.That(inside, Does.Contain("inner.log").And.Contain("class=\"crumbs\""));
        Assert.That(inside, Does.Not.Contain("trace one.zip"));
    }


    [Test]
    public async Task Folders_AreMarkedApartFromFiles_InTheTable()
    {
        var (folder, dir) = Local("folder-rows");
        Directory.CreateDirectory(Path.Combine(dir, "archive"));
        File(dir, "plain.txt", 1);

        var page = await Page(folder.Id);

        Assert.That(Regex.Matches(page, "<tr class=\"folder-row\">").Count, Is.EqualTo(1), "one row for the one folder");
        Assert.That(page, Does.Match("<tr class=\"folder-row\">\\s*<td>\\s*<a class=\"file-folder\"[^>]*>archive</a>"));
        Assert.That(page, Does.Not.Match("<tr class=\"folder-row\">\\s*<td>\\s*<a[^>]*>plain.txt"));
    }

    [Test]
    public async Task AtTheTopOfAFolder_ThereIsNoWayUp_InsideThereAreThreeWaysBack()
    {
        var (folder, dir) = Local("going-back");
        Directory.CreateDirectory(Path.Combine(dir, "2026", "11"));
        File(Path.Combine(dir, "2026", "11"), "deep.log", 1);

        var top = await Page(folder.Id);
        Assert.That(top, Does.Not.Contain("up-btn").And.Not.Contain("up-row"), "nothing above the top");
        Assert.That(top, Does.Contain("class=\"crumb-current\""));

        var level1 = await Page(folder.Id, "?path=2026");
        Assert.That(level1, Does.Match($"class=\"button ghost up-btn\" href=\"/Shares/{folder.Id}\" "), "Up goes to the top of the share");
        Assert.That(level1, Does.Match($"class=\"file-folder\" href=\"/Shares/{folder.Id}\""), "so does the .. row");
        Assert.That(level1, Does.Contain("up one level"));
        Assert.That(level1, Does.Match($"class=\"crumb\" href=\"/Shares/{folder.Id}\""), "and the breadcrumb");
        Assert.That(level1, Does.Contain("<strong class=\"crumb-current\">2026</strong>"), "where you are is not a link");

        var level2 = await Page(folder.Id, @"?path=2026%5C11");
        Assert.That(level2, Does.Match($"up-btn\" href=\"/Shares/{folder.Id}\\?path=2026\""), "Up from the second level goes to the first");
        Assert.That(level2, Does.Match($"class=\"file-folder\" href=\"/Shares/{folder.Id}\\?path=2026\""));
        Assert.That(level2, Does.Match($"class=\"crumb\" href=\"/Shares/{folder.Id}\\?path=2026\">2026</a>"));
        Assert.That(level2, Does.Contain("<strong class=\"crumb-current\">11</strong>").And.Contain("deep.log"));
    }

    [Test]
    public async Task TheUpRow_IsOnlyOnTheFirstPageOfALongFolder()
    {
        var (folder, dir) = Local("up-paging");
        Directory.CreateDirectory(Path.Combine(dir, "sub"));
        for (var i = 0; i < 230; i++) File(Path.Combine(dir, "sub"), $"f{i:D3}.txt", 1);

        Assert.That(await Page(folder.Id, "?path=sub"), Does.Contain("up-row"));
        var second = await Page(folder.Id, "?path=sub&p=2");
        Assert.That(second, Does.Not.Contain("up-row"), "not repeated on the next pages");
        Assert.That(second, Does.Contain("up-btn"), "but the Up button is always there");
    }

    [Test]
    public async Task TheFolderPage_Filters_Sorts_AndPages()
    {
        var (folder, dir) = Local("page-tools");
        for (var i = 0; i < 230; i++) File(dir, $"log{i:D3}.txt", i + 1);
        File(dir, "special.zip", 5000);

        var filtered = await _client.GetStringAsync($"/Shares/{folder.Id}?q=SPECIAL");
        Assert.That(filtered, Does.Contain("special.zip").And.Not.Contain("log001.txt").And.Contain("1 entry"));

        var first = await _client.GetStringAsync($"/Shares/{folder.Id}");
        Assert.That(first, Does.Contain("Page 1 of 2").And.Contain("Next →"));
        var second = await _client.GetStringAsync($"/Shares/{folder.Id}?p=2");
        Assert.That(second, Does.Contain("Page 2 of 2").And.Contain("← Previous"));

        var bySize = await _client.GetStringAsync($"/Shares/{folder.Id}?sort=size&dir=desc");
        Assert.That(bySize.IndexOf("special.zip", StringComparison.Ordinal), Is.LessThan(bySize.IndexOf("log229.txt", StringComparison.Ordinal)));
        Assert.That(bySize, Does.Contain("Size &#x25BC;").Or.Contain("Size ▼"));
    }

    [Test]
    public async Task ADownload_SendsTheFile_AsAnAttachment_AndCanBeResumed()
    {
        var (folder, dir) = Local("download");
        await System.IO.File.WriteAllTextAsync(Path.Combine(dir, "trace 1.log"), "0123456789");

        var whole = await _client.GetAsync($"/Shares/{folder.Id}?handler=Download&path=trace%201.log");
        Assert.That(whole.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(await whole.Content.ReadAsStringAsync(), Is.EqualTo("0123456789"));
        Assert.That(whole.Content.Headers.ContentDisposition!.DispositionType, Is.EqualTo("attachment"));
        Assert.That(whole.Content.Headers.ContentDisposition.FileNameStar ?? whole.Content.Headers.ContentDisposition.FileName, Does.Contain("trace 1.log").Or.Contain("trace%201.log"));

        var request = new HttpRequestMessage(HttpMethod.Get, $"/Shares/{folder.Id}?handler=Download&path=trace%201.log");
        request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(4, 6);
        var part = await _client.SendAsync(request);
        Assert.That(part.StatusCode, Is.EqualTo(HttpStatusCode.PartialContent));
        Assert.That(await part.Content.ReadAsStringAsync(), Is.EqualTo("456"));
    }

    [Test]
    public async Task ADownload_RefusesAnythingOutsideTheFolder_AMissingFile_AndAnUnknownFolder()
    {
        var (folder, dir) = Local("download-refused");
        await System.IO.File.WriteAllTextAsync(Path.Combine(_files, "secret.txt"), "not for you");
        Directory.CreateDirectory(Path.Combine(dir, "sub"));

        foreach (var path in new[] { "..%5Csecret.txt", "..%2Fsecret.txt", "C%3A%5CWindows%5Cwin.ini", "no-such.txt", "sub", "%2E%2E" })
        {
            var answer = await _client.GetAsync($"/Shares/{folder.Id}?handler=Download&path={path}");
            Assert.That(answer.StatusCode, Is.EqualTo(HttpStatusCode.UnprocessableEntity), path);
            Assert.That(await answer.Content.ReadAsStringAsync(), Does.Not.Contain("not for you"), path);
        }
        Assert.That((await _client.GetAsync("/Shares/999999?handler=Download&path=x")).StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
        Assert.That((await _client.GetAsync("/Shares/999999")).StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
    }

    [Test]
    public async Task APageThatCannotConnect_ShowsWhy_AndTheWayToChangeTheAccount()
    {
        var id = Repo.Save(new SharedFolder { Name = "Unreachable", Path = @"\\127.0.0.1\unreachable", Username = "u", Password = "p" });
        _connector.Failure = "The network path was not found.";

        var page = await _client.GetStringAsync($"/Shares/{id}");

        Assert.That(page, Does.Contain("Could not connect to").And.Contain("network path was not found"));
        Assert.That(page, Does.Contain("Edit"));
        Assert.That(page, Does.Not.Contain("class=\"files-table\""));
    }


    // ---- The substring filters ---------------------------------------------------------------------------

    private static string Names(ShareListing listing) => string.Join(",", listing.Entries.Select(e => e.Name));

    [Test]
    public async Task TheTickedSubstrings_KeepOnlyTheFilesContainingAnyOfThem_FoldersAlwaysStay()
    {
        var (folder, dir) = Local("anyof");
        Directory.CreateDirectory(Path.Combine(dir, "sub"));
        File(dir, "trace_PROD_01.zip", 1);
        File(dir, "trace_TEST_01.zip", 1);
        File(dir, "TRACE_prod_02.zip", 1);
        File(dir, "report.txt", 1);
        File(dir, "error.log", 1);

        async Task<string> List(string? q, params string[] anyOf) =>
            Names(await Browser.ListAsync(folder, null, q, null, false, 1, default, anyOf));

        Assert.That(await List(null), Is.EqualTo("sub,error.log,report.txt,trace_PROD_01.zip,TRACE_prod_02.zip,trace_TEST_01.zip"), "none ticked: everything");
        Assert.That(await List(null, "_prod_"), Is.EqualTo("sub,trace_PROD_01.zip,TRACE_prod_02.zip"), "one substring, case ignored, the folder stays");
        Assert.That(await List(null, "_PROD_", ".log"), Is.EqualTo("sub,error.log,trace_PROD_01.zip,TRACE_prod_02.zip"), "any of the ticked ones");
        Assert.That(await List(null, "nothing-like-this"), Is.EqualTo("sub"), "no file matches: only the folder is left");
        Assert.That(await List("01", "_PROD_", "_TEST_"), Is.EqualTo("trace_PROD_01.zip,trace_TEST_01.zip"), "together with the name filter box: both must hold");
        Assert.That(await List(null, ""), Is.EqualTo("sub,error.log,report.txt,trace_PROD_01.zip,TRACE_prod_02.zip,trace_TEST_01.zip"), "an empty substring filters nothing");
    }

    [Test]
    public void TheFilterListOfAFolder_IsAddedChangedAndRemoved_WithoutDuplicatesOrBlanks()
    {
        var (folder, _) = Local("filter-repo");
        var other = Local("filter-repo-other").Folder;

        var a = Repo.SaveFilter(folder.Id, 0, "_PROD_");
        var b = Repo.SaveFilter(folder.Id, 0, ".zip");
        var c = Repo.SaveFilter(other.Id, 0, "elsewhere");

        Assert.That(Repo.GetFilters(folder.Id).Select(x => x.Text), Is.EqualTo(new[] { "_PROD_", ".zip" }), "in the order they were added");
        Assert.That(Repo.GetFilters(other.Id).Select(x => x.Text), Is.EqualTo(new[] { "elsewhere" }), "each folder has its own list");

        Repo.SetFilterActive(a, true);
        Repo.SaveFilter(folder.Id, a, "_LIVE_");
        var changed = Repo.GetFilter(a)!;
        Assert.That((changed.Text, changed.Active), Is.EqualTo(("_LIVE_", true)), "changing the text keeps the tick and the place");
        Assert.That(Repo.GetFilters(folder.Id).First().Id, Is.EqualTo(a));

        Assert.That(Repo.FilterProblem(folder.Id, "", 0), Does.Contain("Type a substring"));
        Assert.That(Repo.FilterProblem(folder.Id, new string('x', 101), 0), Does.Contain("too long"));
        Assert.That(Repo.FilterProblem(folder.Id, ".ZIP", 0), Does.Contain("already in the list"), "case does not make it another one");
        Assert.That(Repo.FilterProblem(folder.Id, ".zip", b), Is.Null, "a filter is not a duplicate of itself");
        Assert.That(Repo.FilterProblem(other.Id, ".zip", 0), Is.Null, "another folder may have the same");

        Repo.ClearActiveFilters(folder.Id);
        Assert.That(Repo.GetFilters(folder.Id).Any(x => x.Active), Is.False);
        Repo.DeleteFilter(b);
        Assert.That(Repo.GetFilters(folder.Id).Select(x => x.Text), Is.EqualTo(new[] { "_LIVE_" }));
        Assert.That(Repo.GetFilter(c), Is.Not.Null, "another folder's list is untouched");
    }

    [Test]
    public void DeletingAFolder_DeletesItsFilterList()
    {
        var (folder, _) = Local("filter-cascade");
        var id = Repo.SaveFilter(folder.Id, 0, "x");

        Repo.Delete(folder.Id);

        Assert.That(Repo.GetFilter(id), Is.Null);
    }


    [Test]
    public async Task TheSubstrings_IgnoreCaseAndAccents_SoEAcuteEGraveAndEAllMatchE()
    {
        var (folder, dir) = Local("accents");
        File(dir, "résumé_été.txt", 1);
        File(dir, "RESUME_ETE.log", 1);
        File(dir, "Resumè-ÉTÉ.csv", 1);
        File(dir, "other.txt", 1);
        File(dir, "çà et là.txt", 1);
        File(dir, "œuvre.txt", 1);

        async Task<string> List(string? q, params string[] anyOf) =>
            Names(await Browser.ListAsync(folder, null, q, "name", false, 1, default, anyOf));

        Assert.That(await List(null, "ete"), Is.EqualTo("RESUME_ETE.log,Resumè-ÉTÉ.csv,résumé_été.txt"), "plain letters find the accented names");
        Assert.That(await List(null, "été"), Is.EqualTo("RESUME_ETE.log,Resumè-ÉTÉ.csv,résumé_été.txt"), "accented letters find the plain ones");
        Assert.That(await List(null, "ÉTÈ"), Is.EqualTo("RESUME_ETE.log,Resumè-ÉTÉ.csv,résumé_été.txt"), "any accent, any case");
        Assert.That(await List(null, "resume"), Is.EqualTo("RESUME_ETE.log,Resumè-ÉTÉ.csv,résumé_été.txt"));
        Assert.That(await List(null, "ca et la"), Is.EqualTo("çà et là.txt"), "ç and à too");
        Assert.That(await List(null, "e"), Does.Contain("résumé_été.txt").And.Contain("other.txt"), "e matches é and è wherever they are");
        Assert.That(await List(null, "oeuvre"), Is.EqualTo(""), "a ligature is not decomposed: œ stays œ");
        Assert.That(await List(null, "œuvre"), Is.EqualTo("œuvre.txt"));
        Assert.That(await List("ETE"), Is.EqualTo("RESUME_ETE.log,Resumè-ÉTÉ.csv,résumé_été.txt"), "the name filter box behaves the same");
    }

    [Test]
    public void TextMatch_FoldsAccentsAndCase_AndSurvivesStrangeText()
    {
        Assert.That(TextMatch.Fold("Été à Çà œ ÅØ"), Is.EqualTo("ete a ca œ aø"));
        Assert.That(TextMatch.Fold(""), Is.EqualTo(""));
        Assert.That(TextMatch.Fold("plain.txt"), Is.EqualTo("plain.txt"));
        Assert.That(TextMatch.Contains("Café.txt", TextMatch.Fold("CAFE")), Is.True);
        Assert.That(() => TextMatch.Fold("broken \ud800 surrogate"), Throws.Nothing);
    }

    [Test]
    public void ASubstringThatDiffersOnlyByAccentsOrCase_IsTheSameSubstring()
    {
        var (folder, _) = Local("filter-accent-dup");
        var id = Repo.SaveFilter(folder.Id, 0, "été");

        Assert.That(Repo.FilterProblem(folder.Id, "ETE", 0), Does.Contain("already in the list"));
        Assert.That(Repo.FilterProblem(folder.Id, "ete", 0), Does.Contain("already in the list"));
        Assert.That(Repo.FilterProblem(folder.Id, "ÉTÉ", id), Is.Null, "changing it to another spelling of itself is allowed");
        Assert.That(Repo.FilterProblem(folder.Id, "automne", 0), Is.Null);
    }

    private async Task<string> Page(long folderId, string query = "") =>
        await _client.GetStringAsync($"/Shares/{folderId}{query}");

    [Test]
    public async Task TheBox_ShowsTheSubstrings_AndTickingThemNarrowsTheListOfFiles()
    {
        var (folder, dir) = Local("filter-page");
        Directory.CreateDirectory(Path.Combine(dir, "archive"));
        File(dir, "a_PROD_1.zip", 1);
        File(dir, "b_TEST_1.zip", 1);
        File(dir, "c_DEV_1.zip", 1);
        var prod = Repo.SaveFilter(folder.Id, 0, "_PROD_");
        var test = Repo.SaveFilter(folder.Id, 0, "_TEST_");

        var plain = await Page(folder.Id);
        Assert.That(plain, Does.Contain("Substrings").And.Contain("_PROD_").And.Contain("_TEST_").And.Contain("+ Add"));
        Assert.That(plain, Does.Contain("a_PROD_1.zip").And.Contain("b_TEST_1.zip").And.Contain("c_DEV_1.zip"), "nothing ticked: all the files");
        Assert.That(plain, Does.Not.Contain("Untick all").And.Not.Contain("filtered by the ticked"));

        var ticked = await Post($"/Shares/{folder.Id}?handler=ToggleFilter&filter={prod}", ("active", "true"));
        Assert.That(ticked.Headers.GetValues("HX-Refresh").Single(), Is.EqualTo("true"));

        var one = await Page(folder.Id);
        Assert.That(one, Does.Contain("a_PROD_1.zip").And.Not.Contain("b_TEST_1.zip").And.Not.Contain("c_DEV_1.zip"));
        Assert.That(one, Does.Contain("archive"), "folders stay so the tree can be walked");
        Assert.That(one, Does.Match("class=\"chip on\"").And.Contain("Untick all").And.Contain("filtered by the ticked substrings"));
        Assert.That(one, Does.Match($"name=\"active\" value=\"true\" checked"));

        await Post($"/Shares/{folder.Id}?handler=ToggleFilter&filter={test}", ("active", "true"));
        var two = await Page(folder.Id);
        Assert.That(two, Does.Contain("a_PROD_1.zip").And.Contain("b_TEST_1.zip").And.Not.Contain("c_DEV_1.zip"), "any of the ticked ones");

        // Unticking: the checkbox sends nothing when it is off.
        await Post($"/Shares/{folder.Id}?handler=ToggleFilter&filter={prod}");
        Assert.That(await Page(folder.Id), Does.Not.Contain("a_PROD_1.zip").And.Contain("b_TEST_1.zip"));

        var cleared = await Post($"/Shares/{folder.Id}?handler=ClearFilters");
        Assert.That(cleared.Headers.GetValues("HX-Refresh").Single(), Is.EqualTo("true"));
        Assert.That(await Page(folder.Id), Does.Contain("a_PROD_1.zip").And.Contain("c_DEV_1.zip"), "nothing ticked: all the files again");
    }

    [Test]
    public async Task TheTicks_StayWhenTheSortThePageOrTheSubFolderChange()
    {
        var (folder, dir) = Local("filter-sticky");
        Directory.CreateDirectory(Path.Combine(dir, "sub"));
        File(dir, "keep_1.log", 5);
        File(dir, "drop_1.log", 9);
        File(Path.Combine(dir, "sub"), "keep_2.log", 1);
        File(Path.Combine(dir, "sub"), "drop_2.log", 1);
        var id = Repo.SaveFilter(folder.Id, 0, "keep");
        Repo.SetFilterActive(id, true);

        Assert.That(await Page(folder.Id, "?sort=size&dir=desc"), Does.Contain("keep_1.log").And.Not.Contain("drop_1.log"));
        var inside = await Page(folder.Id, "?path=sub");
        Assert.That(inside, Does.Contain("keep_2.log").And.Not.Contain("drop_2.log"), "the same filter applies inside a sub-folder");
    }

    [Test]
    public async Task AddingAChangingAndRemovingASubstring_GoThroughTheDialog_AndReloadThePage()
    {
        var (folder, _) = Local("filter-dialog");

        var dialog = await _client.GetStringAsync($"/Shares/{folder.Id}?handler=FilterForm&filter=0");
        Assert.That(dialog, Does.Contain("Add a substring").And.Contain("name=\"text\"").And.Not.Contain("Remove"));

        var added = await Post($"/Shares/{folder.Id}?handler=SaveFilter&filter=0", ("text", "  _PROD_  "));
        Assert.That(added.Headers.GetValues("HX-Refresh").Single(), Is.EqualTo("true"));
        var id = Repo.GetFilters(folder.Id).Single().Id;
        Assert.That(Repo.GetFilter(id)!.Text, Is.EqualTo("_PROD_"), "trimmed");

        var edit = await _client.GetStringAsync($"/Shares/{folder.Id}?handler=FilterForm&filter={id}");
        Assert.That(edit, Does.Contain("Change the substring").And.Contain("value=\"_PROD_\"").And.Contain("Remove"));

        await Post($"/Shares/{folder.Id}?handler=SaveFilter&filter={id}", ("text", "_LIVE_"));
        Assert.That(Repo.GetFilters(folder.Id).Single().Text, Is.EqualTo("_LIVE_"));

        var removed = await Post($"/Shares/{folder.Id}?handler=DeleteFilter&filter={id}");
        Assert.That(removed.Headers.GetValues("HX-Refresh").Single(), Is.EqualTo("true"));
        Assert.That(Repo.GetFilters(folder.Id), Is.Empty);
        Assert.That(await Page(folder.Id), Does.Contain("Add substrings to keep only some of the files"));
    }

    [Test]
    public async Task TheDialog_RefusesABlankOrDuplicateSubstring_AndShowsWhy()
    {
        var (folder, _) = Local("filter-invalid");
        Repo.SaveFilter(folder.Id, 0, "keep");

        var blank = await Post($"/Shares/{folder.Id}?handler=SaveFilter&filter=0", ("text", "   "));
        Assert.That(await blank.Content.ReadAsStringAsync(), Does.Contain("Type a substring"));
        Assert.That(blank.Headers.GetValues("HX-Retarget").Single(), Is.EqualTo("#dlg"));

        var duplicate = await Post($"/Shares/{folder.Id}?handler=SaveFilter&filter=0", ("text", "KEEP"));
        Assert.That(await duplicate.Content.ReadAsStringAsync(), Does.Contain("already in the list"));
        Assert.That(Repo.GetFilters(folder.Id), Has.Count.EqualTo(1));
    }

    [Test]
    public async Task AFilterOfAnotherFolder_OrOneThatDoesNotExist_CannotBeTouched()
    {
        var (mine, _) = Local("filter-mine");
        var (theirs, _) = Local("filter-theirs");
        var foreign = Repo.SaveFilter(theirs.Id, 0, "secret");

        foreach (var handler in new[] { "ToggleFilter", "DeleteFilter" })
            Assert.That((await Post($"/Shares/{mine.Id}?handler={handler}&filter={foreign}")).StatusCode, Is.EqualTo(HttpStatusCode.NotFound), handler);
        Assert.That((await Post($"/Shares/{mine.Id}?handler=SaveFilter&filter={foreign}", ("text", "hijack"))).StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
        Assert.That((await _client.GetAsync($"/Shares/{mine.Id}?handler=FilterForm&filter={foreign}")).StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
        Assert.That((await Post($"/Shares/{mine.Id}?handler=DeleteFilter&filter=999999")).StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
        Assert.That((await Post("/Shares/999999?handler=ClearFilters")).StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
        Assert.That(Repo.GetFilter(foreign)!.Text, Is.EqualTo("secret"));
        Assert.That(Repo.GetFilter(foreign)!.Active, Is.False);
    }

    [Test]
    public async Task TheMenuListsSharedFoldersAfterWorkspaces_AndBeforeSettings()
    {
        var html = await _client.GetStringAsync("/Shares");

        var workspaces = html.IndexOf("href=\"/Workplaces\"", StringComparison.Ordinal);
        var shares = html.IndexOf("href=\"/Shares\"", StringComparison.Ordinal);
        var settings = html.IndexOf("href=\"/Settings\"", StringComparison.Ordinal);

        Assert.That(workspaces, Is.GreaterThan(0));
        Assert.That(shares, Is.GreaterThan(workspaces), "Shared folders come after Workspaces");
        Assert.That(settings, Is.GreaterThan(shares), "and Settings stays last");
        Assert.That(html.IndexOf("href=\"/Oracle\"", StringComparison.Ordinal), Is.LessThan(workspaces), "Connections stay before Workspaces");
    }

    [Test]
    public async Task TheMenuHasASharedFoldersEntry()
    {
        Assert.That(await _client.GetStringAsync("/Shares"), Does.Contain("href=\"/Shares\"").And.Contain("Shared folders"));
    }
}
