using Dynamo.Core;

namespace Dynamo.Modules.Queries;

/// <summary>
/// What is left of the Queries section, which was removed (a saved query is a
/// workspace of one query): only its two tables and their migrations, kept so
/// that the database stays consistent and nothing is deleted. The Workplaces
/// module's last migration copied the saved queries into a workspace; nothing
/// reads these tables any more. It has no page and no menu entry (empty
/// <see cref="Href"/>).
/// </summary>
public sealed class QueriesModule : IModule
{
    public string Id => "queries";
    public string Title => "";
    public string Href => "";
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
        // The categories collapsed on the list: a row means collapsed ("" is
        // Uncategorized).
        """
        CREATE TABLE collapsed_category (
            category TEXT PRIMARY KEY COLLATE NOCASE);
        """,
    ];
}
