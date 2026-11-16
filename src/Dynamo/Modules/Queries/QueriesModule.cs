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

    // A table of results.
    public string Icon =>
        "<rect x=\"3\" y=\"4\" width=\"18\" height=\"16\" rx=\"2\"/>" +
        "<path d=\"M3 10h18M3 15h18M9 4v16\"/>";

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
        // The categories collapsed on the list: a row means collapsed ("" is
        // Uncategorized).
        """
        CREATE TABLE collapsed_category (
            category TEXT PRIMARY KEY COLLATE NOCASE);
        """,
    ];
}
