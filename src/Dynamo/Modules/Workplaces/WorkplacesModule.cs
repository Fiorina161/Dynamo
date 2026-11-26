using Dynamo.Core;

namespace Dynamo.Modules.Workplaces;

/// <summary>
/// Workspaces: named sets of queries shown together on one page, each in its
/// own panel with its own auto-refresh, to monitor a few things at once.
/// A panel owns its query: written in the workspace, or copied from a panel of
/// another workspace (an import). The bind
/// values are shared by the whole workspace.
///
/// The concept started as "watches": the module id and the tables
/// (watch, watch_panel) keep that name, since shipped migrations are
/// append-only.
/// </summary>
public sealed class WorkplacesModule : IModule
{
    public string Id => "watches";
    public string Title => "Workspaces";
    public string Href => "/Workplaces";
    public int Order => 35;

    // Panels side by side.
    public string Icon =>
        "<rect x=\"3\" y=\"3\" width=\"8\" height=\"10\" rx=\"1.5\"/>" +
        "<rect x=\"13\" y=\"3\" width=\"8\" height=\"5\" rx=\"1.5\"/>" +
        "<rect x=\"13\" y=\"11\" width=\"8\" height=\"10\" rx=\"1.5\"/>" +
        "<rect x=\"3\" y=\"16\" width=\"8\" height=\"5\" rx=\"1.5\"/>";

    public IReadOnlyList<string> Migrations { get; } =
    [
        """
        CREATE TABLE watch (
            id    INTEGER PRIMARY KEY,
            name  TEXT NOT NULL COLLATE NOCASE UNIQUE,
            binds TEXT NOT NULL DEFAULT '{}');

        CREATE TABLE watch_panel (
            id              INTEGER PRIMARY KEY,
            watch_id        INTEGER NOT NULL REFERENCES watch(id) ON DELETE CASCADE,
            query_id        INTEGER NOT NULL REFERENCES saved_query(id) ON DELETE CASCADE,
            connection_name TEXT    NOT NULL DEFAULT '',
            refresh_seconds INTEGER NOT NULL DEFAULT 0,
            position        INTEGER NOT NULL);

        CREATE INDEX ix_watch_panel ON watch_panel(watch_id, position);
        """,
        // Rows a panel fetches (each panel has its own limit).
        "ALTER TABLE watch_panel ADD COLUMN max_rows INTEGER NOT NULL DEFAULT 500;",
        // A panel owns its query. The table is rebuilt without the link to
        // saved_query (a saved query is only a template now); each existing
        // panel becomes an independent copy of the query it pointed at, with
        // the connection it effectively used.
        """
        CREATE TABLE watch_panel_v2 (
            id              INTEGER PRIMARY KEY,
            watch_id        INTEGER NOT NULL REFERENCES watch(id) ON DELETE CASCADE,
            name            TEXT    NOT NULL,
            sql_text        TEXT    NOT NULL,
            last_params     TEXT    NOT NULL DEFAULT '{}',
            connection_name TEXT    NOT NULL DEFAULT '',
            refresh_seconds INTEGER NOT NULL DEFAULT 0,
            max_rows        INTEGER NOT NULL DEFAULT 500,
            position        INTEGER NOT NULL);

        INSERT INTO watch_panel_v2
            (id, watch_id, name, sql_text, last_params, connection_name,
             refresh_seconds, max_rows, position)
        SELECT p.id, p.watch_id, q.name, q.sql_text, q.last_params,
               CASE WHEN p.connection_name <> '' THEN p.connection_name
                    ELSE q.connection_name END,
               p.refresh_seconds, p.max_rows, p.position
          FROM watch_panel p JOIN saved_query q ON q.id = p.query_id;

        DROP TABLE watch_panel;
        ALTER TABLE watch_panel_v2 RENAME TO watch_panel;
        CREATE INDEX ix_watch_panel ON watch_panel(watch_id, position);
        """,
        // How many rows of the result a panel shows before it scrolls.
        "ALTER TABLE watch_panel ADD COLUMN view_rows INTEGER NOT NULL DEFAULT 10;",
        // The Queries page was removed (a saved query is a workspace of one
        // query): every saved query becomes a panel of a new workspace "Saved
        // queries" (or "Saved queries (from Queries)" when that name is taken),
        // named "Category / Name" when it had a category, in the order the
        // Queries page listed them. The tables saved_query and
        // collapsed_category are left as they are (nothing is deleted); nothing
        // reads them any more. Nothing is created when there was no saved query.
        """
        CREATE TEMP TABLE migrated_queries_workspace (id INTEGER);

        INSERT INTO watch (name)
        SELECT CASE WHEN EXISTS (SELECT 1 FROM watch WHERE name = 'Saved queries')
                    THEN 'Saved queries (from Queries)' ELSE 'Saved queries' END
         WHERE EXISTS (SELECT 1 FROM saved_query);

        INSERT INTO migrated_queries_workspace SELECT last_insert_rowid() WHERE changes() > 0;

        INSERT INTO watch_panel
            (watch_id, name, sql_text, last_params, connection_name,
             refresh_seconds, max_rows, view_rows, position)
        SELECT (SELECT id FROM migrated_queries_workspace),
               substr(CASE WHEN category <> '' THEN category || ' / ' || name ELSE name END, 1, 100),
               sql_text, last_params, connection_name, 0, 500, 10,
               ROW_NUMBER() OVER (ORDER BY (category = ''), category, name)
          FROM saved_query
         WHERE EXISTS (SELECT 1 FROM migrated_queries_workspace);

        DROP TABLE migrated_queries_workspace;
        """,
        // A panel collapsed to its title line (kept with the panel, so a duplicate
        // and every browser agree).
        "ALTER TABLE watch_panel ADD COLUMN collapsed INTEGER NOT NULL DEFAULT 0;",
        // "Visible rows" 0 now means "the default number of rows from Settings" (which
        // starts at 10). Until now every panel got a hard-coded 10 unless changed, so the
        // panels at 10 become 0 and follow the setting; any other number was chosen and
        // stays.
        "UPDATE watch_panel SET view_rows = 0 WHERE view_rows = 10;",
        // What a panel is: 'query' (an ordinary query) or 'sessions' (the DBA sessions panel, which
        // adds a Kill button to each row). Copied by Duplicate and by an import.
        "ALTER TABLE watch_panel ADD COLUMN kind TEXT NOT NULL DEFAULT 'query';",
        // The order of the columns of the panel's result as the user dragged them: a JSON array of column
        // names, empty = the query's own order. Copied by Duplicate and by an import.
        "ALTER TABLE watch_panel ADD COLUMN column_order TEXT NOT NULL DEFAULT '';",
    ];
}
