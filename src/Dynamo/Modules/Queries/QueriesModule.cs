using Dynamo.Core;

namespace Dynamo.Modules.Queries;

/// <summary>
/// Saved SQL queries (read-only SELECTs) that can be run against any of the
/// named Oracle connections, with their bind variables filled in on the page.
/// </summary>
public sealed class QueriesModule : IModule
{
    public string Id => "queries";
    public string Title => "Queries";
    public string Href => "/Queries";
    public int Order => 30;

    public IReadOnlyList<string> Migrations { get; } =
    [
        """
        CREATE TABLE saved_query (
            id              INTEGER PRIMARY KEY,
            name            TEXT NOT NULL COLLATE NOCASE,
            category        TEXT NOT NULL DEFAULT '',
            connection_name TEXT NOT NULL DEFAULT '',
            sql_text        TEXT NOT NULL,
            notes           TEXT NOT NULL DEFAULT '',
            last_params     TEXT NOT NULL DEFAULT '{}');

        CREATE INDEX ix_saved_query_category ON saved_query(category, name);
        """,
    ];
}
