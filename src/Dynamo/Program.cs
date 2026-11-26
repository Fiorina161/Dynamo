using Dynamo.Core;
using Dynamo.Modules.Bookmarks;
using Dynamo.Modules.OracleConnections;
using Dynamo.Modules.Queries;
using Dynamo.Modules.Settings;
using Dynamo.Modules.Workplaces;
using Microsoft.Extensions.Hosting.WindowsServices;

const string ServiceName = "Dynamo";
const int DefaultPort = 4040;
const int DefaultHttpsPort = 4343;

var home = DynamoHome.Resolve();

// A published build carries its wwwroot beside the executable: use that
// whatever the working directory is (System32 under the service manager,
// anywhere when started by hand). From source, `dotnet run` runs in the
// project directory and the default content root is right.
var options = new WebApplicationOptions
{
    Args = args,
    ContentRootPath = Directory.Exists(Path.Combine(AppContext.BaseDirectory, "wwwroot"))
        ? AppContext.BaseDirectory
        : null,
};
var builder = WebApplication.CreateBuilder(options);

// Runs as a Windows service when started by the service manager, as a plain
// console application otherwise.
builder.Services.AddWindowsService(o => o.ServiceName = ServiceName);

// Listens on every network interface, so other computers can reach it; the
// Windows Firewall stays closed to them unless the installer was told to open
// the port. This computer is never asked for a password; another one is
// refused until a password is set in Settings, then has to log in (see
// AccessMiddleware). DYNAMO_LOCAL_ONLY=1 listens on loopback only.
var port = int.TryParse(Environment.GetEnvironmentVariable("DYNAMO_PORT"), out var p)
    ? p
    : DefaultPort;
var localOnly = Environment.GetEnvironmentVariable("DYNAMO_LOCAL_ONLY") is "1" or "true" or "TRUE" or "True";
// https: a second port with Dynamo's own certificate (DYNAMO_HTTPS_PORT, 0 = off;
// off too when listening on this computer only, where nothing needs encrypting).
var httpsPort = localOnly ? 0
    : int.TryParse(Environment.GetEnvironmentVariable("DYNAMO_HTTPS_PORT"), out var hp) ? hp : DefaultHttpsPort;
builder.Services.AddSingleton(new HttpsOptions(httpsPort));
builder.WebHost.ConfigureKestrel(kestrel =>
{
    if (localOnly)
    {
        kestrel.ListenLocalhost(port);
        return;
    }

    kestrel.ListenAnyIP(port);
    if (httpsPort > 0)
    {
        var certificates = kestrel.ApplicationServices.GetRequiredService<CertificateService>();
        // The certificate is picked at each handshake: a renewal needs no restart.
        kestrel.ListenAnyIP(httpsPort, listen => listen.UseHttps(https =>
            https.ServerCertificateSelector = (_, _) => certificates.Current));
    }
});

builder.Services.AddSingleton(home);
builder.Services.AddSingleton<Db>();
builder.Services.AddSingleton<BookmarkRepository>();
builder.Services.AddSingleton<BookmarkImporter>();
builder.Services.AddSingleton<OracleConnectionRepository>();
builder.Services.AddSingleton<OracleConnector>();
builder.Services.AddSingleton<TnsNamesImporter>();
builder.Services.AddSingleton<IQueryRunner, QueryRunner>();
builder.Services.AddSingleton<IPlanRunner, PlanRunner>();
builder.Services.AddSingleton<ISessionKiller, SessionKiller>();
builder.Services.AddSingleton<WorkplaceRepository>();
builder.Services.AddSingleton<SettingsRepository>();
builder.Services.AddSingleton<MenuOptions>();
builder.Services.AddSingleton<ResultOptions>();
builder.Services.AddSingleton<PasswordService>();
builder.Services.AddSingleton<SessionStore>();
builder.Services.AddSingleton<LoginThrottle>();
builder.Services.AddSingleton<CertificateService>();
// "A new version is available": a look at GitHub's public releases, only when
// the user asks for it (Settings, Check now); nothing runs on its own.
builder.Services.AddHttpClient(UpdateChecker.ClientName, client =>
{
    client.Timeout = TimeSpan.FromSeconds(10);
    client.DefaultRequestHeaders.UserAgent.ParseAdd("Dynamo-update-check");
    client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
});
builder.Services.AddSingleton<UpdateChecker>();
// "Update now": the download is big, so its own client with a long timeout; it
// is only used for the one release address UpdateInstaller accepts.
builder.Services.AddHttpClient(UpdateInstaller.DownloadClientName, client =>
{
    client.Timeout = TimeSpan.FromMinutes(10);
    client.DefaultRequestHeaders.UserAgent.ParseAdd("Dynamo-update");
});
builder.Services.AddSingleton(new UpdateInstallerOptions(
    Microsoft.Extensions.Hosting.WindowsServices.WindowsServiceHelpers.IsWindowsService(), port));
builder.Services.AddSingleton<IUpdateLauncher, PowerShellUpdateLauncher>();
builder.Services.AddSingleton<UpdateInstaller>();
builder.Services.AddHostedService<CertificateRenewalService>();

// Each use case registers its module here.
builder.Services.AddSingleton<IModule, BookmarksModule>();
builder.Services.AddSingleton<IModule, OracleConnectionsModule>();
builder.Services.AddSingleton<IModule, QueriesModule>();
builder.Services.AddSingleton<IModule, WorkplacesModule>();
builder.Services.AddSingleton<IModule, SettingsModule>();

builder.Services.AddRazorPages();

var app = builder.Build();

app.Services.GetRequiredService<Db>().Migrate(app.Services.GetServices<IModule>());

// The Oracle passwords and the private keys of the https certificates are protected (Windows DPAPI,
// machine scope). What an older version stored in clear is converted here, once, before anything
// reads it; the certificate below is made after this.
{
    var (passwords, keys) = Secrets.ProtectStored(app.Services.GetRequiredService<Db>());
    if (passwords + keys > 0)
        app.Logger.LogInformation(
            "Protected {Passwords} Oracle password(s) and {Keys} certificate key(s) in the database", passwords, keys);
}

// The https certificate exists before the first connection (made on first start,
// renewed by the hosted service afterwards).
if (app.Services.GetRequiredService<HttpsOptions>().Enabled)
    app.Services.GetRequiredService<CertificateService>().Ensure();

// One-shot command, no web server:  Dynamo import-bookmarks <bookmarks.json>
if (args is ["import-bookmarks", var file])
{
    var r = app.Services.GetRequiredService<BookmarkImporter>().Import(file);
    Console.WriteLine(
        $"Imported into {home.DatabaseFile}: {r.Added} bookmarks added, " +
        $"{r.Skipped} already present, {r.Groups} groups created.");
    return;
}

// One-shot command, no web server:  Dynamo import-tnsnames [tnsnames.ora]
// (default: the file in TNS_ADMIN)
if (args is ["import-tnsnames", ..var rest] && rest.Length <= 1)
{
    var tnsAdmin = Environment.GetEnvironmentVariable("TNS_ADMIN");
    var tnsFile = rest.Length == 1
        ? rest[0]
        : tnsAdmin is { Length: > 0 } ? Path.Combine(tnsAdmin, "tnsnames.ora") : "";
    if (!File.Exists(tnsFile))
    {
        Console.Error.WriteLine(
            tnsFile.Length == 0
                ? "No file given and TNS_ADMIN is not set."
                : $"File not found: {tnsFile}");
        Environment.ExitCode = 1;
        return;
    }

    var t = app.Services.GetRequiredService<TnsNamesImporter>().Import(tnsFile);
    Console.WriteLine($"Read {tnsFile}");
    Console.WriteLine(
        $"Imported into {home.DatabaseFile}: {t.Added} connections added, " +
        $"{t.AlreadyThere} already defined.");
    if (t.Identical.Count > 0)
        Console.WriteLine($"Defined more than once, identical (kept once): {string.Join(", ", t.Identical)}");
    if (t.Conflicting.Count > 0)
        Console.WriteLine(
            $"Defined more than once with DIFFERENT targets (the first was kept): {string.Join(", ", t.Conflicting)}");
    return;
}

// The gate comes first: nothing but the login page and its assets is served
// to another computer before it has logged in.
app.UseMiddleware<AccessMiddleware>();
app.UseStaticFiles();
// The authority's public certificate: what a browser must trust to accept https.
app.MapGet(AccessMiddleware.CertificatePath, (CertificateService certificates) =>
    certificates.AuthorityCertificate is { } der
        ? Results.File(der, "application/x-x509-ca-cert", "dynamo-certificate.cer")
        : Results.NotFound());
// The concept used to be called "watches": keep old links working.
app.MapGet("/Watches/{**rest}", (string? rest) => Results.Redirect(
    "/Workplaces" + (string.IsNullOrEmpty(rest) ? "" : "/" + rest), permanent: true));

app.MapRazorPages();

app.Logger.LogInformation("Dynamo home: {Home}", home.Path);
app.Run();

// Lets the tests start the whole application in memory.
public partial class Program;
