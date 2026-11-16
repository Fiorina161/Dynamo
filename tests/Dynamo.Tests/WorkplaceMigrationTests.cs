using Dapper;
using Dynamo.Core;
using Dynamo.Modules.Queries;
using Dynamo.Modules.Workplaces;

namespace Dynamo.Tests;

/// <summary>
/// The migration that made panels own their query: a database in the previous
/// format (panels pointing at saved queries) must come out with independent
/// copies, losing nothing.
/// </summary>
public class WorkplaceMigrationTests
{
    /// <summary>The workspaces module as it was before: the first migrations only.</summary>
    private sealed class OlderWorkplaces(IReadOnlyList<string> migrations) : IModule
    {
        public string Id => new WorkplacesModule().Id;
        public string Title => "old";
        public string Href => "/old";
        public int Order => 35;
        public IReadOnlyList<string> Migrations { get; } = migrations;
    }

    private string _home = "";
    private string? _previousHome;

    [SetUp]
    public void SetUp()
    {
        _previousHome = Environment.GetEnvironmentVariable("DYNAMO_HOME");
        _home = Path.Combine(Path.GetTempPath(), "dynamo-migration-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("DYNAMO_HOME", _home);
    }

    [TearDown]
    public void TearDown()
    {
        Environment.SetEnvironmentVariable("DYNAMO_HOME", _previousHome);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_home, true); } catch { /* best effort */ }
    }

    [Test]
    public void ExistingPanels_BecomeIndependentCopies_WithTheirSettings()
    {
        var current = new WorkplacesModule();
        var db = new Db(DynamoHome.Resolve());
        db.Migrate([new QueriesModule(), new OlderWorkplaces(current.Migrations.Take(2).ToList())]);

        using (var conn = db.Open())
        {
            conn.Execute("""
                INSERT INTO saved_query (id, name, category, connection_name, sql_text, notes, last_params)
                VALUES (1, 'Orders', 'Ops', 'PROD', 'select * from o where k = :k', '', '{"k":"v1"}'),
                       (2, 'Queue',  '',    '',     'select 2 from dual',            '', '{}');
                INSERT INTO watch (id, name, binds) VALUES (1, 'Debug', '{"k":"shared"}');
                INSERT INTO watch_panel (id, watch_id, query_id, connection_name, refresh_seconds, position, max_rows)
                VALUES (10, 1, 1, '',     5, 1, 100),
                       (11, 1, 2, 'TEST', 0, 2, 500),
                       (12, 1, 1, 'TEST', 9, 3, 25);
                """);
        }

        // The real module now: the pending migration runs on that data.
        db.Migrate([new QueriesModule(), current]);

        var repo = new WorkplaceRepository(db);
        var panels = repo.Get(1)!.Panels;
        Assert.That(panels.Select(p => p.Id), Is.EqualTo(new long[] { 10, 11, 12 }), "same panels, same order");
        Assert.That(panels.Select(p => p.QueryName), Is.EqualTo(new[] { "Orders", "Queue", "Orders" }));
        Assert.That(panels[0].Sql, Is.EqualTo("select * from o where k = :k"));
        // The connection a panel effectively used: its own, else the query's.
        Assert.That(panels.Select(p => p.Connection), Is.EqualTo(new[] { "PROD", "TEST", "TEST" }));
        Assert.That(panels.Select(p => p.RefreshSeconds), Is.EqualTo(new long[] { 5, 0, 9 }));
        Assert.That(panels.Select(p => p.MaxRows), Is.EqualTo(new long[] { 100, 500, 25 }));
        Assert.That(panels[0].LastParams["k"], Is.EqualTo("v1"), "the seed values came along");
        Assert.That(repo.Get(1)!.Binds["k"], Is.EqualTo("shared"));

        // From now on the saved queries are only templates.
        using var after = db.Open();
        after.Execute("UPDATE saved_query SET sql_text = 'select 99 from dual', name = 'Changed' WHERE id = 1");
        after.Execute("DELETE FROM saved_query WHERE id = 2");
        var again = repo.Get(1)!.Panels;
        Assert.That(again, Has.Count.EqualTo(3), "deleting a saved query removes no panel");
        Assert.That(again[0].Sql, Is.EqualTo("select * from o where k = :k"));
        Assert.That(again[1].QueryName, Is.EqualTo("Queue"));

        // No link to saved_query is left in the table.
        Assert.That(after.Query<string>("SELECT name FROM pragma_table_info('watch_panel')"),
            Does.Not.Contain("query_id"));
        // And it still works as a table: a new panel, a deleted workspace.
        repo.AddNew(1, "Fresh", "select 5 from dual", "");
        repo.Delete(1);
        Assert.That(after.ExecuteScalar<long>("SELECT COUNT(*) FROM watch_panel"), Is.EqualTo(0));
    }
}
