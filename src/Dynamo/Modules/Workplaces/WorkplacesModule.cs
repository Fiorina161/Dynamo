using Dynamo.Core;

namespace Dynamo.Modules.Workplaces;

/// <summary>
/// Workspaces: named sets of queries shown together on one page, each in its
/// own panel with its own auto-refresh, to monitor a few things at once.
/// A panel owns its query: a copy of a saved query made when it was added, or
/// one written in the workspace (the saved queries act as templates). The bind
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
    ];
}
