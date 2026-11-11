using Dynamo.Core;
using Dynamo.Modules.Bookmarks;
using Dynamo.Modules.OracleConnections;
using Dynamo.Modules.Queries;
using Microsoft.Extensions.Hosting.WindowsServices;

const string ServiceName = "Dynamo";
const int DefaultPort = 4040;

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

// Local use only: listen on the loopback interface.
var port = int.TryParse(Environment.GetEnvironmentVariable("DYNAMO_PORT"), out var p)
    ? p
    : DefaultPort;
builder.WebHost.UseUrls($"http://localhost:{port}");

builder.Services.AddSingleton(home);
builder.Services.AddSingleton<Db>();
builder.Services.AddSingleton<BookmarkRepository>();
builder.Services.AddSingleton<BookmarkImporter>();
builder.Services.AddSingleton<OracleConnectionRepository>();
builder.Services.AddSingleton<OracleConnector>();
builder.Services.AddSingleton<TnsNamesImporter>();
builder.Services.AddSingleton<QueryRepository>();
builder.Services.AddSingleton<IQueryRunner, QueryRunner>();

// Each use case registers its module here.
builder.Services.AddSingleton<IModule, BookmarksModule>();
builder.Services.AddSingleton<IModule, OracleConnectionsModule>();
builder.Services.AddSingleton<IModule, QueriesModule>();

builder.Services.AddRazorPages();

var app = builder.Build();

app.Services.GetRequiredService<Db>().Migrate(app.Services.GetServices<IModule>());

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

app.UseStaticFiles();
app.MapRazorPages();

app.Logger.LogInformation("Dynamo home: {Home}", home.Path);
app.Run();

// Lets the tests start the whole application in memory.
public partial class Program;
